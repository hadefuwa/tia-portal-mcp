# Reusable prompt — comment a TIA Portal project's networks, tags and UDTs

Copy everything below the line into a fresh Claude Code session, after editing the three
**PROJECT SPECIFICS** values at the top.

---

I want you to add network titles/comments, tag comments and UDT/DB member comments to a TIA Portal
V20 project, **without changing any logic**. Work through the `tia-portal-mcp` server at
`C:\Users\HamedA\Documents\tia-portal-mcp`.

## PROJECT SPECIFICS — edit these

- Project path: `C:\Users\HamedA\Documents\Automation\<PROJECT>\<PROJECT>.ap20`
- Device name: `<e.g. S7-1200 station_3>` (get it from `list_devices`; it must be URL-encoded in HTTP calls)
- Scope: networks + tag tables + UDTs + global DB members

## Ground rules

1. **Never change logic.** Comments live in `MultilingualText` / `<Comment>` elements, entirely
   outside `FlgNet` and `StructuredText`. Edit the exported XML at **string level** — do not
   round-trip it through an XML library, which rewrites namespace prefixes and can break the import.
2. **Prove it after every import.** Re-export and diff: the `<FlgNet>…</FlgNet>` and
   `<StructuredText>…</StructuredText>` sections must be **byte-identical** to before. Report the
   comparison, don't just assert it.
3. **Understand before you write.** Read every block first and work out what it actually does.
   Comments that restate the rung are useless; comments that explain *why*, what triggers something,
   what the thresholds mean, and which variables are dead are what's wanted.
4. **Flag defects, don't silently fix them.** Dead code, unwritten variables, misleading names,
   latching bits with no reset — call them out in the comment and in your summary.
5. **Don't save the project.** Tell me when it's ready and I'll save it as a new version myself.

## Workflow that works

1. Check only **one** TIA Portal instance is running with a project open. `AttachToRunningAsync`
   picks an instance itself; extra instances (including project-less helper shells TIA spawns) make
   it attach to the wrong one. Pass `projectPath` explicitly.
2. Start `TiaPortalDashboard.exe` with **no arguments** for HTTP mode on `:5000`, then
   `POST /api/connect` with `{"projectPath": "..."}`. **I must click "Yes to all" on the Openness
   approval dialog** — the call blocks until I do, and every dashboard restart needs a fresh
   approval. Use a long timeout and ask me if it stalls.
3. Prefer the **HTTP API over MCP tools** for bulk work — it keeps tens of thousands of lines of XML
   out of your context. Save each `xmlContent` to a working folder and parse from disk.
4. `POST /api/devices/{device}/compile` (whole-software compile) **before exporting anything**.
5. Export all blocks, types and tag tables; build a condensed digest to read from.
6. Write the comments, apply, verify, then re-export everything as the new baseline.

## Traps that will cost you hours

- **An inconsistent block or type exports as an EMPTY file with no error.** It looks exactly like an
  empty block. Fix: whole-software compile. Compiling individual blocks does **not** make UDTs
  consistent.
- **Dependency cascade:** changing a UDT makes every dependent block/type inconsistent, so the next
  export returns empty. Expect to compile → retry the failures, two or three rounds.
- **The server returns errors as `{"error": "..."}` with HTTP 200.** A client that only catches HTTP
  failures will report success on a failed import. **Always check the response body.**
- **Tag table import is REPLACE, not merge.** Re-emit every tag in the table or the omitted ones are
  deleted. Read the table, modify, write the whole thing back, then diff all tags afterwards
  (name, dataType, address, accessible, writable).
- **Blocks and UDTs inside folders** must be imported into the composition that owns them, not the
  root — otherwise `ImportOptions.Override` falls through to Create and fails with "a program
  element with this fully qualified name already exists". The server handles this via
  `FindBlockComposition` / `FindTypeComposition`; confirm those exist in the build you're using.
- **A type sharing a name with a type folder cannot be imported** (deterministic failure, same
  error). Note it and move on.
- **Technology objects (`PID_Compact`) and the HMI are out of reach** — no Openness MCP surface.

## Comment carriers (the XML shapes)

| What | Carrier |
|---|---|
| Network + block titles/comments | `<MultilingualText CompositionName="Title"\|"Comment">` → `<MultilingualTextItem>` → `<Culture>` + `<Text>` |
| FB/FC interface, UDT and DB member comments | `<Comment><MultiLanguageText Lang="xx">…</MultiLanguageText></Comment>` |
| Network "free comment" | its own `MultilingualText` inside the network — easy to miss, check for it |
| Tag comments | tag-table XML, same `MultilingualText`/`MultilingualTextItem` shape |

For `<Member>` elements: insert `<Comment>` **after** `</AttributeList>` if one exists, otherwise
right after the opening tag. Self-closing `<Member … />` must be expanded to a full element.

**Section paths:** when targeting members, track the **outermost** open `<Section>`. UDT-typed
members contain their own nested `<Sections>`, and a naive tracker resets to `None` after the first
one closes, silently missing every later member.

## Keep comments SHORT

They display inline in the ladder next to operands. Anything over ~80 characters makes the rungs
unreadable. Target **40–70 characters**, one line. Put the long explanation in a markdown doc, not
in the PLC. Preserve genuinely load-bearing warnings ("never reset", "NOT counted", "not read by
any block") even if terse.

Mark anything inferred rather than read from the code — physical I/O meaning especially — e.g.
`[inferred]`, and tell me it needs checking against the wiring drawing.

## If the project has more than one language

Check `GET /api/project/languages` first. If both e.g. en-GB and en-US are active, **use
`POST /api/project/texts/export` → edit the XLSX → `POST /api/project/texts/import`** instead of
editing block XML per language. That covers *every* text in the project in one pass — PLC, HMI, free
comments — and is far less error-prone.

If you do edit XML per language: TIA **pre-creates an empty `<MultilingualTextItem>` for every
active language**. You must **fill the existing empty slot**, not add another — a second item with
the same `<Culture>` in one composition is rejected on import. Member `<Comment>` elements have no
pre-created sibling, so there you *do* add one.

## Deliverables

1. Comments applied to all networks, tag tables, UDTs and global DB members.
2. A verification table: per block/type — comments applied, and logic identical yes/no.
3. Confirmation that the block count and tag counts are unchanged and there are no duplicates.
4. A short markdown doc holding the full-length explanations and a list of every defect/oddity found.
5. Tell me what you could **not** do and why.

Start by connecting, doing a whole-software compile, exporting everything, and giving me a summary
of the program structure and what you propose to comment — before you write anything.
