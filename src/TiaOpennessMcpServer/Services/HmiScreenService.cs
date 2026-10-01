using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Controls;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Parts;
using Siemens.Engineering.HmiUnified.UI.ScreenGroup;
using Siemens.Engineering.HmiUnified.UI.Screens;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Services;

public sealed class HmiScreenService
{
    private readonly TiaPortalService _tia;
    private readonly StaTaskScheduler _sta;
    private readonly ILogger<HmiScreenService> _log;

    public HmiScreenService(TiaPortalService tia, StaTaskScheduler sta, ILogger<HmiScreenService> log)
    { _tia = tia; _sta = sta; _log = log; }

    // ── List ALL screens (including those inside screen groups) ───────────────

    public async Task<object> ListScreensAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi    = GetHmiSoftware(deviceName);
            var result = new List<object>();
            CollectAllScreens(hmi, result);
            _log.LogInformation("Listed {Count} HMI screens for '{Device}'.", result.Count, deviceName);
            return (object)result;
        });
    }

    // ── Scan all screens for tag refs matching a pattern (read only) ──────────

    public async Task<object> ScanAllTagRefsAsync(string deviceName, string? filterPattern = null)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi     = GetHmiSoftware(deviceName);
            var screens = new List<HmiScreen>();
            GatherScreens(hmi, screens);

            var result = new List<object>();
            foreach (var screen in screens)
            {
                var refs = new List<ScreenTagRef>();
                CollectTagRefs(screen, refs);
                foreach (var r in refs)
                {
                    if (filterPattern is null || r.Tag.IndexOf(filterPattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        result.Add(new {
                            screen       = screen.Name,
                            kind         = r.Kind,
                            screenItem   = r.ScreenItem,
                            propertyName = r.PropertyName,
                            tag          = r.Tag,
                        });
                }
            }
            return (object)result;
        });
    }

    // ── Tag refs in a single screen ───────────────────────────────────────────

    public async Task<object> GetScreenTagRefsAsync(string deviceName, string screenName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi    = GetHmiSoftware(deviceName);
            var screen = FindScreen(hmi, screenName)
                ?? throw new KeyNotFoundException($"HMI screen '{screenName}' not found.");
            var refs = new List<ScreenTagRef>();
            CollectTagRefs(screen, refs);
            return (object)refs;
        });
    }

    // ── Replace-by-regex across ALL screens ───────────────────────────────────
    // Replaces any FaceplateInterface value matching `pattern` with `replacement`.
    // Supports numbered capture groups: pattern="IO\.Outputs\.Digitals\.A\[(\d+)\]\.State"
    //                                   replacement="DQ_A_$1"

    public async Task<object> ReplaceTagPatternAsync(string deviceName, string pattern, string replacement)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi     = GetHmiSoftware(deviceName);
            var screens = new List<HmiScreen>();
            GatherScreens(hmi, screens);

            var rx      = new Regex(pattern, RegexOptions.IgnoreCase);
            var changes = new List<object>();

            foreach (var screen in screens)
            {
                foreach (HmiScreenItemBase item in screen.ScreenItems)
                {
                    // TagDynamization bindings (BackColor, ForeColor, Visible, Left, etc.)
                    foreach (DynamizationBase dyn in item.Dynamizations)
                    {
                        if (dyn is not TagDynamization td) continue;
                        var current = td.Tag ?? "";
                        if (!rx.IsMatch(current)) continue;

                        var updated = rx.Replace(current, replacement);
                        td.Tag = updated;
                        changes.Add(new {
                            screen       = screen.Name,
                            kind         = "TagDynamization",
                            screenItem   = item.Name,
                            parameter    = td.PropertyName,
                            oldValue     = current,
                            newValue     = updated,
                        });
                        _log.LogInformation("Replaced '{Old}' → '{New}' on {Screen}/{Item}.{Prop}",
                            current, updated, screen.Name, item.Name, td.PropertyName);
                    }

                    // FaceplateInterface parameters
                    if (item is not HmiFaceplateContainer fp) continue;
                    foreach (HmiFaceplateInterface iface in fp.Interface)
                    {
                        var current = iface.Value?.ToString() ?? "";
                        if (!rx.IsMatch(current)) continue;

                        var updated = rx.Replace(current, replacement);
                        iface.Value = updated;
                        changes.Add(new {
                            screen       = screen.Name,
                            kind         = "FaceplateInterface",
                            screenItem   = fp.Name,
                            parameter    = iface.PropertyName,
                            oldValue     = current,
                            newValue     = updated,
                        });
                        _log.LogInformation("Replaced '{Old}' → '{New}' on {Screen}/{Container}.{Param}",
                            current, updated, screen.Name, fp.Name, iface.PropertyName);
                    }
                }
            }

            return (object)changes;
        });
    }

    // ── Update faceplate interface parameters by container name ───────────────

    public async Task<object> UpdateFaceplateTagsAsync(string deviceName, string screenName,
        List<FaceplateTagUpdate> updates)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi    = GetHmiSoftware(deviceName);
            var screen = FindScreen(hmi, screenName)
                ?? throw new KeyNotFoundException($"HMI screen '{screenName}' not found.");

            var results = new List<object>();
            foreach (HmiScreenItemBase item in screen.ScreenItems)
            {
                if (item is not HmiFaceplateContainer fp) continue;
                foreach (var upd in updates)
                {
                    if (!fp.Name.Equals(upd.ContainerName, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (HmiFaceplateInterface iface in fp.Interface)
                    {
                        if (!iface.PropertyName.Equals(upd.ParameterName, StringComparison.OrdinalIgnoreCase)) continue;
                        var old = iface.Value?.ToString() ?? "";
                        iface.Value = upd.NewValue;
                        results.Add(new { screen = screenName, container = fp.Name,
                            parameter = iface.PropertyName, oldValue = old, newValue = upd.NewValue, status = "updated" });
                    }
                }
            }
            return (object)results;
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Collect screens as summary objects (for listing)
    private static void CollectAllScreens(HmiSoftware hmi, List<object> result)
    {
        foreach (HmiScreen s in hmi.Screens)
            result.Add(new { name = s.Name, group = (string?)null, width = s.Width, height = s.Height });

        foreach (HmiScreenGroup grp in hmi.ScreenGroups)
            CollectGroupScreens(grp, result);
    }

    private static void CollectGroupScreens(HmiScreenGroup grp, List<object> result)
    {
        foreach (HmiScreen s in grp.Screens)
            result.Add(new { name = s.Name, group = grp.Name, width = s.Width, height = s.Height });
    }

    // Gather actual HmiScreen objects recursively
    private static void GatherScreens(HmiSoftware hmi, List<HmiScreen> screens)
    {
        foreach (HmiScreen s in hmi.Screens)
            screens.Add(s);

        foreach (HmiScreenGroup grp in hmi.ScreenGroups)
            foreach (HmiScreen s in grp.Screens)
                screens.Add(s);
    }

    private static HmiScreen? FindScreen(HmiSoftware hmi, string name)
    {
        foreach (HmiScreen s in hmi.Screens)
            if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return s;

        foreach (HmiScreenGroup grp in hmi.ScreenGroups)
            foreach (HmiScreen s in grp.Screens)
                if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return s;

        return null;
    }

    private static void CollectTagRefs(HmiScreen screen, List<ScreenTagRef> refs)
    {
        foreach (HmiScreenItemBase item in screen.ScreenItems)
        {
            foreach (DynamizationBase dyn in item.Dynamizations)
            {
                if (dyn is TagDynamization td)
                    refs.Add(new ScreenTagRef("TagDynamization", item.Name, td.PropertyName, td.Tag));
            }

            if (item is HmiFaceplateContainer fp)
                foreach (HmiFaceplateInterface iface in fp.Interface)
                    refs.Add(new ScreenTagRef("FaceplateInterface", fp.Name,
                        iface.PropertyName, iface.Value?.ToString() ?? ""));
        }
    }

    private sealed record ScreenTagRef(string Kind, string ScreenItem, string PropertyName, string Tag);

    private HmiSoftware GetHmiSoftware(string deviceName)
    {
        foreach (Device device in _tia.Project!.Devices)
        {
            if (!device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (DeviceItem di in device.DeviceItems)
            {
                var sw = di.GetService<SoftwareContainer>()?.Software as HmiSoftware;
                if (sw is not null) return sw;
            }
        }
        throw new KeyNotFoundException($"No WinCC Unified HMI software found for device '{deviceName}'.");
    }
}
