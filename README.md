# TIA Portal Dashboard

A desktop app that lets you read and edit your TIA Portal V20 project through a built-in browser window — no extra software or cloud connection needed. It connects to TIA Portal while it's running on your PC and opens a native Windows window at startup.

> **Who is this for?** Anyone working with Siemens S7 PLCs who wants a faster way to browse blocks, view/edit SCL code, inspect tag tables, and manage their project — without clicking through TIA Portal menus.

> **New to TIA Portal Openness?** See [What is TIA Portal Openness?](docs/what-is-tia-openness.md) for a plain-English explanation of the API this project is built on.

---

## What it can do

| Feature | Description |
|---|---|
| **Browse blocks** | See all OBs, FBs, FCs, and DBs in the sidebar |
| **Read & edit SCL code** | View and save SCL source directly in the window |
| **Raw XML editor** | View and import block XML for any language (LAD, FBD, STL, GRAPH) |
| **Compile blocks** | Trigger compilation and see the result inline |
| **Analyse SCL** | Scan SCL code for issues (unbalanced blocks, nested IFs, etc.) |
| **Browse tag tables** | See every tag, its data type, address, and comment |
| **Clone project** | Duplicate the open project to a new folder with all hardware, blocks, and tags |
| **Used products** | List the products/option packs the project references (e.g. StartDrive, Safety) |
| **Save project** | Save the TIA Portal project without switching to TIA Portal |

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

The first time TIA Portal Openness is used it will show an access approval dialog — click **Yes to all** to approve. This is a one-time step.

> The window minimises to the **system tray** rather than closing. Right-click the tray icon to exit completely.

---

## How to use it

### Browsing blocks

Once connected, your PLC devices appear in the left sidebar. Click a device name to expand it and see its blocks. Click any block to open it in the main panel.

### Editing SCL code

SCL blocks show a code editor. Make your changes and click **Save SCL** to write the code back to TIA Portal. Then click **Compile** to check for errors.

> Only SCL blocks have readable source code via the TIA Portal Openness API. LAD, FBD, STL, and GRAPH blocks are stored as graphical data — you can still view and edit their raw XML (see below).

### Raw XML editing

Every block — regardless of language — can be viewed as XML. For SCL blocks, switch to the **Raw XML** tab. For LAD/graphical blocks the XML view is shown by default.

Edit the XML in the browser, then click **Save XML** to import it back into TIA Portal. This is equivalent to exporting a block, editing the file, and reimporting it.

> Invalid XML will fail to import and TIA Portal will show an error — always check your edits before saving.

### Cloning a project

Click **Clone Project** in the sidebar to duplicate the currently open project. Give the clone a name and choose a destination folder. The dashboard will:

1. Export all blocks and tag tables from the source project
2. Close the source project temporarily (required by TIA Portal — only one project can be open at a time)
3. Create the new project and recreate the hardware configuration
4. Import all blocks and tag tables into the new project
5. Reopen the original project so you can keep working

> After cloning, the dashboard reconnects to the original project automatically.

### Viewing tag tables

Click **Load tables** under a device's Tag Tables section in the sidebar. Click a table name to view every tag with its type, address, and comment.

### Used products

Click **Option Packages** in the sidebar to see which Siemens products the project references. This list comes directly from the project file and is read-only — to remove a product dependency you need to do it inside TIA Portal.

---

## Troubleshooting

**"Security error — not a member of Siemens TIA Openness group"**
Complete setup step 3. Make sure you signed out and back in after being added to the group.

**"No running TIA Portal process found"**
TIA Portal must be open with a project loaded before you click Connect.

**"Inconsistent blocks and PLC data types (UDT) cannot be exported"**
Your project has UDT changes that haven't been compiled. In TIA Portal, press **Ctrl+B** to compile everything, then retry.

**"Another project is already open" during clone**
This is handled automatically by the clone feature. If you see it in other operations, disconnect from TIA Portal and reconnect.

**Build fails with "Siemens.Engineering.dll not found"**
The project expects TIA Portal V20 at the default path (`C:\Program Files\Siemens\Automation\Portal V20`). If yours is installed elsewhere, update the `HintPath` entries in the `.csproj` file.

**The window doesn't open / WebView2 error**
Make sure Microsoft Edge is installed and up to date. The built-in browser window uses the Edge WebView2 runtime, which ships with Edge on Windows 10/11.

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

- [What is TIA Portal Openness?](docs/what-is-tia-openness.md) — plain-English guide to the API
- [Siemens TIA Portal Openness documentation](https://support.industry.siemens.com/cs/document/109792902) — official Siemens overview and links
- [TIA Portal Openness system manual (PDF)](https://support.industry.siemens.com/cs/ww/en/view/109748523) — full API reference

---

## Licence

MIT
