using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using System.Text;
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

                // SCL bodies are a StructuredText token tree in the XML, not text, so read
                // the source back the same way it is written: via an external source.
                if (lang == ModelLanguage.SCL)
                {
                    var sclFile = Path.Combine(_opts.ExportDirectory, $"{deviceName}_{blockName}.scl");
                    plc.ExternalSourceGroup.GenerateSource(
                        new[] { block }, new FileInfo(sclFile));
                    sclSource = File.ReadAllText(sclFile);
                }

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
            var plc = GetPlcSoftware(deviceName);
            // Fail fast with a clear message if the block does not exist yet.
            var block = FindBlock(plc.BlockGroup, blockName);

            GenerateFromScl(plc, blockName, sclSource, block.Parent as PlcBlockUserGroup);
            _log.LogInformation("Written SCL to {Block} on {Device}.", blockName, deviceName);
        });
    }

    /// <summary>
    /// Imports SCL by writing it to a temporary external source and generating blocks from it.
    /// This is the only supported Openness route for SCL bodies: SimaticML carries them as a
    /// StructuredText token tree, never as text, so patching XML cannot work. Generation
    /// overwrites a block of the same name, which makes this serve both create and update.
    /// </summary>
    private void GenerateFromScl(
        PlcSoftware plc, string name, string sclSource, PlcBlockUserGroup? targetGroup)
    {
        Directory.CreateDirectory(_opts.ExportDirectory);
        var sclFile = Path.Combine(_opts.ExportDirectory, $"{name}.scl");
        File.WriteAllText(sclFile, sclSource, new UTF8Encoding(false));

        var sources  = plc.ExternalSourceGroup.ExternalSources;
        var sourceId = $"{name}.scl";

        // A leftover source from a previous run would block CreateFromFile.
        sources.Find(sourceId)?.Delete();

        var source = sources.CreateFromFile(sourceId, sclFile);
        try
        {
            // Generate into the block's own folder so an update does not relocate it.
            if (targetGroup is not null)
                source.GenerateBlocksFromSource(targetGroup, GenerateBlockOption.None);
            else
                source.GenerateBlocksFromSource(GenerateBlockOption.None);
        }
        finally
        {
            source.Delete();
        }
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Models.BlockInfo> CreateBlockAsync(string deviceName, BlockCreateRequest req)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);
            Directory.CreateDirectory(_opts.ExportDirectory);

            if (req.Type == Models.BlockType.GlobalDB)
            {
                // GlobalDB: SimaticML XML import works correctly for DBs.
                var xmlContent = XmlHelper.CreateGlobalDbXml(req.Name, req.Number, req.SourceCode);
                var importFile = Path.Combine(_opts.ExportDirectory, $"create_{deviceName}_{req.Name}.xml");
                File.WriteAllText(importFile, xmlContent);
                plc.BlockGroup.Blocks.Import(new FileInfo(importFile), ImportOptions.Override);
            }
            else
            {
                // FC/FB/OB carry code, so they go through the external-source route.
                // SourceCode must be a complete declaration, e.g.
                // FUNCTION "Name" : Void ... BEGIN ... END_FUNCTION
                GenerateFromScl(plc, req.Name, req.SourceCode, targetGroup: null);
            }

            var block = FindBlock(plc.BlockGroup, req.Name);
            _log.LogInformation("Created {Type} {Block} on {Device}.", req.Type, req.Name, deviceName);
            return BlockToInfo(block);
        });
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteBlockAsync(string deviceName, string blockName)
    {
        _tia.EnsureConnected();
        await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);
            block.Delete();
            _log.LogInformation("Deleted block {Block} on {Device}.", blockName, deviceName);
        });
    }

    // ── Watch tables ──────────────────────────────────────────────────────────
    // Creates (or replaces) a watch table in the PLC's "Watch and force tables" folder. Each entry is a
    // symbolic tag (e.g. "Robot_DB".IO.Commands.Pickup_Position) with an optional modify value, which is
    // only prepared: the user still goes online and clicks "Modify" in TIA, nothing is written to the PLC here.

    public sealed record WatchEntry(string Tag, string? ModifyValue);
    public sealed record WatchTableRequest(string Name, List<WatchEntry> Entries);

    public async Task<int> CreateWatchTableAsync(string deviceName, string tableName, IReadOnlyList<WatchEntry> entries)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var group = plc.WatchAndForceTableGroup;
            var old   = group.WatchTables.Find(tableName);
            if (old is not null) old.Delete();
            // Openness has no way to add a watch row in code (Entries.Create() only makes comment rows and the row
            // properties are read-only), so: create an empty table, export it, add the rows to the XML, re-import.
            var table = group.WatchTables.Create(tableName);
            Directory.CreateDirectory(_opts.ExportDirectory);
            var file = Path.Combine(_opts.ExportDirectory, $"watchtable_{deviceName}_{tableName}.xml");
            if (File.Exists(file)) File.Delete(file);
            table.Export(new FileInfo(file), ExportOptions.WithDefaults);

            var doc = System.Xml.Linq.XDocument.Load(file);
            var tableEl = doc.Descendants().First(x => x.Name.LocalName == "SW.WatchAndForceTables.PlcWatchTable");
            var list = tableEl.Elements().FirstOrDefault(x => x.Name.LocalName == "ObjectList");
            if (list is null) { list = new System.Xml.Linq.XElement("ObjectList"); tableEl.Add(list); }
            var id = doc.Descendants().Select(x => (string?)x.Attribute("ID"))
                        .Where(v => v is not null)
                        .Select(v => int.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out var n) ? n : 0)
                        .DefaultIfEmpty(0).Max() + 1;
            foreach (var e in entries)
            {
                var attrs = new System.Xml.Linq.XElement("AttributeList",
                    new System.Xml.Linq.XElement("Name", e.Tag));
                if (!string.IsNullOrWhiteSpace(e.ModifyValue))
                {
                    attrs.Add(new System.Xml.Linq.XElement("ModifyIntention", "true"));
                    attrs.Add(new System.Xml.Linq.XElement("ModifyValue", e.ModifyValue));
                }
                list.Add(new System.Xml.Linq.XElement("SW.WatchAndForceTables.PlcWatchTableEntry",
                    new System.Xml.Linq.XAttribute("ID", (id++).ToString("X")),
                    new System.Xml.Linq.XAttribute("CompositionName", "Entries"),
                    attrs));
            }
            doc.Save(file);
            table.Delete();
            ExceptionDetail.Wrap($"Watch table import of '{tableName}' failed (XML kept at {file})", () =>
                group.WatchTables.Import(new FileInfo(file), ImportOptions.Override));
            _log.LogInformation("Created watch table {Table} ({Count} entries) on {Device}.", tableName, entries.Count, deviceName);
            return entries.Count;
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
            // Real error text lives in nested child messages; the top-level ones are bare headers.
            var messages = new List<string>();
            void Flatten(IEnumerable<CompilerResultMessage> msgs, int depth)
            {
                foreach (var m in msgs)
                {
                    messages.Add($"{new string(' ', depth * 2)}[{m.State}] {m.Description}"
                        + (string.IsNullOrEmpty(m.Path) ? "" : $"  ({m.Path})"));
                    Flatten(m.Messages.Cast<CompilerResultMessage>(), depth + 1);
                }
            }
            Flatten(result.Messages.Cast<CompilerResultMessage>(), 0);

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

    /// <summary>
    /// Compiles the whole PLC software (equivalent to Compile &gt; Software in TIA Portal).
    /// Compiling individual blocks does NOT make PLC data types (UDTs) consistent, and an
    /// inconsistent object exports as an empty file — so this is the fix for empty exports.
    /// </summary>
    public async Task<string> CompileSoftwareAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);

            var compilable = plc.GetService<ICompilable>()
                ?? throw new NotSupportedException(
                    $"PLC software on '{deviceName}' does not support compilation.");

            var result = compilable.Compile();

            var messages = result.Messages
                .Cast<CompilerResultMessage>()
                .Select(m => $"[{m.State}] {m.Description}")
                .ToList();

            _log.LogInformation("Compiled software on {Device}: {State} ({W}W {E}E)",
                deviceName, result.State, result.WarningCount, result.ErrorCount);

            return string.Join("\n",
                $"Result:   {result.State}",
                $"Errors:   {result.ErrorCount}",
                $"Warnings: {result.WarningCount}",
                "",
                string.Join("\n", messages));
        });
    }

    // ── Create LAD block ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds LAD SimaticML from a structured description, validates it, imports it via the same
    /// path as <see cref="WriteBlockXmlAsync"/> (STA + Override), then compiles. A successful
    /// import is not success — the compile result is returned with it.
    /// </summary>
    public async Task<LadBlockResult> CreateLadBlockAsync(string deviceName, LadBlockRequest req)
    {
        // TIA rejects an auto-numbered ProgramCycle OB ("'Number' attribute is missing"), so pick a free
        // one (user OBs start at 123). dryRun never touches TIA, so it just shows 123.
        var number = req.Number;
        if (number is null && req.Type.Equals("OB", StringComparison.OrdinalIgnoreCase))
        {
            if (req.DryRun) number = 123;
            else
            {
                _tia.EnsureConnected();
                var used = (await ListBlocksAsync(deviceName)).Select(b => b.Number).ToHashSet();
                number = 123;
                while (used.Contains(number.Value)) number++;
            }
        }

        // Pure: throws LadValidationException / ArgumentException with a clear message, no TIA needed.
        var xml = XmlHelper.CreateLadBlockXml(
            req.Name, req.Type, number, req.Interface, req.Networks, req.Culture);

        if (req.DryRun)
            return new LadBlockResult { Name = req.Name, Imported = false, Xml = xml };

        _tia.EnsureConnected();

        if (!req.Overwrite)
        {
            var exists = await _sta.RunAsync(() =>
            {
                var plc = GetPlcSoftware(deviceName); // unknown device must surface, not read as "block absent"
                try { FindBlock(plc.BlockGroup, req.Name); return true; }
                catch (KeyNotFoundException) { return false; }
            });
            if (exists)
                throw new InvalidOperationException(
                    $"Block '{req.Name}' already exists on '{deviceName}'. Pass overwrite:true to replace it.");
        }

        await WriteBlockXmlAsync(deviceName, req.Name, xml);

        string? output = null;
        bool? ok = null;
        if (req.Compile)
        {
            output = await CompileBlockAsync(deviceName, req.Name);
            var m = System.Text.RegularExpressions.Regex.Match(output, @"Errors:\s+(\d+)");
            ok = m.Success && m.Groups[1].Value == "0";
        }

        return new LadBlockResult
        {
            Name = req.Name, Imported = true, CompileOutput = output, CompileSucceeded = ok,
            XmlPath = Path.Combine(_opts.ExportDirectory, $"{deviceName}_{req.Name}_xml_edit.xml"),
        };
    }

    // ── Create Instance DB ────────────────────────────────────────────────────

    public async Task<Models.BlockInfo> CreateInstanceDbAsync(
        string deviceName, string name, string instanceOfName, int? number)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc     = GetPlcSoftware(deviceName);
            var xml     = XmlHelper.CreateInstanceDbXml(name, instanceOfName, number);
            var path    = Path.Combine(_opts.ExportDirectory, $"create_{deviceName}_{name}_idb.xml");
            Directory.CreateDirectory(_opts.ExportDirectory);
            File.WriteAllText(path, xml, System.Text.Encoding.UTF8);
            plc.BlockGroup.Blocks.Import(new FileInfo(path), ImportOptions.Override);
            var block = FindBlock(plc.BlockGroup, name);
            _log.LogInformation("Created InstanceDB {Name} (of {FB}) on {Device}.",
                name, instanceOfName, deviceName);
            return BlockToInfo(block);
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

            var path = exportPath ?? Path.Combine(
                _opts.ExportDirectory, $"{deviceName}_{blockName}.xml");

            // Create the parent of the resolved path, not just ExportDirectory —
            // a caller-supplied path may point somewhere else entirely.
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            block.Export(new FileInfo(path), ExportOptions.WithDefaults);
            _log.LogInformation("Exported {Block} → {Path}", blockName, path);
            return path;
        });
    }

    public async Task PatchBlockTextsAsync(string deviceName, string blockName, Models.BlockTextsRequest req)
    {
        _tia.EnsureConnected();
        await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            if (!string.IsNullOrEmpty(req.BlockTitle))
                block.SetAttribute("Title", req.BlockTitle);
            if (!string.IsNullOrEmpty(req.BlockComment))
                block.SetAttribute("Comment", req.BlockComment);

            _log.LogInformation("Patched texts on block {Block}.", blockName);
        });
    }

    public async Task<object> GetBlockAttributeInfosAsync(string deviceName, string blockName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            var attrs = block.GetAttributeInfos()
                .Cast<Siemens.Engineering.EngineeringAttributeInfo>()
                .Select(i => new { name = i.Name, access = i.AccessMode.ToString() })
                .ToList();

            var iEng = (Siemens.Engineering.IEngineeringObject)block;
            var compositions = iEng.GetCompositionInfos()
                .Cast<Siemens.Engineering.EngineeringCompositionInfo>()
                .Select(i => i.Name)
                .ToList();

            return (object)new { blockType = block.GetType().Name, attributes = attrs, compositions };
        });
    }

    public async Task WriteBlockXmlAsync(string deviceName, string blockName, string xmlContent)
    {
        _tia.EnsureConnected();
        if (string.IsNullOrWhiteSpace(xmlContent))
            throw new ArgumentException("XML content is empty.");

        // The block name decides which folder (user group) the XML is imported into. Without it the
        // import lands in the root "Program blocks" folder, and TIA refuses it with only a generic
        // "Error when calling method 'Import'" whenever the block already lives in a sub-folder.
        // So when no name is given, take it from the block's <Name> in the XML.
        if (string.IsNullOrWhiteSpace(blockName))
        {
            var m = System.Text.RegularExpressions.Regex.Match(xmlContent,
                @"<SW\.Blocks\.\w+[^>]*>\s*<AttributeList>.*?<Name>([^<]+)</Name>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (m.Success) blockName = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
        }

        await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);
            Directory.CreateDirectory(_opts.ExportDirectory);
            var importFile = Path.Combine(_opts.ExportDirectory,
                $"{deviceName}_{blockName}_xml_edit.xml");
            File.WriteAllText(importFile, xmlContent);
            ExceptionDetail.Wrap($"Import of '{blockName}' failed", () =>
                FindBlockComposition(plc.BlockGroup, blockName)
                    .Import(new FileInfo(importFile), ImportOptions.Override));
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
            ExceptionDetail.Wrap($"Import of '{Path.GetFileName(xmlFilePath)}' failed", () =>
                plc.BlockGroup.Blocks.Import(new FileInfo(xmlFilePath), ImportOptions.Override));
            _log.LogInformation("Imported block from {Path} into {Device}.", xmlFilePath, deviceName);
        });
    }

    // ── PLC data types (UDTs) ─────────────────────────────────────────────────
    // PlcTypeComposition mirrors PlcBlockComposition: PlcType.Export(FileInfo, ExportOptions)
    // and composition.Import(FileInfo, ImportOptions). Types live in PlcTypeUserGroup folders
    // exactly as blocks do, so the same group-aware composition lookup is required.

    public async Task<IReadOnlyList<PlcTypeInfo>> ListTypesAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);
            var results = new List<PlcTypeInfo>();
            CollectTypes(plc.TypeGroup.Types, plc.TypeGroup.Groups, "", results);
            return (IReadOnlyList<PlcTypeInfo>)results;
        });
    }

    public async Task<PlcTypeContent> ReadTypeAsync(string deviceName, string typeName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc  = GetPlcSoftware(deviceName);
            var type = FindType(plc, typeName)
                       ?? throw new KeyNotFoundException($"PLC data type '{typeName}' not found.");

            var xmlContent = "";
            try
            {
                Directory.CreateDirectory(_opts.ExportDirectory);
                var safe = typeName.Replace('/', '_').Replace('\\', '_');
                var exportFile = Path.Combine(
                    _opts.ExportDirectory,
                    $"{deviceName}_type_{safe}_{DateTime.UtcNow:yyyyMMddHHmmss}.xml");

                type.Export(new FileInfo(exportFile), ExportOptions.WithDefaults);
                xmlContent = File.ReadAllText(exportFile);
                _log.LogDebug("Read type {Type} ({Bytes} bytes XML)", typeName, xmlContent.Length);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Export failed for type {Type}: {Msg}", typeName, ex.Message);
            }

            return new PlcTypeContent
            {
                Name          = type.Name,
                Namespace     = type.Namespace ?? "",
                IsConsistent  = type.IsConsistent,
                IsKnowHow     = type.IsKnowHowProtected,
                Modified      = type.ModifiedDate.ToString("O"),
                SizeBytes     = xmlContent.Length,
                XmlContent    = xmlContent,
            };
        });
    }

    public async Task WriteTypeXmlAsync(string deviceName, string typeName, string xmlContent)
    {
        _tia.EnsureConnected();
        if (string.IsNullOrWhiteSpace(xmlContent))
            throw new ArgumentException("XML content is empty.");

        await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);
            Directory.CreateDirectory(_opts.ExportDirectory);
            var safe = typeName.Replace('/', '_').Replace('\\', '_');
            var importFile = Path.Combine(_opts.ExportDirectory,
                $"{deviceName}_type_{safe}_xml_edit.xml");
            File.WriteAllText(importFile, xmlContent);

            FindTypeComposition(plc, typeName)
                .Import(new FileInfo(importFile), ImportOptions.Override);
            _log.LogInformation("Imported raw XML for type {Type} on {Device}.", typeName, deviceName);
        });
    }

    private static void CollectTypes(
        Siemens.Engineering.SW.Types.PlcTypeComposition types,
        Siemens.Engineering.SW.Types.PlcTypeUserGroupComposition groups,
        string path,
        List<PlcTypeInfo> list)
    {
        foreach (Siemens.Engineering.SW.Types.PlcType t in types)
        {
            list.Add(new PlcTypeInfo
            {
                Name         = t.Name,
                Namespace    = t.Namespace ?? "",
                Group        = path,
                IsConsistent = t.IsConsistent,
                IsKnowHow    = t.IsKnowHowProtected,
                Modified     = t.ModifiedDate.ToString("O"),
            });
        }
        foreach (Siemens.Engineering.SW.Types.PlcTypeUserGroup g in groups)
            CollectTypes(g.Types, g.Groups, path.Length == 0 ? g.Name : $"{path}/{g.Name}", list);
    }

    private static Siemens.Engineering.SW.Types.PlcType? FindType(PlcSoftware plc, string name)
    {
        var comp = FindTypeCompositionOrNull(plc.TypeGroup.Types, plc.TypeGroup.Groups, name);
        if (comp is null) return null;
        foreach (Siemens.Engineering.SW.Types.PlcType t in comp)
            if (t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    /// <summary>Composition that owns the type, falling back to the root for a genuine create.</summary>
    private static Siemens.Engineering.SW.Types.PlcTypeComposition FindTypeComposition(
        PlcSoftware plc, string name)
        => FindTypeCompositionOrNull(plc.TypeGroup.Types, plc.TypeGroup.Groups, name)
           ?? plc.TypeGroup.Types;

    private static Siemens.Engineering.SW.Types.PlcTypeComposition? FindTypeCompositionOrNull(
        Siemens.Engineering.SW.Types.PlcTypeComposition types,
        Siemens.Engineering.SW.Types.PlcTypeUserGroupComposition groups,
        string name)
    {
        foreach (Siemens.Engineering.SW.Types.PlcType t in types)
            if (t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return types;

        foreach (Siemens.Engineering.SW.Types.PlcTypeUserGroup g in groups)
        {
            var found = FindTypeCompositionOrNull(g.Types, g.Groups, name);
            if (found is not null) return found;
        }
        return null;
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

    /// <summary>
    /// Returns the composition that actually owns <paramref name="name"/> — the root block
    /// folder or whichever user group the block sits in. Importing with ImportOptions.Override
    /// only overwrites within the composition it is called on; calling it on the root while the
    /// block lives in a folder makes TIA fall through to Create and fail with
    /// "a program element with this fully qualified name already exists".
    /// Falls back to the root composition when the block does not exist yet (a genuine create).
    /// </summary>
    private static PlcBlockComposition FindBlockComposition(PlcBlockGroup root, string name)
    {
        if (root.Blocks.Cast<PlcBlock>()
                .Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return root.Blocks;

        foreach (PlcBlockUserGroup sub in root.Groups)
        {
            var found = FindCompositionInUserGroup(sub, name);
            if (found is not null) return found;
        }
        return root.Blocks;
    }

    private static PlcBlockComposition? FindCompositionInUserGroup(PlcBlockUserGroup group, string name)
    {
        if (group.Blocks.Cast<PlcBlock>()
                .Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return group.Blocks;

        foreach (PlcBlockUserGroup sub in group.Groups)
        {
            var found = FindCompositionInUserGroup(sub, name);
            if (found is not null) return found;
        }
        return null;
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
