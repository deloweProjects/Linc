# BRAIN.md — Living Project Context

Condensed operational knowledge for anyone (human or AI) resuming work on Linc. Read this after
`CLAUDE.md`. **Linc is an established, working project under active maintenance — not a greenfield
build.**

> **This is the Claude Code doc set (`claude-docs/`).** Antigravity has a parallel copy in
> `gemini-docs/` (with root `AGENTS.md` / `GEMINI.md` / `GUARDRAILS.md` enforcing stricter rules).
> **Only one agent works at a time**; the human planner keeps the active agent's folder current and
> syncs the other at each switch. Edit only your own folder.
>
> **Where the docs live:**
> - **`Documentation/`** = the **formal, human-facing documentation** of the whole project — what it is,
>   how each app works, how they connect, its history, features, decisions. Start at
>   `Documentation/00-Linc-Master-Documentation.md`. Shared by both agents; read it for the settled picture.
> - **`claude-docs/`** = the **working docs** you build from: `PROTOCOL.md` (canonical wire spec),
>   `DECISIONS.md` (decision log, D-001…), `ROADMAP.md` (the milestone series), `FEATURES.md`,
>   `REQUIREMENTS-ANALYSIS.md`, this file, plus `ARCHITECTURE.md`/`TECH_STACK.md`/`THEME.md`/
>   `CONTRIBUTING.md`/`CLAUDE.md`.
> - **`v2.docs.old/`** = the archived Era 1/2 planning docs. Nothing was deleted; the settled
>   version of that history is in `Documentation/02-History.md`.

## What this is

Linc links an Android phone to a Windows PC over ADB (wireless debugging or USB) plus a direct
app-to-app TLS channel — files, screen (both directions), clipboard, notifications, media,
messages, calls, live stats — with the rule that **users never touch ADB or a terminal** (except
the deliberate power-user shell box on Settings, D-009). Android 11+ only (D-002). Two apps, no
shared code (D-006), one versioned JSON protocol (**shipped v14**).

## Current state (2026-07-23)

- **The core product is complete and hardware-verified.** Era 1 (M0–M19) + Era 2 (M00–M05) are
  done; M06 Extend was dropped; M07 Polish is partway (Sync Messages/Calls split landed). See
  `Documentation/02-History.md` for the full account and `Documentation/07-Feature-Catalog.md` for what ships.
- **A new phase ("Convergence") is planned** in the freshly-rewritten `claude-docs/ROADMAP.md` (milestone
  series **M1…**, unrelated to the archived numbering). It comes straight from the owner's new
  requirement set; `claude-docs/REQUIREMENTS-ANALYSIS.md` maps each requirement to
  Implemented/Polish/New/Bug and to the code that already exists to reuse. **Feature work has begun
  — M1 and M2a are done (below); M2b is next.**
- **Uncommitted working tree:** the previous session's M07 Sync-page split
  (`SyncViewModel.cs`/`SyncPage.xaml*`) is still uncommitted, alongside this docs reorg. Nothing
  here has been committed — review and commit when ready.
- **Owner corrections (2026-07-22)** reshaped the plan — see `REQUIREMENTS-ANALYSIS.md` › "Owner
  corrections" and the revised `ROADMAP.md`. Key deltas: **device tabs exist but hide for
  single-device users** (make always-visible), **onboarding must be a specific guided front-door
  flow** with the desktop **bundling + injecting the companion APK**, **scrcpy vendors as
  `SCRCPY/Default` + `SCRCPY/Custom`** (Linc-branded top bar), **Desktop Mode has a full spec**
  (`DESKTOP-MODE-SPEC.md`), the **phone Tools suite is broad** (keyboard/trackpad/multimedia/
  slideshow/sound/brightness + camera→PC-webcam), and the **Home page gets removable/flippable
  sections + an Apps section**.
- **M1 landed (2026-07-22).** Files page fixed (root cause: the load was edge-triggered on the
  `Connected` transition, but `FilesViewModel` is created lazily on first navigation — *after* the
  launch-time connect already fired — so it never did an initial load; now it also loads in its
  ctor when already `Connected`). Device-tab strip made always-visible once ≥1 device is paired
  (`IsStripVisible` `> 1` → `>= 1`). Added `tools/filesim` (real `TlsFileService`/`FileServiceRouter`
  + `Framing`). Both fixes UIA-verified against the Pixel 7; USB priority / connection-picker / BLE
  confirmed wired; preemptive auto-connect confirmed (desktop self-connected ~1 s after launch).
- **M2a landed (2026-07-23) — device management works + Files reaches the whole phone.** The dead
  `+` was pairing gated by `IsDisconnected` (QR card hidden whenever a phone was connected); now
  `DeviceViewModel.ShowPairing => PairingActive` and `+` navigates to the Device page + runs
  `StartPairingCommand`. `CloseTab` dropped its `Count<=1` guard (last tab hides; phone stays paired,
  D-033; active-tab close switches to a neighbour first). New **Settings › Devices** card (list / Set
  active / Forget). Files page: `UpAsync` climbs to `/`, denied dirs show a plain-language note (a
  shell `ls` probe catches `Permission denied`, which the sync protocol returns as empty), downloads
  → `Downloads/Linc`; storage dropdown → a live "Go to" menu. Sync/Mirror VMs confirmed clear of the
  M1 lazy-load bug. **Next: M2b** — the guided onboarding wizard + **bundle/inject the companion
  APK**; it must keep the "pair a phone" entry reachable at **zero devices** (close-last-tab hides
  the strip *and* the `+`).
- **Caveat:** an M2a `devicesim` run (killed mid-way before its restore step) overwrote the real
  Pixel 7 pairing with fakes; cleaned up and the phone re-paired over USB, but its **sync-lane /
  folder-sync config may need re-setting**. See the devicesim footgun note under "Where things live".

## The new phase in one breath

Phone and PC become one control surface, with a proper guided front door. Milestones (revised):
**M1** Files fix + visible tabs + verify → **M2** guided onboarding + bundled/injected companion +
device management → **M3** vendor scrcpy (`Default`/`Custom`, branded) → **M4** phone-as-remote
Tools suite (control the PC; camera→webcam is its own step) → **M5** PC-controls-phone widgets +
Device Tools + scrcpy settings → **M6** Home layout overhaul + Apps section → **M7** Apps windows
(per-app Custom-scrcpy) → **M8** Desktop Mode (full spec) → **M9** SQLite + notification history +
offline queue → **M10** sharing & install-APK → **M11** audio routing → **M12** presence polish +
packaging/beta. Dependencies: M7/M8/M11 + scrcpy-settings depend on **M3**; M7 rides M6's Apps
section; notif-history/queue depend on M9's store. Decisions: **D-041…D-048** in `DECISIONS.md`.

## How it hangs together (one paragraph)

Desktop polls mDNS (`DiscoveryService`) + the ADB device list (`UsbWatcherService`) + BLE
(`BlePresenceService`) → `ConnectionSupervisor` (state machine: NoDevice/Searching/Connecting/
Connected/Paused; reconnect count, sticky error, 15 s health loop, per-address backoff, transport
ranking **USB > wireless ADB > Direct TLS** with preemption, resume-from-sleep recovery) drives
`ConnectionManager` (wireless `adb connect` or USB, then getprop + `adb forward tcp:0
localabstract:linc` + handshake via `CompanionClient` + TLS cert exchange). Feature traffic rides
typed **channels** over the connected transport (0 control JSON, 1 mirror, 2 files, 3 bulk, 4
audio, 5 pc-video). Phone side: `CompanionService` (exported FGS) hosts `SocketServer`, which
serves every connection on its own coroutine and routes the first frame to the control handler
(`hello`) or a typed-channel handler (token-checked). Only the control connection touches
`CompanionStateHolder`/`CompanionOutbox`. Eagerly-started desktop services live in
`AppShellViewModel`. Full detail: `Documentation/04-Desktop-Application.md`, `Documentation/05-Android-Application.md`,
`Documentation/06-Connectivity-and-Protocol.md`.

## Environment facts (this dev machine)

- **Desktop builds:** plain `dotnet build` CANNOT build WinUI — use **VS MSBuild**:
  `"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" DESKTOP\Linc.Desktop\Linc.Desktop.csproj -restore -p:Configuration=Debug -p:Platform=x64`
  Kill any running `Linc.Desktop` process first or the copy fails on a locked exe (MSB3027).
- **Android builds:** `JAVA_HOME = C:\Program Files\Android\Android Studio\jbr`, then
  `ANDROID\gradlew.bat assembleDebug testDebugUnitTest --no-daemon`. Heap pinned to 1 GB; JVM error
  1455 means "free memory," not a code problem.
- **adb:** `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`. **scrcpy v3.3.4 on PATH** (M2 will
  vendor + bundle a chromeless build). Test phone: **Pixel 7** (serial `<your-device-serial>`), USB
  serial == `ro.serialno`.
- **125% display scaling** silently changes what display APIs report; the desktop and `mirrorsim`
  declare **PerMonitorV2** so capture/virtual-desktop/`SendInput` spaces all agree on physical
  pixels. Any new console touching display geometry needs that manifest.
- MVVM Toolkit partial properties need `<LangVersion>preview</LangVersion>` and can't have
  initializers (set defaults in constructors). H.NotifyIcon.WinUI pinned to 2.2.0.

## Recurring gotchas (still true — heed them)

- **A silent catch is a bug factory.** If a path can fail and nobody is awaiting it, it must log.
  Several of the worst bugs were invisible fire-and-forget failures.
- **Anything that must run regardless of the open page belongs in `AppShellViewModel`,** never a
  page's view model (the supervisor once lived in the Device page, so the app never connected
  unless you opened it). *(Relevant to the M1 Files-page bug — check the load/refresh path.)*
- **A custom title bar swallows clicks** — anything handed to `SetTitleBar` becomes a drag region
  that eats mouse input; keep the drag region to an empty spacer.
- **Any COM/Media-Foundation/D3D object with a hot cross-thread call path must be created off the
  UI thread** — the companion receive loop resumes on the UI thread (STA), so anything reached
  from `OnCompanionMessage` is STA by default.
- **A reconnect machine driven only by fresh adverts is blind to a transport already up but quiet**
  — give it a way to adopt what the adb server already holds.
- **Not every file op exists on every transport** — `TlsFileService` throws `NeedsAdb()` for
  delete/rename/mkdir/screenshot (D-024); check `connection.HasAdb` or catch `LincException`.
- **Protocol changes:** bump the version in `PROTOCOL.md` first, gate on the negotiated version,
  keep unknown-type/field tolerance.
- **Every user-facing error is plain language** (CONTRIBUTING.md); raw adb/scrcpy output never
  reaches the UI except the D-009 box. New phone-setting writes (M4) should ride companion protocol
  messages, not raw ADB, to honor D-001.
- The desktop is an unpackaged exe the screenshot tooling can't target — drive it headlessly via
  **UI Automation** (real click vs. `InvokePattern.Invoke` reveals swallowed clicks; first click
  only activates the window). **But a real cursor-driven click/drag hijacks the pointer — don't run
  it while the owner may be using the PC; hand cursor/interaction checks to the owner as a numbered
  manual script** (CLAUDE.md §5, the PC-side twin of D-031's phone-tap rule). Tree enumeration and
  `Invoke` don't move the pointer and stay fine.
- Never log clipboard text or notification bodies (only that an event happened). **M6's opt-in
  notification history is the one scoped, consented exception (D-045) — default off.**

## Where things live

- **Protocol spec:** `claude-docs/PROTOCOL.md` — implemented twice, Kotlin `app.linc.android.protocol`
  and C# `Linc.Desktop.Protocol`, zero shared code (D-006).
- **Desktop:** `DESKTOP/Linc.Desktop/` — `Services/`, `ViewModels/`, `Views/`, `Themes/`. Map in
  `Documentation/04-Desktop-Application.md`.
- **Android:** `ANDROID/app/src/main/java/app/linc/android/` — `service/`, `ui/`, `protocol/`. Map
  in `Documentation/05-Android-Application.md`.
- **Persistence:** `%LOCALAPPDATA%\Linc\` — `settings.json`, `cache/<serial>/`, `sync-state.json`,
  `logs/`. (M5 introduces a SQLite `LincStore` alongside these.)
- **Verification harnesses:** `tools/probe`, `tools/syncsim`, `tools/filesim`, `tools/devicesim`,
  `tools/blescan`, `tools/mirrorsim` (D-036) — plain `net8.0` consoles, run before handing anything
  to the user. See `Documentation/08-Build-Test-and-Deploy.md`. **`devicesim` footgun:** it backs up, briefly
  deletes, then restores the real `settings.json`, so it must **run to completion** — don't pipe it
  into something that can exit early (`| Select -First`, `| head`) or kill it mid-run, or the restore
  never happens and the real pairing is left overwritten with fake test devices (this bit M2a).
