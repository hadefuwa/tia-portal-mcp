# Future Features & Research Notes

Ideas and patterns gathered from open-source TIA Portal MCP projects and community posts. None of this is committed — it's a backlog to draw from.

---

## 1. Skills / Methodology Layer

**Source:** LinkedIn post describing a three-layer architecture (MCP server + Skills + Claude Code agent mode)

The most impactful addition would not be more tools — it would be a *methodology layer*: documented procedures that Claude loads on demand to guide how it uses the existing tools.

Examples of what a skill file looks like:
- `naming-conventions.md` — FB/FC/DB naming rules, tag prefixes, group structure
- `db-architecture.md` — when to use instance DBs vs global DBs, shadow DB patterns
- `recipe-handling.md` — how recipe data flows, parameter DB layout
- `operating-modes.md` — standard state machine pattern for your sites
- `alarm-handling.md` — alarm DB structure, acknowledgement logic
- `oee-calculation.md` — standard OEE block architecture

Claude Code loads only the skill it needs at the time it needs it. This keeps context small and prevents Claude from applying the wrong conventions to the wrong problem.

**Why this matters:** The LinkedIn author described generating a full project tree (device → station FBs) in ~10 minutes, with the Siemens compiler as the guardrail on every iteration. The skills are what make the output coherent — not the tools.

---

## 2. Stdio Transport Option

**Source:** GitHub research — 7 of 8 open-source repos use stdio, not HTTP

Currently the server runs on HTTP (`localhost:5000/mcp`), which requires users to add it via Claude Desktop's Custom Connectors UI. An stdio entry point would let users configure it in `claude_desktop_config.json` directly:

```json
{
  "mcpServers": {
    "tia-portal": {
      "command": "C:\\path\\to\\TiaPortalDashboard.exe",
      "args": ["--mcp-stdio"]
    }
  }
}
```

The dashboard UI would still run over HTTP; the stdio mode would be a headless alternative for Claude Code / Claude Desktop users who don't need the dashboard.

**Effort:** Medium. Needs a second entry-point branch in `Program.cs` that reads JSON-RPC from stdin and writes to stdout, bypassing the HTTP listener and WinForms window.

---

## 3. Two-Process Architecture (.NET 8 + .NET 4.8)

**Source:** [Czarnak/tia-portal-mcp](https://github.com/Czarnak/tia-portal-mcp)

Currently the entire app runs in .NET 4.8 because TIA Openness depends on `System.Runtime.Remoting`. The downside: no modern C# features, no NuGet packages that target .NET 5+.

The two-process pattern solves this cleanly:
- **Worker process (.NET 4.8):** owns all TIA Openness COM objects, runs on STA thread, receives commands over stdin/stdout as newline-delimited JSON
- **MCP server process (.NET 8):** runs the MCP protocol, has access to all modern packages, delegates COM calls to the worker via the pipe

**Effort:** High. Requires splitting the project, defining an IPC message format, and handling process lifecycle. Worth doing if we ever want to use modern MCP SDKs or .NET 8+ features.

---

## 4. Lite Profile (Reduced Tool Surface)

**Source:** [bulaofen0036-coder/TIA_Portal_Openness_MCP](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP)

With 20 tools, tool definitions already consume a chunk of Claude's context window. A `TIA_MCP_PROFILE=lite` environment variable (or a query param) that returns only the most-used 8–10 tools would help in long conversations.

Suggested lite set: `connect_to_tia_portal`, `get_status`, `list_devices`, `list_blocks`, `read_block`, `write_block_scl`, `compile_block`, `get_tags`, `save_project`

**Effort:** Low. One extra condition in the `tools/list` handler.

---

## 5. Project Path as CLI Argument

**Source:** Common pattern across most open-source repos

Most repos pass the `.ap21` project path as a command-line argument, which means Claude doesn't need to call `connect_to_tia_portal` as a first step — the server connects automatically at startup.

```
TiaPortalDashboard.exe --project "C:\Projects\MyProject.ap21"
```

This removes one round-trip from every agent session and makes scripts more deterministic.

**Effort:** Low. Add argument parsing in `Program.cs`, call `TiaPortalService.ConnectAsync()` at startup if `--project` is provided.

---

## 6. Claude Code Agent Mode Workflow

**Source:** LinkedIn post

The LinkedIn author's workflow is:
1. Describe the project upfront (architecture, stations, operating modes, conventions)
2. Claude Code plans the work in phases
3. Claude writes SCL files to disk
4. Claude calls the MCP tools to import and compile
5. Claude reads compiler errors and iterates — no human between steps

This is different from our current model (human fills in fields, clicks Run). Supporting it well requires:
- Good skill files (see item 1)
- Reliable `compile_block` with structured error output (we have this)
- Possibly a `get_project_signature` snapshot before/after to verify changes (we have this)

**Effort:** Mostly zero on the server side — the tools already support this. The work is writing the skill files and a good `CLAUDE.md` entry point.

---

## 7. V21 Support (Dual Binaries)

**Source:** [bulaofen0036-coder/TIA_Portal_Openness_MCP](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP)

TIA Portal V20 and V21 have different assembly binding for `Siemens.Engineering.dll` — they can't share a single binary. If V21 support becomes needed, the `.csproj` would need a second target that references the V21 DLL, producing a separate exe.

**Effort:** Medium. Mostly `.csproj` and build pipeline changes; the C# code itself is largely the same between versions.
