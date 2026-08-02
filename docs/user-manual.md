# TIA Portal Dashboard — User Manual

## Overview

The TIA Portal Dashboard has two operating modes, accessible as tabs at the top of the window:

| Tab | Who uses it | What it does |
|-----|-------------|--------------|
| **User Control** | You (human) | Run tools manually — fill in fields and click Run |
| **Agent Control** | Claude AI via MCP | Claude calls tools automatically over the MCP protocol |

Both tabs talk to the same REST server running inside the app (port 5000) and the same TIA Portal Openness connection. The difference is who's driving.

---

## Getting started

1. Open your project in **TIA Portal V20**.
2. Launch the app from your desktop shortcut (`Start TIA Dashboard.bat`).
3. The dashboard opens in a native Windows window.
4. Click **Connect to TIA Portal** (top of the left sidebar) — the status bar turns green and shows your project name.

> First-time only: TIA Portal will show an Openness access approval dialog. Click **Yes to all**. This is a one-time step per installation.

---

## User Control tab

The User Control tab is for manual, human-driven work. Use it when you want to inspect or change something right now without involving Claude.

### Sidebar

The left sidebar has three sections:

**Quick Actions** — one-click buttons at the top:
- **Connect** — attach to the running TIA Portal process
- **Save** — save the TIA Portal project
- **Status** — poll the current connection and project info
- **Devices** — list all PLCs and HMIs in the project

**Tree** — shows devices and their contents once connected. Click a device to expand it; click **Blocks** or **Tag Tables** to load them into the main panel.

**Tools** — a categorised list of all available tools. Clicking a tool name opens it in the main panel (same as the Run Tool view).

### Run Tool

Click **Run Tool** in the sidebar (or any tool name in the Tools section) to open the tool runner. Select a tool from the chip grid, fill in any required parameters, and click **▶ Run Tool**. The JSON response appears below.

**Available tools by category:**

| Category | Tools |
|----------|-------|
| Connection | connect_to_tia_portal, get_status |
| Hardware | list_devices |
| Blocks | list_blocks, read_block, write_block_scl, import_block_xml, compile_block, analyze_block, create_block, create_instance_db |
| Tags | list_tag_tables, get_tags, import_tag_table, batch_rename_tags |
| Analysis | analyze_scl |
| Project | save_project, clone_project, get_option_packages, get_project_signature |

### Docs & API Reference

Click **Docs** in the sidebar to access built-in reference material:

- **Quick Start** — connection and first steps
- **Claude Capabilities** — table of all 16 things Claude can do in TIA Portal, with status and how-it-works
- **TIA Openness API** — PlcBlock class hierarchy, attribute reference, service table, namespace list
- **Troubleshooting** — common errors and fixes
- **MCP Setup** — how to connect Claude Desktop to this server

---

## Agent Control tab

The Agent Control tab shows the MCP (Model Context Protocol) server that Claude uses to call tools automatically. Switch to this tab to see live status and monitor what Claude is doing.

### What you see

- **Capabilities** — grid of all 16 things Claude can do, with live status badges (🟢 Ready / 🟡 Needs endpoint / etc.)
- **MCP Server** — connection URL and Claude Desktop config snippet to copy-paste
- **Available tools** — full table of every tool Claude can call, with its description
- **Live call log** — last 50 MCP tool calls made by Claude, with timestamp and success/failure

### Connecting Claude to the server

1. Make sure the app is running (the server starts automatically at launch).
2. Open **Claude Desktop** → Settings → Developer → Edit Config.
3. Paste the config shown in the Agent Control tab:

```json
{
  "mcpServers": {
    "tia-portal": {
      "type": "http",
      "url": "http://localhost:5000/mcp"
    }
  }
}
```

4. Save and restart Claude Desktop.
5. In Claude Desktop, start a new conversation. The TIA Portal tools will appear in the tool list (hammer icon).

### Using Claude in a conversation

Once connected, you can ask Claude things like:

- *"List all the FBs on PLC_1"*
- *"Read block Conveyor_Control and tell me what it does"*
- *"Create a new FB called SpeedRamp with a ramp-up/ramp-down function in SCL"*
- *"Rename all tags in the IO_Mapping table — prefix everything with HMI_"*
- *"Show me the project signature — what blocks exist across all devices?"*

Claude will call the tools automatically, show you what it's doing, and ask for confirmation before writing anything.

---

## Tool reference

### Connection tools

**connect_to_tia_portal**
Attaches to the running TIA Portal V20 process. TIA Portal must be open with a project loaded. Returns the project name and path on success.

**get_status**
Returns the current connection state and, if connected, the project name and path. Use this to confirm the server is alive and connected.

---

### Hardware tools

**list_devices**
Returns all devices in the open project — PLCs, HMIs, drives. Each entry has a name, type, and type identifier. Use this to get the device name you need for block and tag operations.

---

### Block tools

**list_blocks** · `device`
Lists every block (OB, FB, FC, DB) on a device, including block number, language, and modification date. Searches recursively through all subgroups.

**read_block** · `device`, `block`
Reads a block's full content — SCL source (if available), raw XML, language, type, number, author, and modification date. For LAD/FBD/GRAPH blocks the SCL field is empty; the raw XML is always returned.

**write_block_scl** · `device`, `block`, `source`
Overwrites the SCL source of a block. Exports the block XML, patches the source section, and reimports. Always call **compile_block** afterwards to check for errors.

**import_block_xml** · `device`, `block`, `content`
Imports raw SimaticML XML into a block. Use this for LAD, FBD, STL, and GRAPH blocks, or when you have a complete XML to write. The block is replaced by the import (Override mode).

**compile_block** · `device`, `block`
Compiles a block using the TIA Openness compiler service. Returns the compiler state, error count, warning count, and all compiler messages with line numbers.

**analyze_block** · `device`, `block`
Runs static SCL analysis on a block without compiling it. Checks for common issues: unbalanced `IF`/`END_IF`, nested control structures, variables declared but unused, and more. Returns a list of findings with severity and line numbers.

**create_block** · `device`, `name`, `type`, `sourceCode`, `[number]`
Creates a new block (FB, FC, OB, or GlobalDB) from SCL source. Generates a SimaticML XML skeleton, imports it, and returns the new block info. Block type must be one of: `FB`, `FC`, `OB`, `GlobalDB`.

**create_instance_db** · `device`, `name`, `instanceOfName`, `[number]`
Creates a new Instance DB linked to an FB. Generates the SimaticML XML with the correct `InstanceOfName` attribute and imports it. If `number` is omitted, TIA Portal assigns one automatically.

---

### Tag tools

**list_tag_tables** · `device`
Lists all tag tables on a device with their names and tag counts. Searches recursively through all subgroups.

**get_tags** · `device`, `table`
Returns all tags in a tag table — name, data type, logical address, accessibility flags, and comment.

**import_tag_table** · `device`, `content`
Imports a complete tag table from SimaticML XML content. Creates a new table or replaces an existing one (Override mode). The XML must follow the `SW.Tags.PlcTagTable` SimaticML format.

**batch_rename_tags** · `device`, `table`, `renames`
Renames multiple tags in a single atomic operation. Reads the current table, applies the rename map, generates new XML, and reimports. The `renames` parameter is a JSON array of `{"from": "OldName", "to": "NewName"}` pairs.

---

### Analysis tools

**analyze_scl** · `source`, `[blockName]`, `[blockType]`
Runs static SCL analysis on any SCL text without needing an open block. Useful for checking code before writing it to TIA Portal.

---

### Project tools

**save_project**
Saves the currently open TIA Portal project. Equivalent to pressing Ctrl+S in TIA Portal.

**clone_project** · `name`, `path`
Clones the open project to a new folder. Exports all blocks and tag tables, creates a new project with the same hardware configuration, and imports everything. The original project is reopened afterwards.

**get_option_packages**
Lists all option packages and used products referenced by the project — for example StartDrive, Safety, or TIA Portal Comfort Panels. Useful for auditing what licenses a project requires.

**get_project_signature** *(new)*
Returns a complete index of the entire project: every device, every block (name, type, number, language, consistency state), and every tag table (name, tag count). Use this to understand the full project structure at a glance, or to compare before and after a change.

---

## Tips

- **Refresh after writes**: After writing SCL or XML, always compile the block to catch errors early.
- **Auto-number**: Leave the `number` field empty on create_block and create_instance_db to let TIA Portal assign the next available number.
- **Batch rename JSON format**: The `renames` field for batch_rename_tags must be valid JSON: `[{"from":"Old","to":"New"},{"from":"Old2","to":"New2"}]`
- **Tag table XML**: Use get_tags on an existing table, then ask Claude to generate the import XML in the same format — this is the easiest way to create a new table from a spreadsheet.
- **App must stay open**: The MCP server runs inside the app process. If you close the dashboard window (it goes to the system tray — right-click tray icon to exit), Claude loses the connection.

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| "Security error — not a member of Siemens TIA Openness group" | Add your Windows account to the `Siemens TIA Openness` local group (Computer Management → Local Users and Groups → Groups) and sign out/in |
| "No running TIA Portal process found" | TIA Portal must be open with a project loaded before clicking Connect |
| "Inconsistent blocks cannot be exported" | Press Ctrl+B in TIA Portal to compile everything, then retry |
| My changes don't appear after restarting the app | The app bat file runs `bin\Release\net48\TiaPortalDashboard.exe` — rebuild with `dotnet build -c Release` or the dashboard will run old code |
| Claude says "Unknown tool" | Restart Claude Desktop after editing the MCP config — it only loads tools at startup |
| Live call log is empty | The log only fills when Claude calls a tool via MCP (`/mcp` endpoint), not when you use User Control manually |
