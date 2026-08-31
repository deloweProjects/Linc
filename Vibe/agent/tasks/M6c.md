# TASK M6c — the Apps section, end to end (protocol v16)

**Agent for this session: Claude Code.** This is a deliberately large session: a protocol bump on both
apps, a new per-device cache, and a new Home section. Read this whole file before editing anything.
Design decisions marked **decided** are settled; where it says *your judgement*, it genuinely is yours.

---

## 0. Orientation and standing rules

- Workspace/project = **yellow** (`<repo-root>`). Product/code = **Linc**, at
  **`Linc`** — every relative path below (`DESKTOP\…`, `ANDROID\…`,
  `tools\…`) hangs off that `Linc/` root.
- **Live project docs for every agent (D-056):** `Vibe/agent/opencode-docs/`.
  Read **`PROTOCOL.md` › "v16 — Installed-app inventory"** and **`DECISIONS.md` › D-058** — that is the
  contract you are implementing, already written. Then `BRAIN.md`'s "Recurring gotchas".
  `Vibe/agent/claude-docs/` project docs are **DEAD**; only `claude-docs/CLAUDE.md` (your behavioural
  guide) is live. Also `Vibe/agent/AGENTS.md` and `GUARDRAILS.md`.
- **Do not delegate to nemo.**
- **Never move the real mouse cursor. Never tap the phone. Do not install the APK.**
- **Code + tests only. Do not edit any `.md` file** — the planner owns docs, including `PROTOCOL.md`.
- Desktop build: kill any running `Linc.Desktop`, then
  `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
- Android build: `JAVA_HOME = C:\Program Files\Android\Android Studio\jbr`, then
  `ANDROID\gradlew.bat assembleDebug testDebugUnitTest --no-daemon`.
- **`git status` works — run it from `Linc`.** (`yellow/` itself is not a repo;
  last session reported "not a git repository" after checking one level too high.)
- **Harnesses are safe now (D-057/H1).** They run against temp roots. **Do not copy the old
  "never run devicesim" warning** — it is retired.

---

## PART 0 — close two gaps H1 left open. Do first, report before moving on.

`tools/homelayoutsim`'s `[10/10]` D-057 guard has two known weaknesses, named in H1's own report:

1. **It scans four hardcoded filenames.** Make it **enumerate `tools/*/Program.cs` by directory scan**
   instead, so a brand-new harness is covered the moment it exists. Keep the existing rule: every
   `new DeviceRegistry(...)` must pass an explicit root.
2. **`blescan` is exempt and unguarded.** It legitimately opens the real store
   (`new DeviceRegistry(DeviceRegistry.DefaultRootPath)`) because it must match BLE beacons against
   the owner's real pinned certificates. Its exemption is safe **only while it never writes.** Add an
   assertion that `blescan/Program.cs` contains **no** `Save*(` call on a registry. If a future
   session adds one, this must fail.

Keep the "deliberately crude text check" comments. These guards exist because a clean build and a
green harness once hid a completely dead UI (M5c-2) and a real-data-destroying write path (M6b).

---

## PART 1 — Android side of v16

Read the v16 section of `PROTOCOL.md` first; implement exactly it. Your closest precedent is the v15
work in `SocketServer.kt` (`display.rotation.set`) and, for shaping a list reply, the existing
`photos.recent` / `sms.list` handlers.

1. **`protocol/Protocol.kt`** — a `// v16` block with `APPS_GET = "apps.get"` and `APPS = "apps"`.
   Bump `PROTOCOL_VERSION` 15 → **16**.
2. **New `service/AppInventory.kt`** — the only place that touches `PackageManager`. Returns
   **launchable apps only** (packages resolving a `CATEGORY_LAUNCHER` intent), each with `package`,
   `label` (the user-visible localised label — never derived from the package id), `versionName`
   (optional; may be absent/empty) and `system` (`FLAG_SYSTEM`). **System apps are included**, just
   flagged. Keep the mapping logic in pure, testable functions wherever `PackageManager` isn't needed.
3. **`service/SocketServer.kt`** — an `APPS_GET` branch gated `if (negotiated >= 16)`, replying with an
   `APPS` envelope, `replyTo` set. Follow the surrounding style. Wire the callback through
   `CompanionService` as the existing handlers do; do not reach for a singleton.
4. **Unit tests** — the pure mapping functions: a launchable app maps to all four fields; a
   missing/empty `versionName` is tolerated; the `system` flag is derived correctly; an app with no
   launcher intent is excluded.

**Note on the version bump.** The phone negotiates `minOf(maxV, PROTOCOL_VERSION)`, so bumping the
phone alone changes nothing on a live link until the desktop's `maxV` rises in Part 2. Both move this
session — say so explicitly in your report.

---

## PART 2 — desktop client + per-device app cache

1. **`Protocol/Envelope.cs`** — `AppsGet = "apps.get"`, `Apps = "apps"`; `ProtocolConstants.Version`
   15 → **16**.
2. **`Services/CompanionClient.cs`** — `GetAppsAsync(CancellationToken)`, modelled on the existing
   request/reply methods, returning a parsed list. **Parsing must tolerate a missing `versionName`,
   an absent `system`, and unknown extra fields** per the envelope rules. Keep the parse in a pure
   static helper so a harness can exercise it without a socket (the `DisplayPayload` pattern).
3. **New `Services/AppCatalog.cs`** — the per-device cache.
   - Cache lives at `<root>/cache/<serial>/apps.json`, icons at `<root>/cache/<serial>/icons/<package>.png`.
   - **`<root>` MUST come from the same injected path `DeviceRegistry` uses (D-057/D-058), not from
     `Environment.GetFolderPath` directly.** Expose whatever small accessor makes that clean —
     a `RootPath` property on `DeviceRegistry` is the obvious one. **Decided:** a harness pointed at a
     temp root must not be able to write into the owner's real `cache/` either. This is the rule for
     every future store under `%LOCALAPPDATA%\Linc`.
   - Load-from-cache is synchronous and instant; refresh happens in the background.
   - **Reconcile, don't replace:** on a fresh `apps` reply, diff against cache — added, removed,
     relabelled — and persist the new list. Report counts in a log line.
   - Icons are fetched **lazily, per package, only for apps being displayed**, via the **existing**
     bulk `appIcon` request in `CompanionClient` (bulk kind has existed since v5 — **do not invent a
     second icon path**), then cached as PNG. A missing icon is normal: fall back to a placeholder and
     do not retry in a tight loop.
4. **Tolerate a hostile/odd cache file.** A corrupt or truncated `apps.json` must degrade to "empty
   cache, refresh on connect" — never throw on startup.

---

## PART 3 — the Apps section on Home

1. **`Services/HomeLayout.cs`** — add `apps` to `AllSectionIds` (display order: *your judgement*, but
   state where you put it and why) and a `DisplayName` of "Apps". Because storage is
   **hidden-by-absence**, an existing saved layout gains the new section **visible by default** — that
   is intended. **Do not change the record's shape or parameter order.**
2. **`ViewModels/HomeViewModel.cs`** — `ShowApps`, the section's entry in `SectionToggles`, the app
   list bound for display, and an empty state. Sort for display (the wire order is not a contract).
   **Re-raise it in `RefreshSections()` like every other section**, and — per the M6a regression —
   **if the section's visibility is composed with a data condition, raise the composed property at
   every site that changes either half.**
3. **`Views/HomePage.xaml`** — the Apps card among the other six, `Visibility` bound like theirs.
   Show icon + label per app.
   - When the peer negotiated **< 16**, show a plain-language empty state ("This phone's Linc app is
     older and can't list apps yet") — **disable, don't hide.**
   - When connected and the list is empty, say so plainly rather than showing a blank card.
4. **Clicking an app does NOTHING this session.** Opening apps in their own windows is **M7**. Render
   the entries as **non-interactive** items — no buttons, no hover affordance, nothing that invites a
   click that won't work. **Decided.**

---

## Acceptance

1. Android: `gradlew assembleDebug testDebugUnitTest --no-daemon` → build + **all** tests green, new
   tests visible among them. Paste the tail.
2. Desktop: MSBuild x64 → **0 errors**, no new warnings in files you touched.
3. New harness **`tools/appssim`** (modelled on `displaysim`/`homelayoutsim`; temp root per D-057):
   the pure `apps` parser over a full payload, a payload missing `versionName`/`system`, one with
   unknown extra fields, and an empty list; `AppCatalog` round-trip; a **corrupt `apps.json`**
   degrading to empty; reconcile producing correct added/removed counts; and **UI wiring checks** that
   `HomePage.xaml` binds the Apps card and `HomeViewModel` exposes the list.
4. `tools/homelayoutsim` (now with Part 0's changes), `mirrorsettingssim`, `displaysim` green.
   `desktopsim`'s ADB section fails with no phone attached — say so; **do not weaken it**.
5. **Three negative proofs**, each broken → FAIL → restored → green, all outputs pasted:
   - Part 0: add a bare `new DeviceRegistry()` to any harness → `homelayoutsim` must fail.
   - Part 0: add a `SaveHome(` call to `blescan/Program.cs` → must fail.
   - Part 3: remove the Apps card's `Visibility` binding → `appssim` must fail.
   Rebuild both apps afterwards and confirm the restores.

---

## Report

Per-part result lines **written as you finish each part**, not reconstructed at the end. Files changed
with one-line reasons. Verbatim commands and results for every build, test and harness run. An
explicit statement that you raised **both** version constants to 16 and what that changes on a live
link. Where you placed the Apps section in the order and why. How you guaranteed no echo/refresh loop
in the new view-model wiring. All three negative proofs. `git status` (from `Linc/`). A numbered
manual test script for the owner. **What you could not verify and why.** Anything in this task file
that was ambiguous or wrong — that is feedback the planner wants.

**Out of scope:** opening apps in windows (M7), pop-out panels (M6d), any push/observer for app
installs (D-058 rejected it), a second icon transport, `KnownDevice` schema changes, restyling other
cards, the tabbed panel, the zero-device state.
