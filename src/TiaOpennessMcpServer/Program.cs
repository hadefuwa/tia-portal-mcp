using System.Diagnostics;
using System.Net;
using System.Windows.Forms;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TiaOpennessMcpServer;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Services;
using TiaOpennessMcpServer.Utilities;

// ── Assembly resolver — must run before any Siemens type is referenced ────────
string[] TiaSearchPaths = new[]
{
    @"C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20",
    @"C:\Program Files\Siemens\Automation\Portal V20\Bin\PublicAPI",
    @"C:\Program Files\Siemens\Automation\Portal V20\Bin\PublicAPI\Client",
    @"C:\Program Files\Siemens\Automation\Portal V20\Bin",
};
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var n = new AssemblyName(e.Name).Name!;
    foreach (var dir in TiaSearchPaths)
    {
        var p = Path.Combine(dir, n + ".dll");
        if (File.Exists(p)) return Assembly.LoadFrom(p);
    }
    return null;
};

// ── DI setup ──────────────────────────────────────────────────────────────────
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.Configure<TiaOpennessOptions>(_ => { });
services.AddSingleton<StaTaskScheduler>();
services.AddSingleton<TiaPortalService>();
services.AddSingleton<HardwareService>();
services.AddSingleton<SoftwareService>();
services.AddSingleton<SclAnalyzerService>();
services.AddSingleton<TagService>();

var sp     = services.BuildServiceProvider();
var tia    = sp.GetRequiredService<TiaPortalService>();
var hw     = sp.GetRequiredService<HardwareService>();
var sw     = sp.GetRequiredService<SoftwareService>();
var scl    = sp.GetRequiredService<SclAnalyzerService>();
var tagSvc = sp.GetRequiredService<TagService>();

// ── HTTP listener ─────────────────────────────────────────────────────────────
var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:5000/");
listener.Start();

Console.CancelKeyPress += (_, e) => { e.Cancel = true; listener.Stop(); };

// Launch the WinForms window on a dedicated STA thread (required by WinForms/COM)
var uiThread = new System.Threading.Thread(() =>
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new MainForm());
    listener.Stop(); // stop the HTTP loop when the window is closed via tray "Exit"
});
uiThread.SetApartmentState(System.Threading.ApartmentState.STA);
uiThread.IsBackground = false;
uiThread.Start();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented               = false,
};
jsonOpts.Converters.Add(new JsonStringEnumConverter());

while (listener.IsListening)
{
    HttpListenerContext ctx;
    try   { ctx = await listener.GetContextAsync(); }
    catch { break; }
    _ = Task.Run(() => HandleAsync(ctx));
}

sp.Dispose();

// ── Request dispatcher ────────────────────────────────────────────────────────

async Task HandleAsync(HttpListenerContext ctx)
{
    var req = ctx.Request;
    var res = ctx.Response;
    res.AddHeader("Access-Control-Allow-Origin",  "*");
    res.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
    res.AddHeader("Access-Control-Allow-Headers", "Content-Type");

    if (req.HttpMethod == "OPTIONS")
    {
        res.StatusCode = 200;
        res.Close();
        return;
    }

    var path   = req.Url?.AbsolutePath.TrimEnd('/') ?? "/";
    if (path == "") path = "/";
    var method = req.HttpMethod;

    Dictionary<string, string> m;

    try
    {
        // ── Static file ───────────────────────────────────────────────────────
        if (method == "GET" && path == "/")
        {
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "dashboard.html");
            var html     = File.Exists(htmlPath)
                ? File.ReadAllText(htmlPath)
                : "<h1>dashboard.html not found next to the exe.</h1>";
            await WriteBytes(res, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
        }

        // ── Status ────────────────────────────────────────────────────────────
        else if (method == "GET" && path == "/api/status")
        {
            if (!tia.IsConnected) { await Json(res, new { connected = false }); return; }
            try { await Json(res, new { connected = true, project = await tia.GetProjectInfoAsync() }); }
            catch (Exception ex) { await Json(res, new { connected = true, error = ex.Message }); }
        }

        // ── Connect ───────────────────────────────────────────────────────────
        else if (method == "POST" && path == "/api/connect")
        {
            try   { await Json(res, await tia.AttachToRunningAsync()); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Devices ───────────────────────────────────────────────────────────
        else if (method == "GET" && path == "/api/devices")
        {
            try   { await Json(res, await hw.GetDevicesAsync()); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Blocks list ───────────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/blocks", out m))
        {
            try   { await Json(res, await sw.ListBlocksAsync(m["device"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block read ────────────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/blocks/{block}", out m))
        {
            try   { await Json(res, await sw.ReadBlockAsync(m["device"], m["block"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block create ──────────────────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/blocks", out m))
        {
            try
            {
                var body = await ReadJson<BlockCreateRequest>(req);
                if (body is null) { await Json(res, new { error = "Request body required." }, 400); return; }
                await Json(res, await sw.CreateBlockAsync(m["device"], body));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block SCL write ───────────────────────────────────────────────────
        else if (method == "PUT" && TryMatch(path, "/api/devices/{device}/blocks/{block}/scl", out m))
        {
            try
            {
                var body = await ReadJson<SclWriteRequest>(req);
                await sw.WriteBlockSclAsync(m["device"], m["block"], body?.Source ?? "");
                await Json(res, new { success = true });
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block compile ─────────────────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/blocks/{block}/xml", out m))
        {
            try
            {
                var body = await ReadJson<XmlWriteRequest>(req);
                await sw.WriteBlockXmlAsync(m["device"], m["block"], body?.Content ?? "");
                await Json(res, new { success = true });
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/blocks/{block}/compile", out m))
        {
            try   { await Json(res, new { result = await sw.CompileBlockAsync(m["device"], m["block"]) }); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block analyze ─────────────────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/blocks/{block}/analyze", out m))
        {
            try
            {
                var content = await sw.ReadBlockAsync(m["device"], m["block"]);
                if (string.IsNullOrWhiteSpace(content.SourceCode))
                { await Json(res, new { error = "Block is not SCL or source could not be read." }); return; }
                await Json(res, await scl.AnalyzeAsync(content.SourceCode, m["block"], content.Type.ToString()));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Tag tables ────────────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/tags", out m))
        {
            try   { await Json(res, await tagSvc.GetTagTablesAsync(m["device"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Tags in table ─────────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/tags/{table}", out m))
        {
            try   { await Json(res, await tagSvc.GetTagsAsync(m["device"], m["table"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Save project ──────────────────────────────────────────────────────
        // ── Clone project ─────────────────────────────────────────────────────────
        else if (method == "POST" && path == "/api/project/clone")
        {
            try
            {
                var body = await ReadJson<CloneRequest>(req);
                if (string.IsNullOrWhiteSpace(body?.Name) || string.IsNullOrWhiteSpace(body?.Path))
                { await Json(res, new { error = "name and path are required." }); return; }
                await Json(res, await tia.CloneProjectAsync(body.Name, body.Path));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Option packages ───────────────────────────────────────────────────────
        else if (method == "GET" && path == "/api/project/options")
        {
            try   { await Json(res, await tia.GetOptionPackagesAsync()); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        else if (method == "POST" && path == "/api/project/save")
        {
            try   { await tia.SaveAsync(); await Json(res, new { success = true }); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Standalone SCL analysis ───────────────────────────────────────────
        else if (method == "POST" && path == "/api/analyze")
        {
            try
            {
                var body = await ReadJson<SclAnalyzeRequest>(req);
                await Json(res, await scl.AnalyzeAsync(
                    body?.Source ?? "", body?.BlockName ?? "Block", body?.BlockType ?? "FB"));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        else
        {
            await Json(res, new { error = "Not found" }, 404);
        }
    }
    catch (Exception ex)
    {
        try { await Json(res, new { error = ex.Message }, 500); } catch { }
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

async Task Json(HttpListenerResponse res, object? data, int status = 200)
{
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, jsonOpts));
    res.StatusCode = status;
    res.ContentType = "application/json; charset=utf-8";
    await WriteBytes(res, bytes, res.ContentType);
}

async Task WriteBytes(HttpListenerResponse res, byte[] bytes, string contentType)
{
    res.ContentType     = contentType;
    res.ContentLength64 = bytes.Length;
    try
    {
        await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        res.Close();
    }
    catch { }
}

async Task<T?> ReadJson<T>(HttpListenerRequest req) where T : class
{
    using var reader = new System.IO.StreamReader(req.InputStream, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return null;
    return JsonSerializer.Deserialize<T>(body, jsonOpts);
}

bool TryMatch(string path, string pattern, out Dictionary<string, string> vars)
{
    vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var ps = path.Split('/');
    var pp = pattern.Split('/');
    if (ps.Length != pp.Length) return false;
    for (int i = 0; i < pp.Length; i++)
    {
        if (pp[i].StartsWith("{") && pp[i].EndsWith("}"))
            vars[pp[i].Substring(1, pp[i].Length - 2)] = Uri.UnescapeDataString(ps[i]);
        else if (!string.Equals(ps[i], pp[i], StringComparison.OrdinalIgnoreCase))
            return false;
    }
    return true;
}

// ── Request body DTOs ─────────────────────────────────────────────────────────

class SclWriteRequest   { public string Source    { get; set; } = ""; }
class SclAnalyzeRequest { public string Source    { get; set; } = "";
                          public string BlockName { get; set; } = "Block";
                          public string BlockType { get; set; } = "FB"; }
class XmlWriteRequest   { public string Content   { get; set; } = ""; }
class CloneRequest      { public string Name      { get; set; } = ""; public string Path { get; set; } = ""; }
