using System.Collections;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Services;

/// <summary>
/// Generic, reflection-based access to the Openness object model by path. A path is a
/// '/'-separated list of property names and composition item names, starting at the project:
///   Devices/HMI_1/DeviceItems/[0]          ([n] = index into a composition)
///   hmi:HMI_1/Screens/Beacon_Control/ScreenItems/IO_Run   (hmi:&lt;device&gt; = the device's HmiSoftware)
///   plc:PLC_1/BlockGroup/Blocks/Main       (plc:&lt;device&gt; = the device's PlcSoftware)
///   Devices/PLC_1/DeviceItems/[1]/@Software
/// Discovery uses .NET reflection, never GetAttributeInfos(): that call crashed TIA on an HmiTag.
/// </summary>
public sealed class ObjectBrowserService
{
    private readonly TiaPortalService _tia;
    private readonly StaTaskScheduler _sta;
    private readonly ILogger<ObjectBrowserService> _log;

    public ObjectBrowserService(TiaPortalService tia, StaTaskScheduler sta, ILogger<ObjectBrowserService> log)
    { _tia = tia; _sta = sta; _log = log; }

    // ── describe ──────────────────────────────────────────────────────────────

    public async Task<object> DescribeAsync(string path)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var obj = Resolve(path);
            var type = obj.GetType();
            var values = new List<object>();
            var compositions = new List<object>();
            var children = new List<object>();

            foreach (var p in Props(type))
            {
                object? v;
                try { v = p.GetValue(obj); }
                catch (Exception ex) { values.Add(new { name = p.Name, type = Short(p.PropertyType), error = Inner(ex) }); continue; }

                if (v is null) { values.Add(new { name = p.Name, type = Short(p.PropertyType), value = (string?)null, writable = p.CanWrite }); continue; }
                if (IsSimple(v.GetType()))
                    values.Add(new { name = p.Name, type = Short(p.PropertyType), value = Fmt(v), writable = p.CanWrite });
                else if (v is IEnumerable en && v is not string)
                    compositions.Add(new { name = p.Name, type = Short(v.GetType()), items = Names(en, 60, out var count), count });
                else
                    children.Add(new { name = p.Name, type = Short(v.GetType()) });
            }
            return (object)new { path, type = type.Name, values, compositions, children };
        });
    }

    // ── get ───────────────────────────────────────────────────────────────────

    public async Task<object> GetAsync(string path, List<string> names)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var obj = Resolve(path);
            var result = new Dictionary<string, object?>();
            foreach (var n in names)
            {
                try
                {
                    var p = FindProp(obj.GetType(), n);
                    object? v = p is not null ? p.GetValue(obj) : (obj as IEngineeringObject)?.GetAttribute(n);
                    result[n] = v is null ? null : IsSimple(v.GetType()) ? Fmt(v) : "(" + Short(v.GetType()) + ")";
                }
                catch (Exception ex) { result[n] = "ERROR: " + Inner(ex); }
            }
            return (object)new { path, values = result };
        });
    }

    // ── set ───────────────────────────────────────────────────────────────────

    public async Task<object> SetAsync(string path, Dictionary<string, object?> attributes)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var obj = Resolve(path);
            var results = new List<object>();
            foreach (var kv in attributes)
            {
                try
                {
                    var p = FindProp(obj.GetType(), kv.Key);
                    if (p is not null && p.CanWrite)
                    {
                        p.SetValue(obj, Convert(kv.Value, p.PropertyType));
                    }
                    else if (obj is IEngineeringObject eo && obj.GetType().Name.StartsWith("HmiTag", StringComparison.Ordinal))
                    {
                        // SetAttribute is only used on HmiTag (live-verified). On other Unified objects it can throw
                        // NonRecoverableException and crash TIA, so those need a real writable property.
                        var cur = p is not null ? p.PropertyType : null;
                        eo.SetAttribute(kv.Key, cur is not null ? Convert(kv.Value, cur) : Plain(kv.Value));
                    }
                    else throw new InvalidOperationException($"'{kv.Key}' is not writable on {obj.GetType().Name}.");
                    object? back = null;
                    try { back = p?.GetValue(obj); } catch { }
                    results.Add(new { name = kv.Key, status = "set", value = back is not null && IsSimple(back.GetType()) ? Fmt(back) : null });
                }
                catch (Exception ex) { results.Add(new { name = kv.Key, status = "failed", error = Inner(ex) }); }
            }
            return (object)new { path, results };
        });
    }

    // ── create ────────────────────────────────────────────────────────────────

    public async Task<object> CreateAsync(string parentPath, string name)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var parent = Resolve(parentPath);
            var m = parent.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => x.Name == "Create" && !x.IsGenericMethodDefinition
                                     && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(string))
                ?? throw new NotSupportedException(
                    $"{parent.GetType().Name} has no Create(string). Use a dedicated tool (e.g. build_hmi_screen for screen items).");
            var created = m.Invoke(parent, new object[] { name });
            return (object)new { parentPath, name, type = created?.GetType().Name, status = "created" };
        });
    }

    // ── delete ────────────────────────────────────────────────────────────────

    public async Task<object> DeleteAsync(string path)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var obj = Resolve(path);
            var m = obj.GetType().GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                ?? throw new NotSupportedException($"{obj.GetType().Name} cannot be deleted (no Delete()).");
            try { m.Invoke(obj, null); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
            return (object)new { path, status = "deleted" };
        });
    }

    // ── path resolution ───────────────────────────────────────────────────────

    private object Resolve(string path)
    {
        object cur = _tia.Project ?? throw new InvalidOperationException("No project open.");
        var segs = (path ?? "").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length; i++)
        {
            var seg = segs[i].Trim();
            if (i == 0 && (seg.StartsWith("hmi:", StringComparison.OrdinalIgnoreCase) || seg.StartsWith("plc:", StringComparison.OrdinalIgnoreCase)))
            { cur = SoftwareOf(seg.Substring(4), seg.StartsWith("hmi:", StringComparison.OrdinalIgnoreCase)); continue; }

            if (seg.Equals("@Software", StringComparison.OrdinalIgnoreCase))
            { cur = SoftwareOfItem(cur); continue; }

            if (cur is IEnumerable en && cur is not string)
            {
                if (seg.StartsWith("[") && seg.EndsWith("]") && int.TryParse(seg.Substring(1, seg.Length - 2), out var idx))
                {
                    int k = 0; object? hit = null;
                    foreach (var x in en) { if (k++ == idx) { hit = x; break; } }
                    cur = hit ?? throw new KeyNotFoundException($"Index {idx} out of range at '{seg}'.");
                }
                else
                {
                    object? hit = null;
                    foreach (var x in en)
                        if (x is not null && string.Equals(NameOf(x), seg, StringComparison.OrdinalIgnoreCase)) { hit = x; break; }
                    cur = hit ?? throw new KeyNotFoundException($"No item named '{seg}' (items: {string.Join(", ", Names(en, 25, out _))}).");
                }
                continue;
            }

            var p = FindProp(cur.GetType(), seg)
                ?? throw new KeyNotFoundException($"'{seg}' is not a property of {cur.GetType().Name} (use describe_object to list them).");
            cur = p.GetValue(cur) ?? throw new KeyNotFoundException($"'{seg}' is null on {cur.GetType().Name}.");
        }
        return cur;
    }

    private object SoftwareOf(string deviceName, bool hmi)
    {
        foreach (Device d in _tia.Project!.Devices)
        {
            if (!d.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
            var sw = FindSoftware(d.DeviceItems, hmi);
            if (sw is not null) return sw;
        }
        throw new KeyNotFoundException($"No {(hmi ? "HMI" : "PLC")} software on device '{deviceName}'.");
    }

    private static object? FindSoftware(DeviceItemComposition items, bool hmi)
    {
        foreach (DeviceItem di in items)
        {
            var sw = di.GetService<SoftwareContainer>()?.Software;
            if (sw is not null)
            {
                var isHmi = sw.GetType().Namespace?.Contains("Hmi") == true;
                if (isHmi == hmi) return sw;
            }
            var inner = FindSoftware(di.DeviceItems, hmi);
            if (inner is not null) return inner;
        }
        return null;
    }

    private static object SoftwareOfItem(object cur)
    {
        if (cur is DeviceItem di)
            return di.GetService<SoftwareContainer>()?.Software
                ?? throw new KeyNotFoundException($"Device item '{di.Name}' hosts no software.");
        if (cur is Device d)
            return FindSoftware(d.DeviceItems, true) ?? FindSoftware(d.DeviceItems, false)
                ?? throw new KeyNotFoundException($"Device '{d.Name}' hosts no software.");
        throw new InvalidOperationException("@Software only applies to a Device or DeviceItem.");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<PropertyInfo> Props(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
         .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "Parent" && p.DeclaringType != typeof(object))
         .OrderBy(p => p.Name);

    private static PropertyInfo? FindProp(Type t, string name) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
         .FirstOrDefault(p => p.GetIndexParameters().Length == 0 && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? NameOf(object o)
    {
        try { return o.GetType().GetProperty("Name")?.GetValue(o)?.ToString(); } catch { return null; }
    }

    private static List<string> Names(IEnumerable en, int max, out int count)
    {
        var list = new List<string>(); count = 0;
        foreach (var x in en)
        {
            count++;
            if (list.Count < max) list.Add(x is null ? "(null)" : NameOf(x) ?? x.GetType().Name);
        }
        return list;
    }

    private static bool IsSimple(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)
        || t == typeof(System.Drawing.Color) || t == typeof(Guid);

    private static string Fmt(object v) => v switch
    {
        System.Drawing.Color c => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string Short(Type t) => t.IsGenericType ? t.Name.Split('`')[0] : t.Name;

    private static string Inner(Exception ex)
    {
        while (ex is TargetInvocationException && ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message.Replace("\r", "").Split('\n')[0];
    }

    private static object? Plain(object? v)
    {
        if (v is System.Text.Json.JsonElement je)
            return je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => je.GetString(),
                System.Text.Json.JsonValueKind.Number => je.TryGetInt64(out var l) ? (l >= int.MinValue && l <= int.MaxValue ? (object)(int)l : l) : je.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                _ => je.ToString(),
            };
        return v;
    }

    private static object? Convert(object? raw, Type target)
    {
        var v = Plain(raw);
        var t = Nullable.GetUnderlyingType(target) ?? target;
        if (v is null) return null;
        if (t.IsInstanceOfType(v)) return v;
        var s = System.Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        if (t.IsEnum) return Enum.Parse(t, s, true);
        if (t == typeof(System.Drawing.Color)) return s.StartsWith("#") ? System.Drawing.ColorTranslator.FromHtml(s) : System.Drawing.Color.FromName(s);
        if (t == typeof(string)) return s;
        if (t == typeof(bool)) return bool.Parse(s);
        if (t == typeof(Guid)) return Guid.Parse(s);
        return System.Convert.ChangeType(s, t, CultureInfo.InvariantCulture);
    }
}
