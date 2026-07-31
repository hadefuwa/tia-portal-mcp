# TIA Portal Dashboard

A web-based dashboard that lets you read and edit your TIA Portal V20 project directly in a browser — no extra software or cloud connection needed. It connects to TIA Portal while it's running on your PC and serves a local website at `http://localhost:5000`.

> **Who is this for?** Anyone working with Siemens S7 PLCs who wants a faster way to browse blocks, view/edit SCL code, inspect tag tables, and check which products a project depends on — without clicking through TIA Portal menus.

---

## What it can do

| Feature | Description |
|---|---|
| **Browse blocks** | See all OBs, FBs, FCs, and DBs in the sidebar |
| **Read & edit SCL code** | View and save SCL source directly in the browser |
| **Raw XML editor** | View and import block XML for any language (LAD, FBD, STL, GRAPH) |
| **Compile blocks** | Trigger compilation and see the result inline |
| **Analyse SCL** | Scan SCL code for issues (unbalanced blocks, nested IFs, etc.) |
| **Browse tag tables** | See every tag, its data type, address, and comment |
| **Used products** | List the products/option packs the project references |
| **Save project** | Save the TIA Portal project from the browser |

---

## Requirements

Before you start, make sure you have:

- **Windows 10 or 11** (TIA Portal only runs on Windows)
- **Siemens TIA Portal V20** installed and licensed
- **Your account added to the `Siemens TIA Openness` Windows group** (see setup step 3)
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

The output is a single `.exe` file at:
```
src/TiaOpennessMcpServer/bin/Release/net48/TiaPortalDashboard.exe
```

### 3. Add yourself to the TIA Openness security group

TIA Portal requires your Windows account to be in a specific group before any external tool can connect to it. You only need to do this once.

1. Open **Computer Management** (right-click Start → Computer Management)
2. Go to **Local Users and Groups → Groups**
3. Double-click **Siemens TIA Openness**
4. Click **Add** and add your Windows username
5. Click OK, then **sign out and back in to Windows** for it to take effect

> Without this step the dashboard will show a security error when you try to connect.

### 4. Run the dashboard

1. Open your project in TIA Portal V20
2. Double-click `TiaPortalDashboard.exe` (or run it from PowerShell)
3. A browser tab opens automatically at `http://localhost:5000`
4. Click **Connect to TIA Portal** — the dashboard reads your open project

---

## How to use it

### Browsing blocks

Once connected, your PLC devices appear in the left sidebar. Click a device name to expand it and see its blocks. Click any block to open it in the main panel.

### Editing SCL code

SCL blocks show a code editor in the browser. Make your changes and click **Save SCL** to write the code back to TIA Portal. Then click **Compile** to check for errors.

> Only SCL blocks have readable/editable source code via the TIA Portal API. LAD, FBD, STL, and GRAPH blocks are stored as graphical data — you can still view and edit their raw XML (see below).

### Raw XML editing

Every block — regardless of language — can be viewed as XML. For SCL blocks, switch to the **Raw XML** tab. For LAD/graphical blocks the XML view is shown by default.

Edit the XML directly in the browser, then click **Save XML** to import it back into TIA Portal. This is equivalent to exporting a block, editing the file, and reimporting it.

> Be careful with XML edits — an invalid XML file will fail to import and TIA Portal will report an error.

### Viewing tag tables

Click **Load tables** under a device's Tag Tables section in the sidebar to see all tag tables. Click a table name to view every tag with its type, address, and comment.

### Used products

Click **📦 Option Packages** in the sidebar to see which Siemens products the project references (e.g. StartDrive, Safety). This list comes directly from the project and is read-only.

To remove a product dependency, you need to do it inside TIA Portal:
1. In TIA Portal, go to **Options → Support packages** in the menu bar, or right-click the project → **Properties**
2. Find the option packages / used products section
3. Select the entry and remove it
4. Save the project

---

## Troubleshooting

**"Security error — not a member of Siemens TIA Openness group"**
You need to complete setup step 3 above. Make sure you signed out and back in after being added to the group.

**"No running TIA Portal process found"**
TIA Portal must be open with a project loaded before you click Connect.

**"Inconsistent blocks and PLC data types (UDT) cannot be exported"**
Your project has UDT changes that haven't been compiled. In TIA Portal, press **Ctrl+B** to compile everything, then refresh the block in the dashboard.

**The browser doesn't open automatically**
Navigate to `http://localhost:5000` manually.

**Build fails with "Siemens.Engineering.dll not found"**
The project expects TIA Portal V20 to be installed at the default path (`C:\Program Files\Siemens\Automation\Portal V20`). If yours is installed elsewhere, update the `HintPath` entries in the `.csproj` file.

---

## How it works (technical summary)

The dashboard is a **.NET Framework 4.8** console application that:

1. Resolves the `Siemens.Engineering.dll` from your TIA Portal installation at startup
2. Attaches to the running TIA Portal process using the **TIA Portal Openness API**
3. All API calls are marshalled through a dedicated **STA (Single-Threaded Apartment) thread** because TIA Openness is a COM-based library that requires this
4. Serves a single-page HTML dashboard over **`System.Net.HttpListener`** on port 5000
5. The browser communicates with the backend via a simple JSON REST API

> **.NET Framework 4.8 is required** — not .NET 5/6/7/8. The TIA Openness library uses `System.Runtime.Remoting` which was removed from newer .NET versions.

---

## Project structure

```
src/TiaOpennessMcpServer/
├── Program.cs                  # HTTP server, routing
├── dashboard.html              # Single-page frontend (served at /)
├── Services/
│   ├── TiaPortalService.cs     # Connect/disconnect, project info, used products
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

## Licence

MIT
