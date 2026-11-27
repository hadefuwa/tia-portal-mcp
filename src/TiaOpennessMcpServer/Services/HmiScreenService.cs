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
using Siemens.Engineering.HmiUnified.HmiConnections;
using Siemens.Engineering.HmiUnified.UI.Enum;
using Siemens.Engineering.HmiUnified.UI.Shapes;
using Siemens.Engineering.HmiUnified.UI.Widgets;
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

    // ── HMI connections (read only) ───────────────────────────────────────────

    public async Task<object> ListConnectionsAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmiSoftware(deviceName);
            var list = new List<object>();
            foreach (var c in hmi.Connections)
                list.Add(new { name = c.Name, driver = c.CommunicationDriver, station = c.Station,
                               partner = c.Partner, node = c.Node, initialAddress = c.InitialAddress });
            return (object)list;
        });
    }

    // ── Items on one screen (read only) ───────────────────────────────────────

    public async Task<object> ListScreenItemsAsync(string deviceName, string screenName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmiSoftware(deviceName);
            var screen = FindScreen(hmi, screenName)
                ?? throw new KeyNotFoundException($"HMI screen '{screenName}' not found.");
            string? Attr(HmiScreenItemBase i, string n)
            { try { return i.GetAttribute(n)?.ToString(); } catch { return null; } }

            var list = new List<object>();
            foreach (HmiScreenItemBase i in screen.ScreenItems)
                list.Add(new {
                    name = i.Name, type = i.GetType().Name,
                    left = Attr(i, "Left"), top = Attr(i, "Top"),
                    width = Attr(i, "Width"), height = Attr(i, "Height"),
                    processValue = Attr(i, "ProcessValue"),
                });
            return (object)new { screen = screen.Name, width = screen.Width, height = screen.Height, items = list };
        });
    }

    // ── Layout check (read only) ──────────────────────────────────────────────

    /// <summary>Flags items that run off the screen and items that partially overlap (full containment, such as a
    /// button on a card, is treated as intentional).</summary>
    public async Task<object> CheckLayoutAsync(string deviceName, string screenName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmiSoftware(deviceName);
            var screen = FindScreen(hmi, screenName)
                ?? throw new KeyNotFoundException($"HMI screen '{screenName}' not found.");
            int sw = (int)screen.Width, sh = (int)screen.Height;
            int Num(HmiScreenItemBase i, string n)
            { try { return System.Convert.ToInt32(i.GetAttribute(n)); } catch { return 0; } }

            var items = new List<(string Name, string Type, int L, int T, int R, int B)>();
            foreach (HmiScreenItemBase i in screen.ScreenItems)
            {
                int l = Num(i, "Left"), t = Num(i, "Top"), w = Num(i, "Width"), h = Num(i, "Height");
                items.Add((i.Name, i.GetType().Name, l, t, l + w, t + h));
            }

            var errors = new List<string>();
            var warnings = new List<string>();
            foreach (var a in items)
            {
                if (a.L < 0 || a.T < 0 || a.R > sw || a.B > sh)
                    errors.Add($"{a.Name} ({a.Type}) is outside the {sw}x{sh} screen: {a.L},{a.T} to {a.R},{a.B}. Move or resize it.");
                if (a.R <= a.L || a.B <= a.T)
                    warnings.Add($"{a.Name} ({a.Type}) has zero or negative size.");
            }
            for (int x = 0; x < items.Count; x++)
                for (int y = x + 1; y < items.Count; y++)
                {
                    var a = items[x]; var b = items[y];
                    bool inter = a.L < b.R && b.L < a.R && a.T < b.B && b.T < a.B;
                    if (!inter) continue;
                    bool aInB = b.L <= a.L && b.T <= a.T && b.R >= a.R && b.B >= a.B;
                    bool bInA = a.L <= b.L && a.T <= b.T && a.R >= b.R && a.B >= b.B;
                    if (aInB || bInA) continue;
                    errors.Add($"{a.Name} and {b.Name} partially overlap. Separate them.");
                }
            var verdict = errors.Count > 0 ? "fail" : warnings.Count > 0 ? "warn" : "pass";
            return (object)new { screen = screen.Name, width = sw, height = sh, itemCount = items.Count, verdict, errors, warnings };
        });
    }

    // ── Bind a property to an HMI tag (dynamization) ──────────────────────────

    /// <summary>Creates a TagDynamization on a screen item property (e.g. BackColor, Text) via reflection, using the
    /// concrete type only: an abstract dynamization type crashes TIA. Reads the result back.</summary>
    public async Task<object> BindTagAsync(string deviceName, string screenName, string itemName, string property, string tagName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmiSoftware(deviceName);
            var screen = FindScreen(hmi, screenName)
                ?? throw new KeyNotFoundException($"HMI screen '{screenName}' not found.");
            HmiScreenItemBase? item = null;
            foreach (HmiScreenItemBase i in screen.ScreenItems)
                if (i.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase)) { item = i; break; }
            if (item is null) throw new KeyNotFoundException($"Item '{itemName}' not found on '{screenName}'.");

            var dynProp = item.GetType().GetProperty("Dynamizations")
                ?? throw new NotSupportedException($"{item.GetType().Name} has no Dynamizations.");
            var comp = dynProp.GetValue(item) ?? throw new NotSupportedException("Dynamizations is null.");
            var dynType = typeof(HmiScreenItemBase).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "TagDynamization" && !t.IsAbstract)
                ?? throw new NotSupportedException("TagDynamization type not found in this Openness version.");
            var create = comp.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                ?? throw new NotSupportedException("Dynamizations has no generic Create.");
            var dyn = create.MakeGenericMethod(dynType).Invoke(comp, new object[] { property })
                ?? throw new InvalidOperationException("Create returned null.");
            var tagProp = dynType.GetProperty("Tag") ?? throw new NotSupportedException("TagDynamization has no Tag property.");
            tagProp.SetValue(dyn, tagProp.PropertyType == typeof(string) ? tagName : System.Convert.ChangeType(tagName, tagProp.PropertyType));
            return (object)new { item = itemName, property, tag = tagProp.GetValue(dyn)?.ToString(), status = "bound" };
        });
    }

    private static void BindTagOnItem(HmiScreenItemBase item, string property, string tagName)
    {
        var comp = item.GetType().GetProperty("Dynamizations")?.GetValue(item)
            ?? throw new NotSupportedException($"{item.GetType().Name} has no Dynamizations.");
        var dynType = typeof(HmiScreenItemBase).Assembly.GetTypes()
            .FirstOrDefault(t => t.Name == "TagDynamization" && !t.IsAbstract)
            ?? throw new NotSupportedException("TagDynamization type not found in this Openness version.");
        var create = comp.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
            ?? throw new NotSupportedException("Dynamizations has no generic Create.");
        var dyn = create.MakeGenericMethod(dynType).Invoke(comp, new object[] { property })
            ?? throw new InvalidOperationException("Create returned null.");
        dynType.GetProperty("Tag")!.SetValue(dyn, tagName);
    }

    // ── Value-to-colour map on an existing tag dynamization ───────────────────

    /// <summary>Fills the MappingTable of an existing TagDynamization (see BindTagAsync) with one Range entry per
    /// value (From = To = value), so the property takes the matching value, e.g. a colour. Existing entries are
    /// replaced. Created via reflection on the generic Entries.Create&lt;MappingTableEntryRange&gt;(). UNVERIFIED on V19.</summary>
    public async Task<object> SetColourMapAsync(string deviceName, string screenName, string itemName, string property,
        List<(string Value, string? To, string Color, string? Flashing)> entries)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
          try
          {
            var hmi = GetHmiSoftware(deviceName);
            var screen = FindScreen(hmi, screenName)
                ?? throw new KeyNotFoundException($"HMI screen '{screenName}' not found.");
            HmiScreenItemBase? item = null;
            foreach (HmiScreenItemBase i in screen.ScreenItems)
                if (i.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase)) { item = i; break; }
            if (item is null) throw new KeyNotFoundException($"Item '{itemName}' not found on '{screenName}'.");

            var comp = item.GetType().GetProperty("Dynamizations")?.GetValue(item)
                ?? throw new NotSupportedException($"{item.GetType().Name} has no Dynamizations.");
            object? dyn = null;
            foreach (var d in (System.Collections.IEnumerable)comp)
                if (d.GetType().Name == "TagDynamization"
                    && string.Equals(d.GetType().GetProperty("PropertyName")?.GetValue(d)?.ToString(), property, StringComparison.OrdinalIgnoreCase))
                { dyn = d; break; }
            if (dyn is null) throw new KeyNotFoundException($"No tag dynamization on {itemName}.{property}. Run bind_hmi_tag first.");

            var conv = dyn.GetType().GetProperty("ValueConverter")?.GetValue(dyn)
                ?? throw new NotSupportedException("TagDynamization has no ValueConverter.");
            var table = conv.GetType().GetProperty("MappingTable")?.GetValue(conv)
                ?? throw new NotSupportedException("ValueConverter has no MappingTable.");
            var condProp = table.GetType().GetProperty("ConditionType")
                ?? throw new NotSupportedException("MappingTable has no ConditionType.");
            var entryComp = table.GetType().GetProperty("Entries")?.GetValue(table)
                ?? throw new NotSupportedException("MappingTable has no Entries.");

            var entryType = typeof(HmiScreenItemBase).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "MappingTableEntryRange" && !t.IsAbstract)
                ?? throw new NotSupportedException("MappingTableEntryRange type not found in this Openness version.");
            var create = entryComp.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                ?? throw new NotSupportedException("Entries has no parameterless generic Create.");

            // Replace any existing entries.
            foreach (var old in ((System.Collections.IEnumerable)entryComp).Cast<object>().ToList())
                old.GetType().GetMethod("Delete")?.Invoke(old, null);

            condProp.SetValue(table, Enum.Parse(condProp.PropertyType, "Range"));

            object Conv(object target, string prop, string raw)
            {
                var pt = (target.GetType().GetProperty(prop) ?? throw new NotSupportedException($"No property {prop}.")).PropertyType;
                if (pt == typeof(System.Drawing.Color)) return System.Drawing.ColorTranslator.FromHtml(raw);
                if (pt == typeof(object))
                    return raw.StartsWith("#") ? System.Drawing.ColorTranslator.FromHtml(raw)
                         : double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dv) ? dv : raw;
                if (pt.IsEnum) return Enum.Parse(pt, raw);
                return System.Convert.ChangeType(raw, pt, System.Globalization.CultureInfo.InvariantCulture);
            }

            var done = new List<object>();
            foreach (var (value, to, color, flashing) in entries)
            {
                var entry = create.MakeGenericMethod(entryType).Invoke(entryComp, null)
                    ?? throw new InvalidOperationException("Create returned null.");
                // From/To/Value are typed object: TIA rejects some CLR types, so try the likely ones in turn.
                void SetAny(string prop, params object[] candidates)
                {
                    var pi = entryType.GetProperty(prop)!;
                    var errs = new List<string>();
                    foreach (var c in candidates)
                    {
                        try { pi.SetValue(entry, c); return; }
                        catch (System.Reflection.TargetInvocationException ex)
                        { errs.Add($"{c.GetType().Name}: {ex.InnerException?.Message}"); }
                    }
                    throw new InvalidOperationException($"Could not set {prop}={candidates[0]}. Tried " + string.Join(" | ", errs));
                }
                var num = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                var numTo = double.Parse(to ?? value, System.Globalization.CultureInfo.InvariantCulture);
                SetAny("From", (int)num, num, value);
                SetAny("To", (int)numTo, numTo, to ?? value);
                var rt = entryType.GetProperty("RangeType");
                if (rt is not null && rt.CanWrite) rt.SetValue(entry, Enum.Parse(rt.PropertyType, "Range"));
                // Value: boolean ("true"/"false", e.g. Visible), colour ("#RRGGBB") or a plain number/text.
                if (bool.TryParse(color, out var bv)) SetAny("Value", bv);
                else if (color.StartsWith("#")) SetAny("Value", System.Drawing.ColorTranslator.FromHtml(color), color);
                else if (double.TryParse(color, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var nv))
                    SetAny("Value", nv, color);
                else SetAny("Value", color);
                if (!string.IsNullOrEmpty(flashing))
                {
                    var fl = entryType.GetProperty("Flashing")!;
                    var fr = entryType.GetProperty("FlashingRate")!;
                    fl.SetValue(entry, true);
                    fr.SetValue(entry, Enum.Parse(fr.PropertyType, flashing, true));
                }
                done.Add(new { value, to = to ?? value, color });
            }
            return (object)new { item = itemName, property, status = "mapped", entries = done };
          }
          catch (System.Reflection.TargetInvocationException tie)
          {
              var inner = tie.InnerException;
              throw new InvalidOperationException((inner?.GetType().Name ?? "Error") + ": " + (inner?.Message ?? tie.Message), inner);
          }
        });
    }

    // Lists the messages of an HmiValidationResult (Errors / Warnings are compositions of message objects).
    private static List<string> DescribeValidation(object? result, string listName)
    {
        var list = new List<string>();
        if (result is null) return list;
        var coll = result.GetType().GetProperty(listName)?.GetValue(result) as System.Collections.IEnumerable;
        if (coll is null) return list;
        foreach (var m in coll)
        {
            string? text = null;
            foreach (var pn in new[] { "Message", "Description", "Text" })
            {
                text = m.GetType().GetProperty(pn)?.GetValue(m)?.ToString();
                if (!string.IsNullOrEmpty(text)) break;
            }
            list.Add(text ?? m.ToString() ?? "(unknown)");
        }
        return list;
    }

    // ── Screen event script (Loaded / Unloaded) ───────────────────────────────

    /// <summary>Sets the JavaScript of a screen event (Loaded or Unloaded), e.g. to start a timer that animates items.
    /// Runs the TIA syntax check and reports its output. UNVERIFIED on V19.</summary>
    public async Task<object> SetScreenScriptAsync(string deviceName, string screenName, string eventName, string code)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            try
            {
                var hmi = GetHmiSoftware(deviceName);
                var screen = FindScreen(hmi, screenName)
                    ?? throw new KeyNotFoundException($"HMI screen '{screenName}' not found.");
                var evType = (HmiScreenEventType)Enum.Parse(typeof(HmiScreenEventType), eventName, true);
                var handlers = screen.EventHandlers;
                var h = handlers.Find(evType) ?? handlers.Create(evType);
                h.Script.ScriptCode = code;
                var check = h.Script.SyntaxCheck();
                var errs = DescribeValidation(check, "Errors");
                var warns = DescribeValidation(check, "Warnings");
                return (object)new { screen = screen.Name, @event = evType.ToString(), syntaxOk = errs.Count == 0, errors = errs, warnings = warns };
            }
            catch (System.Reflection.TargetInvocationException tie)
            {
                throw new InvalidOperationException(tie.InnerException?.Message ?? tie.Message, tie.InnerException);
            }
        });
    }

    // ── Download probe (read only) ────────────────────────────────────────────

    /// <summary>Checks whether the HMI device item offers a DownloadProvider and lists the connection modes,
    /// PC interfaces and target interfaces it sees. Downloads nothing.</summary>
    public async Task<object> ProbeDownloadAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var found = new List<object>();
            foreach (Device device in _tia.Project!.Devices)
            {
                if (!device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                var items = new List<IEngineeringServiceProvider> { device };
                foreach (DeviceItem di in device.DeviceItems) items.Add(di);
                foreach (var sp in items)
                {
                    Siemens.Engineering.Download.DownloadProvider? dp = null;
                    try { dp = sp.GetService<Siemens.Engineering.Download.DownloadProvider>(); } catch { }
                    if (dp is null) { found.Add(new { host = sp.GetType().Name, downloadProvider = false }); continue; }
                    var modes = new List<object>();
                    foreach (var mode in dp.Configuration.Modes)
                    {
                        var pcs = new List<object>();
                        foreach (var pc in mode.PcInterfaces)
                        {
                            var targets = new List<string>();
                            try { foreach (var t in pc.TargetInterfaces) targets.Add(t.Name); } catch (Exception ex) { targets.Add("ERROR: " + ex.Message); }
                            pcs.Add(new { name = pc.Name, number = pc.Number, targets });
                        }
                        modes.Add(new { name = mode.Name, pcInterfaces = pcs });
                    }
                    found.Add(new { host = sp.GetType().Name, downloadProvider = true, modes });
                }
            }
            return (object)found;
        });
    }

    // ── Download (outward-facing) ─────────────────────────────────────────────

    /// <summary>Downloads the HMI software through one PC interface and target interface. Overwrites the
    /// runtime on the target. UNVERIFIED on V19. A dialog in TIA can block the STA thread.</summary>
    public async Task<object> DownloadAsync(string deviceName, string pcInterface, int pcNumber, string target)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            foreach (Device device in _tia.Project!.Devices)
            {
                if (!device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (DeviceItem di in device.DeviceItems)
                {
                    Siemens.Engineering.Download.DownloadProvider? dp = null;
                    try { dp = di.GetService<Siemens.Engineering.Download.DownloadProvider>(); } catch { }
                    if (dp is null) continue;
                    var mode = dp.Configuration.Modes.Find("PN/IE")
                        ?? throw new KeyNotFoundException("PN/IE mode not offered.");
                    var pc = mode.PcInterfaces.Find(pcInterface, pcNumber)
                        ?? throw new KeyNotFoundException($"PC interface '{pcInterface}' #{pcNumber} not found.");
                    var ti = pc.TargetInterfaces.Find(target)
                        ?? throw new KeyNotFoundException($"Target '{target}' not found.");
                    var res = dp.Download(ti,
                        c => { },
                        c => { },
                        Siemens.Engineering.Download.DownloadOptions.Software);
                    var msgs = new List<string>();
                    try { foreach (var m in res.Messages) msgs.Add($"{m.State}: {m.Message}"); } catch { }
                    return (object)new { pcInterface, pcNumber, target, state = res.State.ToString(), messages = msgs };
                }
            }
            throw new NotSupportedException("No download service found on that device.");
        });
    }

    // ── Delete a screen ───────────────────────────────────────────────────────

    public async Task<object> DeleteScreenAsync(string deviceName, string screenName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmiSoftware(deviceName);
            var screen = FindScreen(hmi, screenName)
                ?? throw new KeyNotFoundException($"HMI screen '{screenName}' not found.");
            screen.Delete();
            return (object)new { screen = screenName, status = "deleted" };
        });
    }

    // ── Compile the HMI software ──────────────────────────────────────────────

    public async Task<object> CompileHmiAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            // HmiSoftware itself has no GetService; the compile service hangs off the device item that hosts it.
            Siemens.Engineering.Compiler.ICompilable? compilable = null;
            foreach (Device device in _tia.Project!.Devices)
            {
                if (!device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (DeviceItem di in device.DeviceItems)
                    if (di.GetService<SoftwareContainer>()?.Software is HmiSoftware)
                    { compilable = di.GetService<Siemens.Engineering.Compiler.ICompilable>(); if (compilable is not null) break; }
                compilable ??= device.GetService<Siemens.Engineering.Compiler.ICompilable>();
            }
            if (compilable is null)
                throw new NotSupportedException("This HMI device does not offer a compile service through Openness.");
            var result = compilable.Compile();
            var messages = new List<string>();
            void Flatten(IEnumerable<Siemens.Engineering.Compiler.CompilerResultMessage> msgs, int depth)
            {
                foreach (var m in msgs)
                {
                    messages.Add($"{new string(' ', depth * 2)}[{m.State}] {m.Description}");
                    Flatten(m.Messages, depth + 1);
                }
            }
            Flatten(result.Messages, 0);
            return (object)new { state = result.State.ToString(), errors = result.ErrorCount,
                                 warnings = result.WarningCount, messages };
        });
    }

    // ── Build a screen: create (optional), clear (optional), add items ───────
    // Per-item and per-attribute errors are returned, not thrown, so a bad
    // attribute does not lose the rest of the screen.

    public async Task<object> BuildScreenAsync(string deviceName, string screenName, HmiScreenBuildRequest req)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmiSoftware(deviceName);
            var screen = FindScreen(hmi, screenName);
            if (screen is null)
            {
                if (!req.Create) throw new KeyNotFoundException($"HMI screen '{screenName}' not found (set create:true to make it).");
                screen = hmi.Screens.Create(screenName);
            }
            if (req.Width  is uint w) screen.Width  = w;
            if (req.Height is uint h) screen.Height = h;

            if (req.Clear)
            {
                var existing = new List<HmiScreenItemBase>();
                foreach (HmiScreenItemBase i in screen.ScreenItems) existing.Add(i);
                foreach (var i in existing) i.Delete();
            }

            var results = new List<object>();
            foreach (var spec in req.Items)
            {
                var errors = new List<string>();
                try
                {
                    HmiScreenItemBase item = spec.Type.ToLowerInvariant() switch
                    {
                        "text" or "label" => screen.ScreenItems.Create<HmiLabel>(spec.Name),
                        "iofield"      => screen.ScreenItems.Create<HmiIOField>(spec.Name),
                        "button"       => screen.ScreenItems.Create<HmiButton>(spec.Name),
                        "rectangle"    => screen.ScreenItems.Create<HmiRectangle>(spec.Name),
                        "circle"       => screen.ScreenItems.Create<HmiCircle>(spec.Name),
                        "toggleswitch" => screen.ScreenItems.Create<HmiToggleSwitch>(spec.Name),
                        _ => CreateByTypeName(screen, spec.Type, spec.Name),
                    };

                    if (spec.Points is { Count: >= 4 })
                        Try(errors, "Points", () =>
                        {
                            var pts = item.GetType().GetProperty("Points")?.GetValue(item)
                                ?? throw new InvalidOperationException($"{item.GetType().Name} has no Points.");
                            var add = pts.GetType().GetMethod("Create", new[] { typeof(int), typeof(int) })
                                ?? throw new InvalidOperationException("Points has no Create(int,int).");
                            // A new polygon/polyline starts with default points. A shape needs a minimum number of
                            // points, so add the new ones first and only then delete the defaults.
                            var defaults = ((System.Collections.IEnumerable)pts).Cast<object>().ToList();
                            for (int p = 0; p + 1 < spec.Points.Count; p += 2)
                                add.Invoke(pts, new object[] { spec.Points[p], spec.Points[p + 1] });
                            foreach (var old in defaults)
                                old.GetType().GetMethod("Delete")?.Invoke(old, null);
                        });
                    else
                    {
                        Try(errors, "Left",   () => item.SetAttribute("Left",   spec.Left));
                        Try(errors, "Top",    () => item.SetAttribute("Top",    spec.Top));
                        Try(errors, "Width",  () => item.SetAttribute("Width",  spec.Width));
                        Try(errors, "Height", () => item.SetAttribute("Height", spec.Height));
                    }

                    if (spec.Text is not null)
                        Try(errors, "Text", () => SetText(item, "Text", spec.Text));
                    if (spec.ProcessValue is not null)
                        // A static SetAttribute only writes text; TIA needs a Tag dynamization to link the HMI tag.
                        Try(errors, "ProcessValue", () => BindTagOnItem(item, "ProcessValue", spec.ProcessValue));
                    if (spec.IoType is not null && item is HmiIOField io)
                        Try(errors, "IOFieldType", () =>
                            io.IOFieldType = (HmiIOFieldType)Enum.Parse(typeof(HmiIOFieldType), spec.IoType, true));

                    if (spec.OnTap is not null)
                        Try(errors, "OnTap", () =>
                        {
                            if (item is not HmiButton btn)
                                throw new InvalidOperationException("onTap only applies to buttons.");
                            var h = btn.EventHandlers.Find(HmiButtonEventType.Tapped)
                                    ?? btn.EventHandlers.Create(HmiButtonEventType.Tapped);
                            h.Script.ScriptCode = spec.OnTap;
                            var errs = DescribeValidation(h.Script.SyntaxCheck(), "Errors");
                            if (errs.Count > 0)
                                throw new InvalidOperationException("script syntax check: " + string.Join("; ", errs));
                        });

                    if (spec.Attributes is not null)
                        foreach (var kv in spec.Attributes)
                            Try(errors, kv.Key, () => ApplyAttribute(item, kv.Key, kv.Value));

                    results.Add(new { name = spec.Name, type = spec.Type, status = errors.Count == 0 ? "created" : "created_with_errors", errors });
                }
                catch (Exception ex)
                {
                    results.Add(new { name = spec.Name, type = spec.Type, status = "failed", errors = new[] { ex.Message } });
                }
            }
            _log.LogInformation("Built HMI screen '{Screen}' on '{Device}': {Count} items.", screenName, deviceName, req.Items.Count);
            return (object)new { screen = screen.Name, width = screen.Width, height = screen.Height, items = results };
        });
    }

    // Any other screen-item class by name, e.g. "TextBox", "HmiTextBox", "Slider".
    private static HmiScreenItemBase CreateByTypeName(HmiScreen screen, string typeName, string name)
    {
        var asm = typeof(HmiButton).Assembly;
        var t = asm.GetType($"Siemens.Engineering.HmiUnified.UI.Widgets.{typeName}")
             ?? asm.GetType($"Siemens.Engineering.HmiUnified.UI.Widgets.Hmi{typeName}")
             ?? asm.GetType($"Siemens.Engineering.HmiUnified.UI.Shapes.{typeName}")
             ?? asm.GetType($"Siemens.Engineering.HmiUnified.UI.Shapes.Hmi{typeName}")
             ?? FindItemType(asm, typeName)
             ?? throw new ArgumentException($"Unknown item type '{typeName}'. Similar: " + string.Join(", ", SimilarTypes(asm, typeName)));
        var create = screen.ScreenItems.GetType().GetMethods()
            .First(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
            .MakeGenericMethod(t);
        try { return (HmiScreenItemBase)create.Invoke(screen.ScreenItems, new object[] { name })!; }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        { throw ex.InnerException; }
    }

    // Any HmiUnified.UI type whose simple name matches (with or without the Hmi prefix), e.g. "ScreenWindow".
    private static Type? FindItemType(System.Reflection.Assembly asm, string typeName)
    {
        try
        {
            return asm.GetTypes().FirstOrDefault(t => t.IsClass && !t.IsAbstract
                && (t.Namespace ?? "").StartsWith("Siemens.Engineering.HmiUnified.UI", StringComparison.Ordinal)
                && (t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                    || t.Name.Equals("Hmi" + typeName, StringComparison.OrdinalIgnoreCase)));
        }
        catch (System.Reflection.ReflectionTypeLoadException) { return null; }
    }

    private static IEnumerable<string> SimilarTypes(System.Reflection.Assembly asm, string typeName)
    {
        try
        {
            var key = typeName.Replace("Hmi", "");
            return asm.GetTypes()
                .Where(t => (t.Namespace ?? "").StartsWith("Siemens.Engineering.HmiUnified.UI", StringComparison.Ordinal)
                            && t.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(t => t.FullName ?? t.Name).Take(15).ToList();
        }
        catch (System.Reflection.ReflectionTypeLoadException) { return new[] { "(type scan failed)" }; }
    }

    private static void Try(List<string> errors, string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
    }

    private static void SetText(HmiScreenItemBase item, string property, string text)
    {
        var mt = item.GetType().GetProperty(property)?.GetValue(item) as Siemens.Engineering.MultilingualText
            ?? throw new InvalidOperationException($"'{property}' is not a text property on {item.GetType().Name}.");
        foreach (Siemens.Engineering.MultilingualTextItem t in mt.Items)
            t.Text = text;
    }

    // Converts JSON-ish values to what SetAttribute expects.
    private static void ApplyAttribute(HmiScreenItemBase item, string key, object? value)
    {
        object? v = value;
        if (value is System.Text.Json.JsonElement je)
            v = je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => je.GetString(),
                System.Text.Json.JsonValueKind.Number => je.TryGetInt32(out var i) ? (object)i : je.GetDouble(),
                System.Text.Json.JsonValueKind.True   => true,
                System.Text.Json.JsonValueKind.False  => false,
                _ => je.ToString(),
            };

        if (key.Equals("FontSize", StringComparison.OrdinalIgnoreCase))
        {
            var font = item.GetType().GetProperty("Font")?.GetValue(item) as HmiFontPart
                ?? throw new InvalidOperationException("item has no Font.");
            font.Size = Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture);
            return;
        }
        if (key.Equals("Text", StringComparison.OrdinalIgnoreCase) || key.Equals("AlternateText", StringComparison.OrdinalIgnoreCase))
        { SetText(item, key, Convert.ToString(v) ?? ""); return; }

        if (v is string s && s.StartsWith("#") && key.EndsWith("Color", StringComparison.OrdinalIgnoreCase))
            v = System.Drawing.ColorTranslator.FromHtml(s);
        else if (v is int n)
        {
            if (key is "Width" or "Height") v = (uint)n;
            else if (key == "BorderWidth")  v = (byte)n;
            else if (key == "Opacity")      v = (float)n;
        }
        item.SetAttribute(key, v);
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


