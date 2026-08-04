using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiTags;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Services;

public sealed class HmiTagService
{
    private readonly TiaPortalService _tia;
    private readonly StaTaskScheduler _sta;
    private readonly ILogger<HmiTagService> _log;

    public HmiTagService(TiaPortalService tia, StaTaskScheduler sta, ILogger<HmiTagService> log)
    {
        _tia = tia;
        _sta = sta;
        _log = log;
    }

    // ── Tag table list ─────────────────────────────────────────────────────────

    public async Task<object> ListTagTablesAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmiSoftware(deviceName);
            var result = new List<object>();
            CollectTables(hmi.TagTables, result);
            return (object)result;
        });
    }

    // ── Tags in a table ────────────────────────────────────────────────────────

    public async Task<object> GetTagsAsync(string deviceName, string tableName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi   = GetHmiSoftware(deviceName);
            var table = FindTable(hmi.TagTables, tableName)
                ?? throw new KeyNotFoundException($"HMI tag table '{tableName}' not found.");

            var tags = table.Tags.Cast<HmiTag>().Select(t => ReadTag(t)).ToList();
            _log.LogDebug("Read {Count} tags from HMI table '{Table}'.", tags.Count, tableName);
            return (object)tags;
        });
    }

    // ── Create tags in a table ────────────────────────────────────────────────

    public async Task<object> CreateTagsAsync(string deviceName, string tableName, List<HmiTagCreateRequest> tags)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmiSoftware(deviceName);

            // Find() returns null if not found; avoids int-only indexer
            var table = hmi.TagTables.Find(tableName)
                ?? throw new KeyNotFoundException($"HMI tag table '{tableName}' not found.");

            var results = new List<object>();
            foreach (var req in tags)
            {
                // Update existing tag if plcTag is empty, otherwise skip
                var existing = table.Tags.Cast<HmiTag>()
                    .FirstOrDefault(t => t.Name.Equals(req.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    var existingPlc = SafeAttr(existing, "PlcTag");
                    if (!string.IsNullOrEmpty(existingPlc))
                    {
                        results.Add(new { name = req.Name, status = "skipped", reason = "already exists" });
                        continue;
                    }
                    // Tag exists but has no PLC link — patch it
                    if (!string.IsNullOrEmpty(req.PlcTag))
                        existing.SetAttribute("PlcTag", req.PlcTag);
                    results.Add(new { name = req.Name, status = "updated", plcTag = req.PlcTag });
                    continue;
                }

                var tag = table.Tags.Create(req.Name);
                tag.SetAttribute("DataType", req.DataType);
                if (!string.IsNullOrEmpty(req.PlcTag))
                    tag.SetAttribute("PlcTag", req.PlcTag);

                _log.LogInformation("Created HMI tag '{Name}' → '{PlcTag}' in table '{Table}'.",
                    req.Name, req.PlcTag, tableName);
                results.Add(new { name = req.Name, status = "created", plcTag = req.PlcTag });
            }
            return (object)results;
        });
    }

    // ── All tags (flat) ────────────────────────────────────────────────────────

    public async Task<object> GetAllTagsAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi    = GetHmiSoftware(deviceName);
            var result = new List<object>();
            CollectAllTags(hmi.TagTables, result);
            return (object)result;
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private HmiSoftware GetHmiSoftware(string deviceName)
    {
        foreach (Device device in _tia.Project!.Devices)
        {
            if (!device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (DeviceItem di in device.DeviceItems)
            {
                var sw = di.GetService<SoftwareContainer>()?.Software as HmiSoftware;
                if (sw is not null) return sw;
            }
        }
        throw new KeyNotFoundException(
            $"No WinCC Unified HMI software found for device '{deviceName}'.");
    }

    private static void CollectTables(HmiTagTableComposition tables, List<object> result)
    {
        foreach (HmiTagTable t in tables)
            result.Add(new { name = t.Name, tagCount = t.Tags.Count });
    }

    private static HmiTagTable? FindTable(HmiTagTableComposition tables, string name)
    {
        foreach (HmiTagTable t in tables)
            if (t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }

    private static void CollectAllTags(HmiTagTableComposition tables, List<object> result)
    {
        foreach (HmiTagTable table in tables)
            foreach (HmiTag tag in table.Tags.Cast<HmiTag>())
                result.Add(ReadTag(tag, table.Name));
    }

    private static object ReadTag(HmiTag tag, string? tableName = null)
    {
        string plcTag     = SafeAttr(tag, "PlcTag");
        string connection = SafeAttr(tag, "ConnectionName");
        string dataType   = SafeAttr(tag, "DataType");
        string comment    = SafeAttr(tag, "Comment");

        var obj = new {
            name       = tag.Name,
            table      = tableName,
            dataType,
            plcTag,
            connection,
            comment,
        };
        return obj;
    }

    private static string SafeAttr(HmiTag tag, string attr)
    {
        try   { return tag.GetAttribute(attr)?.ToString() ?? ""; }
        catch { return ""; }
    }
}
