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
                    // Tag exists but has no PLC link: patch DataType and, only when bindPlc, the link
                    if (!string.IsNullOrEmpty(req.DataType))
                        existing.SetAttribute("DataType", req.DataType);
                    if (req.BindPlc && !string.IsNullOrEmpty(req.PlcTag))
                    {
                        var err = TryBindPlcTag(existing, req.Connection, req.PlcTag);
                        results.Add(err is null
                            ? new { name = req.Name, status = "updated", plcTag = req.PlcTag, error = (string?)null }
                            : new { name = req.Name, status = "updated_unlinked", plcTag = "", error = (string?)err });
                        continue;
                    }
                    results.Add(new { name = req.Name, status = "updated", plcTag = "", error = (string?)null });
                    continue;
                }

                var tag = table.Tags.Create(req.Name);
                tag.SetAttribute("DataType", req.DataType);

                // The PLC link is opt-in (bindPlc). Without it the tag stays Internal, as before.
                if (req.BindPlc && !string.IsNullOrEmpty(req.PlcTag))
                {
                    var err = TryBindPlcTag(tag, req.Connection, req.PlcTag);
                    _log.LogInformation("Created HMI tag '{Name}' in '{Table}', bind to '{PlcTag}': {Result}.",
                        req.Name, tableName, req.PlcTag, err ?? "linked");
                    results.Add(err is null
                        ? new { name = req.Name, status = "created", plcTag = req.PlcTag, error = (string?)null }
                        : new { name = req.Name, status = "created_unlinked", plcTag = "", error = (string?)err });
                    continue;
                }

                _log.LogInformation("Created HMI tag '{Name}' (internal) in table '{Table}'.", req.Name, tableName);
                results.Add(new { name = req.Name, status = "created", plcTag = "", error = (string?)null });
            }
            return (object)results;
        });
    }

    // ── Delete tags by name ────────────────────────────────────────────────────

    public async Task<object> DeleteTagsAsync(string deviceName, string tableName, List<string> names)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmiSoftware(deviceName);
            var table = hmi.TagTables.Find(tableName)
                ?? throw new KeyNotFoundException($"HMI tag table '{tableName}' not found.");
            var results = new List<object>();
            foreach (var n in names)
            {
                var tag = table.Tags.Cast<HmiTag>()
                    .FirstOrDefault(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (tag is null) { results.Add(new { name = n, status = "not_found" }); continue; }
                tag.Delete();
                results.Add(new { name = n, status = "deleted" });
            }
            return (object)results;
        });
    }


    // ── Re-point existing tags at other PLC tags ──────────────────────────────

    /// <summary>
    /// Re-links EXISTING HMI tags to different PLC tags (create_hmi_tags skips a tag that already has a PLC link).
    /// Typical use: the PLC tags were renamed or replaced, so the HMI tags show the pink "tag not found" state.
    /// Sets Connection first (the live-verified order), then PlcTag. If the new link is refused the previous
    /// Connection and PlcTag are put back and the tag is reported "failed" with the TIA error. tableName
    /// null/empty = search every HMI tag table. dryRun = only report what would change.
    /// </summary>
    public async Task<object> RelinkTagsAsync(string deviceName, string? tableName,
                                              List<HmiTagRelinkRequest> tags, bool dryRun = false)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmiSoftware(deviceName);
            var tables = new List<HmiTagTable>();
            if (string.IsNullOrWhiteSpace(tableName))
                foreach (HmiTagTable t in hmi.TagTables) tables.Add(t);
            else
                tables.Add(FindTable(hmi.TagTables, tableName!)
                    ?? throw new KeyNotFoundException($"HMI tag table '{tableName}' not found."));

            var results = new List<object>();
            foreach (var req in tags)
            {
                HmiTag? tag = null; string? foundIn = null;
                foreach (var t in tables)
                {
                    tag = t.Tags.Cast<HmiTag>()
                        .FirstOrDefault(x => x.Name.Equals(req.Name, StringComparison.OrdinalIgnoreCase));
                    if (tag is not null) { foundIn = t.Name; break; }
                }
                if (tag is null)
                {
                    results.Add(new { name = req.Name, status = "not_found", table = (string?)null,
                                      previousPlcTag = "", plcTag = req.PlcTag, connection = "", error = (string?)null });
                    continue;
                }

                string prevConn = SafeAttr(tag, "Connection");
                string prevPlc  = SafeAttr(tag, "PlcTag");
                string conn = !string.IsNullOrEmpty(req.Connection) ? req.Connection
                            : (!string.IsNullOrEmpty(prevConn) && prevConn != "<Internal tag>") ? prevConn
                            : "HMI_Connection_6";

                if (dryRun)
                {
                    results.Add(new { name = tag.Name, status = "would_relink", table = (string?)foundIn,
                                      previousPlcTag = prevPlc, plcTag = req.PlcTag, connection = conn, error = (string?)null });
                    continue;
                }

                var err = TryBindPlcTag(tag, conn, req.PlcTag);
                if (err is not null)
                {
                    // Put the previous link back (best effort) so a refused relink never leaves the tag Internal.
                    try { if (!string.IsNullOrEmpty(prevConn)) tag.SetAttribute("Connection", prevConn); } catch { }
                    try { if (!string.IsNullOrEmpty(prevPlc))  tag.SetAttribute("PlcTag", prevPlc); }     catch { }
                }
                _log.LogInformation("Relink HMI tag '{Name}' ({Table}): '{Prev}' -> '{New}': {Result}.",
                    tag.Name, foundIn, prevPlc, req.PlcTag, err ?? "linked");
                results.Add(new { name = tag.Name, status = err is null ? "relinked" : "failed", table = (string?)foundIn,
                                  previousPlcTag = prevPlc, plcTag = req.PlcTag, connection = conn, error = (string?)err });
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
        string connection = SafeAttr(tag, "Connection"); // "ConnectionName" does not exist on HmiTag
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

    /// <summary>
    /// Links an HMI tag to a PLC tag. Live-verified order: the Connection must be set BEFORE PlcTag, otherwise
    /// PlcTag throws "controller tag ... was not found" (a new tag starts as "&lt;Internal tag&gt;"). The tag's
    /// DataType then follows the PLC tag. TIA accepts any Connection name without checking it, so on failure the
    /// tag is put back to Internal. Returns null on success, else the error text.
    /// </summary>
    private static string? TryBindPlcTag(HmiTag tag, string connection, string plcTag)
    {
        try
        {
            tag.SetAttribute("Connection", connection);
            tag.SetAttribute("PlcTag", plcTag);
            return null;
        }
        catch (Exception ex)
        {
            try { tag.SetAttribute("Connection", "<Internal tag>"); } catch { /* best effort */ }
            return ex.Message.Replace("\r", "").Replace("\n", " ").Trim();
        }
    }

    private static string SafeAttr(HmiTag tag, string attr)
    {
        try   { return tag.GetAttribute(attr)?.ToString() ?? ""; }
        catch { return ""; }
    }
}
