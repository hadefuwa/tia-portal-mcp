# CLAUDE.md — TIA Portal MCP Server

Project-level guidance for Claude Code working in this repo.

---

## What this project is

A .NET Framework 4.8 app that exposes TIA Portal V20 as an MCP server. Two transports:

- **HTTP** — dashboard window running at `http://localhost:5000`. Used by Claude Desktop (via Custom Connectors UI) and direct REST calls.
- **stdio** — headless mode (`--mcp-stdio` flag). Used by Claude Code, Cursor, VS Code Copilot. The `.mcp.json` at `~/.claude/.mcp.json` points to the exe with this flag.

Both transports share the same `HandleMcpRequest` method in `Program.cs`.

---

## Build and run

```powershell
# Build
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj -c Release

# Run dashboard (HTTP + WinForms window)
.\src\TiaOpennessMcpServer\bin\Release\net48\TiaPortalDashboard.exe

# Run headless stdio MCP (for Claude Code)
.\src\TiaOpennessMcpServer\bin\Release\net48\TiaPortalDashboard.exe --mcp-stdio
```

The `.bat` shortcut on the desktop auto-detects source changes and rebuilds before launching.

**Important**: the running exe locks the binary. You must kill it before rebuilding.

```powershell
taskkill /F /IM TiaPortalDashboard.exe
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj -c Release
```

---

## STA thread requirement

All TIA Openness API calls **must** run on the STA (Single-Threaded Apartment) thread. The `StaTaskScheduler` handles this. Every service method wraps its work in `await _sta.RunAsync(() => { ... })`. Never call TIA Openness objects from a non-STA thread.

---

## Connecting to TIA Portal

`POST /api/connect` calls `tia.AttachToRunningAsync()` which blocks the STA thread while TIA Portal shows its access approval dialog. This has two consequences:

1. **The HTTP response does not return until the user clicks "Yes to all".** Long-poll is fine; set a client timeout of at least 90 seconds.
2. **TIA Portal shows the approval dialog on every new process connection**, not just the first time. Each app restart = one new approval dialog. This is a TIA Openness constraint, not a bug.

If `AttachToRunningAsync` is already in-flight (STA thread blocked waiting for approval), subsequent API calls that require the STA thread will queue. Status checks that only read `_portal is not null` still return immediately.

---

## SimaticML XML — GlobalDB format

TIA Portal's XML importer is strict about GlobalDB structure. Key findings from live testing:

### `<Namespace />` is a child element, not an XML attribute

**Wrong** (causes "Missing 'Namespace' identifier attribute" error):
```xml
<SW.Blocks.GlobalDB ID="0" Namespace="">
```

**Correct** (matches what TIA Portal itself exports):
```xml
<SW.Blocks.GlobalDB ID="0">
  <AttributeList>
    ...
    <Namespace />          <!-- empty child element inside AttributeList -->
    <ProgrammingLanguage>DB</ProgrammingLanguage>
  </AttributeList>
```

TIA Portal calls these "identifier attributes" — they are child elements of `<AttributeList>`, not XML attributes on the parent element. The error message is misleading because it says "attribute" but means child element.

### Culture codes in `<MultilingualText>` must match the project

Any `<MultilingualText>` comment block with `<Culture>en-US</Culture>` will fail to import into a project set to `en-GB`. Solution: omit the comment ObjectList entirely or use `<ObjectList />`. Block comments are optional.

**Wrong**:
```xml
<ObjectList>
  <MultilingualText ID="1" CompositionName="Comment">
    <ObjectList>
      <MultilingualTextItem ID="2" CompositionName="Items">
        <AttributeList>
          <Culture>en-US</Culture>   <!-- fails in en-GB projects -->
          <Text />
        </AttributeList>
```

**Correct**:
```xml
<ObjectList />
```

### Interface section goes inside `<AttributeList>`, not `<ObjectList>`

GlobalDB member declarations live in `<AttributeList> > <Interface> > <Sections> > <Section Name="Static">`. They do **not** use `<CompileUnit>` (that's for SCL/LAD/FBD blocks).

### Minimal valid GlobalDB template

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

---

## HTTP API — key patterns

- **JSON is camelCase** (`PropertyNamingPolicy = CamelCase`). Request bodies must use camelCase keys (`content`, not `Content`).
- **Enums are strings** (`JsonStringEnumConverter`). Use `"GlobalDB"`, `"SCL"`, etc.
- The `WriteBlockXmlAsync` endpoint (`POST /api/devices/{device}/blocks/{block}/xml`) uses `ImportOptions.Override` — it creates the block if it doesn't exist, which makes it useful for creating new blocks when the create endpoint has issues.

---

## Claude Code `.mcp.json`

MCP server config for Claude Code lives at `~/.claude/.mcp.json` (not `settings.json`, which has no `mcpServers` field):

```json
{
  "mcpServers": {
    "tia-portal": {
      "url": "http://localhost:5000/mcp"
    }
  }
}
```

For stdio mode (no dashboard required):
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

## Export directory

Generated and temp XML files go to `C:\Temp\TiaExports` (configurable in `appsettings.json` under `TiaOpenness.ExportDirectory`). Useful for debugging — the XML written before each import call is left on disk.

---

## LAD generation — `create_lad_block`

`LadXmlBuilder` (`Utilities/`) turns a structured rung into SimaticML `FlgNet`. The model never writes UIds or wires. Reference exports live in `docs/lad-samples/`; tests are in `tests/LadXmlBuilder.Tests` (`dotnet test`, XML only, no TIA).

Supported: NO/NC contact, coil / set (`scoil`) / reset (`rcoil`), compare (`eq ne gt ge lt le`, `dataType` default Int), edge (`pbox`/`nbox`, operand = edge-memory Bool), timers `ton`/`tof`/`tp`, counters `ctu`/`ctd`, flip-flops `sr`/`rs`, parallel `branch`, arithmetic `add`/`mul` (2+ `operands`) and `sub`/`div`/`mod` (exactly 2), `norm_x`/`scale_x`, `move`, `call` of an FB (instance DB or `#multi`) or FC with named `parameters`, and a generic `part` for system functions such as `WR_SYS_T`. `move`, arithmetic and an eno-less `part` are DisabledENO boxes: they must be the last element and the network can have no outputs. `sr`/`rs`/`ctu`/`ctd` take a second power path in `other`. Blocks: FB, FC, OB. Add anything else from a real export, not from memory.

**Confirmed from real V20 exports** (these are what the builder copies):
- FlgNet namespace is `.../NetworkSource/FlgNet/v5` in V20.
- `Contact`/`Coil`/`SCoil`/`RCoil` pins are `in`, `operand`, `out`. NC is a child `<Negated Name="operand" />`, not an attribute.
- `TON` needs `Version="1.0"`, `<TemplateValue Name="time_type" Type="Type">Time</TemplateValue>`, pins `IN`/`PT`/`Q`/`ET`. Unused `ET` is written as a wire to `<OpenCon UId=…/>`.
- A source driving several pins is **one** `<Wire>` with several `<NameCon>` (the Powerrail feeding two parallel contacts is a single wire).
- Parallel branches merge into a `Part Name="O"` with `<TemplateValue Name="Card" Type="Cardinality">N</…>` and pins `in1…inN`/`out`. Branch paths share the incoming wire; an empty path cannot be expressed.
- `T#5s` is a `TypedConstant`; a numeric literal is a `LiteralConstant` with `<ConstantType>`.
- Interface/`Namespace` rules are the same as GlobalDB above: `<Namespace />` is a child element; omit `MultilingualText` unless the project culture is passed (`culture`), or en-US vs en-GB breaks the import.

**Verified live on V20 (imported, compiled with 0 errors, re-exported; goldens are `docs/lad-samples/05-08`, tests in `LiveGoldenTests.cs`):**
- Seal-in FB (branch + NC contact + coil, `#local` operands); FC (`Ret_Val`/`Void` is kept by TIA); OB (`SecondaryType` `ProgramCycle`).
- `TON` with `#Tmr` declared in Static as `TON`: plain `Instance Scope="LocalVariable"` component. TIA re-exports the member as `TON_TIME`.
- A block referencing tags that do not exist **imports**, then fails compile with `Tag "X" not defined.` The error text is in nested compiler messages (`CompileBlockAsync` now flattens them).
- Compare `Eq/Ne/Gt/Ge/Lt/Le` (pins `pre`, `in1`, `in2`, `out`; `SrcType` template), `PBox`/`NBox` (pins `in`, `bit`, `out`), `Move` (`DisabledENO="true"`, pins `en`, `in`, `out1`; no `eno`), and `TOF`/`TP` (same pins and `time_type` as `TON`) all import and compile. Goldens `09`.
- `Call` of an FC with params, an FB with an instance DB, and an FB multi-instance (`#Sub`), plus a param-less call: all compile. `CallInfo` holds `Instance` then `Parameter` elements; output params are wired `NameCon -> IdentCon`. Goldens `10`-`13`. TIA fills in the callee's parameter list on import and re-exports unwired `eno`/outputs as `OpenCon`, so tests compare only the wires the builder writes.
- Arithmetic: `Add`/`Mul` are `DisabledENO` with `Card=N` + `<AutomaticTyped Name="SrcType" />` (pins `en`, `in1..N`, `out`); `Sub`/`Div`/`Mod` the same without `Card`. `Normalize`/`Scale_X`: `SrcType`+`DestType` templates, pins `en`, `eno`, `min`, `value`, `max`, `out`. `Sr` pins `s`, `r1`, `operand`, `q`; `Rs` pins `r`, `s1`, `operand`, `q`. `CTU`/`CTD` (member types `CTU_INT`/`CTD_INT`): pins `CU`/`CD`, `R`/`LD`, `PV`, `Q`, `CV` (open). TIA tolerated `QU`/`QD` for CTU/CTD on import but re-exports `Q`, so the builder emits `Q`. System function `WR_SYS_T` as a generic `Part` (`Version`, `date_type` template, pins `en`, `IN`, `RET_VAL`) compiles. Goldens `14`-`22`.
- Round-trip: re-importing unchanged exports of an FB, an FC and an FB with timers under new names (`Name` replaced, `Number` removed, `AutoNumber` true) imports and compiles cleanly.
- TIA renumbers UIds on re-export, so goldens compare wiring topology, not text.

**Found and fixed from live errors:**
- OB: TIA refuses an auto-numbered ProgramCycle OB (`'Number' attribute is missing`). The service now picks the lowest free number >= 123 (dryRun shows 123).
- OB: interface may only have Input/Temp/Constant; an `Output`/`InOut` section is rejected (`Section 'Output' is not valid for this block`). The builder omits them and throws on OB output/inOut/static members.
- `CreateInstanceDbXml` had the `Namespace`-as-attribute bug and an en-US comment; fixed (verified: instance DB of an FB imports).
- Import fails with "not supported in online mode" if the PLC is online: go offline first.

**Still NOT verified:**
- Timer with a non-`#` instance name (`Scope="GlobalVariable"`): it imports but compile fails with `Missing instance DB`, because the DB does not exist. An IEC timer DB cannot be created via `InstanceDB` with `InstanceOfName` `TON`/`TON_TIME` (`Block does not exist`). Needs a real export of a hand-made one (drop a TON in LAD with "single instance") before the builder or `create_instance_db` can support it.
- Call parameter section `InOut` (emitted like an input; not exercised live). Call parameters need an explicit `datatype`; the builder does not look the callee up.
- CTUD (up/down counter), `Rs`/`Sr` with more than one set/reset input, and arithmetic on mixed types are not built. The generic `part` with `eno:true` (power flow continuing from a system function) is not exercised live. Other system functions (WWW, CTRL_PWM, ...) work only if their pin names are copied from a real export.
- Only checked on an S7-1200; S7-1500 may differ.

**Workflow:** `create_lad_block` refuses to replace an existing block unless `overwrite:true`, runs a pre-flight check (unique UIds, wire endpoints resolve, no input pin driven twice, XML well-formed) before touching TIA, and returns the compile output. Compiling is not correctness: have the user review the logic.

---

## WinCC Unified HMI tag API — confirmed behaviours

Tested against TIA Portal V20 with WinCC Unified. Use `HmiTagService` for all HMI tag operations.

### Reading tags — works fully

```csharp
// Get HmiSoftware
var sw = deviceItem.GetService<SoftwareContainer>()?.Software as HmiSoftware;

// Iterate tables
foreach (HmiTagTable table in sw.TagTables)
    foreach (HmiTag tag in table.Tags.Cast<HmiTag>())
        tag.GetAttribute("PlcTag");   // returns plcTag string
        tag.GetAttribute("DataType"); // returns type string
        tag.GetAttribute("Comment");  // returns comment string
```

**"ConnectionName" attribute does NOT exist on HmiTag in WinCC Unified.** Reading it via `GetAttribute("ConnectionName")` throws — use a try/catch and return "".

### Finding tag tables — use Find(), not indexer

```csharp
// WRONG — indexer takes int, not string:
var table = sw.TagTables["Default tag table"];  // CS1503

// CORRECT — Find() returns null if not found:
var table = sw.TagTables.Find("Default tag table");
```

### Creating tags — partial support only

```csharp
// Create() with ONE parameter — works:
var tag = table.Tags.Create(tagName);

// Create() with TWO parameters — throws "Tag table not found":
var tag = table.Tags.Create(tagName, dataType);  // DO NOT USE

// SetAttribute("DataType", ...) — works after Create():
tag.SetAttribute("DataType", "Bool");

// SetAttribute("PlcTag", ...) — throws for newly created tags via API:
tag.SetAttribute("PlcTag", "DI_A_0");  // "controller tag not found"
// The tag IS created successfully as an Internal tag (Connection = "<Internal tag>").
// The PlcTag link must be set manually in TIA Portal UI after the tag exists.
// Workaround: omit PlcTag from Create calls; user links tags to PLC in TIA Portal UI.
```

### WinCC Unified forcing faceplate — struct tag requirement

The forcing screen uses a faceplate template with a `Display_Tag` interface parameter. The template internally accesses `Display_Tag.State`, `.ForcedState`, `.ForcedStatus`. This means:

- The `Display_Tag` must be a **struct-type** HMI tag with those sub-members
- Individual Bool tags (`DI_A_0`, `DQ_A_0`) cannot replace struct references in the faceplate
- If the `IO → IO.IO` struct HMI tag is deleted, the entire forcing screen breaks
- **Fastest fix**: recreate the `IO` HMI tag (type: 1214C CPU, PlcTag: IO.IO) in TIA Portal UI

### PowerShell API calls — use System.Net.WebRequest for POST with body

`Invoke-WebRequest` has issues with body serialization on Windows PowerShell 5.1. Always use raw `WebRequest`:

```powershell
$bytes = [System.Text.Encoding]::UTF8.GetBytes($jsonBody)
$wr = [System.Net.WebRequest]::Create($uri)
$wr.Method = "POST"; $wr.ContentType = "application/json"; $wr.ContentLength = $bytes.Length
$s = $wr.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
try {
    $r = $wr.GetResponse()
    (New-Object System.IO.StreamReader($r.GetResponseStream())).ReadToEnd()
} catch [System.Net.WebException] {
    (New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd()
}
```
