using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Siemens.Engineering;
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
