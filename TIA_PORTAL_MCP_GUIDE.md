# TIA Portal MCP Server — AI Development Guide

Everything learned from live testing against TIA Portal V20. Read this before attempting any automation.

Verified against the source on 2026-08-03. Where the code and this document disagreed, the code won —
see **Known contradictions** at the end for the few points still unresolved.

---

## Architecture

```
Claude Code / Cursor / VS Code        Claude Desktop
  (stdio transport)                   (HTTP transport)
        │                                    │
        │  newline-delimited JSON-RPC        │  HTTP POST /mcp
        ▼                                    ▼
TiaPortalDashboard.exe  ──  HandleMcpRequest()  ──  REST endpoints
  (--mcp-stdio flag)                 (shared)        (/api/*)
        │
        │  TIA Openness API  (COM · STA thread required)
        ▼
Siemens.Engineering.dll  (TIA Portal V20 PublicAPI)
        │
        ▼
TIA Portal V20  (must be open with a project loaded)
```

**Source:**
```
src/TiaOpennessMcpServer/
├── Program.cs                     # HTTP listener, routing, stdio loop, MCP tool defs + dispatch
├── MainForm.cs                    # WinForms tray window (HTTP mode only)
├── Services/TiaPortalService.cs   # Connect, project info/save/clone, signature, option packages
├── Services/HardwareService.cs    # Device and module enumeration
├── Services/SoftwareService.cs    # Blocks — list, read, create, write, compile, instance DB
├── Services/SclAnalyzerService.cs # Static SCL analysis (no compile required)
├── Services/TagService.cs         # Tag tables — list, read, import, batch rename
├── Utilities/XmlHelper.cs         # SimaticML XML builders and SCL extract/inject
├── Utilities/StaTaskScheduler.cs  # The single STA thread every Openness call runs on
└── Models/                        # DTOs — BlockInfo, DeviceInfo, TagInfo, ProjectSignature, …
```

**Export/temp files:** `C:\Temp\TiaExports\` (all XML written before import is left on disk — useful for debugging).

> `appsettings.json` is **not loaded**. `Program.cs` calls `services.Configure<TiaOpennessOptions>(_ => { })`
> with no configuration binding, so every option comes from the C# defaults in `TiaOpennessOptions.cs`.
> The file's values happen to match those defaults — editing it changes nothing. To change the export
> directory you must edit `TiaOpennessOptions.cs` and rebuild, or wire up configuration binding.

---

## Transport modes

### stdio (Claude Code / Cursor / VS Code Copilot)

Run with `--mcp-stdio`. The process communicates entirely over stdin/stdout; no window, no HTTP server.

Claude Code reads MCP servers from **`~/.claude.json`** (the top-level `mcpServers` object, or a
per-project one under `projects`). This is where the server is actually registered on this machine:

```json
{
  "mcpServers": {
    "tia-portal": {
      "type": "stdio",
      "command": "C:\\Users\\HamedA\\Documents\\tia-portal-mcp\\src\\TiaOpennessMcpServer\\bin\\Release\\net48\\TiaPortalDashboard.exe",
      "args": ["--mcp-stdio"],
      "env": {}
    }
  }
}
```

A project-scoped `.mcp.json` in the repo root also works. There is **no** `~/.claude/.mcp.json` —
earlier revisions of this guide and of `CLAUDE.md` named that path and it was wrong.

### HTTP (dashboard + Claude Desktop)

```
http://localhost:5000
```

Start the exe with no arguments. `GET /` serves the dashboard. Claude Desktop connects via
**Settings → Connectors → Add custom connector** pointed at `http://localhost:5000/mcp`.
Do **not** add it to `claude_desktop_config.json` — that file only supports stdio entries.

`GET /mcp` deliberately returns 405 with a JSON-RPC error body so clients detect the modern
Streamable HTTP transport instead of falling back to HTTP+SSE discovery. Protocol version is
`2025-03-26`, or `2024-11-05` echoed back if the client asks for it.

---

## TIA Portal approval dialog

`connect_to_tia_portal` / `POST /api/connect` calls `AttachToRunningAsync()`, which blocks the STA
thread while TIA Portal shows its access approval dialog. Consequences:

1. **The call does not return until the user clicks "Yes to all".** Set client timeouts to ≥90 seconds.
2. The dialog is shown per connecting process, so each app restart or new stdio spawn can require a
   fresh approval. It sometimes appears behind other windows — check the TIA Portal taskbar button.
3. While the STA thread is blocked, every other STA-bound operation queues behind it. `get_status`
   only reads a null check and still returns immediately.

---

## MCP tools

This is what Claude Code and Cursor actually see. All 20 tools are defined in `McpToolDefs()` and
dispatched in `McpDispatch()` in `Program.cs`.

| Tool | Required args | Optional | Returns |
|---|---|---|---|
| `connect_to_tia_portal` | — | `projectPath` ⚠ | Project info: `name, path, author, comment, modifiedDate, deviceCount, isModified` |
| `get_status` | — | — | `{connected:false}` or `{connected:true, project:{…}}` |
| `save_project` | — | — | `{success:true}` |
| `list_devices` | — | — | Array of `{name, typeIdentifier, deviceType, cpuModel, ipAddress, subnetMask, gateway, slotCount, modules[]}` |
| `list_blocks` | `device` | — | Array of `{name, type, number, language, author, comment, modified, isKnowHow, sizeBytes}` — recurses into block folders |
| `read_block` | `device`, `block` | — | Block info plus `sourceCode` (SCL only) and `xmlContent` |
| `write_block_scl` | `device`, `block`, `source` | — | `{success:true}` |
| `import_block_xml` | `device`, `block`, `content` | — | `{success:true}` |
| `compile_block` | `device`, `block` | — | `{result:"…"}` — multi-line text with state, error/warning counts, messages |
| `analyze_block` | `device`, `block` | — | SCL analysis result (see below) |
| `create_block` | `device`, `name`, `type`, `sourceCode` | `number` | Created block info |
| `create_instance_db` | `device`, `name`, `instanceOfName` | `number` | Created block info |
| `list_tag_tables` | `device` | — | Array of `{name, tagCount, comment}` |
| `get_tags` | `device`, `table` | — | Array of `{name, dataType, address, accessible, writable, comment}` |
| `import_tag_table` | `device`, `content` | — | `{success:true}` |
| `batch_rename_tags` | `device`, `table`, `renames` | — | `{renamed:N}` |
| `analyze_scl` | `source` | `blockName`, `blockType` | SCL analysis result |
| `clone_project` | `name`, `path` | — | Clone result |
| `get_option_packages` | — | — | Array of option packages / used products |
| `get_project_signature` | — | — | Full index of every device → blocks + tag tables |

**Tool quirks worth knowing:**

- ⚠ **`projectPath` on `connect_to_tia_portal` is ignored.** The schema advertises it, but dispatch
  calls `tia.AttachToRunningAsync()` with no argument. If several TIA Portal instances are open you
  cannot choose between them — close the ones you don't want.
- **`number` is declared as a `string`** in every schema (`create_block`, `create_instance_db`) and
  parsed with `int.TryParse`. Pass `"5"`, not `5`. Anything unparseable silently becomes auto-number.
- **`create_block` always creates SCL.** `type` selects FB / FC / OB / GlobalDB, but the language is
  hardcoded — there is no way to create a LAD/FBD/STL block through it. Use `import_block_xml` for those.
- **Every tool except `connect_to_tia_portal` and `get_status` calls `EnsureConnected()`** and throws
  if you haven't connected yet.
- Tool errors come back as `isError: true` with the first line of the exception as text, not as a
  JSON-RPC error. The last 200 calls are visible at `GET /api/mcp/log` and on the dashboard.

### SCL analysis result shape

Returned by `analyze_block` and `analyze_scl`:

```
{ blockName, blockType, isValid, summary,
  diagnostics: [{ severity: Error|Warning|Info|Hint, code, message, line, column, suggestion }],
  variables:   [{ name, dataType, section, initValue, comment, declLine }],
  metrics:     { linesOfCode, linesOfComments, cyclomaticComplexity, nestingDepthMax,
                 variableCount, timerCallCount, counterCallCount, functionCallCount } }
```

`analyze_block` returns `{error:"Block is not SCL or source could not be read."}` for LAD/FBD/STL —
those blocks have no extractable source.

---

## HTTP API endpoints

All at `http://localhost:5000`. All JSON. **Keys are camelCase** (`content`, not `Content`).
Enums serialise as strings (`"GlobalDB"`, `"SCL"`). Errors return `{error:"…"}`, usually with HTTP 200.

### Project / connection

| Method | Path | Body | Notes |
|--------|------|------|-------|
| GET | `/` | — | Serves `dashboard.html` from next to the exe |
| GET | `/api/status` | — | `{connected, project?}` — never blocks |
| POST | `/api/connect` | — | Attaches to running TIA Portal; blocks until user approves |
| GET | `/api/project/signature` | — | Every block and tag table on every device, with consistency state |
| GET | `/api/project/options` | — | Option packages and used products |
| POST | `/api/project/clone` | `name, path` | Exports all blocks/tags into a new project |
| POST | `/api/project/save` | — | Saves the open project |

### Devices

| Method | Path | Notes |
|--------|------|-------|
| GET | `/api/devices` | Lists all devices with modules |

Use the **exact** name from this call in path segments — in the SF2 project the PLC is `S7-1200`,
not `S7-1200 station_1`. Names containing spaces must be URL-encoded (`%20`); the router
`Uri.UnescapeDataString`s each segment.

### Blocks

| Method | Path | Body fields | Notes |
|--------|------|-------------|-------|
| GET | `/api/devices/{device}/blocks` | — | List all blocks, recursing into folders |
| GET | `/api/devices/{device}/blocks/{block}` | — | Read block (XML + SCL source) |
| POST | `/api/devices/{device}/blocks` | `name, type, number?, language, sourceCode` | Create block. `language` is accepted but **ignored** — always SCL |
| POST | `/api/devices/{device}/blocks/{block}/xml` | `content` | Import raw SimaticML XML (creates or overwrites) |
| PUT | `/api/devices/{device}/blocks/{block}/scl` | `source` | Patch SCL in an existing block |
| POST | `/api/devices/{device}/blocks/{block}/compile` | — | Compile; returns `{result}` |
| POST | `/api/devices/{device}/blocks/{block}/analyze` | — | Static SCL analysis, no compile |
| POST | `/api/devices/{device}/blocks/instance-db` | `name, instanceOfName, number?` | Create instance DB |

### Tags

| Method | Path | Body fields | Notes |
|--------|------|-------------|-------|
| GET | `/api/devices/{device}/tags` | — | List tag tables |
| GET | `/api/devices/{device}/tags/{table}` | — | Get all tags in a table |
| POST | `/api/devices/{device}/tags/import` | `content` | Import tag table from SimaticML XML |
| POST | `/api/devices/{device}/tags/{table}/rename` | `renames: [{from, to}]` | Batch rename |

### Analysis and diagnostics

| Method | Path | Body fields | Notes |
|--------|------|-------------|-------|
| POST | `/api/analyze` | `source, blockName?, blockType?` | Analyse SCL without an open block |
| GET | `/api/mcp/log` | — | Last 50 MCP tool calls (tool, timestamp, success, error) |
| POST | `/mcp` | JSON-RPC 2.0 | MCP Streamable HTTP endpoint |

---

## Creating blocks

### SCL blocks (FB, FC, OB)

Pass SCL source as `sourceCode`. It is base64-encoded into `<Source Name="BlockSource">` and imported
with `ImportOptions.Override`.

```powershell
$body = @{
    name       = "FB_SpeedControl"
    type       = "FB"
    number     = $null        # auto-number
    language   = "SCL"
    sourceCode = @'
FUNCTION_BLOCK "FB_SpeedControl"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1

VAR_INPUT
    Enable    : Bool;
    Setpoint  : Real;
END_VAR
VAR_OUTPUT
    Running   : Bool;
END_VAR
VAR
    _rampVal  : Real;
END_VAR

BEGIN
    IF Enable THEN
        _rampVal := Setpoint;
        Running  := TRUE;
    ELSE
        Running  := FALSE;
    END_IF;
END_FUNCTION_BLOCK
'@
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:5000/api/devices/S7-1200/blocks" `
    -Method POST -Body $body -ContentType "application/json" -TimeoutSec 30
```

**SCL rules for TIA Portal V20:**
- Block name in the declaration must match the `name` field.
- `{ S7_Optimized_Access := 'TRUE' }` is optional but standard.
- FB sections: `VAR_INPUT`, `VAR_OUTPUT`, `VAR_IN_OUT`, `VAR` (static), `VAR_TEMP`.
- `R_TRIG`, `F_TRIG`, `TON`, `TOF` are valid IEC types — declare them in `VAR`.

### GlobalDB

Pass SCL `DATA_BLOCK…END_VAR` as `sourceCode`. The server regex-scans the source for member
declarations and generates SimaticML from them.

```powershell
$body = @{
    name       = "ProductionData_DB"
    type       = "GlobalDB"
    number     = $null
    language   = "SCL"
    sourceCode = @'
DATA_BLOCK "ProductionData_DB"
   VAR
      LineSpeed    : Real;
      Temperature  : Real;
      PartCount    : DInt;
      ShiftRunning : Bool;
      BatchID      : String[20];
      FaultCode    : Int;
   END_VAR
'@
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:5000/api/devices/S7-1200/blocks" `
    -Method POST -Body $body -ContentType "application/json" -TimeoutSec 30
```

**⚠ Parser limits — `XmlHelper.ParseSclVarSection` is a single regex, `^\s+(\w+)\s*:\s*([\w\[\].\"]+)\s*;`:**

| Declaration | Result |
|---|---|
| `LineSpeed : Real;` | ✅ parsed |
| `BatchID : String[20];` | ✅ parsed |
| `"MyUdt_Type"` members | ✅ parsed (quotes allowed) |
| `Setpoint : Real := 25.0;` | ❌ **silently dropped** — initial values not supported |
| `Buffer : Array[0..9] of Int;` | ❌ **silently dropped** — spaces in the type |
| nested `STRUCT … END_STRUCT` | ❌ **silently dropped** |

Dropped members produce no error — you get a DB that imported cleanly with fields missing. The regex
also scans the whole source, not just between `VAR`/`END_VAR`, so anything shaped like a declaration
anywhere in the text becomes a member. Always `read_block` afterwards to confirm what landed, or hand-write
the XML and use `import_block_xml`.

### Instance DB

```powershell
$body = @{
    name           = "FB_SpeedControl_DB"
    instanceOfName = "FB_SpeedControl"
    number         = $null   # or a specific int
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:5000/api/devices/S7-1200/blocks/instance-db" `
    -Method POST -Body $body -ContentType "application/json"
```

---

## SimaticML XML rules

These rules were discovered through live import attempts against TIA Portal V20.

### GlobalDB — minimal valid template

This is exactly what `XmlHelper.CreateGlobalDbXml` emits.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V20" />
  <SW.Blocks.GlobalDB ID="0">
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <Interface>
        <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
          <Section Name="Static">
            <Member Name="MyVar" Datatype="Real" />
          </Section>
        </Sections>
      </Interface>
      <Name>MyDB</Name>
      <Namespace />
      <ProgrammingLanguage>DB</ProgrammingLanguage>
    </AttributeList>
    <ObjectList />
  </SW.Blocks.GlobalDB>
</Document>
```

`AutoNumber` is `true` when no number is given, `false` when one is — and the number goes on the
element as `Number="…"`, not as a child element.

### FB/FC/OB skeleton

This is exactly what `XmlHelper.CreateSclBlockXml` emits — all seven sections, for every block type.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V20" />
  <SW.Blocks.FB ID="0" Number="1">
    <AttributeList>
      <AutoNumber>false</AutoNumber>
      <Name>FB_MyBlock</Name>
      <ProgrammingLanguage>SCL</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4">
              <Parts /><Wires />
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>SCL</ProgrammingLanguage>
        </AttributeList>
      </SW.Blocks.CompileUnit>
      <!-- Sections for SCL blocks go in ObjectList, not AttributeList -->
      <Section xmlns="…/SW/Interface/v5" Name="Input" />
      <Section xmlns="…/SW/Interface/v5" Name="Output" />
      <Section xmlns="…/SW/Interface/v5" Name="InOut" />
      <Section xmlns="…/SW/Interface/v5" Name="Static" />
      <Section xmlns="…/SW/Interface/v5" Name="Temp" />
      <Section xmlns="…/SW/Interface/v5" Name="Constant" />
      <Section xmlns="…/SW/Interface/v5" Name="Return" />
      <Source Name="BlockSource">BASE64_ENCODED_SCL_HERE</Source>
    </ObjectList>
  </SW.Blocks.FB>
</Document>
```

`xmlns` is shown abbreviated above; the real value is
`http://www.siemens.com/automation/Openness/SW/Interface/v5` on every `Section`.

Note the `Return` section is emitted for FBs too — see **Known contradictions**.

### Common import errors

| Error message | Cause | Fix |
|---|---|---|
| `Missing ''Namespace'' identifier attribute` | `<Namespace />` is missing from `<AttributeList>` — or was set as an XML attribute (`Namespace=""`) instead | Add `<Namespace />` as a **child element** of `<AttributeList>`. TIA calls these "identifier attributes" but means child elements |
| `Cannot import multilingual text with culture 'en-US'` | A `<MultilingualText>` block carries a `<Culture>` the project doesn't have (e.g. project is `en-GB`) | Use `<ObjectList />` — comment blocks are optional |
| `Class of the 'Siemens.Engineering.Section' type is not supported` | `<Section>` elements placed directly in `<ObjectList>` for a GlobalDB | For GlobalDB, `Interface/Sections` goes inside `<AttributeList>` |
| `Section 'Return' is not valid for this block` | `<Section Name="Return">` included in an FB | See **Known contradictions** — the generator emits it unconditionally |
| `Error when calling method 'Import'` (generic) | Catch-all for malformed XML | Export an existing block of the same type from TIA Portal and diff against your XML |

**Key distinction by block type:**

| Element | SCL block (FB/FC/OB) | GlobalDB |
|---|---|---|
| `Interface/Sections` | Inside `<ObjectList>` | Inside `<AttributeList>` |
| `<CompileUnit>` | Required in `<ObjectList>` | Not present |
| `<Namespace />` | Not emitted | Required in `<AttributeList>` |
| SCL source | Base64 in `<Source Name="BlockSource">` | Not applicable — members only |

---

## Tag table XML

`XmlHelper.CreateTagTableXml` builds this format, used by both `import_tag_table` and
`batch_rename_tags`.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V20" />
  <SW.Tags.PlcTagTable ID="0" CompositionName="TagTables">
    <AttributeList>
      <Name>Default tag table</Name>
    </AttributeList>
    <ObjectList>

      <SW.Tags.PlcTag ID="2" CompositionName="Tags">
        <AttributeList>
          <DataTypeName>Bool</DataTypeName>
          <ExternalAccessible>true</ExternalAccessible>
          <ExternalVisible>true</ExternalVisible>
          <ExternalWritable>false</ExternalWritable>
          <LogicalAddress>%I0.0</LogicalAddress>
          <Name>I_StartButton</Name>
        </AttributeList>
        <ObjectList />   <!-- safest: omit comments entirely -->
      </SW.Tags.PlcTag>

    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>
```

**Rules:**
- Every element in `ObjectList` needs a **unique integer `ID`** — start at 2 and increment for every
  element *and* sub-element.
- `ExternalWritable`: `false` for inputs (`%I`), `true` for outputs (`%Q`) and memory (`%M`).
- Addresses: `%I0.0`, `%Q0.0`, `%MW0`, `%MD0`, `%IW0`, etc.
- **Import replaces the table, it does not merge.** `TagTables.Import` uses `ImportOptions.Override`,
  so the imported XML becomes the table's full contents — any tag you leave out is gone. This is why
  `BatchRenameTagsAsync` re-emits every tag in the table, not just the renamed ones. Always read the
  table first, modify, and write the whole thing back.
- **Comment blocks are risky** — `CreateTagTableXml` emits `<Culture>en-US</Culture>` per tag. See
  **Known contradictions**.

---

## Locale / culture gotcha

TIA Portal projects store a language setting (e.g. `en-GB`, `en-US`, `de-DE`). Any
`<MultilingualText>` block with a `<Culture>` that doesn't match the project fails with:

```
Cannot import multilingual text with culture 'en-US': the specified culture does not exist within the current project.
```

**Rule for hand-written XML:** never include `<MultilingualText>` comment blocks — use `<ObjectList />`.
Block and tag comments are optional and the import succeeds without them.

To find a project's culture, export any existing block or tag table and read the `<Culture>` value.

---

## Known contradictions

Unresolved conflicts between this guide's rules and what the code actually emits. Test each on a
throwaway project before trusting either side.

**1. `CreateTagTableXml` and `CreateInstanceDbXml` hardcode `<Culture>en-US</Culture>.**
Both violate the locale rule above ([`XmlHelper.cs`](src/TiaOpennessMcpServer/Utilities/XmlHelper.cs),
the instance-DB template and the per-tag comment block). If the culture rule is right, then
`create_instance_db`, `import_tag_table` and `batch_rename_tags` all fail on a non-en-US project.
Either the generators need `<ObjectList />`, or the rule is narrower than stated. **Not yet tested
against an en-GB project.**

**2. `CreateSclBlockXml` emits `<Section Name="Return" />` for FBs.**
The error table above says `Return` is invalid for an FB, yet the generator emits it for FB, FC and OB
alike — and `create_block` is reported working. Either TIA V20 tolerates an empty `Return` section on
an FB, or FB creation is broken in a way nobody has hit. **Needs one live `create_block` with `type=FB`.**

**3. Approval-dialog frequency.** Documented as "every new process connection". A `connect` against an
already-running stdio process returned immediately with no dialog on 2026-08-03 — consistent with the
approval persisting for the life of the process, but that run may simply have been approved earlier.

---

## Rebuild workflow

The exe is locked while running. Kill it first, then build:

```powershell
taskkill /F /IM TiaPortalDashboard.exe
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj -c Release
.\src\TiaOpennessMcpServer\bin\Release\net48\TiaPortalDashboard.exe
```

The desktop shortcut (`Start TIA Dashboard.bat`) does this automatically — it checks whether any
`.cs`, `.csproj`, or `dashboard.html` is newer than the exe and rebuilds before launching.

For stdio mode, Claude Code spawns the exe itself; restart Claude Code (or reconnect the MCP server)
after a rebuild.

After restarting: connect and approve the TIA Portal dialog. **The project is not affected by
restarting the dashboard.**

---

## Standard workflow

```
1. Open TIA Portal V20 with the project
2. Start TiaPortalDashboard.exe  (or let Claude Code spawn it via stdio)
3. connect_to_tia_portal          (approve the TIA Portal dialog)
4. list_devices                   →  note the exact device name
5. create_block  type=GlobalDB    for each data block
6. create_block  type=FB          for each function block
7. create_instance_db             for each FB instance
8. import_tag_table               for tag tables  (send the FULL table — import overwrites)
9. compile_block                  on each new block, check for errors
10. save_project
```

`get_project_signature` before and after is a cheap way to diff what changed.

---

## Server-side Openness API notes

- All TIA Openness calls must run on the STA thread via `StaTaskScheduler.RunAsync()`. Never call them
  from a thread pool thread.
- `PlcBlockComposition.Import(FileInfo, ImportOptions.Override)` — creates or overwrites a block.
- `PlcTagTableGroup.TagTables.Import(FileInfo, ImportOptions.Override)` — replaces a tag table.
- Block and tag-table lookup recurses through user groups (`PlcBlockUserGroup`, `PlcTagTableUserGroup`),
  so nested folders are found — but names must be unique across folders, since the first match wins.
- `read_block` works by exporting to `C:\Temp\TiaExports` and reading the file back. If the block has
  never been compiled the export can fail; the returned `sourceCode` then contains a `//` comment
  explaining why. Compile the project in TIA Portal (Ctrl+B) and retry.
- `PlcExternalSourceComposition.CreateFromFile(name, path)` + `GenerateBlocksFromSource(GenerateBlockOption.None)`
  — an alternative way to create SCL blocks from `.scl` files directly. Not currently used by the server.
- `block.Export(new FileInfo(path), ExportOptions.WithDefaults)` gives a reference XML to diff against
  when debugging import errors.
