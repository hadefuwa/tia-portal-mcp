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

// ── Command line ──────────────────────────────────────────────────────────────
bool stdioMode = Array.IndexOf(args, "--mcp-stdio") >= 0;

string? ArgValue(string flag)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// --project <path> opens a project at startup, so a session does not have to
// spend its first tool call connecting. Headless unless --with-ui is passed.
string? startupProject = ArgValue("--project");
bool    startupWithUi  = Array.IndexOf(args, "--with-ui") >= 0;

// --profile lite|standard|full trims the advertised tool surface. 38 tool
// definitions is a lot of context to spend before the model has done anything;
// a session that only reads and edits SCL needs ten of them.
string mcpProfile = (ArgValue("--profile")
                     ?? Environment.GetEnvironmentVariable("TIA_MCP_PROFILE")
                     ?? "full").Trim().ToLowerInvariant();

var liteTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "connect_to_tia_portal", "get_status", "save_project", "list_devices",
    "list_blocks", "read_block", "write_block_scl", "compile_block",
    "list_tag_tables", "get_tags",
};

// standard = lite + everything that edits code, tags and HMI content.
// Excluded from standard, present in full: clone_project, get_option_packages,
// open_project, close_project, generate_s7_1200 — project-lifecycle and
// hardware-generation tools that most sessions never touch.
var standardTools = new HashSet<string>(liteTools, StringComparer.OrdinalIgnoreCase)
{
    "analyze_block", "analyze_scl", "create_block", "create_instance_db",
    "import_block_xml", "create_lad_block", "import_tag_table", "batch_rename_tags",
    "get_project_signature", "get_block_attributes", "patch_block_texts",
    "create_tag_table", "create_tag", "export_block", "export_tag_table",
    "get_device", "get_io_mapping",
    "list_hmi_tag_tables", "get_hmi_tags", "get_all_hmi_tags", "create_hmi_tags",
    "list_hmi_screens", "get_screen_tag_refs", "update_faceplate_tags",
};

// readonly = look, never edit. Opt-in (--profile readonly); the default profile
// is unchanged. compile_block and connect_to_tia_portal are included because
// neither alters project content.
var readonlyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "connect_to_tia_portal", "get_status", "list_devices", "list_blocks",
    "read_block", "compile_block", "analyze_block", "analyze_scl",
    "list_tag_tables", "get_tags", "get_device", "get_io_mapping",
    "get_project_signature", "get_block_attributes", "get_option_packages",
    "list_hmi_tag_tables", "get_hmi_tags", "get_all_hmi_tags",
    "list_hmi_screens", "get_screen_tag_refs",
};

// Tools that only read the project. Used for the MCP readOnlyHint annotation.
var readOnlyHintTools = new HashSet<string>(readonlyTools, StringComparer.OrdinalIgnoreCase);
readOnlyHintTools.ExceptWith(new[] { "connect_to_tia_portal", "compile_block" });
readOnlyHintTools.UnionWith(new[] { "export_block", "export_tag_table" }); // write files, not the project

// Tools that overwrite existing project content. Used for destructiveHint.
var destructiveHintTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "write_block_scl", "import_block_xml", "import_tag_table", "patch_block_texts",
    "batch_rename_tags", "update_faceplate_tags", "close_project",
};

// --annotations (or TIA_MCP_ANNOTATIONS=1) adds MCP tool annotations to
// tools/list so clients can prompt only on writes. Off by default: the
// advertised tool definitions are byte-for-byte what they were before.
bool mcpAnnotations = Array.IndexOf(args, "--annotations") >= 0
                      || Environment.GetEnvironmentVariable("TIA_MCP_ANNOTATIONS") == "1";

bool InProfile(string tool) =>
    mcpProfile == "readonly" ? readonlyTools.Contains(tool)
  : mcpProfile == "lite"     ? liteTools.Contains(tool)
  : mcpProfile == "standard" ? standardTools.Contains(tool)
  : true;

// ── DI setup ──────────────────────────────────────────────────────────────────
var services = new ServiceCollection();
services.AddLogging(b => { if (!stdioMode) b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); });
services.Configure<TiaOpennessOptions>(_ => { });
services.AddSingleton<StaTaskScheduler>();
services.AddSingleton<TiaPortalService>();
services.AddSingleton<HardwareService>();
services.AddSingleton<SoftwareService>();
services.AddSingleton<SclAnalyzerService>();
services.AddSingleton<TagService>();
services.AddSingleton<HmiTagService>();
services.AddSingleton<HmiScreenService>();

var sp      = services.BuildServiceProvider();
var tia     = sp.GetRequiredService<TiaPortalService>();
var hw      = sp.GetRequiredService<HardwareService>();
var sw      = sp.GetRequiredService<SoftwareService>();
var scl     = sp.GetRequiredService<SclAnalyzerService>();
var tagSvc  = sp.GetRequiredService<TagService>();
var hmiSvc      = sp.GetRequiredService<HmiTagService>();
var hmiScreenSvc = sp.GetRequiredService<HmiScreenService>();

var mcpLog  = new List<McpLogEntry>();
var mcpLock = new object();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented               = false,
};
jsonOpts.Converters.Add(new JsonStringEnumConverter());

// ── Startup project (--project) ───────────────────────────────────────────────
// Never fatal: if the project cannot be opened the server still starts, so the
// session can connect_to_tia_portal or open_project by hand and see the reason.
// Diagnostics go to stderr — stdout carries JSON-RPC frames and nothing else.
async Task OpenStartupProjectAsync()
{
    if (string.IsNullOrWhiteSpace(startupProject)) return;
    try
    {
        var info = await tia.OpenProjectAsync(startupProject!, headless: !startupWithUi);
        Console.Error.WriteLine($"[tia-mcp] Opened project '{info.Name}' from {startupProject}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[tia-mcp] --project failed: {ex.Message.Split('\n')[0]}");
    }
}

// ── Stdio MCP mode ────────────────────────────────────────────────────────────
if (stdioMode)
{
    await OpenStartupProjectAsync();
    await RunStdioAsync();
    sp.Dispose();
    return;
}

await OpenStartupProjectAsync();

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

        // ── LAD block from structured description ────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/blocks/lad", out m))
        {
            try
            {
                var body = await ReadJson<LadBlockRequest>(req);
                if (body is null) { await Json(res, new { error = "Request body required." }, 400); return; }
                await Json(res, await sw.CreateLadBlockAsync(m["device"], body));
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

        // ── Block delete ──────────────────────────────────────────────────────
        else if (method == "DELETE" && TryMatch(path, "/api/devices/{device}/blocks/{block}", out m))
        {
            try   { await sw.DeleteBlockAsync(m["device"], m["block"]); await Json(res, new { success = true }); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block attribute diagnostics ───────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/blocks/{block}/attributes", out m))
        {
            try   { await Json(res, await sw.GetBlockAttributeInfosAsync(m["device"], m["block"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block texts (direct property patch — works on OBs) ────────────────
        else if (method == "PATCH" && TryMatch(path, "/api/devices/{device}/blocks/{block}/texts", out m))
        {
            try
            {
                var body = await ReadJson<BlockTextsRequest>(req);
                if (body is null) { await Json(res, new { error = "body required" }, 400); return; }
                await sw.PatchBlockTextsAsync(m["device"], m["block"], body);
                await Json(res, new { success = true });
            }
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

        // ── Create instance DB ────────────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/blocks/instance-db", out m))
        {
            try
            {
                var body = await ReadJson<InstanceDbCreateRequest>(req);
                if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.InstanceOfName))
                { await Json(res, new { error = "name and instanceOfName are required." }, 400); return; }
                await Json(res, await sw.CreateInstanceDbAsync(m["device"], body.Name, body.InstanceOfName, body.Number));
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

        // ── Import tag table (XML content) ───────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/tags/import", out m))
        {
            try
            {
                var body = await ReadJson<TagImportRequest>(req);
                if (body is null || string.IsNullOrWhiteSpace(body.Content))
                { await Json(res, new { error = "content is required." }, 400); return; }
                await tagSvc.ImportTagTableFromContentAsync(m["device"], body.Content);
                await Json(res, new { success = true });
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI tag tables (WinCC Unified) ───────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/hmi/tags", out m))
        {
            try   { await Json(res, await hmiSvc.ListTagTablesAsync(m["device"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI all tags (flat) ───────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/hmi/tags/all", out m))
        {
            try   { await Json(res, await hmiSvc.GetAllTagsAsync(m["device"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI tags in table ─────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/hmi/tags/{table}", out m))
        {
            try   { await Json(res, await hmiSvc.GetTagsAsync(m["device"], m["table"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI create tags in table ──────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/hmi/tags/{table}/create", out m))
        {
            try
            {
                var body = await ReadJson<List<HmiTagCreateRequest>>(req);
                if (body is null || body.Count == 0) { await Json(res, new { error = "body required: array of {name, dataType, plcTag}" }, 400); return; }
                await Json(res, await hmiSvc.CreateTagsAsync(m["device"], m["table"], body));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI screens — list ────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/hmi/screens", out m))
        {
            try   { await Json(res, await hmiScreenSvc.ListScreensAsync(m["device"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI screens — update faceplate interface parameters ───────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/hmi/screens/{screen}/update-faceplate-tags", out m))
        {
            try
            {
                var body = await ReadJson<List<FaceplateTagUpdate>>(req);
                if (body is null || body.Count == 0) { await Json(res, new { error = "body required: array of {containerName, parameterName, newValue}" }, 400); return; }
                await Json(res, await hmiScreenSvc.UpdateFaceplateTagsAsync(m["device"], m["screen"], body));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI screens — scan all screens for tag refs (optional filter) ───
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/hmi/screens/scan", out m))
        {
            var filter = req.QueryString["filter"];
            try   { await Json(res, await hmiScreenSvc.ScanAllTagRefsAsync(m["device"], filter)); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI screens — replace tag pattern across all screens ─────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/hmi/screens/replace-tag-pattern", out m))
        {
            try
            {
                var body = await ReadJson<Dictionary<string, string>>(req);
                if (body is null || !body.ContainsKey("pattern") || !body.ContainsKey("replacement"))
                { await Json(res, new { error = "body required: {pattern, replacement}" }, 400); return; }
                await Json(res, await hmiScreenSvc.ReplaceTagPatternAsync(m["device"], body["pattern"], body["replacement"]));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI screens — tag dynamizations in a screen (read only) ─────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/hmi/screens/{screen}/tags", out m))
        {
            try   { await Json(res, await hmiScreenSvc.GetScreenTagRefsAsync(m["device"], m["screen"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Batch rename tags ─────────────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/tags/{table}/rename", out m))
        {
            try
            {
                var body = await ReadJson<TagBatchRenameRequest>(req);
                if (body is null || body.Renames.Count == 0)
                { await Json(res, new { error = "renames list is required." }, 400); return; }
                var count = await tagSvc.BatchRenameTagsAsync(m["device"], m["table"], body.Renames);
                await Json(res, new { renamed = count });
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Project signature ─────────────────────────────────────────────────
        else if (method == "GET" && path == "/api/project/signature")
        {
            try   { await Json(res, await tia.GetProjectSignatureAsync()); }
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

        // ── MCP endpoint info (GET) ───────────────────────────────────────────────
        else if (method == "GET" && path == "/mcp")
        {
            // Return a recognisable MCP error so clients detect the modern Streamable HTTP
            // transport and don't fall back to the old HTTP+SSE discovery flow.
            res.StatusCode = 405;
            await Json(res, new {
                jsonrpc = "2.0", id = (object?)null,
                error   = new { code = -32601, message = "MCP endpoint requires POST. Server: tia-portal-openness v1.0.0, protocol: 2025-03-26" }
            }, 405);
        }

        // ── MCP JSON-RPC 2.0 (Streamable HTTP) ───────────────────────────────────
        else if (method == "POST" && path == "/mcp")
        {
            try
            {
                var body = await ReadJson<McpRpcRequest>(req);
                if (body is null)
                { await Json(res, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }); return; }

                // Notifications have no id — acknowledge and return
                if (body.Id is null && (body.Method?.StartsWith("notifications/") ?? false))
                { res.StatusCode = 202; res.Close(); return; }

                var (result, rpcErr) = await HandleMcpRequest(body);
                if (rpcErr != null)
                    await Json(res, new { jsonrpc = "2.0", id = body.Id, error = rpcErr });
                else
                    await Json(res, new { jsonrpc = "2.0", id = body.Id, result });
            }
            catch (Exception ex) { try { await Json(res, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32603, message = ex.Message } }, 500); } catch { } }
        }

        // ── MCP call log ──────────────────────────────────────────────────────────
        else if (method == "GET" && path == "/api/mcp/log")
        {
            List<McpLogEntry> snapshot;
            lock (mcpLock) { snapshot = mcpLog.Take(50).ToList(); }
            await Json(res, snapshot);
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

async Task<object?> McpDispatch(JsonElement p)
{
    string name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
    JsonElement? args = p.TryGetProperty("arguments", out var a) ? a : (JsonElement?)null;

    // Enforce the profile here too, not just in tools/list — a tool that was
    // never advertised must not be callable by a model that guessed its name.
    if (!InProfile(name))
        throw new InvalidOperationException(
            $"Tool '{name}' is not available in the '{mcpProfile}' profile. " +
            "Restart the server with --profile full to enable it.");

    // ── Argument accessors ────────────────────────────────────────────────────
    // Models do not reliably honour the declared JSON type — a param described as
    // an integer arrives as 5 about as often as "5". Every accessor below is
    // total: it coerces what it can and falls back to the default rather than
    // throwing, so a type mismatch can never take down the dispatch.

    bool TryArg(string key, out JsonElement v)
    {
        v = default;
        if (!args.HasValue || !args.Value.TryGetProperty(key, out var e)) return false;
        if (e.ValueKind == JsonValueKind.Null) return false;
        v = e;
        return true;
    }

    string A(string key, string def = "") =>
        TryArg(key, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : v.GetRawText())
            : def;

    // Null when the argument is absent — for service params whose own default
    // (an export path, say) is meaningfully different from an empty string.
    string? AN(string key) =>
        TryArg(key, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
            : null;

    int? AIN(string key)
    {
        if (!TryArg(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var num)) return num;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var str)) return str;
        return null;
    }

    bool AB(string key, bool def = false)
    {
        if (!TryArg(key, out var v)) return def;
        if (v.ValueKind == JsonValueKind.True)  return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
        return def;
    }

    T? AObj<T>(string key)
    {
        if (!TryArg(key, out var v)) return default;
        try   { return JsonSerializer.Deserialize<T>(v.GetRawText(), jsonOpts); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Argument '{key}' has the wrong shape for tool '{name}': {ex.Message}");
        }
    }

    List<T> AList<T>(string key) => AObj<List<T>>(key) ?? new List<T>();

    // Guard for tools that mutate or overwrite. The model must pass confirm:true.
    void RequireConfirm()
    {
        if (!AB("confirm"))
            throw new InvalidOperationException(
                $"'{name}' modifies the project and requires confirm:true. " +
                "Tell the user what will change and get their agreement before retrying.");
    }

    switch (name)
    {
        case "connect_to_tia_portal":  return await tia.AttachToRunningAsync();
        case "get_status":
            if (!tia.IsConnected) return new { connected = false };
            try   { return new { connected = true, project = await tia.GetProjectInfoAsync() }; }
            catch (Exception ex) { return new { connected = true, error = ex.Message.Split('\n')[0] }; }
        case "save_project":           await tia.SaveAsync(); return new { success = true };
        case "list_devices":           return await hw.GetDevicesAsync();
        case "list_blocks":            return await sw.ListBlocksAsync(A("device"));
        case "read_block":             return await sw.ReadBlockAsync(A("device"), A("block"));
        case "write_block_scl":        await sw.WriteBlockSclAsync(A("device"), A("block"), A("source")); return new { success = true };
        case "import_block_xml":       await sw.WriteBlockXmlAsync(A("device"), A("block"), A("content")); return new { success = true };
        case "compile_block":          return new { result = await sw.CompileBlockAsync(A("device"), A("block")) };
        case "create_lad_block":
            return await sw.CreateLadBlockAsync(A("device"), new LadBlockRequest
            {
                Name      = A("name"),
                Type      = A("type", "FB"),
                Number    = AIN("number"),
                Interface = AObj<LadInterface>("interface"),
                Networks  = AList<LadNetwork>("networks"),
                Culture   = AN("culture"),
                Overwrite = AB("overwrite"),
                Compile   = AB("compile", true),
                DryRun    = AB("dryRun"),
            });
        case "analyze_block":
        {
            var blk = await sw.ReadBlockAsync(A("device"), A("block"));
            if (string.IsNullOrWhiteSpace(blk.SourceCode))
                return new { error = "Block is not SCL or source could not be read." };
            return await scl.AnalyzeAsync(blk.SourceCode, A("block"), blk.Type.ToString());
        }
        case "create_block":
            return await sw.CreateBlockAsync(A("device"), new BlockCreateRequest {
                Name       = A("name"),
                Type       = (BlockType)Enum.Parse(typeof(BlockType), A("type", "FB"), ignoreCase: true),
                Language   = ProgrammingLanguage.SCL,
                Number     = AIN("number"),
                SourceCode = A("sourceCode")
            });
        case "list_tag_tables":        return await tagSvc.GetTagTablesAsync(A("device"));
        case "get_tags":               return await tagSvc.GetTagsAsync(A("device"), A("table"));
        case "analyze_scl":            return await scl.AnalyzeAsync(A("source"), A("blockName", "Block"), A("blockType", "FB"));
        case "clone_project":          return await tia.CloneProjectAsync(A("name"), A("path"));
        case "get_option_packages":    return await tia.GetOptionPackagesAsync();
        case "get_project_signature":  return await tia.GetProjectSignatureAsync();
        case "create_instance_db":
            return await sw.CreateInstanceDbAsync(
                A("device"), A("name"), A("instanceOfName"), AIN("number"));
        case "import_tag_table":
            await tagSvc.ImportTagTableFromContentAsync(A("device"), A("content"));
            return new { success = true };
        case "batch_rename_tags":
        {
            var renamed = await tagSvc.BatchRenameTagsAsync(
                A("device"), A("table"), AList<TagRenameItem>("renames"));
            return new { renamed };
        }

        // ── HMI + block inspection ────────────────────────────────────────────
        // Every tool in this group already round-trips over the REST routes in
        // HandleAsync, so the service code below is exercised.

        case "list_hmi_tag_tables":  return await hmiSvc.ListTagTablesAsync(A("device"));
        case "get_hmi_tags":         return await hmiSvc.GetTagsAsync(A("device"), A("table"));
        case "get_all_hmi_tags":     return await hmiSvc.GetAllTagsAsync(A("device"));
        case "create_hmi_tags":
            return await hmiSvc.CreateTagsAsync(
                A("device"), A("table"), AList<HmiTagCreateRequest>("tags"));
        case "list_hmi_screens":     return await hmiScreenSvc.ListScreensAsync(A("device"));
        case "get_screen_tag_refs":  return await hmiScreenSvc.GetScreenTagRefsAsync(A("device"), A("screen"));
        case "update_faceplate_tags":
            return await hmiScreenSvc.UpdateFaceplateTagsAsync(
                A("device"), A("screen"), AList<FaceplateTagUpdate>("updates"));
        case "get_block_attributes": return await sw.GetBlockAttributeInfosAsync(A("device"), A("block"));
        case "patch_block_texts":
            await sw.PatchBlockTextsAsync(A("device"), A("block"),
                AObj<BlockTextsRequest>("texts") ?? new BlockTextsRequest());
            return new { success = true };

        // ── Project lifecycle, export and hardware ────────────────────────────
        // WARNING: the services behind this group had no caller anywhere in the
        // codebase before these tools existed — no MCP tool and no REST route.
        // They compile but have never run against a live project. Treat failures
        // here as "unproven service code" first, not "bad arguments".

        case "open_project":
            return await tia.OpenProjectAsync(A("path"), AB("headless", true));
        case "close_project":
            RequireConfirm();
            await tia.CloseAsync();
            return new { success = true };
        case "export_block":
            return new { path = await sw.ExportBlockAsync(A("device"), A("block"), AN("path")) };
        case "create_tag_table":
            return await tagSvc.CreateTagTableAsync(A("device"), A("table"));
        case "create_tag":
            return await tagSvc.CreateTagAsync(A("device"), A("table"), new TagDefinition {
                Name       = A("name"),
                DataType   = A("dataType"),
                Address    = A("address"),
                Accessible = AB("accessible", true),
                Writable   = AB("writable",   true),
                Comment    = A("comment"),
            });
        case "export_tag_table":
            return new { path = await tagSvc.ExportTagTableAsync(A("device"), A("table"), AN("path")) };
        case "get_device":      return await hw.GetDeviceAsync(A("device"));
        case "get_io_mapping":  return await hw.GetIoMappingAsync(A("device"));
        case "generate_s7_1200":
            RequireConfirm();
            return await hw.GenerateS71200Async(new S71200Config {
                DeviceName     = A("deviceName"),
                CpuVariant     = A("cpuVariant"),
                IpAddress      = A("ipAddress"),
                SubnetMask     = A("subnetMask", "255.255.255.0"),
                Gateway        = A("gateway"),
                SignalModules  = AList<string>("signalModules"),
                SignalBoards   = AList<string>("signalBoards"),
                CommsModules   = AList<string>("commsModules"),
                EnableProfinet = AB("enableProfinet", true),
            });

        default: throw new InvalidOperationException($"Unknown tool: {name}");
    }
}

// The tool surface actually advertised, after --profile / TIA_MCP_PROFILE.
List<object> McpToolDefsForProfile()
{
    var defs = McpToolDefs().Where(d => InProfile(McpToolName(d)));
    return (mcpAnnotations ? defs.Select(AddAnnotations) : defs).ToList();
}

// Rebuilds a definition with an "annotations" block. Only reached when
// annotations are switched on.
object AddAnnotations(object def)
{
    var t = def.GetType();
    string name = McpToolName(def);
    bool ro = readOnlyHintTools.Contains(name);
    return new {
        name,
        description = t.GetProperty("description")?.GetValue(def),
        inputSchema = t.GetProperty("inputSchema")?.GetValue(def),
        annotations = new {
            readOnlyHint    = ro,
            destructiveHint = !ro && destructiveHintTools.Contains(name),
        }
    };
}

// Definitions are anonymous types; read the name back off the one property we
// need rather than restructuring every McpT call site around a named type.
string McpToolName(object def) =>
    def.GetType().GetProperty("name")?.GetValue(def) as string ?? "";

List<object> McpToolDefs() => new()
{
    McpT("connect_to_tia_portal", "Attaches to a running TIA Portal V20 process with an open project.",
        McpP("projectPath", "string", false, "Optional project path to prefer a specific instance")),
    McpT("get_status",   "Returns connection state and details about the currently open TIA Portal project."),
    McpT("save_project", "Saves the currently open TIA Portal project."),
    McpT("list_devices", "Lists all devices (PLCs, HMIs, drives) in the open project."),
    McpT("list_blocks",  "Lists all blocks (OB, FB, FC, DB) on a device.",
        McpP("device", "string", true, "Device name as shown in TIA Portal")),
    McpT("read_block", "Reads a block's source code, XML, language, type, and number.",
        McpP("device", "string", true, "Device name"),
        McpP("block",  "string", true, "Block name")),
    McpT("write_block_scl", "Overwrites a block's SCL source. Call compile_block afterwards to apply.",
        McpP("device", "string", true, "Device name"),
        McpP("block",  "string", true, "Block name"),
        McpP("source", "string", true, "Full SCL source text")),
    McpT("import_block_xml", "Imports raw SimaticML XML into a block. Use for LAD/FBD/STL blocks.",
        McpP("device",  "string", true, "Device name"),
        McpP("block",   "string", true, "Block name"),
        McpP("content", "string", true, "Full SimaticML XML content")),
    McpT("compile_block", "Compiles a block and returns compiler output with error line numbers.",
        McpP("device", "string", true, "Device name"),
        McpP("block",  "string", true, "Block name")),
    McpT("analyze_block", "Runs static SCL analysis on a block without compiling it.",
        McpP("device", "string", true, "Device name"),
        McpP("block",  "string", true, "Block name")),
    McpT("create_block", "Creates a new SCL block on a device.",
        McpP("device",      "string", true,  "Device name"),
        McpP("name",        "string", true,  "New block name"),
        McpP("type",        "string", true,  "Block type: FB, FC, OB, or GlobalDB"),
        McpP("sourceCode",  "string", true,  "Full SCL source"),
        McpP("number",      "integer", false, "Block number — omit to let TIA Portal assign one")),
    McpT("create_lad_block",
        "Creates a LAD block from a structured rung description, imports it, and compiles it. You never write "
      + "XML, UIds or wires. Each network is series logic in 'elements' (contact, eq/ne/gt/ge/lt/le, pbox/nbox, ton/tof/tp, branch, call, move) ending in "
      + "'outputs' (coil, scoil, rcoil). A seal-in is one network: elements=[branch[[contact Start],[contact Motor]], "
      + "contact Stop negated], outputs=[coil Motor]. Operands: Tag, \"DB\".Member, #localVar; ton pt like T#5s. "
      + "Always read the compile output, and ask the user to review the logic: compiling does not prove it "
      + "behaves correctly. Use dryRun:true to validate and see the XML without touching TIA Portal.",
        McpP("device",    "string",  true,  "Device name"),
        McpP("name",      "string",  true,  "New block name"),
        McpP("type",      "string",  true,  "FB, FC or OB"),
        McpP("number",    "integer", false, "Block number — omit to auto-assign"),
        ("interface", (object)new {
            type = "object",
            description = "Interface members by section. Each section is an array of {name, datatype}.",
            properties = new Dictionary<string, object>
            {
                ["input"]    = LadMemberArraySchema(), ["output"] = LadMemberArraySchema(),
                ["inOut"]    = LadMemberArraySchema(), ["static"] = LadMemberArraySchema(),
                ["temp"]     = LadMemberArraySchema(), ["constant"] = LadMemberArraySchema(),
            } }, false),
        ("networks", (object)new {
            type = "array", description = "Networks (rungs), in order.",
            items = new {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["title"]    = new { type = "string", description = "Optional; needs 'culture' on the tool call" },
                    ["comment"]  = new { type = "string", description = "Optional; needs 'culture' on the tool call" },
                    ["elements"] = LadElementArraySchema(depth: 2),
                    ["outputs"]  = LadElementArraySchema(depth: 0),
                } } }, true),
        McpP("culture",   "string",  false, "Project culture, e.g. en-GB. Only needed when networks have titles/comments; must match the project."),
        McpP("overwrite", "boolean", false, "Replace an existing block of the same name. Default false."),
        McpP("compile",   "boolean", false, "Compile after import. Default true."),
        McpP("dryRun",    "boolean", false, "Validate and return the XML only; do not import.")),
    McpT("list_tag_tables", "Lists all tag tables on a device with their names and tag counts.",
        McpP("device", "string", true, "Device name")),
    McpT("get_tags", "Returns all tags in a tag table with type, address, and comment.",
        McpP("device", "string", true, "Device name"),
        McpP("table",  "string", true, "Tag table name")),
    McpT("analyze_scl", "Runs static analysis on SCL code without needing an open block.",
        McpP("source",     "string", true,  "SCL source code"),
        McpP("blockName",  "string", false, "Block name for context"),
        McpP("blockType",  "string", false, "Block type: FB, FC, OB, or GlobalDB")),
    McpT("clone_project", "Clones the open project — exports all blocks/tags and imports into a new project.",
        McpP("name", "string", true, "New project name"),
        McpP("path", "string", true, "Destination folder path")),
    McpT("get_option_packages", "Lists all option packages and used products referenced by the project."),
    McpT("get_project_signature", "Returns a full index of every block and tag table on every device — names, numbers, languages, and consistency state."),
    McpT("create_instance_db", "Creates a new Instance DB linked to an FB.",
        McpP("device",          "string", true,  "Device name"),
        McpP("name",            "string", true,  "Instance DB name"),
        McpP("instanceOfName",  "string", true,  "FB name this DB is an instance of"),
        McpP("number",          "integer", false, "DB number — omit to let TIA Portal assign one")),
    McpT("import_tag_table", "Imports a complete tag table from SimaticML XML content (creates or replaces).",
        McpP("device",   "string", true, "Device name"),
        McpP("content",  "string", true, "SimaticML XML for the tag table")),
    McpT("batch_rename_tags", "Renames multiple tags in a tag table in a single atomic operation.",
        McpP("device",   "string", true, "Device name"),
        McpP("table",    "string", true, "Tag table name"),
        McpPArr("renames", true, "Rename pairs, applied in order",
            McpP("from", "string", true, "Current tag name"),
            McpP("to",   "string", true, "New tag name"))),

    // ── HMI (WinCC Unified) ───────────────────────────────────────────────────
    McpT("list_hmi_tag_tables", "Lists WinCC Unified HMI tag tables on an HMI device, with tag counts.",
        McpP("device", "string", true, "HMI device name as shown in TIA Portal (often 'HMI')")),
    McpT("get_hmi_tags", "Returns the tags in one WinCC Unified HMI tag table.",
        McpP("device", "string", true, "HMI device name"),
        McpP("table",  "string", true, "HMI tag table name")),
    McpT("get_all_hmi_tags", "Returns every HMI tag on the device as a flat list — name, table, data type, and linked PLC tag.",
        McpP("device", "string", true, "HMI device name")),
    McpT("create_hmi_tags",
        "Creates tags in a WinCC Unified HMI tag table. IMPORTANT: Openness can only create these as "
      + "Internal tags — setting the PlcTag attribute throws for newly created tags, so the tags will "
      + "have no PLC connection. Always tell the user they must open TIA Portal → HMI tags and set the "
      + "Connection and PLC tag by hand afterwards, or the tags will not read anything.",
        McpP("device", "string", true, "HMI device name"),
        McpP("table",  "string", true, "Target HMI tag table"),
        McpPArr("tags", true, "Tags to create",
            McpP("name",       "string", true,  "HMI tag name"),
            McpP("dataType",   "string", true,  "HMI data type, e.g. Bool, Int, Real"),
            McpP("plcTag",     "string", false, "Intended PLC tag — recorded only; not applied (see above)"),
            McpP("connection", "string", false, "Intended HMI connection name — recorded only; not applied"))),
    McpT("list_hmi_screens", "Lists the screens on a WinCC Unified HMI device.",
        McpP("device", "string", true, "HMI device name")),
    McpT("get_screen_tag_refs", "Returns every tag referenced by an HMI screen and the screen item referencing it. Read-only.",
        McpP("device", "string", true, "HMI device name"),
        McpP("screen", "string", true, "Screen name")),
    McpT("update_faceplate_tags", "Updates faceplate container interface parameters on an HMI screen — repoints a faceplate instance at different tags.",
        McpP("device", "string", true, "HMI device name"),
        McpP("screen", "string", true, "Screen name"),
        McpPArr("updates", true, "Parameter updates to apply",
            McpP("containerName", "string", true, "Faceplate container name on the screen"),
            McpP("parameterName", "string", true, "Interface parameter to set"),
            McpP("newValue",      "string", true, "New value, usually a tag name"))),

    // ── Block inspection ──────────────────────────────────────────────────────
    McpT("get_block_attributes", "Lists every readable and writable attribute and composition on a block. Use to discover what set_* operations are possible.",
        McpP("device", "string", true, "Device name"),
        McpP("block",  "string", true, "Block name")),
    McpT("patch_block_texts", "Updates a block's title, comment, and per-network titles/comments without touching its logic.",
        McpP("device", "string", true, "Device name"),
        McpP("block",  "string", true, "Block name"),
        McpPObj("texts", true, "Texts to patch — omit any field to leave it unchanged",
            McpP("blockTitle",   "string", false, "Block title"),
            McpP("blockComment", "string", false, "Block comment"),
            McpPArr("networks", false, "Per-network texts, in network order",
                McpP("title",   "string", false, "Network title"),
                McpP("comment", "string", false, "Network comment")))),

    // ── Project lifecycle ─────────────────────────────────────────────────────
    McpT("open_project",
        "Opens a TIA Portal project file from disk, starting a portal instance if none is running. "
      + "Prefer connect_to_tia_portal when the user already has the project open.",
        McpP("path",     "string",  true,  "Full path to the .ap20 project file"),
        McpP("headless", "boolean", false, "Open without the TIA Portal UI (default true)")),
    McpT("close_project",
        "Closes the open project. Unsaved changes are lost unless the server is configured to auto-save, "
      + "so call save_project first unless the user wants to discard their work.",
        McpP("confirm", "boolean", true, "Must be true. Confirm with the user before closing.")),

    // ── Export ────────────────────────────────────────────────────────────────
    McpT("export_block", "Exports a block to a SimaticML XML file on disk and returns the path.",
        McpP("device", "string", true,  "Device name"),
        McpP("block",  "string", true,  "Block name"),
        McpP("path",   "string", false, "Destination file path — defaults to the server's export directory")),
    McpT("export_tag_table", "Exports a PLC tag table to a SimaticML XML file on disk and returns the path.",
        McpP("device", "string", true,  "Device name"),
        McpP("table",  "string", true,  "Tag table name"),
        McpP("path",   "string", false, "Destination file path — defaults to the server's export directory")),

    // ── Tag creation ──────────────────────────────────────────────────────────
    McpT("create_tag_table", "Creates a new, empty PLC tag table on a device.",
        McpP("device", "string", true, "Device name"),
        McpP("table",  "string", true, "New tag table name")),
    McpT("create_tag",
        "Creates a single PLC tag in an existing tag table. To add many tags at once, build a SimaticML "
      + "tag table and use import_tag_table — it is far fewer round-trips.",
        McpP("device",     "string",  true,  "Device name"),
        McpP("table",      "string",  true,  "Existing tag table name"),
        McpP("name",       "string",  true,  "Tag name"),
        McpP("dataType",   "string",  true,  "PLC data type, e.g. Bool, Int, Real"),
        McpP("address",    "string",  true,  "Absolute address, e.g. %I0.0, %QW10, %M100.0"),
        McpP("accessible", "boolean", false, "Accessible from HMI/OPC UA (default true)"),
        McpP("writable",   "boolean", false, "Writable from HMI/OPC UA (default true)"),
        McpP("comment",    "string",  false, "Tag comment")),

    // ── Hardware ──────────────────────────────────────────────────────────────
    McpT("get_device", "Returns details for one device — type, order number, firmware, and address.",
        McpP("device", "string", true, "Device name")),
    McpT("get_io_mapping", "Returns the I/O points of a device — module, channel, address, and direction. Use this to map physical I/O before writing tag tables.",
        McpP("device", "string", true, "Device name")),
    McpT("generate_s7_1200",
        "Creates a new S7-1200 station with the given CPU, modules, and PROFINET address. "
      + "Adds hardware to the project — confirm the CPU variant and IP with the user first.",
        McpP("confirm",        "boolean", true,  "Must be true. Confirm the configuration with the user."),
        McpP("deviceName",     "string",  true,  "Name for the new device"),
        McpP("cpuVariant",     "string",  true,  "CPU key, e.g. '1214C-DC/DC/DC' or '1215C-AC/DC/Relay'"),
        McpP("ipAddress",      "string",  true,  "PROFINET IP address, e.g. 192.168.0.1"),
        McpP("subnetMask",     "string",  false, "Subnet mask (default 255.255.255.0)"),
        McpP("gateway",        "string",  false, "Default gateway"),
        McpPArrOf("signalModules", "string", false, "Signal module keys, e.g. 'SM1223-8DI-8DO-24VDC'"),
        McpPArrOf("signalBoards",  "string", false, "Signal board keys, e.g. 'SB1232-1AO'"),
        McpPArrOf("commsModules",  "string", false, "Comms module keys, e.g. 'CM1241-RS485'"),
        McpP("enableProfinet", "boolean", false, "Create and connect a PN/IE subnet (default true)")),
};

// A tool parameter carries a ready-made JSON Schema fragment rather than a bare
// type name, so array and object params can declare their real shape. Describing
// the shape only in prose ("array of {from, to} pairs") leaves the model guessing.
object McpT(string name, string desc, params (string n, object s, bool r)[] ps) => new {
    name, description = desc,
    inputSchema = new {
        type       = "object",
        properties = ps.ToDictionary(p => p.n, p => p.s),
        required   = ps.Where(p => p.r).Select(p => p.n).ToArray()
    }
};

object LadMemberArraySchema() => new {
    type = "array",
    items = new {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["name"]     = new { type = "string" },
            ["datatype"] = new { type = "string", description = "e.g. Bool, Int, Real, Time" },
        },
        required = new[] { "name", "datatype" },
    },
};

// LAD element schema. 'branch' nests elements; JSON Schema fragments here are finite, so nesting
// is unrolled to a fixed depth (branch-in-branch-in-branch is already more than a rung needs).
object LadElementArraySchema(int depth) => new {
    type = "array",
    items = new {
        type = "object",
        properties = LadElementProps(depth),
        required = new[] { "type" },
    },
};

Dictionary<string, object> LadElementProps(int depth)
{
    var p = new Dictionary<string, object>
    {
        ["type"]     = new { type = "string", description = "elements: contact | eq ne gt ge lt le | pbox nbox | ton tof tp | branch | call | move; outputs: coil | scoil | rcoil" },
        ["operand"]  = new { type = "string", description = "contact/coil tag: Tag, \"DB\".Member or #local. compare: left value. pbox/nbox: the edge-memory Bool. move: destination" },
        ["negated"]  = new { type = "boolean", description = "contact only: true = normally closed" },
        ["instance"] = new { type = "string", description = "timer: #Tmr (FB multi-instance). call of an FB: instance DB name or #multi" },
        ["pt"]       = new { type = "string", description = "timer only: preset, e.g. T#5s" },
        ["operand2"] = new { type = "string", description = "compare only: right-hand value" },
        ["dataType"] = new { type = "string", description = "compare only: Int (default), DInt, Real..." },
        ["source"]   = new { type = "string", description = "move only: value or tag to copy into 'operand'. A move must be the last element and the network can have no outputs" },
        ["block"]    = new { type = "string", description = "call only: name of the FB/FC to call" },
        ["blockType"] = new { type = "string", description = "call only: FB or FC" },
        ["parameters"] = new
        {
            type = "array",
            description = "call only: parameters to wire",
            items = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["name"]     = new { type = "string" },
                    ["section"]  = new { type = "string", description = "Input | Output | InOut" },
                    ["datatype"] = new { type = "string", description = "the parameter's data type, e.g. Bool, Int, Real" },
                    ["operand"]  = new { type = "string", description = "tag, \"DB\".Member, #local or literal" },
                },
                required = new[] { "name", "section", "datatype", "operand" },
            },
        },
    };
    if (depth > 0)
        p["branches"] = new
        {
            type = "array",
            description = "branch only: 2+ parallel paths, each an array of elements",
            items = LadElementArraySchema(depth - 1),
        };
    return p;
}

// Scalar: McpP("device", "string", true, "Device name")
(string n, object s, bool r) McpP(string n, string t, bool r, string d)
    => (n, new { type = t, description = d }, r);

// Array of scalars: McpPArrOf("signalModules", "string", false, "…")
(string n, object s, bool r) McpPArrOf(string n, string itemType, bool r, string d)
    => (n, new { type = "array", description = d, items = new { type = itemType } }, r);

// Array of objects: McpPArr("renames", true, "…", McpP("from", …), McpP("to", …))
(string n, object s, bool r) McpPArr(string n, bool r, string d, params (string n, object s, bool r)[] item)
    => (n, new {
           type = "array", description = d,
           items = new {
               type       = "object",
               properties = item.ToDictionary(p => p.n, p => p.s),
               required   = item.Where(p => p.r).Select(p => p.n).ToArray()
           }
       }, r);

// Nested object: McpPObj("texts", false, "…", McpP("blockTitle", …), …)
(string n, object s, bool r) McpPObj(string n, bool r, string d, params (string n, object s, bool r)[] fields)
    => (n, new {
           type = "object", description = d,
           properties = fields.ToDictionary(p => p.n, p => p.s),
           required   = fields.Where(p => p.r).Select(p => p.n).ToArray()
       }, r);

// ── Shared MCP request handler (used by both HTTP and stdio) ──────────────────

async Task<(object? result, object? rpcErr)> HandleMcpRequest(McpRpcRequest body)
{
    object? result = null;
    object? rpcErr = null;
    string  mcpTool = "";
    switch (body.Method)
    {
        case "initialize":
        {
            // Echo back the client's requested version if we support it.
            var clientPv = body.Params.HasValue &&
                           body.Params.Value.TryGetProperty("protocolVersion", out var pvEl)
                ? pvEl.GetString() ?? "2025-03-26" : "2025-03-26";
            var responsePv = clientPv == "2024-11-05" ? "2024-11-05" : "2025-03-26";
            result = new {
                protocolVersion = responsePv,
                capabilities    = new { tools = new { } },
                serverInfo      = new { name = "tia-portal-openness", version = "1.0.0" }
            };
            break;
        }
        case "ping":
            result = new { };
            break;
        case "tools/list":
            result = new { tools = McpToolDefsForProfile() };
            break;
        case "tools/call":
            if (!body.Params.HasValue)
            { rpcErr = new { code = -32602, message = "Missing params" }; break; }
            try
            {
                mcpTool = body.Params.Value.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                try
                {
                    var callResult = await McpDispatch(body.Params.Value);
                    var txt = JsonSerializer.Serialize(callResult, jsonOpts);
                    result = new { content = new[] { new { type = "text", text = txt } }, isError = false };
                    lock (mcpLock) { mcpLog.Insert(0, new McpLogEntry { Tool = mcpTool, At = DateTime.Now, Success = true }); if (mcpLog.Count > 200) mcpLog.RemoveAt(mcpLog.Count - 1); }
                }
                catch (Exception ex)
                {
                    var msg = ex.Message.Split('\n')[0];
                    result = new { content = new[] { new { type = "text", text = msg } }, isError = true };
                    lock (mcpLock) { mcpLog.Insert(0, new McpLogEntry { Tool = mcpTool, At = DateTime.Now, Success = false, Error = msg }); if (mcpLog.Count > 200) mcpLog.RemoveAt(mcpLog.Count - 1); }
                }
            }
            catch (Exception ex) { rpcErr = new { code = -32603, message = ex.Message.Split('\n')[0] }; }
            break;
        default:
            rpcErr = new { code = -32601, message = $"Method not found: {body.Method}" };
            break;
    }
    return (result, rpcErr);
}

// ── Stdio MCP loop ─────────────────────────────────────────────────────────────

async Task RunStdioAsync()
{
    var stdin  = new System.IO.StreamReader(Console.OpenStandardInput(),  new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

    string? line;
    while ((line = await stdin.ReadLineAsync()) != null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        McpRpcRequest? req;
        try   { req = JsonSerializer.Deserialize<McpRpcRequest>(line, jsonOpts); }
        catch { await WriteStdio(stdout, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }); continue; }
        if (req is null) continue;

        // Notifications have no id — acknowledge silently
        if (req.Id is null && (req.Method?.StartsWith("notifications/") ?? false)) continue;

        var (result, rpcErr) = await HandleMcpRequest(req);
        if (rpcErr != null)
            await WriteStdio(stdout, new { jsonrpc = "2.0", id = req.Id, error = rpcErr });
        else
            await WriteStdio(stdout, new { jsonrpc = "2.0", id = req.Id, result });
    }
}

async Task WriteStdio(System.IO.StreamWriter w, object obj)
    => await w.WriteLineAsync(JsonSerializer.Serialize(obj, jsonOpts));

// ── Request body DTOs ─────────────────────────────────────────────────────────

class SclWriteRequest   { public string Source    { get; set; } = ""; }
class SclAnalyzeRequest { public string Source    { get; set; } = "";
                          public string BlockName { get; set; } = "Block";
                          public string BlockType { get; set; } = "FB"; }
class XmlWriteRequest   { public string Content   { get; set; } = ""; }
class CloneRequest      { public string Name      { get; set; } = ""; public string Path { get; set; } = ""; }

class TagImportRequest        { public string Content      { get; set; } = ""; }
class TagBatchRenameRequest   { public List<TagRenameItem> Renames { get; set; } = new(); }
class InstanceDbCreateRequest { public string Name           { get; set; } = "";
                                public string InstanceOfName { get; set; } = "";
                                public int?   Number         { get; set; } }

class McpRpcRequest {
    [JsonPropertyName("jsonrpc")] public string       JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")]      public object?      Id      { get; set; }
    [JsonPropertyName("method")]  public string       Method  { get; set; } = "";
    [JsonPropertyName("params")]  public JsonElement? Params  { get; set; }
}
class McpLogEntry {
    public string   Tool    { get; set; } = "";
    public DateTime At      { get; set; }
    public bool     Success { get; set; }
    public string?  Error   { get; set; }
}
