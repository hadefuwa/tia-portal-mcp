# TIA Portal MCP Server — Implementation Plan

Gap-closing roadmap for [hadefuwa/tia-portal-mcp](https://github.com/hadefuwa/tia-portal-mcp), derived from a comparison against
[Czarnak/tia-portal-mcp](https://github.com/Czarnak/tia-portal-mcp),
[eponce00/tiaopen-mcp](https://github.com/eponce00/tiaopen-mcp),
[chewcw/tia-portal-openness-mcpserver](https://github.com/chewcw/tia-portal-openness-mcpserver),
and the [Openness V17–V21 feature matrix](https://t-ia-connect.com/en/compatibility-tia-portal-openness).

**Target repo path:** copy this file to `docs/implementation-plan.md` in the tia-portal-mcp repo.

---

## Current state

| | |
|---|---|
| MCP tools exposed | **38** (`McpToolDefs()`) — was 20 before Phase 0 |
| Dispatch | `McpDispatch()` — single `switch` on tool name |
| Transport | HTTP (`localhost:5000/mcp`) **and stdio** (`--mcp-stdio`) |
| Runtime | .NET Framework 4.8, WinForms host, STA thread via `StaTaskScheduler` |
| Openness | V20; attach-to-running, plus `open_project` from disk |

> **Correction to the original draft.** This plan was written believing the transport was HTTP-only.
> It is not — stdio landed in commit `402a815` on 2026-08-02, complete with the stdout-hygiene rule
> and the Claude Code config snippet in the README. Phase 4.1 was already done before it was written.
> See the note in Phase 4.

---

## Phase 0 — Wire up what already exists ✅ CODE COMPLETE

**Estimated ~4 hours; shipped as 18 tools rather than 15. Not yet validated against a live project.**

Eighteen service methods were implemented but absent from `McpToolDefs()`, so Claude could not call
them. All eighteen are now wired. They split into two groups with very different risk:

### 0a — Exercised by the REST routes

These already round-trip over the dashboard's HTTP API, so the service code behind them works.

| New tool | Backing method | Proven at |
|---|---|---|
| `list_hmi_tag_tables` | `HmiTagService.ListTagTablesAsync` | `GET /api/devices/{device}/hmi/tags` |
| `get_hmi_tags` | `HmiTagService.GetTagsAsync` | `GET /api/devices/{device}/hmi/tags/{table}` |
| `get_all_hmi_tags` | `HmiTagService.GetAllTagsAsync` | `GET /api/devices/{device}/hmi/tags/all` |
| `create_hmi_tags` | `HmiTagService.CreateTagsAsync` | `POST …/hmi/tags/{table}/create` |
| `list_hmi_screens` | `HmiScreenService.ListScreensAsync` | `GET …/hmi/screens` |
| `get_screen_tag_refs` | `HmiScreenService.GetScreenTagRefsAsync` | `GET …/hmi/screens/{screen}/tags` |
| `update_faceplate_tags` | `HmiScreenService.UpdateFaceplateTagsAsync` | `POST …/update-faceplate-tags` |
| `get_block_attributes` | `SoftwareService.GetBlockAttributeInfosAsync` | `GET …/blocks/{block}/attributes` |
| `patch_block_texts` | `SoftwareService.PatchBlockTextsAsync` | `PATCH …/blocks/{block}/texts` |

### 0b — Never executed before

The original draft listed these as "HTTP only". That was wrong: they had **no REST route and no caller
anywhere in the codebase**. They compile, but no line of them has ever run. Budget debugging time.

| New tool | Backing method |
|---|---|
| `open_project` | `TiaPortalService.OpenProjectAsync` |
| `close_project` | `TiaPortalService.CloseAsync` — was filed under Phase 1.5 as new work; it already existed |
| `export_block` | `SoftwareService.ExportBlockAsync` |
| `export_tag_table` | `TagService.ExportTagTableAsync` |
| `create_tag_table` | `TagService.CreateTagTableAsync` |
| `create_tag` | `TagService.CreateTagAsync` |
| `get_device` | `HardwareService.GetDeviceAsync` |
| `get_io_mapping` | `HardwareService.GetIoMappingAsync` — missing from the original table; directly relevant to the IO-Link work in 1.6 |
| `generate_s7_1200` | `HardwareService.GenerateS71200Async` |

### Tasks

- [x] Add 18 `case` arms to the `McpDispatch` switch, grouped and commented by risk tier.
- [x] Add 18 matching `McpT(...)` entries to `McpToolDefs()`. Verified: 38 cases, 38 definitions,
      no orphans on either side, no duplicates.
- [x] **Fix the `A()` helper.** Replaced with a total accessor family — `A`, `AN`, `AIN`, `AB`,
      `AObj<T>`, `AList<T>`, plus `RequireConfirm()`. Every one coerces what it can and falls back to a
      default rather than throwing, because models send `5` for an integer param about as often as `"5"`.
      Add `AI(key, def)` when the first tool with a defaulted int param lands (`limit`/`offset` on
      cross-references will want it) — it was left out rather than shipped unused.
      Migrated `create_block`, `create_instance_db` and `batch_rename_tags` onto them.
- [x] **Also extend the schema builder.** `McpP` only emitted `{type, description}`, so `renames` shipped
      as a bare `"array"` with its shape described in prose. Added `McpPArr`, `McpPArrOf` and `McpPObj`;
      the property tuple now carries a JSON Schema fragment instead of a type name. All existing
      `McpP(n, "string", …)` call sites are unchanged.
- [x] Flip `number` on `create_block` / `create_instance_db` from `"string"` to `"integer"` — the
      string declaration was a workaround for the `A()` bug, now removed.
- [x] WinCC constraint stated in the `create_hmi_tags` description, so Claude relays it.
- [x] Update tool counts in `README.md` and `TIA_PORTAL_MCP_GUIDE.md` (20 → 38), add the 18 new tools to
      the guide's tool table with a maturity note marking the 0b group.
- [x] Two service bugs found while wiring, both fixed: `ExportTagTableAsync` defaulted to a `.xlsx`
      filename for what Openness writes as XML, and neither export method created the parent directory
      of a caller-supplied path.
- [ ] **Validate against a live project** with an S7-1200 + WinCC Unified HMI. Not done — see below.

**Exit criteria:** `tools/list` returns 38 tools ✅; each new tool round-trips against a live project ❌.

**Verification status.** The machine this was written on has no TIA Portal install and only .NET SDK 5,
so the full project cannot be built here. The two riskiest new pieces were extracted and compiled
standalone against .NET 5: all 38 tool definitions build and emit valid nested JSON Schema, and the
accessor family builds and passes coercion tests (`5`→5, `"7"`→7, `true`/`"true"`→true, absent→default,
explicit null→default, malformed object→`InvalidOperationException`). Everything touching Siemens types
is **unverified** — build on a TIA Portal machine with SDK 8+ before trusting it.

---

## Phase 1 — Close the parity gaps

**Effort: ~2–3 days.** These are the features all three comparable servers have and this one does not.

### 1.1 Cross-references / where-used ⭐ highest-value single addition

Openness exposes cross-reference data per object. Czarnak ships this as `read_cross_references`.
Without it Claude cannot answer *"what uses this tag / DB / FB?"* — the most common real question.

- [ ] New `Services/CrossReferenceService.cs`.
- [ ] Tool `read_cross_references(device, objectName, [objectType], [direction])` → returns
      `{ source, target, referenceType, location, access }[]`.
      `direction`: `"usedBy"` (who references this) | `"uses"` (what this references) | `"both"`.
- [ ] Tool `find_unused(device, [kind])` — blocks/tags with zero incoming references. Cheap to layer on
      the same data and immediately useful for cleanup.
- [ ] Cap result size and paginate; a cross-reference dump on a large DB will blow the context window.

### 1.2 Delete / rename / move — the missing half of CRUD

Today the server can only create. Agent loops leave orphans behind with no way to tidy up.

- [ ] `delete_block(device, block, confirm)` — hard-require `confirm: true`.
- [ ] `rename_block(device, block, newName)`.
- [ ] `create_group(device, groupPath)` — create missing parents, like eponce00's `create_group`.
- [ ] `move_block_to_group(device, block, groupPath)`.
- [ ] `list_groups(device)` — Program Blocks groups + PLC data type groups.
- [ ] Extend `list_blocks` to return each block's `groupPath` (eponce00 does this; it makes moves reliable).

### 1.3 PLC data types (UDTs)

Currently a total blind spot. `create_instance_db` exists but the UDT it depends on is invisible.

- [ ] `list_data_types(device)` — recursive, with group path and consistency flag.
- [ ] `read_data_type(device, typeName)` — XML plus a parsed member list.
- [ ] `write_data_type(device, typeName, xml)` / `create_data_type(...)`.
- [ ] `create_type_group` / `move_type_to_group` (mirrors 1.2).

### 1.4 Compile at PLC and project scope

`compile_block` only ever sees one block; cross-block errors stay invisible until a human compiles manually.

- [ ] `compile_software(device)` — compile the whole PLC, return structured messages.
- [ ] `compile_project()` — compile everything.
- [ ] Return `{ state, errorCount, warningCount, messages[{severity, path, line, text}] }` from all three
      compile tools, not a flat string. Structured output is what makes the write→compile→fix loop converge.

### 1.5 Project lifecycle

- [x] ~~`close_project()`~~ — shipped in Phase 0; `CloseAsync()` already existed.
- [ ] `save_project_as(path)`, `archive_project(path)`.
- [x] ~~Expose `open_project`~~ — shipped in Phase 0. Still to do: make it create-or-open.
- [ ] `create_project(name, path, [version])`.

### 1.6 Hardware & network configuration

Directly relevant to the IO-Link work — adding a Balluff/ifm master to a PROFINET network is currently
impossible through the server. `HardwareService` has four methods, all now exposed
(`list_devices`, `get_device`, `get_io_mapping`, `generate_s7_1200`); only the last one writes, and
only for S7-1200 CPUs from a hardcoded order-number table. Start from `get_io_mapping` — it already
reads module/channel/address/direction, which is the data the tools below need to write.

- [ ] `search_hardware_catalog(query, [type])` — order numbers, firmware versions.
- [ ] `add_device(name, orderNumber, firmwareVersion, [deviceType])`.
- [ ] `delete_device(name, confirm)`.
- [ ] `list_device_items(device)` — modules, submodules, slots.
- [ ] `plug_device_item(device, orderNumber, slot)` / `unplug_device_item(device, slot)`.
- [ ] `get_device_attributes(target)` / `set_device_attribute(target, name, value)` — this is how IP
      addresses, PROFINET device names and module parameters are actually set.
- [ ] `list_subnets()` / `create_subnet(name, type)` / `connect_to_subnet(device, interface, subnet)`.
- [ ] `list_io_systems()` / `assign_io_device(ioDevice, ioController)`.

> **GSD/GSDML note:** installing a GSD file is a TIA Portal *application* operation
> (Options → Manage general station description files), not an Openness project operation. Do **not**
> promise an `install_gsd` tool. Once a GSD is installed by hand its devices appear in the hardware
> catalog, and `search_hardware_catalog` / `add_device` can then place them. Document this explicitly.

### 1.7 Libraries, types and master copies

- [ ] `list_libraries()` — global plus project library.
- [ ] `list_master_copies(library, [folder])`.
- [ ] `instantiate_master_copy(library, masterCopy, device, [groupPath])`.
- [ ] `list_library_types(library)` / `create_library_type(...)` / `update_type_version(...)`.

---

## Phase 2 — Openness features nobody has implemented

**Effort: ~3–4 days.** Supported by Openness V20; absent from every open-source MCP server surveyed.
This is the differentiator work.

### 2.1 Download / upload / online ⭐ closes the loop

Right now Claude writes SCL, compiles it, and a human still has to press Download. Openness V20 supports
`DownloadProvider`, `UploadProvider` and `OnlineProvider`.

- [ ] `go_online(device, [pgPcInterface], [address])` / `go_offline(device)`.
- [ ] `get_online_state(device)` — connected, run/stop, operating mode.
- [ ] `download_to_device(device, confirm, [options])` — hardware and/or software.
      **Gate hard:** require `confirm: true` *and* a preview call first (see 3.1). This is the one tool
      that can stop a running machine.
- [ ] `upload_from_device(device)`.
- [ ] `compare_online_offline(device)` — report differences.
- [ ] Surface download `ConfigurationDelta` callbacks as structured prompts rather than auto-answering
      them. Auto-answering a "stop the PLC?" dialog is not acceptable.

> **Safety gate:** ship `download_to_device` behind an explicit opt-in — an `appsettings.json` flag
> (`"AllowDownload": false` by default) *and* an `--allow-download` CLI switch. A read-only default
> posture is the right one for a tool an LLM drives.

### 2.2 Watch and force tables

Openness supports watch-table XML import/export. Cheap to add, immediately useful for testing.

- [ ] `list_watch_tables(device)`, `read_watch_table(device, table)`.
- [ ] `create_watch_table(device, table, entries[])`, `import_watch_table(device, xml)`,
      `export_watch_table(device, table)`.
- [ ] `list_force_tables(device)` / `read_force_table(device)` — read-only; do not add force writes.

### 2.3 Multilingual texts and text lists

- [ ] `list_project_languages()`, `set_editing_language(lang)`.
- [ ] `export_texts(device, language)` / `import_texts(device, xlf)`.
- [ ] `list_text_lists(device)` / `read_text_list(device, name)`.

### 2.4 Alarms, ProDiag, safety

- [ ] `list_alarms(device)`, `export_alarms(device)`.
- [ ] `list_prodiag_blocks(device)`, `read_prodiag_supervision(device, block)`.
- [ ] `list_safety_blocks(device)`, `read_safety_signature(device)` — read-only; do not write F-blocks.

### 2.5 System diagnostics

- [ ] `read_diagnostic_buffer(device, [count])` — requires an online connection (2.1).
- [ ] `get_module_diagnostics(device)`.

---

## Phase 3 — Ergonomics and safety

**Effort: ~2 days.** These make the tools usable in long agent sessions rather than adding new capability.

### 3.1 Preview / confirm token pattern for writes

Adopted from Czarnak. Every mutating tool gets a preview step returning a token bound to the operation list
and the current project state; the apply step refuses without a matching token.

- [ ] `preview_write(operations[])` → `{ token, effects[], warnings[] }`.
- [ ] `apply_write(token, confirm)` → executes only if the bound state still matches.
- [ ] **Do not bind to `GetProjectSignatureAsync()` as-is.** Two problems found while reading it:
      it stamps `CapturedAt = DateTime.UtcNow`, so two calls against an untouched project never compare
      equal; and it walks every device, recurses every block group and calls `Tags.Count` per tag table,
      which over Openness remoting is seconds to tens of seconds. Paying that twice per mutation makes
      every write feel broken. Bind to a **scoped** hash of the target block or tag table, computed over
      the signature fields with `CapturedAt` excluded, and keep the full-project walk for batch ops.
- [ ] Apply to at minimum: `delete_block`, `delete_device`, `download_to_device`, `batch_rename_tags`.
- [x] Interim: `RequireConfirm()` exists in `McpDispatch` and gates `close_project` and
      `generate_s7_1200`. It is a bare `confirm:true` check with no state binding — a placeholder for
      this section, not a substitute. Route new destructive tools through it until the token flow lands.

### 3.2 Batching

- [ ] `execute_read_batch(operations[])` — up to 50 reads in one round-trip.
- [ ] `execute_write_batch(operations[], token, confirm)` — up to 50 writes, sequential, fail-fast.
- [ ] Every COM call still goes through `StaTaskScheduler`; batch at the dispatch layer, not the COM layer.

### 3.3 Instruction reference

eponce00 bundles a V20 instruction reference so the model stops inventing SCL that will not compile.

- [ ] Bundle a V20 instruction table (name, category, signature, description) as an embedded resource.
- [ ] `lookup_instruction(query, [category])`.
- [ ] `list_instruction_categories()`.

### 3.4 Templates and LAD synthesis

- [ ] `list_templates()` / `get_template_xml(name)` / `preview_block(template, tokens)` — render XML without
      importing, so failures are cheap.
- [ ] `build_lad_block(device, name, type, flow)` — generate LAD SimaticML from a structured JSON rung
      description. Today the only path to LAD is hand-authored XML.
- [ ] `preflight_scl(source)` — ASCII check, reserved-keyword check, before import. Partly covered by
      `analyze_scl`; extend that rather than duplicating it.

### 3.5 Tool-surface profiles ✅ DONE

Brought forward from position 9 — at 38 tools the definitions already cost real context, and this was
pure C# that could be verified without a TIA Portal machine.

- [x] `TIA_MCP_PROFILE` env var / `--profile` switch: `lite` | `standard` | `full`. Default `full`,
      so existing configs are unaffected.
- [x] `lite` (10): `connect_to_tia_portal`, `get_status`, `save_project`, `list_devices`, `list_blocks`,
      `read_block`, `write_block_scl`, `compile_block`, `list_tag_tables`, `get_tags`.
      (`compile_software` and `read_cross_references` are in the draft's lite set but do not exist yet —
      add them to `liteTools` when 1.1 and 1.4 land.)
- [x] `standard` (33): lite plus code, tag and HMI editing. Excludes `clone_project`,
      `get_option_packages`, `open_project`, `close_project`, `generate_s7_1200`.
- [x] `full` (38): everything.
- [x] `tools/list` returns `McpToolDefsForProfile()`. **`McpDispatch` also re-checks** — hiding a tool
      that remains callable by a model that guessed the name is not a trimmed surface, it is a lie.
- [x] Verified: 10 / 33 / 38 partition, no stale names in either set, `generate_s7_1200` correctly
      refused under `lite`.

---

## Phase 4 — Infrastructure

**Effort: ~30 min for 4.2; ~1 week for 4.3.**

### 4.1 stdio transport ✅ ALREADY SHIPPED — no work required

This section was written from a mistaken reading of the repo. stdio landed in commit `402a815` on
2026-08-02, well before this plan existed.

- [x] `--mcp-stdio` branch in `Program.cs` reading newline-delimited JSON-RPC from stdin and writing to
      stdout, bypassing the HTTP listener and the WinForms window. It shares `HandleMcpRequest` with the
      HTTP path, so tools reach both transports automatically — this is why Phase 0 needed no
      transport-specific work.
- [x] **Nothing may write to stdout except JSON-RPC frames.** Enforced by
      `services.AddLogging(b => { if (!stdioMode) b.AddConsole(); … })`.
- [x] Ready-to-paste config snippet is in the README under "Connecting Claude Code (stdio MCP)".

### 4.2 Project path as CLI argument ✅ DONE

- [x] `--project "C:\Projects\X.ap20"` → calls `OpenProjectAsync()` at startup, in both stdio and HTTP
      modes. `--with-ui` opens the portal visibly; headless otherwise.
- [x] Failure is never fatal — the server still starts and writes the reason to **stderr**, so the
      session can fall back to `connect_to_tia_portal` and see why. stdout stays JSON-RPC only.
- [x] Removes one round-trip from every session and makes scripted runs deterministic.

### 4.3 Two-process architecture (.NET 8 + .NET 4.8)

Openness needs `System.Runtime.Remoting`, which pins the whole app to .NET Framework 4.8. Split it:

- [ ] **Worker (.NET 4.8):** owns every Openness COM object, STA thread, newline-delimited JSON over
      stdin/stdout.
- [ ] **Server (.NET 8):** MCP protocol, modern SDKs, delegates to the worker.
- [ ] Define the IPC envelope, handle worker crash/restart, propagate COM exceptions with context.
- [ ] Do this **only if** a modern MCP SDK or a .NET 8+ dependency becomes necessary. It is pure
      refactoring with no user-visible feature gain.

### 4.4 V21 support

- [ ] Second `.csproj` target referencing the V21 `Siemens.Engineering.dll`, producing a separate exe.
- [ ] Version-detect at startup and report the bound Openness version in `get_status`.

---

## Phase 5 — Skills / methodology layer

**Effort: ongoing.** Per `docs/future-features.md`, this is where the real leverage is — more tools without
conventions produce coherent-looking but inconsistent projects.

- [ ] `skills/naming-conventions.md` — FB/FC/DB naming, tag prefixes, group structure.
- [ ] `skills/db-architecture.md` — instance vs global DBs, shadow DB patterns.
- [ ] `skills/operating-modes.md` — standard state machine.
- [ ] `skills/alarm-handling.md` — alarm DB structure, acknowledgement logic.
- [ ] `skills/recipe-handling.md`, `skills/oee-calculation.md`.
- [ ] `skills/io-link-integration.md` — IO-Link master setup, PDIN/PDOUT mapping, ISDU access from SCL.
      Ties directly into the existing IO-Link tutorial series.
- [ ] A `CLAUDE.md` in the repo root pointing at the skills and stating the
      write → compile → read-errors → fix loop as the expected workflow.

---

## Suggested order

| Order | Work | Effort | Why here |
|---|---|---|---|
| ~~1~~ | ~~Phase 0 (wire up existing services)~~ | done | Shipped 18 tools; 20 → 38 |
| **1** | **Validate Phase 0 on a TIA Portal machine** | ~2–3 h | Nine of the eighteen have never run. Everything below builds on them |
| 2 | 1.1 cross-references | ~4 h | Biggest capability gap; unblocks the most common question |
| 3 | 1.4 structured compile at PLC scope | ~4 h | Makes the autonomous fix loop actually converge |
| 4 | 1.2 + 1.3 delete/move/groups + UDTs | ~1 d | Completes CRUD; stops orphan accumulation |
| ~~5~~ | ~~4.2 `--project`~~ | done | Also added `--with-ui` |
| 5 | 3.1 preview/confirm | ~1 d | Must land **before** any destructive tool ships; `RequireConfirm()` is a stopgap |
| 6 | 1.6 hardware/network | ~1.5 d | Unblocks the IO-Link / PROFINET use case; start from `get_io_mapping` |
| 7 | 2.1 download/online | ~2 d | The differentiator — but only behind 3.1 and the opt-in flag |
| ~~9~~ | ~~3.5 profiles~~ | done | Pulled forward — pure C#, verifiable without a TIA machine |
| 10 | Phase 5 skills | ongoing | Compounds with everything above |
| 11 | 2.2–2.5, 1.5, 1.7, 3.2–3.4 | as needed | Breadth |
| 12 | 4.3 two-process, 4.4 V21 | ~1 w | Only when a hard dependency forces it |

---

## Cross-cutting rules

1. **Read-only by default.** Anything that changes a running machine (`download_to_device`, force
   operations) ships disabled and requires an explicit config flag *and* a CLI switch to enable.
2. **Structured errors everywhere.** Compiler and Openness exceptions return
   `{ severity, path, line, text }`, never a flattened string — the agent loop depends on parsing these.
3. **Bound every list.** Cross-references, block lists and tag dumps need `limit` / `offset`. One unbounded
   call can consume the whole context window.
4. **Every COM call on the STA thread.** New services follow the existing pattern in
   `Utilities/StaTaskScheduler.cs` — no exceptions.
5. **Document the culture constraint.** XML imports must match the project language setting; state this in
   the description of every import tool, not just in the README.
6. **Tool descriptions carry the caveats.** The WinCC internal-tag limitation, the GSD manual-install
   requirement, the V20-vs-V21 binding — each belongs in the description text of the relevant tool so
   Claude relays it, rather than buried in docs the model never reads.
7. **Keep the tool count honest.** `McpToolDefs()` is the single source of truth; update the README count in
   the same commit that changes it.
