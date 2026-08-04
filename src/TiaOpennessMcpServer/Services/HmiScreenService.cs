using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Controls;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Parts;
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

    // ── List all screens ──────────────────────────────────────────────────────

    public async Task<object> ListScreensAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi    = GetHmiSoftware(deviceName);
            var result = new List<object>();

            foreach (HmiScreen screen in hmi.Screens)
                result.Add(new { name = screen.Name, width = screen.Width, height = screen.Height });

            _log.LogInformation("Listed {Count} HMI screens for '{Device}'.", result.Count, deviceName);
            return (object)result;
        });
    }

    // ── List all tag dynamizations in a screen ────────────────────────────────
    // Returns: screen item name, property bound, tag name — read only, no changes.

    public async Task<object> GetScreenTagRefsAsync(string deviceName, string screenName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi    = GetHmiSoftware(deviceName);
            var screen = hmi.Screens.Find(screenName)
                ?? throw new KeyNotFoundException($"HMI screen '{screenName}' not found on device '{deviceName}'.");

            var refs = new List<object>();
            CollectTagRefs(screen, refs);

            _log.LogInformation("Found {Count} tag refs in screen '{Screen}'.", refs.Count, screenName);
            return (object)refs;
        });
    }

    // ── Update faceplate interface parameter values ───────────────────────────
    // tagUpdates: list of { containerName, parameterName, newValue }

    public async Task<object> UpdateFaceplateTagsAsync(string deviceName, string screenName,
        List<FaceplateTagUpdate> updates)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi    = GetHmiSoftware(deviceName);
            var screen = hmi.Screens.Find(screenName)
                ?? throw new KeyNotFoundException($"HMI screen '{screenName}' not found.");

            var results = new List<object>();
            foreach (HmiScreenItemBase item in screen.ScreenItems)
            {
                if (item is not HmiFaceplateContainer fp) continue;

                foreach (var upd in updates)
                {
                    if (!fp.Name.Equals(upd.ContainerName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (HmiFaceplateInterface iface in fp.Interface)
                    {
                        if (!iface.PropertyName.Equals(upd.ParameterName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var oldValue = iface.Value?.ToString() ?? "";
                        iface.Value = upd.NewValue;
                        results.Add(new
                        {
                            screen        = screenName,
                            container     = fp.Name,
                            parameter     = iface.PropertyName,
                            oldValue,
                            newValue      = upd.NewValue,
                            status        = "updated",
                        });
                    }
                }
            }
            _log.LogInformation("Updated {Count} faceplate parameters on screen '{Screen}'.",
                results.Count, screenName);
            return (object)results;
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void CollectTagRefs(HmiScreen screen, List<object> refs)
    {
        foreach (HmiScreenItemBase item in screen.ScreenItems)
        {
            // Regular tag dynamizations on any screen item
            foreach (DynamizationBase dyn in item.Dynamizations)
            {
                if (dyn is TagDynamization td)
                {
                    refs.Add(new
                    {
                        kind         = "TagDynamization",
                        screenItem   = item.Name,
                        propertyName = td.PropertyName,
                        tag          = td.Tag,
                        readOnly     = td.ReadOnly,
                    });
                }
            }

            // Faceplate containers — check their interface parameter bindings
            if (item is HmiFaceplateContainer fp)
            {
                foreach (HmiFaceplateInterface iface in fp.Interface)
                {
                    refs.Add(new
                    {
                        kind         = "FaceplateInterface",
                        screenItem   = fp.Name,
                        propertyName = iface.PropertyName,
                        tag          = iface.Value?.ToString() ?? "",
                        readOnly     = false,
                    });
                }
            }
        }
    }

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
        throw new KeyNotFoundException($"No WinCC Unified HMI software found for device '{deviceName}'.");
    }
}
