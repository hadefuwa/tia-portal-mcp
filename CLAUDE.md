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
