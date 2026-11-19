using System.Reflection;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Script;
using Siemens.Engineering.HmiUnified.UI.ScreenGroup;
using Siemens.Engineering.HmiUnified.UI.Screens;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Services;

/// <summary>
/// Read and edit the objects on a WinCC Unified screen through the typed
/// Openness object model (V21 has no screen export/import, see README).
/// Every write method takes dryRun and never deletes anything.
/// </summary>
public sealed class HmiScreenEditService
{
    private readonly TiaPortalService _tia;
    private readonly StaTaskScheduler _sta;
    private readonly ILogger<HmiScreenEditService> _log;

    public HmiScreenEditService(TiaPortalService tia, StaTaskScheduler sta, ILogger<HmiScreenEditService> log)
    { _tia = tia; _sta = sta; _log = log; }

    // ── read_hmi_screen ───────────────────────────────────────────────────────

    public async Task<object> ReadScreenAsync(string device, string screenName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var screen = FindScreen(GetHmi(device), screenName);
            var items = new List<object>();
            ReadItems(screen.ScreenItems, "", items);
            return (object)new
            {
                screen = screen.Name,
                width = screen.Width, height = screen.Height,
                itemCount = items.Count,
                screenEventHandlers = ReadEventHandlers(screen),
                items,
            };
        });
    }

    private void ReadItems(HmiScreenItemBaseComposition comp, string prefix, List<object> list)
    {
        foreach (HmiScreenItemBase item in comp)
        {
            list.Add(ReadItem(item, prefix));
            var children = Get(item, "ScreenItems") as HmiScreenItemBaseComposition;
            if (children != null) ReadItems(children, prefix + item.Name + "/", list);
        }
    }

    private static object ReadItem(HmiScreenItemBase item, string path)
    {
        var d = new Dictionary<string, object?>
        {
            ["name"] = item.Name,
            ["path"] = path + item.Name,
            ["type"] = item.GetType().Name,
            ["left"] = Get(item, "Left"), ["top"] = Get(item, "Top"),
            ["width"] = Get(item, "Width"), ["height"] = Get(item, "Height"),
            ["visible"] = Get(item, "Visible"), ["enabled"] = Get(item, "Enabled"),
        };
        var text = ReadMl(Get(item, "Text"));
        if (text != null) d["text"] = text;
        foreach (var p in new[] { "ProcessValue", "OutputFormat", "IOFieldType", "ShiftDecimalPlaces" })
        { var v = Get(item, p); if (v != null) d[Camel(p)] = v is Enum ? v.ToString() : v; }

        var dyn = new List<object>();
        try
        {
            foreach (DynamizationBase x in item.Dynamizations) dyn.Add(DescribeDynamization(x));
        }
        catch (Exception ex) { dyn.Add(new { error = First(ex) }); }
        d["dynamizations"] = dyn;
        // The bound process tag is the TagDynamization on ProcessValue.
        foreach (var o in dyn)
            if (o is Dictionary<string, object?> dd && (string?)dd["property"] == "ProcessValue" && dd.ContainsKey("tag"))
            { d["boundTag"] = dd["tag"]; break; }
        d["eventHandlers"] = ReadEventHandlers(item);
        d["propertyEventHandlers"] = ReadPropertyEventHandlers(item);
        return d;
    }

    private static Dictionary<string, object?> DescribeDynamization(DynamizationBase x)
    {
        var d = new Dictionary<string, object?>
        {
            ["property"] = x.PropertyName,
            ["kind"] = x.DynamizationType.ToString(),
        };
        switch (x)
        {
            case TagDynamization t:
                d["tag"] = t.Tag; d["plcTag"] = Safe(() => t.PlcTag); d["readOnly"] = Safe(() => t.ReadOnly);
                break;
            case ScriptDynamization s:
                d["scriptCode"] = Safe(() => s.ScriptCode);
                d["triggerType"] = Safe(() => s.Trigger?.Type.ToString());
                d["triggerTags"] = Safe(() => s.Trigger?.Tags is null ? null : s.Trigger.Tags.ToString());
                break;
            case ExpressionDynamization e:
                d["expression"] = Safe(() => e.GetAttribute("Expression")?.ToString());
                break;
            case ResourceListDynamization r:
                d["resourceList"] = Safe(() => r.ResourceList?.ToString()); d["tag"] = Safe(() => r.Tag);
                break;
        }
        return d;
    }

    private static List<object> ReadEventHandlers(object owner) => ReadHandlers(Get(owner, "EventHandlers"));
    private static List<object> ReadPropertyEventHandlers(object owner) => ReadHandlers(Get(owner, "PropertyEventHandlers"));

    private static List<object> ReadHandlers(object? comp)
    {
        var list = new List<object>();
        if (comp is not System.Collections.IEnumerable en) return list;
        try
        {
            foreach (var h in en)
            {
                var script = Get(h, "Script");
                list.Add(new
                {
                    eventType = Get(h, "EventType")?.ToString(),
                    property = Get(h, "PropertyName"),
                    scriptCode = script is null ? null : Get(script, "ScriptCode"),
                });
            }
        }
        catch (Exception ex) { list.Add(new { error = First(ex) }); }
        return list;
    }

    // ── set_hmi_screen_item ───────────────────────────────────────────────────

    public sealed record ItemEdit(string? Text, string? ProcessTag, string? OutputFormat, string? VisibilityExpression);

    public async Task<object> SetItemAsync(string device, string screenName, string itemName,
        ItemEdit edit, bool dryRun)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var screen = FindScreen(GetHmi(device), screenName);
            var item = FindItem(screen.ScreenItems, itemName);
            var before = ReadItem(item, "");
            var applied = new List<string>();
            ApplyEdit(item, edit, dryRun, applied);
            return (object)new { screen = screen.Name, item = item.Name, dryRun, applied, before, after = dryRun ? null : ReadItem(item, "") };
        });
    }

    private static void ApplyEdit(HmiScreenItemBase item, ItemEdit edit, bool dryRun, List<string> applied)
    {
        if (edit.Text != null)
        {
            applied.Add($"text -> \"{edit.Text}\"");
            if (!dryRun) SetMl(item, "Text", edit.Text);
        }
        if (edit.OutputFormat != null)
        {
            applied.Add($"outputFormat -> {edit.OutputFormat}");
            if (!dryRun) SetProp(item, "OutputFormat", edit.OutputFormat);
        }
        if (edit.ProcessTag != null)
        {
            applied.Add($"ProcessValue tag -> {edit.ProcessTag}");
            if (!dryRun)
            {
                var td = item.Dynamizations.Find("ProcessValue") as TagDynamization
                         ?? item.Dynamizations.Create<TagDynamization>("ProcessValue");
                td.Tag = edit.ProcessTag;
            }
        }
        if (edit.VisibilityExpression != null)
        {
            applied.Add($"Visible expression -> {edit.VisibilityExpression}");
            if (!dryRun) SetExpression(item, "Visible", edit.VisibilityExpression);
        }
        if (applied.Count == 0) throw new ArgumentException("Nothing to change: pass at least one of text, processTag, outputFormat, visibilityExpression.");
    }

    private static void SetExpression(HmiScreenItemBase item, string property, string expression)
    {
        var ex = item.Dynamizations.Find(property) as ExpressionDynamization
                 ?? item.Dynamizations.Create<ExpressionDynamization>(property);
        ex.SetAttribute("Expression", expression);
    }

    // ── dynamize_hmi_screen_item (script dynamization, e.g. text from a tag) ──

    public async Task<object> DynamizeItemAsync(string device, string screenName, string itemName,
        string property, string scriptCode, List<string>? triggerTags, string? triggerType, bool dryRun)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var screen = FindScreen(GetHmi(device), screenName);
            var item = FindItem(screen.ScreenItems, itemName);
            var before = ReadItem(item, "");
            if (!dryRun)
            {
                var existing = item.Dynamizations.Find(property);
                if (existing != null && existing is not ScriptDynamization)
                    throw new InvalidOperationException(
                        $"'{item.Name}.{property}' already has a {existing.DynamizationType} dynamization; this tool will not replace it.");
                var sd = existing as ScriptDynamization ?? item.Dynamizations.Create<ScriptDynamization>(property);
                sd.ScriptCode = scriptCode;
                ApplyTrigger(sd, triggerTags, triggerType);
            }
            return (object)new { screen = screen.Name, item = item.Name, property, dryRun, scriptCode, triggerTags, before,
                after = dryRun ? null : ReadItem(item, "") };
        });
    }

    private static void ApplyTrigger(ScriptDynamization sd, List<string>? tags, string? type)
    {
        var trig = sd.Trigger;
        if (tags != null && tags.Count > 0)
        {
            SetTriggerTags(trig, tags);
            trig.Type = TriggerType.Tags;
        }
        else if (type != null)
            trig.Type = (TriggerType)Enum.Parse(typeof(TriggerType), type, true);
    }

    // Trigger.Tags is typed Object in the API; probe for the accepted shape.
    private static void SetTriggerTags(Trigger trig, List<string> tags)
    {
        var errs = new List<string>();
        foreach (var attempt in new Func<object>[] { () => tags.ToArray(), () => tags, () => string.Join(",", tags), () => string.Join(";", tags) })
        {
            try { trig.Tags = attempt(); return; }
            catch (Exception ex) { errs.Add(First(ex)); }
        }
        throw new InvalidOperationException("Could not set Trigger.Tags: " + string.Join(" | ", errs));
    }

    // ── set_hmi_item_event_script ─────────────────────────────────────────────

    public async Task<object> SetEventScriptAsync(string device, string screenName, string itemName,
        string eventType, string scriptCode, bool dryRun)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var screen = FindScreen(GetHmi(device), screenName);
            var item = FindItem(screen.ScreenItems, itemName);
            var before = ReadItem(item, "");
            if (!dryRun)
            {
                var handlers = Get(item, "EventHandlers") ?? throw new InvalidOperationException($"{item.GetType().Name} has no EventHandlers.");
                object? target = null;
                foreach (var h in (System.Collections.IEnumerable)handlers)
                    if (string.Equals(Get(h, "EventType")?.ToString(), eventType, StringComparison.OrdinalIgnoreCase)) { target = h; break; }
                if (target == null)
                {
                    var create = handlers.GetType().GetMethods().FirstOrDefault(m => m.Name == "Create" && !m.IsGenericMethod
                        && m.GetParameters().Length == 1);
                    if (create == null) throw new InvalidOperationException("Cannot create an event handler of that type through this API.");
                    var pt = create.GetParameters()[0].ParameterType;
                    target = create.Invoke(handlers, new[] { pt.IsEnum ? Enum.Parse(pt, eventType, true) : (object)eventType });
                }
                var script = Get(target!, "Script") ?? throw new InvalidOperationException("Handler has no Script.");
                SetProp(script, "ScriptCode", scriptCode);
            }
            return (object)new { screen = screen.Name, item = item.Name, eventType, dryRun, scriptCode, before,
                after = dryRun ? null : ReadItem(item, "") };
        });
    }

    // ── copy_hmi_screen_item ──────────────────────────────────────────────────

    public async Task<object> CopyItemAsync(string device, string screenName, string sourceName, string newName,
        ItemEdit edit, int? offsetX, int? offsetY, bool dryRun)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var screen = FindScreen(GetHmi(device), screenName);
            var src = FindItem(screen.ScreenItems, sourceName);
            if (Find(screen.ScreenItems, newName) != null)
                throw new InvalidOperationException($"An item named '{newName}' already exists on '{screenName}'.");
            var notCopied = new List<string>();
            if (dryRun)
                return (object)new { screen = screen.Name, source = src.Name, newName, dryRun, type = src.GetType().Name, source_state = ReadItem(src, "") };

            var create = typeof(HmiScreenItemBaseComposition).GetMethods()
                .First(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            var dst = (HmiScreenItemBase)create.MakeGenericMethod(src.GetType()).Invoke(screen.ScreenItems, new object[] { newName })!;
            try
            {
                CopyProps(src, dst, 0, notCopied);
                CopyDynamizations(src, dst, notCopied);
                if (Get(src, "EventHandlers") is System.Collections.IEnumerable eh && eh.Cast<object>().Any())
                    notCopied.Add("EventHandlers (event scripts are not copied; use set_hmi_item_event_script)");
                if (Get(src, "PropertyEventHandlers") is System.Collections.IEnumerable pe && pe.Cast<object>().Any())
                    notCopied.Add("PropertyEventHandlers");
                if (offsetX.HasValue) SetProp(dst, "Left", (int)Get(src, "Left")! + offsetX.Value);
                if (offsetY.HasValue) SetProp(dst, "Top", (int)Get(src, "Top")! + offsetY.Value);
                var applied = new List<string>();
                if (edit.Text != null || edit.ProcessTag != null || edit.OutputFormat != null || edit.VisibilityExpression != null)
                    ApplyEdit(dst, edit, false, applied);
            }
            catch
            {
                // Leave no half-built copy behind: the new item is ours, so removing it is safe.
                try { dst.Delete(); } catch { }
                throw;
            }
            return (object)new { screen = screen.Name, source = src.Name, newName, dryRun, notCopied, result = ReadItem(dst, "") };
        });
    }

    private static void CopyDynamizations(HmiScreenItemBase src, HmiScreenItemBase dst, List<string> notCopied)
    {
        foreach (DynamizationBase x in src.Dynamizations)
        {
            try
            {
                switch (x)
                {
                    case TagDynamization t:
                    {
                        var n = dst.Dynamizations.Create<TagDynamization>(t.PropertyName);
                        n.Tag = t.Tag;
                        TryCopy(() => n.ReadOnly = t.ReadOnly);
                        break;
                    }
                    case ScriptDynamization s:
                    {
                        var n = dst.Dynamizations.Create<ScriptDynamization>(s.PropertyName);
                        n.ScriptCode = s.ScriptCode;
                        TryCopy(() => n.GlobalDefinitionAreaScriptCode = s.GlobalDefinitionAreaScriptCode);
                        TryCopy(() => n.Async = s.Async);
                        n.Trigger.Type = s.Trigger.Type;
                        if (s.Trigger.Tags != null) n.Trigger.Tags = s.Trigger.Tags;
                        break;
                    }
                    case ExpressionDynamization e:
                    {
                        var n = dst.Dynamizations.Create<ExpressionDynamization>(e.PropertyName);
                        n.SetAttribute("Expression", e.GetAttribute("Expression"));
                        break;
                    }
                    default:
                        notCopied.Add($"dynamization {x.PropertyName} ({x.DynamizationType})");
                        break;
                }
            }
            catch (Exception ex) { notCopied.Add($"dynamization {x.PropertyName}: {First(ex)}"); }
        }
    }

    private static void TryCopy(Action a) { try { a(); } catch { } }

    // Copy writable simple properties; recurse into small part objects (Font, Padding...).
    private static void CopyProps(object src, object dst, int depth, List<string> notCopied)
    {
        foreach (var p in src.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            if (depth == 0 && (p.Name is "Name" or "Parent" or "CurrentQuality" or "Dynamizations" or "EventHandlers" or "PropertyEventHandlers" or "ScreenItems")) continue;
            if (p.Name == "Parent") continue;
            var t = p.PropertyType;
            try
            {
                if (t == typeof(MultilingualText))
                {
                    var txt = ReadMl(p.GetValue(src));
                    if (txt != null) SetMl(dst, p.Name, FirstText(p.GetValue(src)));
                }
                else if (p.CanWrite && (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(System.Drawing.Color)))
                    p.SetValue(dst, p.GetValue(src));
                else if (!p.CanWrite && depth < 2 && typeof(IEngineeringObject).IsAssignableFrom(t)
                         && !typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
                {
                    var s = p.GetValue(src); var d = p.GetValue(dst);
                    if (s != null && d != null) CopyProps(s, d, depth + 1, notCopied);
                }
            }
            catch (Exception ex) { notCopied.Add($"{p.Name}: {First(ex)}"); }
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static object? Get(object o, string name)
    {
        try { return o.GetType().GetProperty(name)?.GetValue(o); } catch { return null; }
    }

    private static void SetProp(object o, string name, object? value)
    {
        var p = o.GetType().GetProperty(name) ?? throw new InvalidOperationException($"{o.GetType().Name} has no property '{name}'.");
        p.SetValue(o, value is IConvertible && value.GetType() != p.PropertyType && !p.PropertyType.IsEnum
            ? Convert.ChangeType(value, p.PropertyType) : value);
    }

    private static object? Safe(Func<object?> f) { try { return f(); } catch { return null; } }

    private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s.Substring(1);
    private static string First(Exception e) => (e.InnerException?.Message ?? e.Message).Split('\n')[0];

    private static Dictionary<string, string>? ReadMl(object? ml)
    {
        if (ml is not MultilingualText mt) return null;
        var d = new Dictionary<string, string>();
        try { foreach (MultilingualTextItem i in mt.Items) d[i.Language.Culture.Name] = i.Text ?? ""; } catch { }
        return d;
    }

    private static string FirstText(object? ml)
    {
        var d = ReadMl(ml);
        return d is { Count: > 0 } ? d.Values.First() : "";
    }

    // Writes the text of the project's editing language (or the only language if there is one).
    private void SetMlInstance(object item, string prop, string text) => SetMl(item, prop, text);

    private static void SetMl(object item, string prop, string text)
    {
        var mt = Get(item, prop) as MultilingualText
                 ?? throw new InvalidOperationException($"{item.GetType().Name} has no multilingual property '{prop}'.");
        var items = mt.Items.Cast<MultilingualTextItem>().ToList();
        if (items.Count == 0) throw new InvalidOperationException($"'{prop}' has no language entries to write.");
        var target = items.Count == 1 ? items[0] : (PickEditing(mt, items) ?? items[0]);
        target.Text = text;
    }

    private static MultilingualTextItem? PickEditing(MultilingualText mt, List<MultilingualTextItem> items)
    {
        try
        {
            var proj = mt.GetType(); // culture of editing language is resolved by name below
            var edit = EditingCulture;
            if (edit != null) return items.FirstOrDefault(i => i.Language.Culture.Name.Equals(edit, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        return null;
    }

    [ThreadStatic] private static string? _editing;
    private static string? EditingCulture => _editing;

    private HmiSoftware GetHmi(string deviceName)
    {
        var proj = _tia.Project!;
        try { _editing = proj.LanguageSettings.EditingLanguage?.Culture.Name; } catch { _editing = null; }
        foreach (Device device in proj.Devices)
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

    private static HmiScreen FindScreen(HmiSoftware hmi, string name)
    {
        foreach (HmiScreen s in hmi.Screens)
            if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return s;
        foreach (HmiScreenGroup g in hmi.ScreenGroups)
            foreach (HmiScreen s in g.Screens)
                if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return s;
        throw new KeyNotFoundException($"HMI screen '{name}' not found.");
    }

    private static HmiScreenItemBase? Find(HmiScreenItemBaseComposition comp, string name)
    {
        foreach (HmiScreenItemBase i in comp)
        {
            if (i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            if (Get(i, "ScreenItems") is HmiScreenItemBaseComposition c && Find(c, name) is { } hit) return hit;
        }
        return null;
    }

    private static HmiScreenItemBase FindItem(HmiScreenItemBaseComposition comp, string name)
        => Find(comp, name) ?? throw new KeyNotFoundException($"Screen item '{name}' not found.");
}
