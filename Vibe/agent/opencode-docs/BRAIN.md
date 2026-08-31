# BRAIN.md — Living Project Context

Condensed operational knowledge for anyone (human or AI) resuming work on Linc. **Before doing
anything, read the root `AGENTS.md`, `GEMINI.md`, and `GUARDRAILS.md` — they are the enforced rules
for this agent (Antigravity) and override anything softer here.** Then read this file.
**Linc is an established, working project under active maintenance — NOT a greenfield build. Change
only what the current task names.**

> **This is the LIVE doc set for the active coding agent — the `opencode` agent (`opencode-docs/`).**
> Its rules are `Vibe/agent/AGENTS.md` + **`Vibe/agent/OPENCODE.md`** + `GUARDRAILS.md`.
> `gemini-docs/` (Antigravity) and `claude-docs/` (Claude Code) are parallel copies and are
> **dormant/frozen**. **Only one agent works at a time**; the human planner keeps the active agent's
> folder current and syncs the others at each switch. Edit only this folder.
>
> **Where the docs live:**
> - **`Documentation/`** = the **formal, human-facing documentation** of the whole project — what it is,
>   how each app works, how they connect, its history, features, decisions. Start at
>   `Documentation/00-Linc-Master-Documentation.md`. Shared by both agents; read it for the settled picture.
> - **`gemini-docs/`** = the **working docs** you build from: `PROTOCOL.md` (canonical wire spec),
>   `DECISIONS.md` (decision log, D-001…), `ROADMAP.md` (the milestone series), `FEATURES.md`,
>   `REQUIREMENTS-ANALYSIS.md`, this file, plus `ARCHITECTURE.md`/`TECH_STACK.md`/`THEME.md`/
>   `CONTRIBUTING.md`/`AGENT-GUIDE.md`.
> - **`v2.docs.old/`** = the archived Era 1/2 planning docs. Nothing was deleted; the settled
>   version of that history is in `Documentation/02-History.md`.

## What this is

Linc links an Android phone to a Windows PC over ADB (wireless debugging or USB) plus a direct
app-to-app TLS channel — files, screen (both directions), clipboard, notifications, media,
messages, calls, live stats — with the rule that **users never touch ADB or a terminal** (except
the deliberate power-user shell box on Settings, D-009). Android 11+ only (D-002). Two apps, no
shared code (D-006), one versioned JSON protocol (**shipped v14**).

## 📍 POSITION AS OF 2026-08-24 — this block wins over everything below it

The narrative under "Current state (2026-07-23)" stops around **M5a** and describes **protocol v14**.
It is kept for its gotchas, which are still true. For where the project actually is, read this block,
then `ROADMAP.md`'s POSITION block and `CHANGELOG.md`.

- **M1 → M14 are done. Protocol is v18 on both sides** (`Protocol\Envelope.cs:12`,
  `protocol\Protocol.kt:12`).
- **🔴 The commits are NOT on `main`.** HEAD = **`d64a1b7`** (M14) on branch **`m13-hotspot-links`**;
  **`main` = `e23fbc3`, three commits behind.** All of M13 and M14 are unmerged.
- **🔴 The M15 session was lost and left nothing on disk** (handed over 2026-08-12; no report, no file
  under `Services/`/`Views/`/`ViewModels/` newer than 2026-08-12 08:02 UTC). M15 is re-issued as
  **`tasks/M15a.md` → `tasks/M15b.md`**, one paste, chained.
- **Active coding agent from 2026-08-24: Claude Code 2** — rules `Vibe/agent/AGENTS.md` +
  `claude-code-2/GUIDE.md` + `GUARDRAILS.md`. Live docs are still **this folder** (D-056).
- **M13's hardware verdict:** phone-hosts-AP / PC-joins works end to end in **514 ms** with no discovery
  of any kind. PC-hosts works but is not snappy. ***Wireless debugging* is greyed out while the phone
  hosts a hotspot**, so self-arming via `adb_wifi_enabled` is dead in that role — `adb tcpip 5555` armed
  once over the cable is the shipping answer. **Do not spend another session trying to beat this.**
- **The wallpaper behind Home's widgets is the phone's PRE-BLURRED THUMBNAIL, not a desktop blur.**
  `ThemeSyncService.cs:22-23` and `HomeViewModel.cs:1624` both say so; the bytes come from
  `FetchBulkAsync("wallpaper", id, …)` (`ThemeSyncService.cs:158`) and are produced by
  `service/WallpaperProvider.kt`. The scrim over it is `WallpaperScrimBrush`
  (`Themes/MaterialExpressive.xaml`, applied at `Views\HomePage.xaml:62`), washed with the wallpaper's
  dominant colour by `ApplyWallpaperAccent()` (`ThemeSyncService.cs:266-274`). **Changing the sharpness
  is a two-sided change.**
- **`ContentOpacity` is `1.0` and stays there** (`HomeViewModel.cs:301`, bound at `HomePage.xaml:403`) —
  D-032: offline is full brightness plus **one** banner, never dimming.
- **M13f's `TransportRank.Choose` did not cure the USB-disconnect delay — and M15a measured why.**
  Detection is **bounded at 6–21 s** (one 15 s health poll + two 3 s confirms). The unbounded stage was
  the one after it: **nothing scheduled a reconnect on a drop.** An mDNS advert, a BLE sighting, a
  `NetworkAddressChanged`, a resume or a preference change start one — **a pulled USB cable raises none
  of them**, so there was no wireless candidate for ranking to rank. `OnLinkDropped()` now ends with
  `discovery.ScanNow(); _ = RecoverPresentWirelessDeviceAsync();`. **Stage 1 (how long the adb server
  keeps a stale entry) has still never been measured — do not touch the 15 s interval until it is.**
  Detection time is now logged on every real drop: *"Connection dropped … Detection took NNNN ms …"*.
- **🔴 MEASURED 2026-08-26: A USB ROW VANISHES FROM `adb devices` IN ~330 ms.** Owner pulled the cable
  against Linc's own server (5038); reproduced twice; no stale entry and **no `offline` row at all**.
  **`ConnectionSupervisor.cs:360-363` says the opposite** (M13f's comment: adb's `device` claim
  "outlives the cable"). That comment is **wrong for USB**; it may still hold for a **wireless**
  `host:port` peer, which is what M13f was actually fighting — **untested, settle it before touching
  the ranking logic.**
- **A pulled USB cable is now noticed in ~3-6 s, not 6-21.** `UsbWatcherService` reports departures
  (`UsbDeviceGone`) after `MissesToDeclareGone = 2` consecutive absences; `OnUsbDeviceGone` drops the
  link **only** when the state is Connected, the transport is `AdbUsb` and the serial matches. A poll
  that throws counts **no** miss, so a dead adb server cannot kill a healthy link. **`HealthInterval`
  stays 15 s** and still covers every other transport.
- **The "second phone" card lives at `DevicePage.xaml` row 0, ABOVE both panels.** It used to require
  `IsDisconnected` *and* sit inside the disconnected panel, so it could never render while a phone was
  connected — the one case it exists for. **Cancel is backed by `DeclineUsbDevice(serial)`**, cleared
  when the serial goes, because the 3 s poll would otherwise re-raise the card every 3 s.
- **THE PC-MEDIA PAYLOAD HAS NO `present` FIELD.** The phone sets `present = false` only on
  `none: true`, which needs **no SMTC session AND no Winamp player**. A *paused* player is published
  with a real title and **enabled** buttons. Since M16, `PcMediaService` routes control through the
  **same** `PcMediaRouting.PickTarget` rule it publishes with (`:203` publish, `:257` control), so
  what the phone sees is by construction what its buttons reach; `WinampRemote` remains the fallback
  for "no SMTC session at all". **Before M16 you could pause from the phone but never resume.**
- **The phone's Tools page scrolls, and the trackpad has a floor.** `ToolsScreen.kt:99`
  `.verticalScroll(rememberScrollState())`; the pad is `.heightIn(min = 280.dp)` — **not** a weight
  (`weight` is illegal in a scrolling `Column`). **Before M16 only the trackpad carried `weight(1f)`,
  so every card added to the page came straight out of the pad.** Add cards freely now; keep the floor.
- **EVERY device-scoped adb call in the desktop already names its device** — enumerated at 31 call
  sites in M15a (list in `Vibe/agent/reports/M15a.md`). The only untargeted ones are `adb devices`,
  `start-server` and `restart-server`. **If you see `error: more than one device/emulator`, it is not
  Linc — check the terminal it came from.**
- **🔴 LINC RUNS ITS OWN ADB SERVER ON PORT 5038** (M15a A4). `AdbServerHost.PrivateServerPort`, set
  once by `AdbServerHost.UseIsolatedServerPort()` in `App.xaml.cs` **above** `new ServiceCollection()`.
  **That ordering is load-bearing** — `AdbClient` fields are readonly on singletons and capture their
  endpoint at construction, so a call placed after DI leaves half of Linc on 5037. `hotspotsim` asserts
  the ordering. **Measured fact behind it:** `AdvancedSharpAdbClient` reads `ANDROID_ADB_SERVER_PORT`
  dynamically, which is the only reason one process-wide set covers `AdbClient`, spawned `adb.exe` and
  scrcpy alike. To talk to Linc's server by hand: `$env:ANDROID_ADB_SERVER_PORT=5038` first.
- **A device ADB can see but cannot use is now reported, not dropped.** `UsbWatcherService` used to
  discard every non-`Online` device, so a phone at the *"Allow USB debugging?"* prompt was invisible end
  to end — `ConnectionManager.EnsureOnline`'s message is only reached on a connect attempt, and none is
  made for a device the watcher never reports. Plain-language wording lives in the pure
  `Services/DeviceAdmission.cs`; recovery is the existing 3 s poll, no new timer. **The prompt itself is
  drawn by adbd on the phone — Linc cannot make it appear**, only report the resulting state.
- **D-037 (one active link) is unchanged, but now VISIBLE.** `DeviceAdmission.DecideDeviceSwitch` can
  never return `Connect` while a different phone is live; a second phone gets a message and the
  confirmation card instead of a silent `return`.
- **🔴 HOME STATES THE CONNECTION EXACTLY ONCE — in the Phone card's `ConnectionCaption`.**
  M15b's D1 found **four** statements stacking (`ModelName` fallback, the caption, the offline banner's
  "Phone disconnected —" prefix, `AppsEmptyText`). `homelayoutsim` now fails if disconnect vocabulary
  reappears as a **rendered literal** in `HomePage.xaml`. **The banner (`Services/HomeCacheFormat.cs`)
  says only how old the cache is; it must never restate the link.** *Lesson: this complaint survived
  three attempts because every enumeration was scoped to `Views/`/`ViewModels/` — the worst offender
  was in `Services/`. Scope by behaviour, not by folder.*
- **THE WALLPAPER'S "BLUR" WAS A 48 px DOWNSAMPLE — there was never a blur filter.** Since M15b
  `WallpaperProvider.kt` sends **1600 px on the longest edge, JPEG q85**, never upscaling. Measured
  17 KB → 96 KB for 226× the pixels; **PNG at that size would be 2 MB**, which is why the format
  changed. No protocol change — same bulk kind, same shapes; the desktop decodes format-agnostically
  (`BitmapImage.SetSourceAsync`, `BitmapDecoder.CreateAsync`), so an old phone's small PNG still
  renders. `ComputeDominantAsync` scans a bounded 96 px version; the UI still decodes full size.
- **THE SCRIM IS ONE NUMBER: `ScrimStrength` in `Services/ThemeSyncService.cs`, default `0.0`.**
  0.35 = light veil, 0.6 = strong, **1.0 = the pre-M15b appearance**. The XAML `WallpaperScrimBrush`
  literal stays as-is because `packagesim` pins it as part of M14's palette check; it is only the
  pre-sync placeholder and the Border is gated on `HasWallpaper`.
- **`packagesim` FAILS AFTER ANY `DESKTOP/` SOURCE EDIT** until the Release profile is re-published —
  that is M12i's staleness guard doing its job, not a regression. Re-publish, don't touch the check.

## Current state (2026-07-23)

- **The core product is complete and hardware-verified.** Era 1 (M0–M19) + Era 2 (M00–M05) are
  done; M06 Extend was dropped; M07 Polish is partway (Sync Messages/Calls split landed). See
  `Documentation/02-History.md` for the full account and `Documentation/07-Feature-Catalog.md` for what ships.
- **A new phase ("Convergence") is planned** in the freshly-rewritten `gemini-docs/ROADMAP.md` (milestone
  series **M1…**, unrelated to the archived numbering). It comes straight from the owner's new
  requirement set; `gemini-docs/REQUIREMENTS-ANALYSIS.md` maps each requirement to
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
  M1 lazy-load bug.
- **M2b landed (2026-07-23) — guided onboarding + the desktop carries the companion APK.** A stepped
  wizard (`OnboardingDialog`/`OnboardingViewModel`: enable debugging → detect over ADB → inject the
  **bundled** companion APK → done), reachable from `+` and the zero-device first-run state
  (`apkprobe`/`onboardsim` harnesses green). Fixed a launch **crash** — `OnboardingDialog.xaml` bound
  a **string** to `Visibility` and `x:Bind` only implicit-converts `bool`→`Visibility`, so it threw
  `E_INVALIDARG` on show (now a bool `HasUsbStatus`) — and a **false-success** shortcut where an
  already-connected phone jumped the wizard to "You're all set" (success now only on a real
  `StateChanged→Connected`). **Known limitation:** adding a 2nd phone while the first is connected
  won't complete (D-037 single active link).
- **M2c landed (2026-07-23, first Antigravity session) — onboarding is now the app's own full-window
  UI.** A `MaterialExpressive`-styled `Views/OnboardingView.xaml` (UserControl) overlay replaced the
  centered `ContentDialog` (removed); shown/hidden via an `IsOnboarding` flag on `AppShellViewModel`;
  `MainWindow`/`HomePage` open it; `OnboardingViewModel` step logic untouched. Owner-checked. **M2 is
  complete.** *Cleanup owed:* Antigravity left `task.md`/`walkthrough.md`/`verify-ui.ps1` in the repo
  root — remove them next session (see the new "no stray files" guardrail).
- **M3 DONE (2026-07-23, Antigravity, D-041 revised).** scrcpy vendored (`SCRCPY/Default` submodule
  v3.3.4) + official prebuilt binaries bundled (`SCRCPY/Custom/bin/`, shipped via csproj) + the mirror
  branded via runtime flags (`--window-borderless --window-title "Linc"` + `SCRCPY_ICON_PATH`) — no
  source built/edited. `MirrorService`/`ToolLocator` prefer the bundled scrcpy. (Loose ends closed in
  M3.5b-1: `adb.exe.bak` purged; from-source build now shipped. Frameless-look UI is being replaced by M3.5b-2.)
- **M3.5a DONE (2026-07-23, Antigravity) — the from-source Windows build is PROVEN.** Built
  *unmodified* scrcpy client from `SCRCPY/Default` via MSYS2/MINGW64, client-only with the bundled
  prebuilt server; `./run x --version` = 3.3.4. Tree kept pristine. **The full working recipe + the
  critical DLL-version nuance are recorded in `SCRCPY-WINDOW-SPEC.md` → "PROVEN from-source build
  recipe".** Key facts: don't install `mingw-w64-x86_64-pkg-config` (conflicts with `pkgconf`);
  `-Dprebuilt_server` must be a **relative** path; output at `SCRCPY/Default/x/app/scrcpy.exe`; the
  from-source exe links **avcodec-62/SDL 2.32** (newer than M3's bundled avcodec-61 release DLLs).
- **M3.5b-1 DONE (2026-07-23, Antigravity) — Linc now ships its OWN from-source scrcpy, standalone.**
  `SCRCPY/Custom/src` = unmodified editable source copy (git-untracked; only `src/x/` build output is
  gitignored). Bundle `SCRCPY/Custom/bin/` now carries our from-source `scrcpy.exe` + its **98 mingw64
  DLLs** (copied from `C:\msys64\mingw64\bin` per `ldd`). csproj Content glob narrowed to
  `SCRCPY/Custom/bin/**/*` (source not copied to build output); `ToolLocator` checks `bin/linc.ico`.
  **Stray `adb.exe.bak` PURGED** (M3 loose end closed). Owner-confirmed: mirror opens and responds.
  **Critical build fix (D-050):** the first build omitted `-Dportable=true`, so scrcpy baked in the
  MSYS2 prefix and looked for the server at `C:/msys64/mingw64/share/scrcpy/scrcpy-server` → failed on
  a clean run. Rebuilt **portable** so it resolves `scrcpy-server`/adb/icon next to the exe. Verified
  non-intrusively via `scrcpy.exe --list-encoders` (pushes+starts the server, no mirror window).
- **M3.5b-2 — the window edits (HIGHEST risk; split b-2a / b-2b / b-2c).** Custom rounded region with a
  tunable radius (scrcpy renders a rectangular framebuffer, so rounding clips real pixels — radius
  owner-tuned by eye).
  - **b-2a DONE (2026-07-23, Antigravity) — frameless shape.** `window.{c,h}` under `sys/win/`:
    `WM_NCCALCSIZE`→0, `WM_NCHITTEST→HTCAPTION` top 28px (draggable), rounded region
    (`SC_WIN_CORNER_RADIUS`), DWM shadow. Owner confirmed corners + drag. Two follow-ups below.
  - **b-2b DONE (2026-07-23, Antigravity) — resize/maximize restored + rounder corners.** Kept
    `WS_THICKFRAME|WS_MAXIMIZEBOX`, `hit_resize()` 6px border in `WM_NCHITTEST`, `WM_GETMINMAXINFO`
    work-area clamp, region-off when `IsZoomed`, radius 24→32. Owner-verified. (D-051.)
  - **b-2c DONE-with-bug (2026-07-23, Antigravity) — hover control bar.** Portable `control_bar.{c,h}`;
    drawn before `SDL_RenderPresent` in `sc_display_render`; mouse intercepted in `sc_screen_handle_event`
    before `sc_input_manager_handle_event` (input path untouched); `window.c` returns `HTCLIENT` over the
    top-right cluster. Actions via SDL. **BUG:** draws with `SDL_GetWindowSize` (logical points) but the
    renderer is in drawable pixels + window is `ALLOW_HIGHDPI` → buttons land mid-top-left, wrong size,
    invisible on a scaled display. Owner saw no buttons.
  - **b-2c-2 APPLIED-IN-SOURCE by the planner (2026-07-23), awaiting owner rebuild+test.** The agent
    re-ran the OLD b-2c prompt twice (identical report, zero net changes — the fix never landed), so the
    planner edited the source directly (small, mechanical): `control_bar_render` now draws with
    `SDL_GetRendererOutputSize` and neutralizes logical size (save/restore); `handle_mouse` takes
    pre-scaled pixel coords (`mx_px,my_px,drawable_w`); `screen.c` scales mouse points→pixels via
    `screen->display.renderer` output size. Now window.c `GetClientRect`, overlay draw, and overlay hit
    all agree in physical px. **Owner must rebuild** (`ninja -Cx && cp x/app/scrcpy.exe ../bin/`) and test.
    (Deviation from the code+tests-agent model — noted because the agent was stuck.) Reveal stays
    cluster-based (hover top-RIGHT); whole-strip reveal + fade = a later b-2c-3 polish.
  - **b-2c CLOSED-AS-DEFERRED (2026-07-27, D-052).** The rebuild was confirmed real (`bin/scrcpy.exe`
    byte-identical to `src/x/app/scrcpy.exe`; `control_bar.c` in `meson.build`; render wired at
    `display.c:352`; call sites match the new signatures) — but the owner still saw **no buttons**.
    Root cause is **design, not arithmetic**: `cb->visible` only turns true once the cursor is already
    inside the cluster, which is `SC_CB_BTN_H`(30) **physical** px tall (~24 logical px at 125%), and
    the rest of the top strip is `HTCAPTION`, which Windows treats as non-client so **SDL never
    receives `SDL_MOUSEMOTION` there**. You must hit an invisible sliver to reveal invisible buttons.
    Fix = the b-2c-3 whole-strip reveal, **moved to a Polish backlog worked after the feature
    milestones**. **M3.5 is closed as substantially done** — frameless/rounded/drag/resize/maximize
    are all owner-verified and every window op stays reachable (Aero-snap, Win+↑, double-click
    caption, Alt+F4).
- **M8 Desktop Mode — BUILT AND WORKING, but as a SECOND SCREEN only (D-053, 2026-07-28).**
  Desktop-side only, no protocol bump. `DesktopModeSettings` (per-device on `KnownDevice`),
  `DesktopModeService` (idempotent first-run ADB setup + reboot handling), `DesktopLaunchService`
  (owns the scrcpy process; `--new-display=WxH/DPI --mouse=uhid --keyboard=uhid --max-fps
  --video-bit-rate --no-audio`), `DesktopModeViewModel` + `DesktopModeSettingsViewModel`, a
  collapsible settings expander on `DevicePage`, and `tools/desktopsim` (7 checks, green).
  **Geometry rule:** cap on a **pixel budget** of 1,440,000 (1600x900) preserving aspect
  end-to-end, uniform up-scale if either axis is under the 640 floor, even dimensions,
  DPI = `round(160 * height / 900)` clamped 120–320. Pixel 7 → MatchMonitor 1600x900/160,
  MatchPhone 804x1788/318, Custom untouched.
  **What it does NOT do, and why:** Android 17 (SDK 37) on the Pixel 7 does **not** declare
  `android.software.freeform_window_management`; `dumpsys` reports our virtual display as
  `mWindowingMode=fullscreen, mActivityType=home`. So apps run **fullscreen** on the second screen
  — no caption bars, no dragging, no resizing. **The freeform half of the spec is dropped (D-053);
  real per-app windowing is now M7's job.**
  **GOTCHA WORTH REMEMBERING:** `settings put global <anything> 1` **always** succeeds and reads
  back, whether or not the platform consumes that key. Two sessions were spent writing freeform
  keys that were never read. **Infer capability from `pm list features`, never from a settings
  round-trip.**
- **✅ RESOLVED (H1, 2026-07-31) — the harness/real-store footgun is DEAD. The old `devicesim`
  warnings are RETIRED; stop copying them into task files.** For most of this project's life,
  harnesses compiled the real `DeviceRegistry` and wrote to the owner's real
  `%LOCALAPPDATA%\Linc/settings.json`, guarded only by an opt-in backup/restore `try/finally`. That
  convention failed **twice** and destroyed real pairing data both times (M2a: a killed `devicesim`
  skipped its restore; M6b: `homelayoutsim` sections 5–6 wrote outside the guard entirely).
  **D-057 fixed the class:** `DeviceRegistry(string? rootPath = null)` — production is unchanged, and
  all 28 construction sites across `tools/` now name an explicit root, 27 of them a temp directory. A
  killed run leaks an empty temp folder instead of destroying the pairing. `homelayoutsim`'s
  `[10/10]` check fails loudly if a bare `new DeviceRegistry()` reappears in a harness.
  **Known remaining holes (honest, from the H1 report — do not assume total safety):**
  1. **`blescan` deliberately opens the real store** (`new DeviceRegistry(DeviceRegistry.DefaultRootPath)`).
     It only reads, and it must — its whole job is matching BLE beacons against the owner's *real*
     pinned certificates, so a temp root would make it useless. **Nothing catches it if a future
     session adds a `Save*` call there.**
  2. **`[10/10]` scans four hardcoded filenames.** A brand-new harness is uncovered until someone adds
     it to that list, and a hand-written `Path.Combine(DeviceRegistry.DefaultRootPath, …)` slips past.
  3. **Only `settings.json` is covered.** `cache/`, `sync-state.json`, `tls-identity.pfx` and `logs/`
     are reached by other services and were never audited.
- **✅ MEASURED (M13, 2026-08-11) — THE HOTSPOT ADB LINK WORKS, PHONE-AP DIRECTION, 514 ms END TO END.**
  Owner-run `hotspotprobe` on a real phone hotspot: PC on `10.229.217.12/24`, gateway `10.229.217.188`
  → `DecideRole` picked **CLIENT → dial the gateway**, TCP 5555 **accepted in 43 ms**, `adb connect`
  succeeded, `get-state` = `device`, total **514 ms**. **No mDNS, no ARP, no subnet scan** — the gateway
  address is deterministic on a hotspot and that is the whole trick (D-062).
- **🔴 MEASURED BLOCKER (M13, 2026-08-11) — *Wireless debugging* is GREYED OUT ("wifi disconnected")
  while the phone hosts a hotspot.** Owner-verified on the Pixel 7. The toggle wants the phone to be a
  Wi-Fi **client**, exactly as ROADMAP M13 predicted in July. **Consequence: M13c's self-arming via
  `Settings.Global.adb_wifi_enabled` is DEAD in the phone-AP role — you cannot programmatically enable a
  toggle the OS has disabled.** The working path is **legacy `adb tcpip 5555`, armed once over the
  cable** (survives until reboot), which is also why D-063's onboarding grant is the right home for it.
  **Do not spend another session trying to make wireless debugging work while hosting an AP.**
- **🔴 GOTCHA (M13, 2026-08-11) — `adb get-serialno` OVER TCP RETURNS `ip:port`, NOT THE HARDWARE SERIAL.**
  The live probe returned `get-serialno = 10.229.217.188:5555` and still reported `identity ok: True`.
  **So v18's "reachability is not identity" check is currently comparing an endpoint against an
  endpoint and proves nothing about which device answered.** The real serial needs
  `adb -s <endpoint> shell getprop ro.serialno`. **Fix before trusting any identity claim on a TCP link.**
- **🔴 GOTCHA (M12i, 2026-08-10) — `packagesim` CAN PASS AGAINST A PUBLISH THAT IS MONTHS OLD.** M4b
  added a NuGet dependency (`System.Management`) and `packagesim` stayed green, because the publish
  output it inspects was dated **2026-08-07 21:09** — before M4a and M4b existed — and satisfied every
  check it makes. `System.Management.dll` was not in the folder at all. **This is the M12b lesson in a
  new costume:** M12c fixed *which artefact* the harness inspects; nothing made that artefact **current**.
  A staleness guard now compares the publish against the newest `DESKTOP/Linc.Desktop/**` source.
  **Standing rule: any milestone that adds a package reference must re-publish and confirm the new DLL
  lands** — a dependency that exists only on the dev machine is the exact second-PC failure mode.
- **GOTCHA (M4b, 2026-08-09) — `IsPwrSuspendAllowed()` reports on LEGACY ACPI S3 ONLY and returns
  `false` on Modern Standby machines where Sleep works fine.** Runtime-verified on this dev machine:
  `canSleep: false` while Sleep is a working option in that machine's own Start menu. Use
  `GetPwrCapabilities`' `SystemS0LowPower` alongside it. **Generalises: a Win32 "is X allowed" export
  often answers about one specific legacy mechanism, not about the user-visible capability.**
- **TECHNIQUE WORTH REUSING (M4b) — how to runtime-verify code you are forbidden to execute.** M4b had
  to prove the `IAudioEndpointVolume` COM chain and the WMI brightness queries actually resolve, while
  banned from executing any power action. It built a throwaway console **outside the repo**, compiled
  the real `PcControlService.cs` verbatim, and called only its **read-only** private statics by
  reflection — real production methods, not a model of them — then deleted it (`git status` identical
  before and after). That is genuinely stronger than "it compiles" and cost nothing. **Reuse this shape
  whenever the risky half of a service must stay untouched.**
- **🔴 CRITICAL (M12f, 2026-08-07) — `Environment.GetFolderPath(LocalApplicationData)` IGNORES the
  `LOCALAPPDATA` environment variable. You CANNOT sandbox a child Linc process by redirecting it.**
  `GetFolderPath` resolves through the Windows known-folder API, not the environment. Since
  `DeviceRegistry.DefaultRootPath` (`DeviceRegistry.cs:213`) is built from that call, **a published
  `Linc.Desktop.exe` launched with `LOCALAPPDATA` pointed at a temp directory still reads and writes the
  owner's REAL `%LOCALAPPDATA%\Linc\`** — `settings.json`, `linc.db`, `tls-identity.pfx`, `cache/`,
  `logs/`. Measured in M12f, both in-process and for a child process. The planner had specified exactly
  this as a safety mechanism; the agent probed it, found it false, and refused to launch.
  **The correct lever is the injected root that D-057 already built** — `DeviceRegistry(string? rootPath)`
  with every other store taking `IDeviceRegistry.RootPath` (`App.xaml.cs:70`, `:77`). M12g adds a
  `--data-root` switch so an out-of-process launch can use it.
  **META-LESSON: "redirect the env var" is a Unix reflex and it does not transfer to Windows known
  folders. Probe a sandboxing assumption before you rely on it — the cost of being wrong here is the
  owner's real data, and this was the third near-miss of that exact class** (M2a `devicesim`, M6b
  `homelayoutsim`, this).
- **✅ RESOLVED (M12g + amend, 2026-08-07) — D-057 IS NOW WHOLE, AND A GUARD KEEPS IT THAT WAY.** All four
  bypassing services take `IDeviceRegistry.RootPath`; **exactly three** `GetFolderPath(…LocalApplicationData`
  calls remain in production and `cleanroomsim`'s GUARD section asserts the count at each
  (`DeviceRegistry.cs` exactly 1, `ToolLocator.cs` exactly 2), scanning the tree rather than a filename
  list. A `--data-root <path>` switch (`App.xaml.cs:23/46`) lets an out-of-process launch inject a root;
  it is `null` on every normal launch, so production paths are unchanged.
  **`syncsim`'s real-store backup/delete/restore dance around `sync-state.json` is deleted too** — the
  third harness footgun of that family, after M2a's `devicesim` and M6b's `homelayoutsim`.
  **Gotcha for anyone using `--data-root`: the injected path becomes `RootPath` directly — no `Linc`
  segment is appended (that lives only in `DefaultRootPath`).** And **a bare launch does not write
  `settings.json`** — `DeviceRegistry` only reads it if present and nothing calls `Persist()` at startup,
  so "did the redirect work?" must be proved from something the app actually writes (the startup log line
  under `<root>/logs/` is what `cleanroomsim` uses).
  _(Historical, kept for the lesson — the state before M12g:)_
- **🔴 D-057 WAS ONLY HALF DONE, AND HERE IS THE EXACT LIST (audited M12f/M12g, 2026-08-07).** H1 said
  "only `settings.json` is covered; `cache/`, `sync-state.json`, `tls-identity.pfx`, `logs/` were never
  audited." They have now been. **Four production sites resolve the store root directly and bypass
  `IDeviceRegistry.RootPath`:** `DeviceCacheService.cs:67` (`…\Linc/cache`), `LogService.cs:40`
  (`…\Linc/logs`), `SyncEngine.cs:39`, `TlsTransportService.cs:49` (`…\Linc/tls-identity.pfx` — the
  phone's pinned identity; overwriting it breaks the pairing). Only `LincStore` and `AppCatalog` take the
  injected root (`App.xaml.cs:70`, `:77`). **So any attempt to sandbox a whole Linc process by injecting
  a root gives a PARTIAL sandbox that looks safe and is not** — worse than refusing to run.
  **`ToolLocator.cs:31,56` is NOT part of this** — it reads the Android SDK path, deliberately, and stays.
  **And do NOT redirect the user-facing folders**: `HomeViewModel.cs:182`, `SyncViewModel.cs:286`,
  `FilesViewModel.cs:198`, `FileService.cs:192`, `ShareService.cs:51`, `SyncEngine.cs:89` resolve
  `MyPictures`/`UserProfile` (`Pictures/Linc`, `Downloads/Linc`) — those are the **user's own files** and
  must never move. **`SyncEngine` is in both lists: line 39 is our store, line 89 is the user's Pictures.**
  The rule: **`LocalApplicationData` = our private store, belongs under the injected root.
  `MyPictures`/`UserProfile` = the user's folders, never touch.**
- **GOTCHA (M12d, 2026-08-07) — a `.` in a folder name under `Content` trips the WinUI resource indexer.**
  The scrcpy bundle lives at `SCRCPY/Linc.scrcpy/bin/` (the owner's chosen name, final). `makepri.exe`
  scans every project-wide `Content` item and reads the **dot** as a resource-qualifier delimiter, so a
  publish now emits `PRI249: 0xdef00520 - Invalid qualifier: 0-0`. **The output is correct despite the
  warning** — verified 104 files in the publish bundle, `Linc.Desktop.pri` present, indexer reported
  17,790 candidates and "Successfully Completed". It was absent before M12d **only because the stale
  `SCRCPY/Custom` glob matched zero files, so nothing under that path was indexed at all.** Noise, not
  breakage — but noise around `.pri` generation deserves watching, since a missing `Linc.Desktop.pri` is
  exactly what killed the app on the second PC in M12b.
- **GOTCHA (M12d) — `ToolLocator.FindAdb()` fails by SILENTLY FALLING THROUGH, never by erroring.**
  M12d's negative proof pointed the bundled-adb candidate at the old path; `FindAdb()` did not return
  null or throw — it walked on down its candidate list and **resolved the dev machine's real
  `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`**, which is the M12c defect exactly. **Any check on
  this function must assert the EXACT expected path, never merely non-null.** `packagesim` check 7 does;
  copy that shape.
- **THE SECOND-PC LESSON, GENERALISED (M12b/M12c/M12d) — the dev machine lends things the package does
  not.** Every defect the second-PC trips found was that one shape: a missing `.pri`, an adb lookup that
  never checked our own bundle, a dev-tree depth wrong since M3 and covered for by the `PATH` scan below
  it. **Two of those three are reproducible on this machine** by running the publish output with `PATH`
  reduced to `system32`, `ANDROID_HOME`/`ANDROID_SDK_ROOT` unset and `LOCALAPPDATA` redirected to a temp
  root — which also keeps the child process out of the owner's real store, the D-057 rule applied to a
  whole process. **What genuinely cannot be faked locally:** machine-wide installs (VC++ redist, .NET
  desktop runtime, WindowsAppSDK). Those stay second-PC territory, but they change far less often than
  our own code. → `tools/cleanroomsim`, M12f.
- **GOTCHA (M6c, 2026-07-31) — `Border` is not a `Control`, so it has NO `IsEnabled`.** The house
  cards (`ExpressiveCard`, `FrostedCard`) are `Border`s, so "disable, don't hide" cannot be done
  literally on a card. The established substitute is dimming — `Opacity` 0.55, the same value the
  offline state already uses via `ContentOpacity` — plus making the contents inert.
- **GOTCHA (M6c) — adding a Home section breaks `homelayoutsim` until you fix the count.** Its
  "all sections hidden" check hard-coded `6`. It now counts off `HomeLayout.AllSectionIds`, but the
  lesson generalises: **a harness that hard-codes a collection's size will fail the day the collection
  grows.** Any task that adds a section must also add its `ViewModel.Show*` binding to the wiring list.
- **RUN HARNESSES WITH CWD = `…\yellow\Linc\`, NOT `…\yellow\`.** `homelayoutsim`'s `FindRepoRoot`
  walks up looking for a **direct** `DESKTOP` child, so launched from `yellow/` it resolves the repo
  root to `D:\` and fails `[9/11]`/`[10/11]` with a misleading `Cannot read D:\DESKTOP\…`. Nothing is
  wrong with the code — it is looking in the wrong place. (`storesim`'s own `FindRepoRoot` handles both
  layouts; the others do not.) Found in M9a.
- **`Linc.Desktop` is single-instance and does NOT exit on `CloseMainWindow`.** `App.OnLaunched` holds a
  `Local/LincDesktopSingleInstance` mutex and exits a second copy, so you cannot run two for a test.
  And `CloseMainWindow` hides to the tray (H.NotifyIcon) rather than terminating — an agent waiting for
  the process to end will wait forever and conclude it hung. Use `Stop-Process` to end a test launch.
  Found in M9a; **not a bug, do not "fix" it.**
- **✅ CORRECTED (M7b, 2026-07-31) — `ItemsRepeater` DOES virtualize, and the old note here was wrong.**
  This entry previously claimed the Apps list was not virtualized and that a wrapper element would kill
  virtualization. **Both claims were speculation that was never measured, and both are false.** M7b
  built `tools/appgridprobe` (a WinUI-hosting console) and measured it: 500 items, 640×420 viewport →
  **peak 75 tiles realized**, against a theoretical bound of 126 (a screenful ≈ 24, plus
  `VerticalCacheLength`'s default 2.0 viewports either side). It then re-ran with a `StackPanel`
  wrapper (**still 75**) and with no scrolling host at all (**67**). `ItemsRepeater` decides what to
  realize from `EffectiveViewportChanged`, which the framework propagates from the window — **not**
  from the height it is measured with. The "a wrapper kills virtualization" rule is `ItemsControl` /
  `ItemsStackPanel` folklore and does not apply to `ItemsRepeater`.
  **What actually caused M6c to fetch every icon** was the view model's own eager sweep over `Apps`,
  not the layout. M7b deleted that sweep; the only icon trigger is now `ElementPrepared`.
  **META-LESSON — this cost a milestone's worth of wrong planning.** An unmeasured guess from a
  session report was written into BRAIN as a gotcha, became doctrine, and shaped two task files. **Do
  not record a performance or framework-behaviour claim here unless something measured it.** If it is
  a suspicion, label it a suspicion.
- **GOTCHA (M9e, 2026-08-06) — `LincStore.IsAvailable` starts FALSE and every method silently no-ops
  until the schema task finishes. This is a startup race, and it ate three features.** `App.OnLaunched`
  fires `EnsureSchemaAsync()` in a **non-blocking** `Task.Run` after `_window.Activate()`, but
  `HomeViewModel`/`SyncViewModel` are constructed **synchronously as part of `Activate()`** — faster than
  the background task. So their constructor cache reads run against a store that returns empty and
  **logs nothing at all**. Measured, not guessed — instrumented timestamps from a real launch:
  `13:06:02.690 IsAvailable=False` → `13:06:04.019 IsAvailable=False` → `13:06:04.086 schema ready` →
  `13:06:04.162 IsAvailable=True`. Two reads lost before the store was ready.
  **The fix is `await _store.EnsureSchemaAsync()` before the first read** — it is idempotent and
  documented safe to call at startup. **Any new consumer of `LincStore` that reads during construction
  must do the same.** `homecachesim` [11] and `storesim` [12] now fail if the await is removed.
  **META-LESSON: a silent degrade path is a race detector that never fires.** `IsAvailable` returning
  empty with no log is what made this invisible through three green milestones.
- **MEASURED (M9e, 2026-08-06) — scrcpy virtual displays do NOT crash at low counts, and the old
  suspicion that they did was wrong.** Measured on the real Pixel 7 with production args: **4 staggered
  windows survived, 8 survived, 5 launched near-simultaneously survived.** No documented concurrent-display
  limit exists in the vendored server (`DisplayManager.java`, `NewDisplayCapture.java`). Killing one
  window's process leaves the others alive — `OnProcessExited` was already correctly scoped.
  **What IS real, and measured:** (1) **a virtual-display leak on abrupt kill** — `dumpsys SurfaceFlinger
  --display-id` showed orphaned displays 60/74/75 alive after their Windows processes were gone; scrcpy
  destroys the display on a *clean* exit only. (2) **System-wide memory pressure at 8 windows** —
  `mem-pressure-event`, webview/Files processes dying, renderer restarts. (3) **A one-off app cross-wire**:
  a window labelled "Lichess" was running Reddit under near-simultaneous launch; **seen once, not
  reproduced — treat as a suspicion, not a fact.**
  **A precautionary cap of 6 concurrent windows shipped (`AppLaunchService.MaxConcurrentWindows`). It is
  a judgement call, NOT a measured ceiling** — nothing crashed at 8. Labelled as such in code.
- **NOT A BUG (M9e) — app windows show desktop chrome because system decorations are ON by default.**
  `dumpsys window displays` shows a `StatusBar` window on every virtual display; there is no
  `--no-vd-system-decorations` in the launch args. That is why per-app windows "look like Desktop Mode."
  **Adding that flag is the one-line change if the owner wants bare app windows.**
- **GOTCHA (M9b, 2026-08-02) — a harness that RE-IMPLEMENTS the guard it is testing proves nothing about
  production.** `storesim` §8 verifies "toggle off means an empty table" by writing its own
  `if (historyEnabled) await store.InsertNotificationAsync(...)` — a faithful *model* of
  `NotificationSyncService.cs:182`, but only a model. **Delete the real guard and §8 stays green.** The
  negative proof for that acceptance item broke the harness's copy, so it demonstrated the harness works,
  not that the feature does. This is the M5c-2 shape again ("a green harness can sit directly underneath a
  dead UI"), and the fix is the one M5c-3 already established here: **a crude text check that reads the
  production source and fails if the guard identifier is gone.** Generalised rule: **when the behaviour
  under test lives in a file the harness cannot call into, assert against the file's text — and break the
  PRODUCTION source for the negative proof, never the harness's model of it.**
- **M9b DONE (2026-08-02, opencode) — notification history, opt-in and default OFF (D-045).**
  `NotificationHistoryEnabled` (false) + `NotificationHistoryRetentionDays` (30) are **app-wide** on
  `DeviceRegistry` in `settings.json`, beside `ClipboardSyncEnabled` — **not** on `KnownDevice`, because
  the preference is about this PC's disk, not about a phone. One guarded persist at
  `NotificationSyncService.OnCompanionMessage`, inside the `!isBacklog` branch, so a reconnect backlog
  never backfills history. Text only — **no icon/art blobs** (they need a hashing/GC design of their own).
  Turning the feature OFF stops new rows and deletes nothing; `Clear history` is the only deleter, and the
  Settings card says so in plain language because a user will otherwise assume otherwise.
- **GOTCHA (M6a, 2026-07-31) — a computed visibility property needs a raise at EVERY source's change
  site.** M6a replaced three cards' direct `Visibility="{x:Bind …HasMedia}"` bindings with combined
  ones (`ShowMediaWidget => ShowMedia && HasMedia`). The combined property was raised only where the
  *layout* half changed, never where the *data* half changed — so the cards stopped appearing when
  music started or a photo arrived. **When you compose a property out of two sources, grep for every
  `OnPropertyChanged` of both sources and add the composed name there too** (or use
  `[NotifyPropertyChangedFor]`). A clean build and a green harness cannot see this.
- **GOTCHA (M6a) — `x:Bind` has no `&&`, and `x:DataType` cannot resolve a nested class.** Combining
  two conditions means a computed property on the view model (see above), and any type used as a
  `DataTemplate`'s `x:DataType` must be **top-level**, not nested inside the view model.
- **GOTCHA (M6a) — a `record` holding an `IReadOnlyList<T>` does NOT get value equality.** The
  compiler-generated `Equals` compares the list **by reference**, so two records with identical
  contents are unequal and round-trip tests fail. `HomeLayout` overrides `Equals`/`GetHashCode` for
  this reason. `MirrorSettings`/`DesktopModeSettings` never hit it because they hold only scalars.
- **GOTCHA (M5c-2, 2026-07-30) — a green harness can sit directly underneath a dead UI.** The v15
  display widgets shipped with the rotation `ComboBox` and the brightness `ToggleSwitch` bound
  `Mode=OneWay` and **no `SelectionChanged`/`Toggled` handler**, so the commands that talk to the phone
  were never invoked from the view. The build was clean (a `OneWay` binding to a read-only property is
  legal XAML) and `tools/displaysim` was 23/23 green — because it tests the pure payload helpers, a
  layer below the bug. **When the deliverable is UI, grep `Views/` for the command names and confirm
  every interactive control has a path to one.**
- **GOTCHA (same session) — `Slider.ValueChanged` fires on PROGRAMMATIC changes too.** Pairing
  `Value="{x:Bind …, Mode=OneWay}"` with `ValueChanged="…"` means every incoming `status` update sets
  the value, fires the handler and **sends a write straight back to the phone**. Any control that both
  displays remote state and originates commands needs a suppression flag around programmatic updates
  (`MirrorSettingsViewModel`'s `_syncing` is the house pattern). The view model's own private setters
  were the right defence and correctly prevented this one layer up — the view reintroduced it.
- **M5b DONE (2026-07-29, opencode) — the normal mirror's scrcpy settings are editable + per-device.**
  `Services/MirrorSettings.cs` (dep-free record + pure `ValidateSettingsFields`) persisted as
  `KnownDevice.Mirror`; `MirrorService.StartAsync` takes the record and builds args via a pure static
  `BuildScrcpyArgs`; new `MirrorSettingsViewModel` + a "Mirror settings" expander in the Tools card;
  new `tools/mirrorsettingssim`. **D-054: the preset combo is only a shortcut that writes into
  `MirrorSettings` — that record is the single source of truth**, and a never-touched device must
  still emit the old Balanced args byte-for-byte.
  **GOTCHA WORTH REMEMBERING — the harnesses that compile app source verbatim are a hidden build
  surface.** `tools/devicesim` and `tools/blescan` `<Compile Include>` the real `DeviceRegistry.cs`
  (D-036) with **no** `ProjectReference`, so **every new type `KnownDevice` gains must be added to
  their csproj too.** M8a added `DesktopModeSettings` and M5b added `MirrorSettings` without doing
  that, and **both harnesses silently stopped compiling** — unnoticed because `devicesim` carries a
  do-not-run footgun warning and `blescan` needs hardware. **When you touch `KnownDevice`, grep
  `tools/*/*.csproj` for `DeviceRegistry.cs` and fix every consumer.**
- **M5a DONE (2026-07-29, opencode) — the Device page can finally scroll, and has a "Tools" card.**
  `Views/DevicePage.xaml` had **no scroll host at all** (root was a bare `<Grid Padding="32">`), so
  anything taller than the window was clipped with no way to reach it — which is why the expanded
  Desktop Mode settings were unreachable. Now wrapped in a `ScrollViewer` (vertical Auto, horizontal
  Disabled) with the `Padding="32"` moved onto the inner Grid so the scrollbar sits at the window
  edge. The Screen-mirroring and Desktop-Mode blocks were moved verbatim out of their two separate
  `ExpressiveCard` borders into one "Tools" `ExpressiveCard`, with section sub-headings and a divider.
  **Lesson worth keeping: every new WinUI page needs a scroll host from day one** — a page that fits
  today silently becomes unusable the first time a section expands. **Cosmetic debt:** the moved
  blocks kept their inline `TitleText` labels, so "Tools" / "Screen mirroring" / "Screen" all render
  at the same size — folded into M5b.
- **ORDER CHANGE (2026-07-27, D-052): M8 Desktop Mode is NEXT**, taking M4's slot; **M4
  (phone-as-remote Tools) moves into M8's old slot.** M8 needs only M3's Custom scrcpy (done); M4
  needs a protocol bump (v14→v15), so deferring it keeps the wire format frozen. Independent
  milestones — no dependency risk.
- **Operational note — Antigravity runs display-headless:** it could NOT run `mirrorsim` (DXGI desktop
  duplication needs a real display). Any display-capture-dependent harness/check under Antigravity must
  be run by the owner (or Claude) instead — don't treat "mirrorsim skipped" as a pass or a regression.
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
  manual script** (AGENT-GUIDE.md §5, the PC-side twin of D-031's phone-tap rule). Tree enumeration and
  `Invoke` don't move the pointer and stay fine.
- Never log clipboard text or notification bodies (only that an event happened). **M6's opt-in
  notification history is the one scoped, consented exception (D-045) — default off.**

## Where things live

- **Protocol spec:** `gemini-docs/PROTOCOL.md` — implemented twice, Kotlin `app.linc.android.protocol`
  and C# `Linc.Desktop.Protocol`, zero shared code (D-006).
- **Desktop:** `DESKTOP/Linc.Desktop/` — `Services/`, `ViewModels/`, `Views/`, `Themes/`. Map in
  `Documentation/04-Desktop-Application.md`.
- **Android:** `ANDROID/app/src/main/java/app/linc/android/` — `service/`, `ui/`, `protocol/`. Map
  in `Documentation/05-Android-Application.md`.
- **Persistence:** `%LOCALAPPDATA%\Linc\` — `settings.json`, `cache/<serial>/`, `sync-state.json`,
  `logs/`. (M5 introduces a SQLite `LincStore` alongside these.)
- **Verification harnesses:** `tools/probe`, `tools/syncsim`, `tools/filesim`, `tools/devicesim`,
  `tools/blescan`, `tools/mirrorsim` (D-036) — plain `net8.0` consoles, run before handing anything
  to the user. See `Documentation/08-Build-Test-and-Deploy.md`. **All harnesses now run against a temp root
  (D-057) and cannot touch `%LOCALAPPDATA%\Linc` — the footgun below is RETIRED, kept for history.**
  ~~**`devicesim` footgun:**~~ it backs up, briefly
  deletes, then restores the real `settings.json`, so it must **run to completion** — don't pipe it
  into something that can exit early (`| Select -First`, `| head`) or kill it mid-run, or the restore
  never happens and the real pairing is left overwritten with fake test devices (this bit M2a).
