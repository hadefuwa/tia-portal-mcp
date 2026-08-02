# TIA Portal MCP Server — AI Development Guide

Everything learned from live testing against TIA Portal V20. Read this before attempting any automation.

---

## Architecture

```
Claude Code / Cursor / VS Code        Claude Desktop
  (stdio transport)                   (HTTP transport)
        │                                    │
        │  newline-delimited JSON-RPC         │  HTTP POST /mcp
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
├── Program.cs                  # HTTP listener, routing, stdio loop, MCP handler
├── Services/SoftwareService.cs # Blocks
├── Services/TagService.cs      # Tag tables
├── Services/TiaPortalService.cs# Connect / project ops
└── Utilities/XmlHelper.cs      # SimaticML XML builders
```

**Export/temp files:** `C:\Temp\TiaExports\` (all XML written before import is left on disk — useful for debugging)

---

## Transport modes

### HTTP (dashboard + Claude Desktop)

```
http://localhost:5000
```

Start the exe normally. Claude Desktop connects via **Settings → Connectors → Add custom connector** with the URL above. Do **not** add it to `claude_desktop_config.json` — that file only supports stdio entries.

### stdio (Claude Code / Cursor / VS Code Copilot)

Run with `--mcp-stdio`. The process communicates entirely over stdin/stdout; no window, no HTTP server.

`~/.claude/.mcp.json` (not `settings.json`):
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

---

## TIA Portal approval dialog

**This dialog appears on every new process connection, not just the first time.** Each app restart or new stdio spawn = one new approval required. The dialog sometimes appears behind other windows — check the TIA Portal taskbar button.

`POST /api/connect` blocks until the user clicks **Yes to all**. Set timeouts to at least 90 seconds. While the STA thread is blocked waiting for approval, any other STA-bound operation queues behind it.

---

## API Endpoints

All at `http://localhost:5000`. All JSON. **Keys are camelCase** (`content`, not `Content`).

### Connection

| Method | Path | Notes |
|--------|------|-------|
| GET | `/api/status` | Returns `{connected, project}` — never blocks |
| POST | `/api/connect` | Attaches to running TIA Portal; blocks until user approves |
| POST | `/api/project/save` | Saves the open project |

### Devices

| Method | Path | Notes |
|--------|------|-------|
| GET | `/api/devices` | Lists all devices |

Device names look like `"S7-1200 station_1"`. URL-encode spaces when using in path segments.

### Blocks

| Method | Path | Body fields | Notes |
|--------|------|-------------|-------|
| GET | `/api/devices/{device}/blocks` | — | List all blocks |
| GET | `/api/devices/{device}/blocks/{block}` | — | Read block (XML + SCL source) |
| POST | `/api/devices/{device}/blocks` | `name, type, number?, language, sourceCode` | Create new block |
| POST | `/api/devices/{device}/blocks/{block}/xml` | `content` | Import raw SimaticML XML (creates or overwrites) |
| PUT | `/api/devices/{device}/blocks/{block}/scl` | `source` | Patch SCL in existing block |
| POST | `/api/devices/{device}/blocks/{block}/compile` | — | Compile; returns result string |
| POST | `/api/devices/{device}/blocks/instance-db` | `name, instanceOfName, number?` | Create instance DB |

### Tags

| Method | Path | Body fields | Notes |
|--------|------|-------------|-------|
| GET | `/api/devices/{device}/tags` | — | List tag tables |
| GET | `/api/devices/{device}/tags/{table}` | — | Get all tags in a table |
| POST | `/api/devices/{device}/tags/import` | `content` | Import tag table from SimaticML XML |

---

## Creating blocks

### SCL blocks (FB, FC, OB)

Pass SCL source as `sourceCode` in the create request. It is base64-encoded and stored in a `<Source Name="BlockSource">` element in the SimaticML XML before import.

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

Invoke-RestMethod -Uri "http://localhost:5000/api/devices/S7-1200 station_1/blocks" `
    -Method POST -Body $body -ContentType "application/json" -TimeoutSec 30
```

**SCL rules for TIA Portal V20:**
- Block name in the declaration must match the `name` field.
- `{ S7_Optimized_Access := 'TRUE' }` is optional but standard.
- FB sections: `VAR_INPUT`, `VAR_OUTPUT`, `VAR_IN_OUT`, `VAR` (static), `VAR_TEMP`.
- FBs do **not** have a `Return` section. FCs do.
- `R_TRIG`, `F_TRIG`, `TON`, `TOF` are valid IEC types — declare them in `VAR`.

### GlobalDB

Pass SCL `DATA_BLOCK...END_VAR` as `sourceCode`. The server parses the `VAR` section to extract member names and types, then generates correct SimaticML XML.

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

Invoke-RestMethod -Uri "http://localhost:5000/api/devices/S7-1200 station_1/blocks" `
    -Method POST -Body $body -ContentType "application/json" -TimeoutSec 30
```

### Instance DB

```powershell
$body = @{
    name           = "FB_SpeedControl_DB"
    instanceOfName = "FB_SpeedControl"
    number         = $null   # or a specific int
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:5000/api/devices/S7-1200 station_1/blocks/instance-db" `
    -Method POST -Body $body -ContentType "application/json"
```

---

## SimaticML XML rules

These rules were discovered through live import attempts against TIA Portal V20.

### GlobalDB — minimal valid template

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

### FB/FC skeleton

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
      <Section xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5" Name="Input" />
      <Section xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5" Name="Output" />
      <Section xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5" Name="Static" />
      <Section xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5" Name="Temp" />
      <Source Name="BlockSource">BASE64_ENCODED_SCL_HERE</Source>
    </ObjectList>
  </SW.Blocks.FB>
</Document>
```

### Common import errors

| Error message | Cause | Fix |
|---|---|---|
| `Missing ''Namespace'' identifier attribute` | `<Namespace />` is missing from `<AttributeList>` — OR it was set as an XML attribute on the element (`Namespace=""`) instead | Add `<Namespace />` as a child element of `<AttributeList>` |
| `Cannot import multilingual text with culture 'en-US'` | `<MultilingualText>` comment block has a `<Culture>` that doesn't exist in the project (e.g. project is `en-GB`) | Use `<ObjectList />` — comment blocks are optional |
| `Class of the 'Siemens.Engineering.Section' type is not supported` | `<Section>` elements placed directly in `<ObjectList>` for a GlobalDB | For GlobalDB, `Interface/Sections` goes inside `<AttributeList>`, not `<ObjectList>` |
| `Section 'Return' is not valid for this block` | `<Section Name="Return">` included in an FB | FBs have no Return section; only FCs do |
| `Error when calling method 'Import'` (generic) | Catch-all for malformed XML | Export an existing block of the same type from TIA Portal and diff against your XML |

**Key distinction by block type:**

| Element | SCL block (FB/FC/OB) | GlobalDB |
|---|---|---|
| `Interface/Sections` | Inside `<ObjectList>` | Inside `<AttributeList>` |
| `<CompileUnit>` | Required in `<ObjectList>` | Not present |
| `<Namespace />` | Not required | Required in `<AttributeList>` |
| SCL source | Base64 in `<Source Name="BlockSource">` | Not applicable — members only |

---

## Tag table XML

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
        <!-- Comment is optional. If included, culture MUST match the project language. -->
        <!-- Safest: omit the ObjectList entirely, or use <ObjectList /> -->
        <ObjectList />
      </SW.Tags.PlcTag>

    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>
```

**Rules:**
- Every element in `ObjectList` needs a **unique integer `ID`** — start at 2, increment for every element and sub-element.
- `ExternalWritable`: `false` for inputs (`%I`), `true` for outputs (`%Q`) and memory (`%M`).
- Addresses: `%I0.0`, `%Q0.0`, `%MW0`, `%MD0`, `%IW0`, etc.
- Importing to an existing table merges/overwrites tags of the same name.
- **Do not include `<Culture>en-US</Culture>` in comment blocks** unless the project was created with US English — use `<ObjectList />` to omit the comment safely.
- The `XmlHelper.CreateTagTableXml` helper generates this format correctly.

---

## Locale / culture gotcha

TIA Portal projects store a language setting (e.g. `en-GB`, `en-US`, `de-DE`). Any `<MultilingualText>` block with a `<Culture>` tag that doesn't match the project will fail with:

```
Cannot import multilingual text with culture 'en-US': the specified culture does not exist within the current project.
```

**Solution:** Never include `<MultilingualText>` comment blocks in hand-crafted XML. Use `<ObjectList />` instead. Block and tag comments are optional — the import succeeds without them.

To find what culture a project uses, export any existing block or tag table and look at the `<Culture>` value inside the exported XML.

---

## Rebuild workflow

The exe is locked while running. Kill it first, then build:

```powershell
taskkill /F /IM TiaPortalDashboard.exe
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj -c Release
.\src\TiaOpennessMcpServer\bin\Release\net48\TiaPortalDashboard.exe
```

The desktop shortcut (`Start TIA Dashboard.bat`) does this automatically — it checks whether any `.cs`, `.csproj`, or `dashboard.html` is newer than the exe and rebuilds before launching.

After restarting: call `POST /api/connect` and approve the TIA Portal dialog. **The project is not affected by restarting the dashboard.**

---

## Standard workflow

```
1. Open TIA Portal V20 with the project
2. Start TiaPortalDashboard.exe
3. POST /api/connect  (approve TIA Portal dialog)
4. GET /api/devices   →  note the exact device name
5. POST /api/devices/{device}/blocks  (type=GlobalDB)     for each data block
6. POST /api/devices/{device}/blocks  (type=FB, SCL)      for each function block
7. POST /api/devices/{device}/blocks/instance-db          for each FB instance DB
8. POST /api/devices/{device}/tags/import                 for tag tables
9. POST /api/project/save
```

---

## Server-side Openness API notes

- All TIA Openness calls must run on the STA thread via `StaTaskScheduler.RunAsync()`. Never call them from a thread pool thread.
- `PlcBlockComposition.Import(FileInfo, ImportOptions.Override)` — creates or overwrites a block from SimaticML XML.
- `PlcTagTableGroup.TagTables.Import(FileInfo, ImportOptions.Override)` — imports a tag table.
- `PlcExternalSourceComposition.CreateFromFile(name, path)` + `GenerateBlocksFromSource(GenerateBlockOption.None)` — alternative way to create SCL blocks from `.scl` files directly.
- SCL source in exports uses a tokenised `StructuredText` format for LAD/FBD blocks; for SCL blocks created via the API, the source is stored as base64 in `<Source Name="BlockSource">`.
- Export an existing block with `block.Export(new FileInfo(path), ExportOptions.WithDefaults)` to get a reference XML you can diff against when debugging import errors.
