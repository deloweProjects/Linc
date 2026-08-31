# TASK M9a — `LincStore`: the SQLite foundation (schema, migration, harness). No features on top yet.

> **Agent for this session: opencode.** **Read this whole file before editing anything.** It is the
> complete specification. Every design decision is already made — implement it exactly. If something
> here is impossible or self-contradictory, **stop and report that**; do not improvise an alternative.

---

## 0. Standing rules (non-negotiable)

- Workspace/project = **yellow** (`<repo-root>`). Product/code = **Linc**, at
  **`Linc`** — every relative path below hangs off that `Linc/` root.
- **Live docs (D-056):** `Vibe/agent/opencode-docs/` — read it as "the live
  docs". Read **`DECISIONS.md` › D-044** (the decision you are implementing), **D-057** and **D-058**
  (the store-root rules you must obey), and `BRAIN.md`'s "Recurring gotchas".
  Your rules: `Vibe/agent\{AGENTS,OPENCODE,GUARDRAILS}.md`. Nothing auto-loads — read from those paths.
- **Rate limit ~40 req/min. On a throttle, WAIT AND RETRY.** Never abandon the task or change approach
  because of a throttle. Batch reads; combine shell commands.
- **One tool call at a time.**
- **Never claim you did NOT do something. Never describe file state as "pre-existing" or "already
  applied."** Report only what a file contains *now*. **Write each part's result line into your report
  the moment you finish that part** — do not reconstruct the session at the end.
- **Never move the real cursor. Never tap the phone. Do not install the APK.**
- **Code + tests only. Do not edit any `.md` file. No protocol work** — v16 stays as it is.
- Desktop build (plain `dotnet build` cannot build WinUI): kill any running `Linc.Desktop`, then
  `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
- `git status` works **from `Linc/`** (not from `yellow/`).
- Harnesses are safe now (D-057) — they run against temp roots. **Do not add any "don't run devicesim"
  warning**; that footgun is retired.

---

## 1. What this session is, and is not

D-044 makes **SQLite (`Microsoft.Data.Sqlite`)** the persistence foundation for what comes next:
notification history, the offline action queue, and cache-first Sync data.

**This session builds ONLY the foundation**: the store, its schema, its migration-on-first-run, and a
harness. **Nothing in the app changes behaviour, and no feature reads from it yet.** Notification
history is **M9b**, the offline queue **M9c**, cache-first Sync **M9d**.

**`settings.json` remains the source of truth this session.** The store is written and verified but
not yet authoritative. That is deliberate: it lets the schema land with zero regression risk.

---

## 2. Decided design — implement exactly

**2.1 One file, beside the others: `<root>/linc.db`.**
**`<root>` MUST come from `DeviceRegistry.RootPath`** — the same injected path `AppCatalog` and
`AppWindowStore` take. **A call to `Environment.GetFolderPath` anywhere in the new code is a defect**
(D-057/D-058): it would let a harness write into the owner's real store. This rule has already been
broken twice and cost real user data both times.

**2.2 `Microsoft.Data.Sqlite`** — add the package to `Linc.Desktop.csproj`. Pick the latest version
compatible with `net8.0-windows`; **state the version you chose** in your report.

**2.3 Schema — create exactly these tables, no more.** Later sessions own their own contents; you are
laying the table shapes so they don't churn.

- `schema_version` — a single row holding an integer. **Set it to 1.**
- `devices` — `serial` TEXT PRIMARY KEY, `model` TEXT, `first_paired_utc` TEXT (ISO-8601).
- `notifications` — `id` INTEGER PRIMARY KEY AUTOINCREMENT, `serial` TEXT, `posted_utc` TEXT,
  `app_package` TEXT, `title` TEXT, `body` TEXT, `key` TEXT. Index on `(serial, posted_utc)`.
- `outbox` — `id` INTEGER PRIMARY KEY AUTOINCREMENT, `serial` TEXT, `queued_utc` TEXT, `kind` TEXT,
  `payload_json` TEXT, `attempts` INTEGER DEFAULT 0. Index on `(serial, id)`.
- `sync_cache` — `serial` TEXT, `kind` TEXT, `key` TEXT, `payload_json` TEXT, `updated_utc` TEXT,
  PRIMARY KEY `(serial, kind, key)`.

**Create the tables but write no rows into `notifications`, `outbox` or `sync_cache` this session.**
They exist so M9b–M9d don't have to migrate a schema.

**2.4 Migration is one-way and additive.** On first open, if `devices` is empty, import every
`KnownDevice` from `DeviceRegistry` (serial, model, first-paired). **Never delete or rewrite
`settings.json`** — the registry keeps owning it. Re-running the import must be a no-op, not a
duplicate: make it idempotent and prove it in the harness.

**2.5 A versioned migration path from day one.** `schema_version` exists so future sessions can
upgrade. Write `EnsureSchemaAsync` so it reads the version, applies steps in order, and writes the new
version — even though today there is only step 1. **Do not** hand-roll a "drop and recreate" shortcut;
that becomes data loss the moment M9b stores real notifications.

**2.6 A corrupt or unreadable `linc.db` must never crash the app.** Log it in plain language, degrade
to "no store", and let the app run exactly as it does today. This is the whole reason the store is
non-authoritative this session — nothing depends on it, so nothing can break.

**2.7 Keep it dependency-free enough to test.** SQL and mapping live in a class a plain `net8.0`
console can construct with an injected root, no WinUI. Follow `AppCatalog`'s shape.

---

## 3. The work

1. **`DESKTOP/Linc.Desktop/Linc.Desktop.csproj`** — add `Microsoft.Data.Sqlite`.
2. **New `DESKTOP/Linc.Desktop/Services/LincStore.cs`** — per section 2. Constructor takes the root
   path (plus a convenience overload taking `IDeviceRegistry`, exactly as `AppWindowStore` does).
   `EnsureSchemaAsync`, `ImportFromRegistryAsync`, and simple typed accessors for `devices` only.
3. **`DESKTOP/Linc.Desktop/App.xaml.cs`** — register it as a singleton on
   `DeviceRegistry.RootPath`, and call schema-ensure + import once at startup **without blocking
   startup and without throwing** (2.6).
4. **New harness `Linc/tools/storesim`** — plain `net8.0` console, numbered PASS/FAIL, non-zero exit on
   failure, **temp root**, no WinUI, no phone. Cover:
   - Schema creates cleanly on an empty directory; `schema_version` reads back **1**.
   - `EnsureSchemaAsync` twice in a row is a no-op (no duplicate tables, version still 1).
   - All five tables exist with the stated columns.
   - Import from a registry holding two devices lands two `devices` rows; **running it again still
     leaves two** (2.4 idempotence).
   - A **corrupt** `linc.db` (write garbage bytes) degrades without throwing (2.6).
   - **D-057:** the store resolves under the injected root, and **the real
     `%LOCALAPPDATA%\Linc` is never touched** — assert on the resolved path string, do not write to
     the real one to find out.
5. **`tools/homelayoutsim`'s D-057 directory scan** already enumerates `tools/*/Program.cs`, so
   `storesim` comes under it automatically. **Verify that it does** and say so.

---

## 4. Acceptance — all must pass

1. Desktop MSBuild x64 → **0 errors**, no new warnings in files you touched (the `CS9113` on
   `PhotoVm`'s constructor in `HomeViewModel.cs` is pre-existing; that file is not yours this session).
2. `tools/storesim` green, exit 0 — paste full output.
3. `appssim`, `applaunchsim`, `homelayoutsim`, `mirrorsettingssim`, `displaysim` all still green.
   `desktopsim`'s ADB section fails with no phone attached — say so plainly and **do not weaken it**.
4. **Launch the built `Linc.Desktop.exe`, confirm it starts and stays alive ~10 s, then close it**, and
   confirm `linc.db` was created in the **real** `%LOCALAPPDATA%\Linc\` with the devices imported.
   That is the app's own store under its own root — expected and correct. Report the row count.
5. **Two negative proofs**, broken → FAIL → restored → green, both outputs pasted:
   - Make `LincStore` resolve its own root via `Environment.GetFolderPath` → `storesim`'s D-057 check
     must fail. **Assert on the path string only — do NOT run any code that writes to the real store
     while it is broken.**
   - Change `schema_version` to be written as 2 → the version check must fail.
   Rebuild afterwards and confirm both restores.

---

## 5. Report format

- One result line per part, **written as you finish it**.
- Files changed, one line of reason each. The `Microsoft.Data.Sqlite` version you chose.
- Verbatim commands and results for the build, every harness, and the app launch (item 4).
- Confirmation that **no `Environment.GetFolderPath` call exists** in the new code, and that
  `homelayoutsim`'s scan now covers `storesim`.
- Both negative proofs.
- `git status` from `Linc/`.
- **What you could NOT verify and why.**
- Anything in this task file that was ambiguous or wrong — that is feedback the planner wants.

**Out of scope — do not build:** notification history (M9b), the offline queue (M9c), cache-first Sync
(M9d), any UI, any settings toggle, any read path that changes app behaviour, moving `settings.json`
ownership into SQLite, protocol changes, `KnownDevice` schema changes.
