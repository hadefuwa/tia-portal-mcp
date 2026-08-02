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

bool stdioMode = Array.IndexOf(args, "--mcp-stdio") >= 0;

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

var sp     = services.BuildServiceProvider();
var tia    = sp.GetRequiredService<TiaPortalService>();
var hw     = sp.GetRequiredService<HardwareService>();
var sw     = sp.GetRequiredService<SoftwareService>();
var scl    = sp.GetRequiredService<SclAnalyzerService>();
var tagSvc = sp.GetRequiredService<TagService>();

var mcpLog  = new List<McpLogEntry>();
var mcpLock = new object();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented               = false,
};
jsonOpts.Converters.Add(new JsonStringEnumConverter());

// ── Stdio MCP mode ────────────────────────────────────────────────────────────
if (stdioMode)
{
    await RunStdioAsync();
    sp.Dispose();
    return;
}

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
    string A(string key, string def = "") =>
        args.HasValue && args.Value.TryGetProperty(key, out var v) ? v.GetString() ?? def : def;

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
                Number     = int.TryParse(A("number"), out var nb) ? (int?)nb : null,
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
                A("device"), A("name"), A("instanceOfName"),
                int.TryParse(A("number"), out var idb) ? (int?)idb : null);
        case "import_tag_table":
            await tagSvc.ImportTagTableFromContentAsync(A("device"), A("content"));
            return new { success = true };
        case "batch_rename_tags":
        {
            var rawRenames = args.HasValue && args.Value.TryGetProperty("renames", out var rv)
                ? JsonSerializer.Deserialize<List<TagRenameItem>>(rv.GetRawText(), jsonOpts) ?? new()
                : new List<TagRenameItem>();
            var renamed = await tagSvc.BatchRenameTagsAsync(A("device"), A("table"), rawRenames);
            return new { renamed };
        }
        default: throw new InvalidOperationException($"Unknown tool: {name}");
    }
}

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
        McpP("number",      "string", false, "Block number (optional integer)")),
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
        McpP("number",          "string", false, "DB number (optional)")),
    McpT("import_tag_table", "Imports a complete tag table from SimaticML XML content (creates or replaces).",
        McpP("device",   "string", true, "Device name"),
        McpP("content",  "string", true, "SimaticML XML for the tag table")),
    McpT("batch_rename_tags", "Renames multiple tags in a tag table in a single atomic operation.",
        McpP("device",   "string", true, "Device name"),
        McpP("table",    "string", true, "Tag table name"),
        McpP("renames",  "array",  true, "Array of {from, to} rename pairs")),
};

object McpT(string name, string desc, params (string n, string t, bool r, string d)[] ps) => new {
    name, description = desc,
    inputSchema = new {
        type       = "object",
        properties = ps.ToDictionary(p => p.n, p => (object)new { type = p.t, description = p.d }),
        required   = ps.Where(p => p.r).Select(p => p.n).ToArray()
    }
};
(string n, string t, bool r, string d) McpP(string n, string t, bool r, string d) => (n, t, r, d);

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
            result = new { tools = McpToolDefs() };
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
