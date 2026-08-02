# TIA Portal Dashboard

A desktop app that lets you read and edit your TIA Portal V20 project through a built-in browser window — no extra software or cloud connection needed. It connects to TIA Portal while it's running on your PC and opens a native Windows window at startup.

> **Who is this for?** Anyone working with Siemens S7 PLCs who wants a faster way to browse blocks, view/edit SCL code, inspect tag tables, and manage their project — without clicking through TIA Portal menus. It also exposes all tools to Claude AI via MCP so you can describe changes in plain English and have Claude implement them.

> **New to TIA Portal Openness?** See [What is TIA Portal Openness?](docs/what-is-tia-openness.md) for a plain-English explanation of the API this project is built on.

> **Ready to use?** See the [User Manual](docs/user-manual.md) for a full walkthrough of both operating modes.

---

## Two modes

| Mode | Tab | How to use it |
|------|-----|---------------|
| **User Control** | Left tab | Fill in fields and click Run — fully manual, no AI |
| **Agent Control** | Right tab | Claude calls tools automatically via MCP — describe what you want in plain English |

Both modes talk to the same REST server and the same TIA Portal connection.

---

## What it can do

| Feature | Description |
|---|---|
| **Browse blocks** | See all OBs, FBs, FCs, and DBs in the sidebar |
| **Read & edit SCL code** | View and save SCL source directly in the window |
| **Raw XML editor** | View and import block XML for any language (LAD, FBD, STL, GRAPH) |
| **Compile blocks** | Trigger compilation and see the result inline |
| **Analyse SCL** | Scan SCL code for issues (unbalanced blocks, nested IFs, etc.) |
| **Create blocks** | Generate new FB, FC, OB, or GlobalDB from SCL source |
| **Create instance DBs** | Create a new Instance DB linked to any FB |
| **Browse tag tables** | See every tag, its data type, address, and comment |
| **Import tag tables** | Import a complete tag table from SimaticML XML |
| **Batch rename tags** | Rename many tags at once in a single atomic operation |
| **Project signature** | Full index of every block and tag table across all devices |
| **Clone project** | Duplicate the open project to a new folder with all hardware, blocks, and tags |
| **Used products** | List the products/option packs the project references (e.g. StartDrive, Safety) |
| **Save project** | Save the TIA Portal project without switching to TIA Portal |
| **Claude integration** | All tools exposed via MCP so Claude can call them automatically |

---

## Requirements

Before you start, make sure you have:

- **Windows 10 or 11** (TIA Portal only runs on Windows)
- **Siemens TIA Portal V20** installed and licensed
- **Your account added to the `Siemens TIA Openness` Windows group** (see setup step 3)
- **Microsoft Edge** installed (comes with Windows 10/11 — needed for the built-in browser window)
- **.NET Framework 4.8** — already included in Windows 10/11, nothing to install
- **.NET SDK 8 or later** — needed to build the project ([download here](https://dotnet.microsoft.com/download))

---

## Setup

### 1. Clone the repository

Open PowerShell and run:

```bash
git clone https://github.com/hadefuwa/tia-portal-mcp.git
cd tia-portal-mcp
```

### 2. Build the project

```bash
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj -c Release
```

The output goes to:
```
src/TiaOpennessMcpServer/bin/Release/net48/TiaPortalDashboard.exe
```

### 3. Add yourself to the TIA Openness security group

TIA Portal requires your Windows account to be in a specific security group before any external tool can connect to it. You only need to do this once.

1. Open **Computer Management** (right-click Start → Computer Management)
2. Go to **Local Users and Groups → Groups**
3. Double-click **Siemens TIA Openness**
4. Click **Add** and enter your Windows username
5. Click OK, then **sign out and back in** for it to take effect

> Without this step the dashboard will show a security error when you try to connect.

### 4. Run the dashboard

1. Open your project in **TIA Portal V20**
2. Double-click `TiaPortalDashboard.exe`
3. A native desktop window opens with the dashboard inside it
4. Click **Connect to TIA Portal** — the dashboard reads your open project

TIA Portal will show an access approval dialog — click **Yes to all** to approve. This dialog appears **every time a new instance of the exe connects**, not just the first time. Each app restart will require one approval click.

> The window minimises to the **system tray** rather than closing. Right-click the tray icon to exit completely.

---

## How to use it

See the [User Manual](docs/user-manual.md) for a full walkthrough of both the User Control and Agent Control tabs, with a reference for every tool.

### Quick start — User Control (manual)

Once connected, your PLC devices appear in the left sidebar. Click a device to expand it. Click **Blocks** to list all blocks; click any block name to read it. Use **Run Tool** in the sidebar to access all tools with a form interface.

### Quick start — Agent Control (Claude)

1. Switch to the **Agent Control** tab in the dashboard.
2. Copy the MCP config snippet shown there and paste it into Claude Desktop's developer config.
3. Restart Claude Desktop.
4. In a new conversation, ask Claude to do something: *"List the blocks on PLC_1"*, *"Create an FB called SpeedControl with a ramp function"*, *"Rename all tags in the IO_Mapping table — prefix with HMI_"*.

Claude calls the tools automatically and shows you every step.

---

## Connecting Claude Code (stdio MCP)

Claude Code, Cursor, and VS Code Copilot use **stdio transport** — they spawn the exe directly and communicate over stdin/stdout. No dashboard window is needed.

### Step 1 — Build the exe

```bash
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj -c Release
```

### Step 2 — Add to `~/.claude/.mcp.json`

Create (or edit) `C:\Users\<you>\.claude\.mcp.json`:

```json
{
  "mcpServers": {
    "tia-portal": {
      "command": "C:\\path\\to\\tia-portal-mcp\\src\\TiaOpennessMcpServer\\bin\\Release\\net48\\TiaPortalDashboard.exe",
      "args": ["--mcp-stdio"]
    }
  }
}
```

> Use `~/.claude/.mcp.json`, **not** `~/.claude/settings.json`. The settings file has no `mcpServers` field and will silently ignore it.

### Step 3 — Start TIA Portal and connect

Before asking Claude Code to use the tools, make sure TIA Portal is open and you've run a connect call once (either via the dashboard or via the MCP `connect_to_tia_portal` tool). Claude Code will spawn a new headless instance of the exe; TIA Portal will show its approval dialog — click **Yes to all**.

---

## Connecting Claude Desktop (MCP setup)

The dashboard exposes all tools over MCP (Model Context Protocol) on `http://localhost:5000/mcp`. Claude Desktop connects to it as a **Custom Connector** — not via the config file.

### Step 1 — Add the connector in Claude Desktop UI

1. Open Claude Desktop → **Settings** → **Connectors** → **Add custom connector**
2. Enter the URL: `http://localhost:5000/mcp`
3. Save and restart Claude Desktop

> **Do not use the config file for this.** Claude Desktop's `claude_desktop_config.json` only supports `stdio` transport (local executables). HTTP MCP servers must be added through the Connectors UI. If you add `"type": "http"` to the config file, Claude Desktop will not recognise it and the tools will not appear.

### Step 2 — Verify tools appear

Start a new conversation in Claude Desktop. Click the tools/hammer icon — you should see all 20 TIA Portal tools listed. If they don't appear, see the MCP troubleshooting section below.

### What the server reports

- **URL**: `http://localhost:5000/mcp`
- **Protocol version**: MCP `2025-03-26` (with automatic fallback to `2024-11-05` for older clients)
- **Transport**: Streamable HTTP (POST to `/mcp`)

---

## Troubleshooting

### App / TIA Portal

**"Security error — not a member of Siemens TIA Openness group"**
Complete setup step 3. Make sure you signed out and back in after being added to the group.

**"No running TIA Portal process found"**
TIA Portal must be open with a project loaded before you click Connect.

**TIA Portal approval dialog keeps appearing on every restart**
This is expected behaviour — TIA Portal Openness shows the "Allow external access" dialog on every new process connection, not just the first time. Each time you restart the dashboard or the stdio exe a new connection is made and a new approval is required. Keep an eye on the TIA Portal taskbar button; the dialog sometimes appears behind other windows.

**"Inconsistent blocks and PLC data types (UDT) cannot be exported"**
Your project has UDT changes that haven't been compiled. In TIA Portal, press **Ctrl+B** to compile everything, then retry.

**"Another project is already open" during clone**
This is handled automatically by the clone feature. If you see it in other operations, disconnect from TIA Portal and reconnect.

**Build fails with "Siemens.Engineering.dll not found"**
The project expects TIA Portal V20 at the default path (`C:\Program Files\Siemens\Automation\Portal V20`). If yours is installed elsewhere, update the `HintPath` entries in the `.csproj` file.

**The window doesn't open / WebView2 error**
Make sure Microsoft Edge is installed and up to date. The built-in browser window uses the Edge WebView2 runtime, which ships with Edge on Windows 10/11.

### MCP / Claude Desktop

**Tools don't appear in Claude Desktop after adding the connector**
- Confirm the dashboard app is running (the MCP server only runs while the app is open).
- Make sure you added the connector via **Settings → Connectors → Add custom connector**, not via the config file. HTTP connectors added to `claude_desktop_config.json` are silently ignored.
- Restart Claude Desktop after adding the connector — it only loads tools at startup.
- Open a browser and navigate to `http://localhost:5000/mcp`. You should get a `405 Method Not Allowed` JSON response. If you get a connection error, the app is not running.

**The connector shows "connected" but no tools appear**
This is usually a protocol version mismatch. The server implements MCP `2025-03-26`. If you are using an older Claude Desktop build that sends `2024-11-05` in its `initialize` request, the server will negotiate down automatically. If tools still don't appear, check the Live call log in the Agent Control tab to see if any `initialize` calls are arriving.

**"type": "http" in claude_desktop_config.json doesn't work**
Claude Desktop's config file (`%APPDATA%\Claude\claude_desktop_config.json`) only supports `stdio` entries — local executables started by Claude Desktop. HTTP MCP servers are not supported via this file. Use the Connectors UI instead (Settings → Connectors).

**"Missing 'Namespace' identifier attribute" when creating a GlobalDB**
This error appears when the SimaticML XML has `Namespace` as an XML attribute on the element (`<SW.Blocks.GlobalDB Namespace="">`) instead of as a child element inside `<AttributeList>`. The correct form is `<Namespace />` inside `<AttributeList>`. This is handled correctly by the built-in `create_block` tool; you would only see this if crafting XML manually.

**"Cannot import multilingual text with culture 'en-US'" when creating a block**
The `<MultilingualText>` comment blocks in SimaticML XML include a `<Culture>` tag that must match the project's language. A project created in British English (`en-GB`) will reject `en-US`. The built-in block templates omit the comment section entirely to avoid this — if you are writing custom XML, use `<ObjectList />` for the ObjectList instead of including a MultilingualText entry.

**My code changes aren't reflected after restarting the app**
The desktop shortcut (`Start TIA Dashboard.bat`) automatically checks whether any source file (`.cs`, `.csproj`, `dashboard.html`) is newer than the compiled exe and rebuilds before launching. If the build fails, the bat window will pause and show the error — fix it and run the shortcut again. To build manually:

```bash
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
```

---

## How it works (technical summary)

The dashboard is a **.NET Framework 4.8** WinForms application that:

1. Resolves `Siemens.Engineering.dll` from your TIA Portal installation at startup
2. Attaches to the running TIA Portal process using the **TIA Portal Openness API**
3. Runs all API calls through a dedicated **STA (Single-Threaded Apartment) thread** — required because TIA Openness is a COM-based library
4. Serves the dashboard UI over `System.Net.HttpListener` on port 5000
5. Displays the UI in a native **WinForms window** with an embedded **WebView2** (Edge-based) browser control
6. The browser communicates with the backend via a simple JSON REST API

> **.NET Framework 4.8 is required** — not .NET 5/6/7/8. The TIA Openness library depends on `System.Runtime.Remoting`, which was removed from modern .NET.

---

## Project structure

```
src/TiaOpennessMcpServer/
├── Program.cs                  # HTTP server, routing, app entry point
├── MainForm.cs                 # WinForms window + WebView2 + system tray
├── dashboard.html              # Single-page frontend
├── Services/
│   ├── TiaPortalService.cs     # Connect/disconnect, project clone, used products
│   ├── SoftwareService.cs      # Blocks — list, read, write SCL, write XML, compile
│   ├── HardwareService.cs      # Device enumeration
│   ├── TagService.cs           # Tag tables
│   └── SclAnalyzerService.cs   # SCL static analysis
├── Models/                     # Data transfer objects
└── Utilities/
    ├── StaTaskScheduler.cs     # STA thread wrapper for COM calls
    ├── XmlHelper.cs            # Block XML parse/patch helpers
    └── NetFxPolyfills.cs       # C# 9/11 types missing from net48
```

---

## Further reading

- [User Manual](docs/user-manual.md) — full guide to both tabs, all tools, MCP setup, and troubleshooting
- [What is TIA Portal Openness?](docs/what-is-tia-openness.md) — plain-English guide to the API
- [Siemens TIA Portal Openness documentation](https://support.industry.siemens.com/cs/document/109792902) — official Siemens overview and links
- [TIA Portal Openness system manual (PDF)](https://support.industry.siemens.com/cs/ww/en/view/109748523) — full API reference

---

## Licence

MIT
