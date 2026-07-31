using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;

// Alias our model enums to avoid ambiguity with Siemens.Engineering.SW.Blocks.*
using ModelBlockType = TiaOpennessMcpServer.Models.BlockType;
using ModelLanguage  = TiaOpennessMcpServer.Models.ProgrammingLanguage;

namespace TiaOpennessMcpServer.Services;

public sealed class SoftwareService
{
    private readonly TiaPortalService _tia;
    private readonly StaTaskScheduler _sta;
    private readonly TiaOpennessOptions _opts;
    private readonly ILogger<SoftwareService> _log;

    public SoftwareService(
        TiaPortalService tia,
        StaTaskScheduler sta,
        IOptions<TiaOpennessOptions> opts,
        ILogger<SoftwareService> log)
    {
        _tia  = tia;
        _sta  = sta;
        _opts = opts.Value;
        _log  = log;
    }

    // ── Block enumeration ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Models.BlockInfo>> ListBlocksAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);
            var results = new List<Models.BlockInfo>();
            CollectBlocks(plc.BlockGroup, results);
            return (IReadOnlyList<Models.BlockInfo>)results;
        });
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<BlockContent> ReadBlockAsync(string deviceName, string blockName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            var lang       = MapLanguage(block);
            var xmlContent = "";
            var sclSource  = "";

            try
            {
                Directory.CreateDirectory(_opts.ExportDirectory);
                var exportFile = Path.Combine(
                    _opts.ExportDirectory,
                    $"{deviceName}_{blockName}_{DateTime.UtcNow:yyyyMMddHHmmss}.xml");

                block.Export(new FileInfo(exportFile), ExportOptions.WithDefaults);
                xmlContent = File.ReadAllText(exportFile);
                sclSource  = XmlHelper.ExtractSclSource(xmlContent) ?? "";
                _log.LogDebug("Read block {Block} ({Bytes} bytes XML)", blockName, xmlContent.Length);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Export failed for {Block}: {Msg}", blockName, ex.Message);
                sclSource = lang == ModelLanguage.SCL
                    ? $"// Cannot export block source.\n// {ex.Message.Split('\n')[0]}\n//\n// Tip: compile the full project in TIA Portal (Ctrl+B), then refresh."
                    : $"// Language: {lang}\n// Only SCL blocks have readable source code in TIA Openness.";
            }

            return new BlockContent
            {
                Name       = block.Name,
                Type       = MapBlockType(block),
                Number     = block.Number,
                Language   = lang,
                Author     = block.HeaderAuthor ?? "",
                Comment    = BlockComment(block),
                Modified   = block.ModifiedDate.ToString("O"),
                IsKnowHow  = block.IsKnowHowProtected,
                SizeBytes  = xmlContent.Length,
                SourceCode = sclSource,
                XmlContent = xmlContent,
            };
        });
    }

    // ── Write (SCL) ───────────────────────────────────────────────────────────

    public async Task WriteBlockSclAsync(string deviceName, string blockName, string sclSource)
    {
        _tia.EnsureConnected();
        await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            Directory.CreateDirectory(_opts.ExportDirectory);
            var exportFile = Path.Combine(_opts.ExportDirectory,
                $"{deviceName}_{blockName}_edit.xml");
            block.Export(new FileInfo(exportFile), ExportOptions.WithDefaults);

            var xml     = File.ReadAllText(exportFile);
            var patched = XmlHelper.InjectSclSource(xml, sclSource);
            File.WriteAllText(exportFile, patched);

            plc.BlockGroup.Blocks.Import(new FileInfo(exportFile), ImportOptions.Override);
            _log.LogInformation("Written SCL to {Block} on {Device}.", blockName, deviceName);
        });
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Models.BlockInfo> CreateBlockAsync(string deviceName, BlockCreateRequest req)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);

            Directory.CreateDirectory(_opts.ExportDirectory);
            var xmlContent = XmlHelper.CreateSclBlockXml(
                req.Name, req.Type.ToString(), req.Number, req.SourceCode);

            var importFile = Path.Combine(_opts.ExportDirectory,
                $"create_{deviceName}_{req.Name}.xml");
            File.WriteAllText(importFile, xmlContent);

            plc.BlockGroup.Blocks.Import(new FileInfo(importFile), ImportOptions.Override);
            var block = FindBlock(plc.BlockGroup, req.Name);

            _log.LogInformation("Created {Type} {Block} on {Device}.",
                req.Type, req.Name, deviceName);

            return new Models.BlockInfo
            {
                Name      = block.Name,
                Type      = MapBlockType(block),
                Number    = block.Number,
                Language  = MapLanguage(block),
                Author    = block.HeaderAuthor ?? "",
                Comment   = BlockComment(block),
                Modified  = block.ModifiedDate.ToString("O"),
                IsKnowHow = block.IsKnowHowProtected,
                SizeBytes = 0,
            };
        });
    }

    // ── Compile ───────────────────────────────────────────────────────────────

    public async Task<string> CompileBlockAsync(string deviceName, string blockName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            var compilable = block.GetService<ICompilable>()
                ?? throw new NotSupportedException(
                    $"Block '{blockName}' does not support compilation.");

            var result   = compilable.Compile();
            var messages = result.Messages
                .Cast<CompilerResultMessage>()
                .Select(m => $"[{m.State}] {m.Description}")
                .ToList();

            _log.LogInformation("Compiled {Block}: {State} ({W}W {E}E)",
                blockName, result.State, result.WarningCount, result.ErrorCount);

            return string.Join("\n",
                $"Result:   {result.State}",
                $"Errors:   {result.ErrorCount}",
                $"Warnings: {result.WarningCount}",
                "",
                string.Join("\n", messages));
        });
    }

    // ── Export / Import ───────────────────────────────────────────────────────

    public async Task<string> ExportBlockAsync(
        string deviceName, string blockName, string? exportPath = null)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            Directory.CreateDirectory(_opts.ExportDirectory);
            var path = exportPath ?? Path.Combine(
                _opts.ExportDirectory, $"{deviceName}_{blockName}.xml");

            block.Export(new FileInfo(path), ExportOptions.WithDefaults);
            _log.LogInformation("Exported {Block} → {Path}", blockName, path);
            return path;
        });
    }

    public async Task WriteBlockXmlAsync(string deviceName, string blockName, string xmlContent)
    {
        _tia.EnsureConnected();
        if (string.IsNullOrWhiteSpace(xmlContent))
            throw new ArgumentException("XML content is empty.");

        await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);
            Directory.CreateDirectory(_opts.ExportDirectory);
            var importFile = Path.Combine(_opts.ExportDirectory,
                $"{deviceName}_{blockName}_xml_edit.xml");
            File.WriteAllText(importFile, xmlContent);
            plc.BlockGroup.Blocks.Import(new FileInfo(importFile), ImportOptions.Override);
            _log.LogInformation("Imported raw XML for {Block} on {Device}.", blockName, deviceName);
        });
    }

    public async Task ImportBlockAsync(string deviceName, string xmlFilePath)
    {
        _tia.EnsureConnected();
        if (!File.Exists(xmlFilePath))
            throw new FileNotFoundException($"Import file not found: {xmlFilePath}");

        await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);
            plc.BlockGroup.Blocks.Import(new FileInfo(xmlFilePath), ImportOptions.Override);
            _log.LogInformation("Imported block from {Path} into {Device}.", xmlFilePath, deviceName);
        });
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    internal PlcSoftware GetPlcSoftware(string deviceName)
    {
        foreach (Device device in _tia.Project!.Devices)
        {
            if (!device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (DeviceItem di in device.DeviceItems)
            {
                var sw = di.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                if (sw is not null) return sw;
            }
        }
        throw new KeyNotFoundException(
            $"No PLC software found for device '{deviceName}'. " +
            "Ensure the device contains a CPU with software.");
    }

    private static PlcBlock FindBlock(PlcBlockGroup group, string name)
    {
        var block = group.Blocks
            .Cast<PlcBlock>()
            .FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (block is not null) return block;

        foreach (PlcBlockUserGroup sub in group.Groups)
        {
            block = FindBlockInUserGroup(sub, name);
            if (block is not null) return block;
        }

        throw new KeyNotFoundException($"Block '{name}' not found.");
    }

    private static PlcBlock? FindBlockInUserGroup(PlcBlockUserGroup group, string name)
    {
        var block = group.Blocks
            .Cast<PlcBlock>()
            .FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (block is not null) return block;

        foreach (PlcBlockUserGroup sub in group.Groups)
        {
            block = FindBlockInUserGroup(sub, name);
            if (block is not null) return block;
        }
        return null;
    }

    private static void CollectBlocks(PlcBlockGroup group, List<Models.BlockInfo> list)
    {
        foreach (PlcBlock block in group.Blocks.Cast<PlcBlock>())
            list.Add(BlockToInfo(block));

        foreach (PlcBlockUserGroup sub in group.Groups)
            CollectBlocksFromUserGroup(sub, list);
    }

    private static void CollectBlocksFromUserGroup(PlcBlockUserGroup group, List<Models.BlockInfo> list)
    {
        foreach (PlcBlock block in group.Blocks.Cast<PlcBlock>())
            list.Add(BlockToInfo(block));

        foreach (PlcBlockUserGroup sub in group.Groups)
            CollectBlocksFromUserGroup(sub, list);
    }

    private static Models.BlockInfo BlockToInfo(PlcBlock block) => new()
    {
        Name      = block.Name,
        Type      = MapBlockType(block),
        Number    = block.Number,
        Language  = MapLanguage(block),
        Author    = block.HeaderAuthor ?? "",
        Comment   = BlockComment(block),
        Modified  = block.ModifiedDate.ToString("O"),
        IsKnowHow = block.IsKnowHowProtected,
        SizeBytes = 0,
    };

    private static string BlockComment(PlcBlock block)
    {
        try { return block.GetAttribute("Comment") as string ?? ""; }
        catch { return ""; }
    }

    private static ModelBlockType MapBlockType(PlcBlock block) => block switch
    {
        OB       => ModelBlockType.OB,
        FB       => ModelBlockType.FB,
        FC       => ModelBlockType.FC,
        GlobalDB => ModelBlockType.GlobalDB,
        _        => ModelBlockType.DB,
    };

    private static ModelLanguage MapLanguage(PlcBlock block)
    {
        return block.ProgrammingLanguage switch
        {
            Siemens.Engineering.SW.Blocks.ProgrammingLanguage.SCL   => ModelLanguage.SCL,
            Siemens.Engineering.SW.Blocks.ProgrammingLanguage.LAD   => ModelLanguage.LAD,
            Siemens.Engineering.SW.Blocks.ProgrammingLanguage.FBD   => ModelLanguage.FBD,
            Siemens.Engineering.SW.Blocks.ProgrammingLanguage.STL   => ModelLanguage.STL,
            Siemens.Engineering.SW.Blocks.ProgrammingLanguage.GRAPH => ModelLanguage.GRAPH,
            _                                                        => ModelLanguage.SCL,
        };
    }
}
