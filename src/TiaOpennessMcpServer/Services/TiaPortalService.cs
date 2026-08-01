using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Services;

/// <summary>
/// Manages the lifecycle of a TIA Portal instance and open project.
/// All Portal API calls are marshaled through <see cref="StaTaskScheduler"/>
/// to ensure they originate from the required STA thread.
/// </summary>
public sealed class TiaPortalService : IDisposable
{
    private readonly StaTaskScheduler    _sta;
    private readonly TiaOpennessOptions  _opts;
    private readonly ILogger<TiaPortalService> _log;

    private TiaPortal? _portal;
    private Project?   _project;

    // Expose raw objects to peer services (all access must go through STA).
    internal TiaPortal? Portal  => _portal;
    internal Project?   Project => _project;

    public bool IsConnected => _portal is not null && _project is not null;

    public TiaPortalService(
        StaTaskScheduler sta,
        IOptions<TiaOpennessOptions> opts,
        ILogger<TiaPortalService> log)
    {
        _sta  = sta;
        _opts = opts.Value;
        _log  = log;

        Directory.CreateDirectory(_opts.ExportDirectory);
    }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches to a TIA Portal V20 process that is already running on this machine.
    /// Prefers the process that has the target project open (if projectPath is supplied).
    /// </summary>
    public async Task<ProjectInfo> AttachToRunningAsync(string? projectPath = null)
    {
        return await _sta.RunAsync(() =>
        {
            var processes = TiaPortal.GetProcesses();
            if (processes.Count == 0)
                throw new InvalidOperationException(
                    "No running TIA Portal process found. Please open TIA Portal V20 first.");

            _log.LogInformation("{Count} TIA Portal process(es) found — attaching…", processes.Count);

            // Prefer the process whose project path matches, otherwise take the first one.
            TiaPortalProcess targetProcess = processes[0];
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                foreach (TiaPortalProcess p in processes)
                {
                    if (p.ProjectPath is not null &&
                        p.ProjectPath.FullName.Equals(projectPath, StringComparison.OrdinalIgnoreCase))
                    {
                        targetProcess = p;
                        break;
                    }
                }
            }

            _portal   = targetProcess.Attach();
            _project  = _portal.Projects.Count > 0 ? _portal.Projects[0] : null;

            if (_project is null)
                throw new InvalidOperationException(
                    "Attached to TIA Portal but no project is open. " +
                    "Please open your project in TIA Portal and try again.");

            _log.LogInformation("Attached to running TIA Portal — project: {Name}", _project.Name);
            return BuildProjectInfo(_project);
        });
    }

    public async Task<ProjectInfo> OpenProjectAsync(string projectPath, bool headless = true)
    {
        return await _sta.RunAsync(() =>
        {
            _log.LogInformation("Opening TIA Portal (headless={Headless})…", headless);

            var mode = headless
                ? TiaPortalMode.WithoutUserInterface
                : TiaPortalMode.WithUserInterface;

            // Reuse existing portal instance if one is already running.
            _portal ??= new TiaPortal(mode);

            var file = new FileInfo(projectPath);
            if (!file.Exists)
                throw new FileNotFoundException($"Project file not found: {projectPath}");

            _project = _portal.Projects.Open(file);

            _log.LogInformation("Opened project: {Name}", _project.Name);
            return BuildProjectInfo(_project);
        });
    }

    public async Task SaveAsync()
    {
        EnsureConnected();
        await _sta.RunAsync(() =>
        {
            _log.LogInformation("Saving project…");
            _project!.Save();
        });
    }

    public async Task<ProjectInfo> GetProjectInfoAsync()
    {
        EnsureConnected();
        return await _sta.RunAsync(() => BuildProjectInfo(_project!));
    }

    // ── Clone project ─────────────────────────────────────────────────────────

    public async Task<Models.CloneResult> CloneProjectAsync(string newName, string targetFolder)
    {
        EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var result = new Models.CloneResult();
            var exportDir = Path.Combine(_opts.ExportDirectory, "clone_export");
            Directory.CreateDirectory(exportDir);

            // ── 1. Snapshot device info before we touch anything ──────────────
            var srcProject = _project!;
            var deviceSnapshots = new List<(string Name, string TypeId, string Ip)>();
            var blockFiles   = new List<(string DeviceName, string FilePath)>();
            var tagFiles     = new List<(string DeviceName, string FilePath)>();

            foreach (Device device in srcProject.Devices)
            {
                string typeId = "", ip = "";
                try { typeId = device.TypeIdentifier ?? ""; } catch { }
                ip = ReadIpFromDevice(device);
                deviceSnapshots.Add((device.Name, typeId, ip));

                var plc = GetPlcFromDevice(device);
                if (plc is null) continue;

                // Export blocks
                foreach (PlcBlock block in plc.BlockGroup.Blocks.Cast<PlcBlock>())
                {
                    var f = Path.Combine(exportDir, $"{device.Name}__BLOCK__{block.Name}.xml");
                    try
                    {
                        block.Export(new FileInfo(f), ExportOptions.WithDefaults);
                        blockFiles.Add((device.Name, f));
                        result.BlocksExported++;
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Export block {block.Name}: {ex.Message.Split('\n')[0]}");
                    }
                }

                // Export tag tables
                foreach (PlcTagTable table in plc.TagTableGroup.TagTables.Cast<PlcTagTable>())
                {
                    var f = Path.Combine(exportDir, $"{device.Name}__TAGS__{table.Name}.xml");
                    try
                    {
                        table.Export(new FileInfo(f), ExportOptions.WithDefaults);
                        tagFiles.Add((device.Name, f));
                        result.TagTablesExported++;
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Export tags {table.Name}: {ex.Message.Split('\n')[0]}");
                    }
                }
            }

            _log.LogInformation("Exported {B} blocks, {T} tag tables. Closing source project to create clone…",
                result.BlocksExported, result.TagTablesExported);

            // ── 2. Close source project (TIA only allows one project open at a time)
            var originalPath = srcProject.Path.FullName;
            srcProject.Save();
            srcProject.Close();
            _project = null;

            // ── 3. Create new project ─────────────────────────────────────────
            var dir = new DirectoryInfo(targetFolder);
            dir.Create();
            var newProject = _portal!.Projects.Create(dir, newName);
            result.ProjectPath = Path.Combine(targetFolder, newName + ".ap20");

            // ── 4. Recreate hardware ──────────────────────────────────────────
            foreach (var (devName, typeId, ip) in deviceSnapshots)
            {
                if (string.IsNullOrWhiteSpace(typeId))
                {
                    result.Warnings.Add($"Skipped device '{devName}': no type identifier.");
                    continue;
                }
                try
                {
                    var newDev = newProject.Devices.CreateWithItem(typeId, devName, devName);
                    result.DevicesCreated++;
                    if (!string.IsNullOrWhiteSpace(ip))
                        SetIpOnDevice(newDev, ip);
                }
                catch (Exception ex)
                {
                    // Firmware version mismatch — retry without version suffix
                    var baseId = System.Text.RegularExpressions.Regex.Replace(typeId, @"/V\d+\.\d+.*$", "");
                    try
                    {
                        var newDev = newProject.Devices.CreateWithItem(baseId, devName, devName);
                        result.DevicesCreated++;
                        result.Warnings.Add($"Device '{devName}' created with base type (version stripped).");
                        if (!string.IsNullOrWhiteSpace(ip))
                            SetIpOnDevice(newDev, ip);
                    }
                    catch (Exception ex2)
                    {
                        result.Warnings.Add($"Could not create device '{devName}': {ex2.Message.Split('\n')[0]}");
                    }
                }
            }

            // ── 5. Import blocks ──────────────────────────────────────────────
            foreach (var (devName, filePath) in blockFiles)
            {
                var plc = GetPlcFromProject(newProject, devName);
                if (plc is null) { result.Warnings.Add($"No PLC found for device '{devName}' in new project."); continue; }
                try
                {
                    plc.BlockGroup.Blocks.Import(new FileInfo(filePath), ImportOptions.Override);
                    result.BlocksImported++;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Import block {Path.GetFileName(filePath)}: {ex.Message.Split('\n')[0]}");
                }
            }

            // ── 6. Import tag tables ──────────────────────────────────────────
            foreach (var (devName, filePath) in tagFiles)
            {
                var plc = GetPlcFromProject(newProject, devName);
                if (plc is null) continue;
                try
                {
                    plc.TagTableGroup.TagTables.Import(new FileInfo(filePath), ImportOptions.Override);
                    result.TagTablesImported++;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Import tags {Path.GetFileName(filePath)}: {ex.Message.Split('\n')[0]}");
                }
            }

            // ── 7. Save new project ───────────────────────────────────────────
            newProject.Save();
            result.Success = true;
            _log.LogInformation("Clone complete → {Path}", result.ProjectPath);

            // ── 8. Reopen the original project so the dashboard stays connected
            try
            {
                _project = _portal!.Projects.Open(new FileInfo(originalPath));
                _log.LogInformation("Reopened original project: {Path}", originalPath);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Clone succeeded but could not reopen original project: {ex.Message.Split('\n')[0]}. Click Connect to reopen it manually.");
                _project = newProject;
            }

            return result;
        });
    }

    private static PlcSoftware? GetPlcFromDevice(Device device)
    {
        foreach (DeviceItem di in device.DeviceItems)
        {
            var sw = di.GetService<SoftwareContainer>()?.Software as PlcSoftware;
            if (sw is not null) return sw;
        }
        return null;
    }

    private static PlcSoftware? GetPlcFromProject(Project project, string deviceName)
    {
        foreach (Device device in project.Devices)
        {
            if (!device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
            var plc = GetPlcFromDevice(device);
            if (plc is not null) return plc;
        }
        return null;
    }

    private static string ReadIpFromDevice(Device device)
    {
        try
        {
            foreach (DeviceItem di in device.DeviceItems)
            {
                var ni = di.GetService<NetworkInterface>();
                if (ni is null) continue;
                foreach (Node n in ni.Nodes)
                {
                    var addr = n.GetAttribute("Address") as string;
                    if (!string.IsNullOrWhiteSpace(addr)) return addr;
                }
            }
        }
        catch { }
        return "";
    }

    private static void SetIpOnDevice(Device device, string ip)
    {
        try
        {
            foreach (DeviceItem di in device.DeviceItems)
            {
                var ni = di.GetService<NetworkInterface>();
                if (ni is null) continue;
                foreach (Node n in ni.Nodes)
                {
                    try { n.SetAttribute("Address", ip); } catch { }
                    try { n.CreateAndConnectToSubnet("PN/IE_1"); } catch { }
                }
            }
        }
        catch { }
    }

    // ── Option packages ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Models.OptionPackageInfo>> GetOptionPackagesAsync()
    {
        EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var result = new List<Models.OptionPackageInfo>();
            foreach (UsedProduct pkg in _project!.UsedProducts)
            {
                result.Add(new Models.OptionPackageInfo
                {
                    DisplayName    = pkg.Name    ?? "",
                    DisplayVersion = pkg.Version ?? "",
                });
            }
            return (IReadOnlyList<Models.OptionPackageInfo>)result;
        });
    }

    public async Task CloseAsync()
    {
        if (_project is null) return;
        await _sta.RunAsync(() =>
        {
            if (_opts.AutoSaveOnDisconnect)
            {
                _log.LogInformation("Auto-saving before close…");
                _project.Save();
            }
            _project.Close();
            _project = null;
            _log.LogInformation("Project closed.");
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException(
                "No TIA Portal project is open. Call open_project first.");
    }

    private static ProjectInfo BuildProjectInfo(Project project)
    {
        string author = "", comment = "", modified = "", version = "";
        bool   changed = false;

        try { author   = project.Author ?? ""; } catch { }
        try { comment  = project.Comment.Items.Cast<MultilingualTextItem>()
                             .FirstOrDefault()?.Text ?? ""; } catch { }
        try { modified = project.LastModified.ToString("O"); } catch { }
        try { version  = project.GetAttribute("PortalVersion") as string ?? ""; } catch { }
        try { changed  = project.IsModified; } catch { }

        return new ProjectInfo
        {
            Name          = project.Name,
            Path          = project.Path.FullName,
            Author        = author,
            Comment       = comment,
            CreatedDate   = "",
            ModifiedDate  = modified,
            PortalVersion = version,
            DeviceCount   = project.Devices.Count,
            IsModified    = changed,
        };
    }

    public void Dispose()
    {
        if (_project is not null)
        {
            try
            {
                _sta.RunAsync(() =>
                {
                    if (_opts.AutoSaveOnDisconnect) _project.Save();
                    _project.Close();
                }).Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error during dispose close.");
            }
        }

        if (_portal is not null)
        {
            try { _portal.Dispose(); } catch { /* best effort */ }
            _portal = null;
        }
    }
}
