using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Services;

/// <summary>Reads and writes PLC tag tables.</summary>
public sealed class TagService
{
    private readonly TiaPortalService _tia;
    private readonly StaTaskScheduler _sta;
    private readonly SoftwareService  _sw;
    private readonly TiaOpennessOptions _opts;
    private readonly ILogger<TagService> _log;

    public TagService(
        TiaPortalService tia,
        StaTaskScheduler sta,
        SoftwareService sw,
        IOptions<TiaOpennessOptions> opts,
        ILogger<TagService> log)
    {
        _tia  = tia;
        _sta  = sta;
        _sw   = sw;
        _opts = opts.Value;
        _log  = log;
    }

    // ── Tag table enumeration ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<TagTableInfo>> GetTagTablesAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc    = _sw.GetPlcSoftware(deviceName);
            var result = new List<TagTableInfo>();
            CollectTagTables(plc.TagTableGroup, result);
            return (IReadOnlyList<TagTableInfo>)result;
        });
    }

    public async Task<IReadOnlyList<TagInfo>> GetTagsAsync(string deviceName, string tableName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = _sw.GetPlcSoftware(deviceName);
            var table = FindTagTable(plc.TagTableGroup, tableName);
            return (IReadOnlyList<TagInfo>)table.Tags
                .Cast<PlcTag>()
                .Select(t => new TagInfo
                {
                    Name       = t.Name,
                    DataType   = t.DataTypeName,
                    Address    = t.LogicalAddress,
                    Accessible = t.ExternalAccessible,
                    Writable   = t.ExternalWritable,
                    Comment    = ReadComment(t),
                })
                .ToList();
        });
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<TagTableInfo> CreateTagTableAsync(string deviceName, string tableName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc = _sw.GetPlcSoftware(deviceName);
            var table = plc.TagTableGroup.TagTables.Create(tableName);
            _log.LogInformation("Created tag table '{Table}' on {Device}.", tableName, deviceName);
            return new TagTableInfo { Name = table.Name, TagCount = 0, Comment = "" };
        });
    }

    public async Task<TagInfo> CreateTagAsync(
        string deviceName, string tableName, TagDefinition def)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = _sw.GetPlcSoftware(deviceName);
            var table = FindTagTable(plc.TagTableGroup, tableName);

            var tag = table.Tags.Create(def.Name, def.DataType, def.Address);
            tag.ExternalAccessible = def.Accessible;
            tag.ExternalWritable   = def.Writable;
            SetComment(tag, def.Comment);

            _log.LogInformation("Created tag '{Tag}' ({Type} @ {Addr}) in '{Table}'.",
                def.Name, def.DataType, def.Address, tableName);

            return new TagInfo
            {
                Name       = tag.Name,
                DataType   = tag.DataTypeName,
                Address    = tag.LogicalAddress,
                Accessible = tag.ExternalAccessible,
                Writable   = tag.ExternalWritable,
                Comment    = def.Comment,
            };
        });
    }

    // ── Import / Export ───────────────────────────────────────────────────────

    public async Task<string> ExportTagTableAsync(
        string deviceName, string tableName, string? exportPath = null)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = _sw.GetPlcSoftware(deviceName);
            var table = FindTagTable(plc.TagTableGroup, tableName);
            var path  = exportPath ?? Path.Combine(
                _opts.ExportDirectory, $"{deviceName}_{tableName}_tags.xlsx");

            table.Export(new FileInfo(path), ExportOptions.None);
            _log.LogInformation("Exported tag table '{Table}' → {Path}", tableName, path);
            return path;
        });
    }

    public async Task ImportTagTableAsync(string deviceName, string filePath)
    {
        _tia.EnsureConnected();
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Import file not found: {filePath}");

        await _sta.RunAsync(() =>
        {
            var plc = _sw.GetPlcSoftware(deviceName);
            plc.TagTableGroup.TagTables.Import(new FileInfo(filePath), ImportOptions.Override);
            _log.LogInformation("Imported tag table from {Path}.", filePath);
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PlcTagTable FindTagTable(PlcTagTableGroup group, string name)
    {
        var table = group.TagTables
            .Cast<PlcTagTable>()
            .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (table is not null) return table;

        foreach (PlcTagTableUserGroup sub in group.Groups)
        {
            table = FindTagTableInGroup(sub, name);
            if (table is not null) return table;
        }

        throw new KeyNotFoundException($"Tag table '{name}' not found.");
    }

    private static PlcTagTable? FindTagTableInGroup(PlcTagTableUserGroup group, string name)
    {
        var table = group.TagTables
            .Cast<PlcTagTable>()
            .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (table is not null) return table;

        foreach (PlcTagTableUserGroup sub in group.Groups)
        {
            table = FindTagTableInGroup(sub, name);
            if (table is not null) return table;
        }
        return null;
    }

    private static string ReadComment(PlcTag tag)
    {
        try
        {
            return tag.Comment.Items
                .Cast<MultilingualTextItem>()
                .FirstOrDefault()?.Text ?? "";
        }
        catch { return ""; }
    }

    private static void SetComment(PlcTag tag, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var item = tag.Comment.Items
                .Cast<MultilingualTextItem>()
                .FirstOrDefault();
            if (item is not null)
                item.Text = text;
        }
        catch { /* best effort */ }
    }

    private static void CollectTagTables(PlcTagTableGroup group, List<TagTableInfo> list)
    {
        foreach (PlcTagTable t in group.TagTables.Cast<PlcTagTable>())
        {
            list.Add(new TagTableInfo
            {
                Name     = t.Name,
                TagCount = t.Tags.Count,
                Comment  = "",
            });
        }
        foreach (PlcTagTableUserGroup sub in group.Groups)
            CollectTagTablesFromGroup(sub, list);
    }

    private static void CollectTagTablesFromGroup(PlcTagTableUserGroup group, List<TagTableInfo> list)
    {
        foreach (PlcTagTable t in group.TagTables.Cast<PlcTagTable>())
        {
            list.Add(new TagTableInfo
            {
                Name     = t.Name,
                TagCount = t.Tags.Count,
                Comment  = "",
            });
        }
        foreach (PlcTagTableUserGroup sub in group.Groups)
            CollectTagTablesFromGroup(sub, list);
    }
}
