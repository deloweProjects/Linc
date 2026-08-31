# M12g-amend — AMENDMENT A1: the D-057 hole is bigger than `M12g.md` assumes

> **Read this together with `M12g.md`. It does not replace that file; it inserts a new Part A0 before
> its Part A, and raises the size budget in its §2.**
>
> Reason for an amendment rather than an edit: `M12g.md` was already handed over, and a task file is
> immutable once handed over.

---

## A1.1 What M12f's report exposed, and why it changes M12g

M12f reported that the app's local store is resolved in several places, not one. **Planner-verified
directly in the tree — this is not a hypothesis:**

| File:line | Resolves | Status |
|---|---|---|
| `Services\DeviceRegistry.cs:214` | `…\Linc` (`DefaultRootPath`) | ✅ correct — this is the injectable one |
| `Services\DeviceCacheService.cs:67` | `…\Linc/cache` | ❌ bypasses `IDeviceRegistry.RootPath` |
| `Services\LogService.cs:40` | `…\Linc/logs` | ❌ bypasses it |
| `Services\SyncEngine.cs:39` | under `…\Linc` | ❌ bypasses it |
| `Services\TlsTransportService.cs:49` | `…\Linc/tls-identity.pfx` | ❌ bypasses it |
| `Services\ToolLocator.cs:31, 56` | `…\Android\Sdk\platform-tools` | ✅ leave alone — a **read** of the Android SDK, deliberate, nothing to do with our store |

**So `M12g.md`'s Part A as written would deliver a sandbox that looks safe and is not.** `--data-root`
would redirect `settings.json`, `linc.db` and `AppCatalog`'s cache — while the child process still wrote
the owner's **real** `logs/`, **real** `cache/`, and **real** `tls-identity.pfx` (the phone's pinned TLS
identity — losing or overwriting it breaks the pairing).

**A partial sandbox is worse than M12f's honest SKIP,** because the next reader will trust it. This
amendment exists so we do not ship that.

**This is the hole H1 named and left open in 2026-07-31** — BRAIN's H1 entry says in as many words: *"Only
`settings.json` is covered. `cache/`, `sync-state.json`, `tls-identity.pfx` and `logs/` are reached by
other services and were never audited."* It is now audited. Close it.

## A1.2 NEW PART A0 — route the four through the injected root, FIRST

**Do this before `M12g.md`'s Part A.** For each of `DeviceCacheService`, `LogService`, `SyncEngine`,
`TlsTransportService`: take the root from `IDeviceRegistry.RootPath` instead of calling
`Environment.GetFolderPath` directly, exactly the way `LincStore` and `AppCatalog` already do at
`App.xaml.cs:70` and `:77`.

- **Keep every subfolder and filename identical.** `cache`, `logs`, `tls-identity.pfx` keep their names
  and their position under the root. **Only where the root comes from changes.**
- **Production behaviour must be byte-identical.** Production passes no root, so
  `RootPath == DefaultRootPath` and every resolved path is the same string it is today. **Prove this** —
  paste the resolved paths from a normal launch and show they match the pre-change values. An existing
  user's `tls-identity.pfx` must still be found where it already lives; a silent migration here would
  break pairing on every installed copy.
- Some of these may be constructed before or outside DI. **Report any that cannot cleanly take
  `IDeviceRegistry` and say why** rather than forcing it — if one genuinely cannot, that is a finding,
  and Half Two must keep skipping until it is solved.

## A1.3 ⚠️ DO NOT TOUCH THE USER-FACING OUTPUT FOLDERS

These also call `GetFolderPath`, and they are **correct as they are.** They are where the user's own
files land, not our private store. **Redirecting any of them would move the owner's synced photos and
downloads:**

`HomeViewModel.cs:182`, `SyncViewModel.cs:286`, `FilesViewModel.cs:198`, `FileService.cs:192`,
`ShareService.cs:51`, `SyncEngine.cs:89` — all `MyPictures` / `UserProfile` (`Pictures/Linc`,
`Downloads/Linc`, `Pictures/Linc/Photos`, `Pictures/Linc/Shared`).

**The rule: `LocalApplicationData` is our private store and belongs under the injected root.
`MyPictures` / `UserProfile` are the user's own folders and must never move.** Note that `SyncEngine`
appears in **both** lists — line 39 is our store and must change; line 89 is the user's Pictures folder
and must not. Read both before editing either.

## A1.4 New acceptance items, added to `M12g.md` §3

- **A guard that keeps the hole shut.** Add a check — to `cleanroomsim`, or `storesim` if it fits better
  there; say which and why — that scans `DESKTOP/Linc.Desktop/**/*.cs` for
  `GetFolderPath(Environment.SpecialFolder.LocalApplicationData` and **fails if it appears anywhere
  except the three sanctioned sites**: `DeviceRegistry.DefaultRootPath` and `ToolLocator`'s two SDK
  candidates. Scan a **directory tree**, not a hardcoded filename list — M6c's Part 0 showed a
  hardcoded list leaves new files uncovered, and that lesson is in BRAIN.
- **A third negative proof:** reintroduce a bare
  `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` into `LogService.cs` → the
  new guard fails → restore → green.
- **`M12g.md` §3.3 now binds.** With A0 done, Half Two has no remaining excuse: it must **actually run**,
  not skip. If it still skips, the reason is a finding and the report leads with it.
- **`M12g.md` §3.7 gets stricter:** assert the owner's real `%LOCALAPPDATA%\Linc\` is untouched across
  the run — not just `settings.json` but also that **no new file appeared under `logs/` or `cache/`** and
  that `tls-identity.pfx`'s size and last-write time are unchanged. That is the whole point of A0.

## A1.5 Size budget

`M12g.md` §2 says "smallest possible change… if it grows past a handful of lines, stop and report."
**That no longer applies to Part A0** — four services is more than a handful, and it is expected. It
still applies to Parts A and B. **A0 remains mechanical: change where a root comes from, change nothing
else.** If A0 starts requiring behaviour changes, stop and report.

## A1.6 Also report

- Whether `HKCU/Software/Microsoft/Windows/CurrentVersion/Run` currently has a `Linc` value, and if so
  **what path it points at.** M12e landed without its report reaching the planner, and if its toggle was
  hand-tested the key may be pinned to a **Debug build** path. **Report it; do not change it** — that is
  the owner's to decide.
