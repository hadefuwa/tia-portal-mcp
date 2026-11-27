using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Creates WinCC Unified screens and builds their items from a JSON layout map
/// (see "Mimic_V2 Layout Map.json" in the SF2 project for the format).
///
/// Safety rules:
///  - create_hmi_screen never touches an existing screen.
///  - build_hmi_screen_from_map refuses a screen that holds any item whose name is not in the map,
///    so it can only ever work on screens it built itself. Existing map items are skipped unless
///    replaceExisting is true, in which case only those map items are deleted and rebuilt.
///  - Every property write is attempted individually and reported (applied / notApplied), because the
///    Openness property names differ per shape type (e.g. circles have no Left/Top).
/// </summary>
public sealed class HmiScreenBuildService
{
    private readonly TiaPortalService _tia;
    private readonly StaTaskScheduler _sta;
    private readonly ILogger<HmiScreenBuildService> _log;

    public HmiScreenBuildService(TiaPortalService tia, StaTaskScheduler sta, ILogger<HmiScreenBuildService> log)
    { _tia = tia; _sta = sta; _log = log; }

    // ── describe_hmi_item_type (pure reflection, no project access) ──────────

    public object DescribeItemType(string? typeName)
    {
        var all = ItemTypes();
        if (string.IsNullOrWhiteSpace(typeName))
            return new { types = all.Keys.OrderBy(k => k).ToList() };
        if (!all.TryGetValue(typeName!, out var t))
            throw new KeyNotFoundException($"No Unified screen item type '{typeName}'. Call without typeName for the list.");
        return new { type = t.Name, properties = DescribeProps(t, 0) };
    }

    private static List<object> DescribeProps(Type t, int depth)
    {
        var list = new List<object>();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name is "Parent") continue;
            var pt = p.PropertyType;
            object? parts = null;
            if (depth < 1 && !pt.IsPrimitive && !pt.IsEnum && pt != typeof(string) && pt != typeof(Color)
                && typeof(IEngineeringObject).IsAssignableFrom(pt) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(pt))
                parts = DescribeProps(pt, depth + 1);
            list.Add(new
            {
                name = p.Name,
                type = pt.Name,
                writable = p.CanWrite,
                values = pt.IsEnum ? Enum.GetNames(pt) : null,
                parts,
                collection = typeof(System.Collections.IEnumerable).IsAssignableFrom(pt) && pt != typeof(string) ? true : (bool?)null,
            });
        }
        return list;
    }

    private static Dictionary<string, Type> _types = null!;
    private static Dictionary<string, Type> ItemTypes()
    {
        if (_types != null) return _types;
        var d = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Append(typeof(HmiScreenItemBase).Assembly).Distinct())
        {
            Type[] ts;
            try { ts = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types.Where(x => x != null).ToArray()!; } catch { continue; }
            foreach (var t in ts)
                if (t.IsClass && !t.IsAbstract && typeof(HmiScreenItemBase).IsAssignableFrom(t) && !d.ContainsKey(t.Name))
                    d[t.Name] = t;
        }
        return _types = d;
    }

    // ── create_hmi_screen ─────────────────────────────────────────────────────

    public async Task<object> CreateScreenAsync(string device, string name, int width, int height, string? backColor, bool dryRun)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmi(device);
            if (TryFindScreen(hmi, name) != null)
                throw new InvalidOperationException($"Screen '{name}' already exists; create_hmi_screen never changes an existing screen.");
            if (dryRun) return (object)new { dryRun, name, width, height, backColor };
            var s = hmi.Screens.Create(name);
            var applied = new List<string>(); var notApplied = new List<string>();
            TrySet(s, "Width", width, applied, notApplied);
            TrySet(s, "Height", height, applied, notApplied);
            if (backColor != null) TrySet(s, "BackColor", ParseColor(backColor, 1), applied, notApplied);
            _log.LogInformation("Created HMI screen {Screen} on {Device}.", name, device);
            return (object)new { dryRun, created = s.Name, applied, notApplied };
        });
    }

    // ── build_hmi_screen_from_map ─────────────────────────────────────────────

    public async Task<object> BuildFromMapAsync(string device, string screenName, string mapPath, List<int>? phases,
        List<string>? only, bool replaceExisting, bool dryRun)
    {
        _tia.EnsureConnected();
        using var doc = JsonDocument.Parse(File.ReadAllText(mapPath));
        var root = doc.RootElement;
        var palette = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("palette", out var pal))
            foreach (var p in pal.EnumerateObject()) palette[p.Name] = p.Value.GetString() ?? "";
        var objects = root.GetProperty("objects").EnumerateArray().Select(o => o.Clone()).ToList();
        var mapNames = new HashSet<string>(objects.Select(o => o.GetProperty("name").GetString()!), StringComparer.OrdinalIgnoreCase);
        var wanted = objects.Where(o =>
            (phases == null || phases.Count == 0 || phases.Contains(o.TryGetProperty("phase", out var ph) ? ph.GetInt32() : 1)) &&
            (only == null || only.Count == 0 || only.Contains(o.GetProperty("name").GetString()!, StringComparer.OrdinalIgnoreCase))).ToList();

        return await _sta.RunAsync(() =>
        {
            var hmi = GetHmi(device);
            var screen = TryFindScreen(hmi, screenName) ?? throw new KeyNotFoundException($"Screen '{screenName}' not found (create it with create_hmi_screen).");
            var foreign = new List<string>();
            foreach (HmiScreenItemBase i in screen.ScreenItems)
                if (!mapNames.Contains(i.Name)) foreign.Add(i.Name);
            if (foreign.Count > 0)
                throw new InvalidOperationException($"Screen '{screenName}' holds items that are not in the map ({string.Join(", ", foreign.Take(10))}" +
                    $"{(foreign.Count > 10 ? ", ..." : "")}); this tool only works on screens built from the same map.");

            var types = ItemTypes();
            var report = new List<object>();
            int created = 0, skipped = 0, failed = 0;
            var create = typeof(HmiScreenItemBaseComposition).GetMethods()
                .First(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);

            foreach (var o in wanted)
            {
                var name = o.GetProperty("name").GetString()!;
                var typeName = o.GetProperty("type").GetString()!;
                var applied = new List<string>(); var notApplied = new List<string>();
                if (!types.TryGetValue(typeName, out var t)) { failed++; report.Add(new { name, error = $"unknown item type {typeName}" }); continue; }
                var existing = Find(screen.ScreenItems, name);
                if (existing != null && !replaceExisting) { skipped++; report.Add(new { name, skipped = "exists" }); continue; }
                if (dryRun) { report.Add(new { name, type = typeName, wouldReplace = existing != null }); continue; }
                if (existing != null) existing.Delete();

                HmiScreenItemBase item;
                try { item = (HmiScreenItemBase)create.MakeGenericMethod(t).Invoke(screen.ScreenItems, new object[] { name })!; }
                catch (Exception ex) { failed++; report.Add(new { name, error = "create: " + First(ex) }); continue; }

                try
                {
                    ApplyGeometry(item, o, applied, notApplied);
                    ApplyStyle(item, o, palette, applied, notApplied);
                    if (o.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                    {
                        try { SetMl(item, "Text", tx.GetString()!); applied.Add("Text"); }
                        catch (Exception ex) { notApplied.Add("Text: " + First(ex)); }
                    }
                    if (o.TryGetProperty("move", out var mv)) ApplyMove(item, o, mv, applied, notApplied);
                    if (o.TryGetProperty("bind", out var binds))
                        foreach (var b in binds.EnumerateArray()) ApplyBinding(item, b, palette, applied, notApplied);
                    if (o.TryGetProperty("events", out var evs))
                        foreach (var e in evs.EnumerateArray()) ApplyEvent(item, e, phases, applied, notApplied);
                    created++;
                    report.Add(new { name, type = typeName, applied, notApplied });
                }
                catch (Exception ex)
                {
                    failed++;
                    try { item.Delete(); } catch { }
                    report.Add(new { name, error = First(ex), applied, notApplied });
                }
            }
            _log.LogInformation("Built {Created} items on {Screen} ({Skipped} skipped, {Failed} failed).", created, screenName, skipped, failed);
            return (object)new { screen = screen.Name, dryRun, selected = wanted.Count, created, skipped, failed, report };
        });
    }

    // Geometry: Left/Top/Width/Height where the type has them; circles/ellipses by centre + radius;
    // lines by end points; polylines/polygons by a point list (absolute map points, written relative
    // to Left/Top when the type has Left/Top, otherwise absolute).
    private static void ApplyGeometry(HmiScreenItemBase item, JsonElement o, List<string> applied, List<string> notApplied)
    {
        var r = o.GetProperty("rect").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        int L = r[0], T = r[1], W = r[2], H = r[3];
        var type = item.GetType().Name;
        bool hasLT = item.GetType().GetProperty("Left") != null;
        if (type == "HmiCircle")
        {
            SetAny(item, new[] { "CenterX" }, L + W / 2, applied, notApplied);
            SetAny(item, new[] { "CenterY" }, T + H / 2, applied, notApplied);
            SetAny(item, new[] { "Radius" }, W / 2, applied, notApplied);
        }
        else if (type == "HmiEllipse")
        {
            SetAny(item, new[] { "CenterX" }, L + W / 2, applied, notApplied);
            SetAny(item, new[] { "CenterY" }, T + H / 2, applied, notApplied);
            SetAny(item, new[] { "RadiusX" }, W / 2, applied, notApplied);
            SetAny(item, new[] { "RadiusY" }, H / 2, applied, notApplied);
        }
        if (hasLT && type != "HmiCircle" && type != "HmiEllipse")
        {
            TrySet(item, "Left", L, applied, notApplied); TrySet(item, "Top", T, applied, notApplied);
            if (type != "HmiLine" && type != "HmiPolyline" && type != "HmiPolygon")
            { TrySet(item, "Width", W, applied, notApplied); TrySet(item, "Height", H, applied, notApplied); }
        }
        if (!o.TryGetProperty("points", out var pts)) return;
        var points = pts.EnumerateArray().Select(p => p.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToList();
        if (type == "HmiLine" && points.Count == 2)
        {
            int ox = hasLT ? L : 0, oy = hasLT ? T : 0;
            if (item.GetType().GetProperty("X1") != null)
            {
                TrySet(item, "X1", points[0][0] - ox, applied, notApplied); TrySet(item, "Y1", points[0][1] - oy, applied, notApplied);
                TrySet(item, "X2", points[1][0] - ox, applied, notApplied); TrySet(item, "Y2", points[1][1] - oy, applied, notApplied);
                return;
            }
            if (item.GetType().GetProperty("StartX") != null)
            {
                TrySet(item, "StartX", points[0][0] - ox, applied, notApplied); TrySet(item, "StartY", points[0][1] - oy, applied, notApplied);
                TrySet(item, "EndX", points[1][0] - ox, applied, notApplied); TrySet(item, "EndY", points[1][1] - oy, applied, notApplied);
                return;
            }
        }
        var comp = Get(item, "Points");
        if (comp == null) { notApplied.Add("points: type has no Points/X1/StartX"); return; }
        try
        {
            int ox = hasLT ? L : 0, oy = hasLT ? T : 0;
            var createPt = comp.GetType().GetMethods().Where(m => m.Name == "Create" && !m.IsGenericMethod)
                .OrderBy(m => m.GetParameters().Length).FirstOrDefault()
                ?? throw new InvalidOperationException("Points has no Create method");
            // Remove default points the type may start with.
            foreach (var old in ((System.Collections.IEnumerable)comp).Cast<object>().ToList())
                try { old.GetType().GetMethod("Delete")?.Invoke(old, null); } catch { }
            foreach (var p in points)
            {
                var ps = createPt.GetParameters();
                object pt = ps.Length switch
                {
                    0 => createPt.Invoke(comp, null)!,
                    2 => createPt.Invoke(comp, new object[] { Convert.ChangeType(p[0] - ox, ps[0].ParameterType), Convert.ChangeType(p[1] - oy, ps[1].ParameterType) })!,
                    _ => throw new InvalidOperationException($"Points.Create takes {ps.Length} parameters"),
                };
                if (ps.Length == 0) { SetProp(pt, "X", p[0] - ox); SetProp(pt, "Y", p[1] - oy); }
            }
            applied.Add($"Points[{points.Count}] ({(hasLT ? "relative to Left/Top" : "absolute")})");
        }
        catch (Exception ex) { notApplied.Add("Points: " + First(ex)); }
    }

    private static void ApplyStyle(HmiScreenItemBase item, JsonElement o, Dictionary<string, string> pal, List<string> applied, List<string> notApplied)
    {
        if (!o.TryGetProperty("style", out var s)) return;
        double alpha = s.TryGetProperty("alpha", out var a) ? a.GetDouble() : 1.0;
        bool open = item.GetType().Name is "HmiLine" or "HmiPolyline";
        if (s.TryGetProperty("back", out var back) && !open)
        {
            var bv = back.GetString()!;
            if (bv.Equals("transparent", StringComparison.OrdinalIgnoreCase) && item.GetType().GetProperty("BackFillPattern") != null)
                SetEnumAny(item, new[] { "BackFillPattern" }, new[] { "Transparent" }, applied, notApplied);
            else
                SetAny(item, new[] { "BackColor" }, ParseColor(bv, 1), applied, notApplied);
        }
        if (alpha < 1 && item.GetType().GetProperty("Opacity") != null)
            SetAny(item, new[] { "Opacity" }, (float)alpha, applied, notApplied);
        if (s.TryGetProperty("border", out var border))
            SetAny(item, open ? new[] { "LineColor", "BorderColor" } : new[] { "BorderColor", "LineColor" }, ParseColor(border.GetString()!, 1), applied, notApplied);
        if (s.TryGetProperty("lineWidth", out var lw))
            SetAny(item, new[] { "LineWidth", "BorderWidth" }, lw.GetInt32(), applied, notApplied);
        else if (s.TryGetProperty("borderWidth", out var bw))
            SetAny(item, open ? new[] { "LineWidth", "BorderWidth" } : new[] { "BorderWidth", "LineWidth" }, bw.GetInt32(), applied, notApplied);
        else if (!open && item.GetType().GetProperty("BorderWidth") != null)
            SetAny(item, new[] { "BorderWidth" }, 0, applied, notApplied);
        if (s.TryGetProperty("radius", out var rad) && item.GetType().Name == "HmiRectangle")
        {
            var corners = Get(item, "Corners");
            if (corners != null)
            {
                var sub = new List<string>(); var bad = new List<string>();
                foreach (var c in new[] { "TopLeftRadius", "TopRightRadius", "BottomLeftRadius", "BottomRightRadius" })
                    SetAny(corners, new[] { c }, (uint)rad.GetInt32(), sub, bad);
                if (bad.Count == 0) applied.Add($"Corners={rad.GetInt32()}"); else notApplied.AddRange(bad.Select(x => "Corners." + x));
            }
            else if (item.GetType().GetProperty("CornerRadius") != null) SetAny(item, new[] { "CornerRadius" }, rad.GetInt32(), applied, notApplied);
            else notApplied.Add("radius: no Corners part");
        }
        if (s.TryGetProperty("dash", out var dash) && dash.GetBoolean())
            SetEnumAny(item, new[] { "DashType", "LineDashType", "BorderStyle", "LineStyle" }, new[] { "Dash", "Dashed", "DashDot" }, applied, notApplied);
        if (s.TryGetProperty("color", out var fc))
            SetAny(item, new[] { "ForeColor" }, ParseColor(fc.GetString()!, 1), applied, notApplied);
        if (s.TryGetProperty("align", out var al))
        {
            var v = al.GetString()!; var cand = char.ToUpper(v[0]) + v.Substring(1);
            SetEnumAny(item, new[] { "HorizontalTextAlignment", "TextAlignmentHorizontal", "AlignmentHorizontal", "HorizontalAlignment" }, new[] { cand }, applied, notApplied);
        }
        if (s.TryGetProperty("font", out var font)) ApplyFont(item, font.GetString()!, applied, notApplied);
        if (item.GetType().GetProperty("VerticalTextAlignment") != null)
            SetEnumAny(item, new[] { "VerticalTextAlignment" }, new[] { "Top" }, applied, notApplied);
    }

    // "Siemens Sans 11 bold" -> name, size, weight
    private static void ApplyFont(HmiScreenItemBase item, string spec, List<string> applied, List<string> notApplied)
    {
        var f = Get(item, "Font");
        if (f == null) { notApplied.Add("Font: no Font part"); return; }
        var m = Regex.Match(spec, @"^(.*?)\s+(\d+)(\s+bold)?$", RegexOptions.IgnoreCase);
        if (!m.Success) { notApplied.Add("Font: bad spec " + spec); return; }
        var sub = new List<string>(); var bad = new List<string>();
        var fname = m.Groups[1].Value.Replace(" ", "");
        if (f.GetType().GetProperty("Name")?.PropertyType.IsEnum == true) SetEnumAny(f, new[] { "Name" }, new[] { fname, "SiemensSans" }, sub, bad);
        else SetAny(f, new[] { "Name", "FontName", "Family" }, m.Groups[1].Value, sub, bad);
        SetAny(f, new[] { "Size", "FontSize" }, float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), sub, bad);
        if (m.Groups[3].Success) SetEnumAny(f, new[] { "Weight", "FontWeight" }, new[] { "Bold" }, sub, bad);
        applied.AddRange(sub.Select(x => "Font." + x)); notApplied.AddRange(bad.Select(x => "Font." + x));
    }

    // Bindings: a plain Visible <- T('tag') becomes a tag dynamization; everything else a script
    // dynamization triggered by the listed tags. T('x') -> Tags('x').Read(); C.NAME -> 0xAARRGGBB.
    private static readonly Regex TagRef = new(@"T\('([^']+)'\)", RegexOptions.Compiled);
    private static readonly Regex ColRef = new(@"\bC\.([A-Z_]+)\b", RegexOptions.Compiled);

    private static void ApplyBinding(HmiScreenItemBase item, JsonElement b, Dictionary<string, string> pal, List<string> applied, List<string> notApplied)
    {
        var prop = b.GetProperty("property").GetString()!;
        var expr = b.GetProperty("expr").GetString()!;
        var tags = b.TryGetProperty("tags", out var tg) ? tg.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();
        try
        {
            var single = Regex.Match(expr.Trim(), @"^T\('([^']+)'\)$");
            if (prop == "Visible" && single.Success)
            {
                var td = item.Dynamizations.Create<TagDynamization>("Visible");
                td.Tag = single.Groups[1].Value;
                applied.Add($"Visible<-tag {td.Tag}");
                return;
            }
            var js = TagRef.Replace(expr, m => $"Tags('{m.Groups[1].Value}').Read()");
            js = ColRef.Replace(js, m => pal.TryGetValue(m.Groups[1].Value, out var hex) ? ColorLiteral(hex) : m.Value);
            var sd = item.Dynamizations.Create<ScriptDynamization>(prop);
            sd.ScriptCode = "return " + js + ";";
            var trig = "";
            try
            {
                if (tags.Count > 0)
                {
                    sd.Trigger.Tags = tags.ToArray();
                    sd.Trigger.Type = TriggerType.Tags; trig = "tags";
                }
            }
            catch
            {
                try { sd.Trigger.Type = (TriggerType)Enum.Parse(typeof(TriggerType), "T1s", true); trig = "T1s (tag trigger refused)"; } catch { }
            }
            applied.Add($"{prop}<-script{(trig.Length > 0 ? " [" + trig + "]" : "")}");
        }
        catch (Exception ex) { notApplied.Add($"bind {prop}: {First(ex)}"); }
    }

    // Native movement (no script): the item slides from its map position (tag = start) to map position + (dx, dy) (tag = end).
    private static void ApplyMove(HmiScreenItemBase item, JsonElement o, JsonElement mv, List<string> applied, List<string> notApplied)
    {
        var part = Get(item, "LinearMovement");
        if (part == null) { notApplied.Add("move: no LinearMovement part"); return; }
        var r = o.GetProperty("rect").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var sub = new List<string>(); var bad = new List<string>();
        SetAny(part, new[] { "Tag" }, mv.GetProperty("tag").GetString()!, sub, bad);
        SetAny(part, new[] { "TagRangeStart" }, mv.GetProperty("start").GetDouble(), sub, bad);
        SetAny(part, new[] { "TagRangeEnd" }, mv.GetProperty("end").GetDouble(), sub, bad);
        SetAny(part, new[] { "TargetPositionX" }, r[0] + (mv.TryGetProperty("dx", out var dx) ? dx.GetInt32() : 0), sub, bad);
        SetAny(part, new[] { "TargetPositionY" }, r[1] + (mv.TryGetProperty("dy", out var dy) ? dy.GetInt32() : 0), sub, bad);
        if (bad.Count == 0) applied.Add($"LinearMovement<-{mv.GetProperty("tag").GetString()}"); else notApplied.AddRange(bad.Select(x => "LinearMovement." + x));
    }

    private static void ApplyEvent(HmiScreenItemBase item, JsonElement e, List<int>? phases, List<string> applied, List<string> notApplied)
    {
        var ph = e.TryGetProperty("phase", out var p) ? p.GetInt32() : 1;
        if (phases != null && phases.Count > 0 && !phases.Contains(ph)) return;
        if (!e.TryGetProperty("script", out var sc)) { notApplied.Add($"event {e.GetProperty("event").GetString()}: no script in map"); return; }
        var evt = e.GetProperty("event").GetString()!;
        try
        {
            var handlers = Get(item, "EventHandlers") ?? throw new InvalidOperationException("no EventHandlers");
            var create = handlers.GetType().GetMethods().First(m => m.Name == "Create" && !m.IsGenericMethod && m.GetParameters().Length == 1);
            var pt = create.GetParameters()[0].ParameterType;
            var h = create.Invoke(handlers, new[] { pt.IsEnum ? Enum.Parse(pt, evt, true) : (object)evt })!;
            SetProp(Get(h, "Script")!, "ScriptCode", sc.GetString());
            applied.Add("event " + evt);
        }
        catch (Exception ex) { notApplied.Add($"event {evt}: {First(ex)}"); }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Color ParseColor(string s, double alpha)
    {
        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(0, 0, 0, 0);
        var h = s.TrimStart('#');
        int r = int.Parse(h.Substring(0, 2), NumberStyles.HexNumber), g = int.Parse(h.Substring(2, 2), NumberStyles.HexNumber),
            b = int.Parse(h.Substring(4, 2), NumberStyles.HexNumber);
        return Color.FromArgb((int)Math.Round(255 * Math.Max(0, Math.Min(1, alpha))), r, g, b);
    }

    private static string ColorLiteral(string hex)
    {
        var c = ParseColor(hex, 1);
        return "0x" + c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
    }

    private static void TrySet(object o, string prop, object value, List<string> applied, List<string> notApplied)
    {
        try { SetProp(o, prop, value); applied.Add(prop); }
        catch (Exception ex) { notApplied.Add($"{prop}: {First(ex)}"); }
    }

    private static void SetAny(object o, string[] props, object value, List<string> applied, List<string> notApplied)
    {
        foreach (var p in props)
            if (o.GetType().GetProperty(p) is { CanWrite: true }) { TrySet(o, p, value, applied, notApplied); return; }
        notApplied.Add($"{string.Join("/", props)}: not on {o.GetType().Name}");
    }

    private static void SetEnumAny(object o, string[] props, string[] values, List<string> applied, List<string> notApplied)
    {
        foreach (var p in props)
        {
            var pi = o.GetType().GetProperty(p);
            if (pi is not { CanWrite: true } || !pi.PropertyType.IsEnum) continue;
            foreach (var v in values)
                if (Enum.GetNames(pi.PropertyType).FirstOrDefault(n => n.Equals(v, StringComparison.OrdinalIgnoreCase)) is { } hit)
                { TrySet(o, p, Enum.Parse(pi.PropertyType, hit), applied, notApplied); return; }
            notApplied.Add($"{p}: none of {string.Join("/", values)} in {string.Join(",", Enum.GetNames(pi.PropertyType))}");
            return;
        }
        notApplied.Add($"{string.Join("/", props)}: not on {o.GetType().Name}");
    }

    private static object? Get(object o, string name)
    {
        try { return o.GetType().GetProperty(name)?.GetValue(o); } catch { return null; }
    }

    private static void SetProp(object o, string name, object? value)
    {
        var p = o.GetType().GetProperty(name) ?? throw new InvalidOperationException($"{o.GetType().Name} has no property '{name}'.");
        p.SetValue(o, value is IConvertible && value.GetType() != p.PropertyType && !p.PropertyType.IsEnum
            ? Convert.ChangeType(value, p.PropertyType, CultureInfo.InvariantCulture) : value);
    }

    private static void SetMl(object item, string prop, string text)
    {
        var mt = Get(item, prop) as MultilingualText
                 ?? throw new InvalidOperationException($"{item.GetType().Name} has no multilingual property '{prop}'.");
        var items = mt.Items.Cast<MultilingualTextItem>().ToList();
        if (items.Count == 0) throw new InvalidOperationException($"'{prop}' has no language entries to write.");
        foreach (var i in items) i.Text = text;   // a new item: same text in every project language
    }

    private static string First(Exception e) => (e.InnerException?.Message ?? e.Message).Split('\n')[0];

    private HmiSoftware GetHmi(string deviceName)
    {
        foreach (Device device in _tia.Project!.Devices)
        {
            if (!device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (DeviceItem di in device.DeviceItems)
                if (di.GetService<SoftwareContainer>()?.Software is HmiSoftware sw) return sw;
        }
        throw new KeyNotFoundException($"No WinCC Unified HMI software found for device '{deviceName}'.");
    }

    private static HmiScreen? TryFindScreen(HmiSoftware hmi, string name)
    {
        foreach (HmiScreen s in hmi.Screens)
            if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return s;
        foreach (HmiScreenGroup g in hmi.ScreenGroups)
            foreach (HmiScreen s in g.Screens)
                if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    private static HmiScreenItemBase? Find(HmiScreenItemBaseComposition comp, string name)
    {
        foreach (HmiScreenItemBase i in comp)
            if (i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }
}
