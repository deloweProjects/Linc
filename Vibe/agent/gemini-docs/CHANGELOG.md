# Changelog

Chronological record of significant features, fixes, refactors, and architectural changes. Newest
first.

> **History before 2026-07-22** (all of Era 1 `M0–M19` and Era 2 `M00–M07`) is captured formally
> in `Documentation/02-History.md`, and the raw old changelog is preserved at
> `v2.docs.old/CHANGELOG.md`. This file starts fresh for the **Convergence** phase (`gemini-docs/ROADMAP.md`).

## [Unreleased]

### Added
- 2026-07-23 — **M3.5b-2c-2: fixed the invisible control-bar buttons (planner-applied, awaiting rebuild).**
  The agent re-ran the original b-2c prompt (identical report, no net change), so the planner edited the
  source directly: `control_bar.c` draws via `SDL_GetRendererOutputSize` + neutralizes logical size;
  `sc_control_bar_handle_mouse` now takes pre-scaled pixel coords; `screen.c` scales SDL mouse points→
  drawable pixels using `screen->display.renderer`. All coordinate spaces (window.c `GetClientRect`,
  overlay draw, overlay hit-test) now agree in physical pixels, so buttons render/interact at the true
  top-right on HiDPI. Files: `control_bar.{c,h}`, `screen.c`. **Needs an owner rebuild + hands-on test.**
- 2026-07-23 — **M3.5b-2c: hover control bar (min/max/close) — implemented, has a coordinate bug (Antigravity).**
  New portable `control_bar.{c,h}` component (SDL-drawn buttons: minimize/maximize-restore/close), drawn
  in `sc_display_render` before `SDL_RenderPresent`, mouse intercepted in `sc_screen_handle_event` before
  `sc_input_manager_handle_event` (protocol/input path untouched), and `window.c` `WM_NCHITTEST` returns
  `HTCLIENT` over the top-right cluster so SDL receives the clicks. Built clean. **BUG (owner: no buttons
  on hover):** the overlay uses `SDL_GetWindowSize` (logical *points*) while the SDL renderer draws in
  *drawable pixels*, and the window has `SDL_WINDOW_ALLOW_HIGHDPI` — so on a scaled display the buttons
  render mid-top-left at the wrong size instead of the top-right corner, effectively invisible. Fix =
  **b-2c-2**: draw + hit-test in drawable pixels (`SDL_GetRendererOutputSize`, scale mouse points→pixels)
  to match window.c's physical-pixel hit-test. `SCRCPY/Default` untouched.
- 2026-07-23 — **M3.5b-2b: restored resize + maximize on the custom window + rounder corners (Antigravity).**
  Fixed the b-2a regression (frameless window couldn't resize/maximize). In `sys/win/window.{c,h}`:
  re-added `WS_THICKFRAME | WS_MAXIMIZEBOX` (via `SetWindowLongPtr` + `SWP_FRAMECHANGED`) while still
  returning 0 from `WM_NCCALCSIZE`; `hit_resize()` returns the 8 `HT*` edge/corner codes for a 6px
  inset border (`SC_WIN_RESIZE_BORDER`) evaluated before the caption check; `WM_GETMINMAXINFO` clamps
  maximize to the monitor work area (`rcWork`); `apply_rounded_region` drops the region when `IsZoomed`
  (square corners maximized). Corner radius 24→32. Built portable via MSYS2; `--list-encoders` clean;
  **owner-verified: edge/corner resize + maximize now work** (double-click strip / Win+↑). See D-051.
  Remaining: **b-2c** hover control bar (min/max/close buttons). `SCRCPY/Default` untouched.
- 2026-07-23 — **M3.5b-2 (first cut): frameless + rounded + draggable custom scrcpy window (Antigravity).**
  New Windows-only module `SCRCPY/Custom/src/app/src/sys/win/window.{c,h}`, invoked from `screen.c`
  after `SDL_CreateWindow` (`#ifdef _WIN32`); `meson.build` adds `window.c` + links `dwmapi`.
  Subclasses SDL's `WndProc` (`SetWindowLongPtr(GWLP_WNDPROC)`, old proc chained): `WM_NCCALCSIZE`→0
  (no title bar), `WM_NCHITTEST` makes the top `SC_WIN_CAPTION_H`(28)px a drag zone, `SetWindowRgn`
  rounded corners (`SC_WIN_CORNER_RADIUS`, re-applied on `WM_SIZE`), DWM drop shadow via
  `DwmExtendFrameIntoClientArea`. Built MSBuild x64; `--list-encoders` clean; owner confirmed corners
  + drag. **Follow-ups → b-2b:** (1) **regression** — window can't resize/maximize (frameless dropped
  `WS_THICKFRAME`); (2) owner wants a slightly larger corner radius. Hover-reveal control buttons split
  to **b-2c**. See `SCRCPY-WINDOW-SPEC.md` and D-051.
- 2026-07-23 — **M3.5b-1: Linc ships its OWN from-source scrcpy, standalone (Antigravity).** Made
  `SCRCPY/Custom/src` an unmodified editable source copy (git-untracked; only `src/x/` build output
  gitignored), built it via the proven MSYS2/MINGW64 recipe, and replaced M3's official prebuilt
  binaries in `SCRCPY/Custom/bin/` with our build + its **98 mingw64 dependency DLLs** (resolved by
  `ldd`, copied from `C:\msys64\mingw64\bin`). csproj Content glob narrowed `../../SCRCPY/Custom/**/*`
  → `../../SCRCPY/Custom/bin/**/*` so the source tree isn't copied into build output; `ToolLocator`
  now also checks `SCRCPY/Custom/bin/linc.ico`. Purged the stray `adb.exe.bak` (M3 loose end) plus
  scrcpy's `open_a_terminal_here.bat` / `scrcpy-console.bat` / `scrcpy-noconsole.vbs` / `icon.png`.
  **Fix (D-050):** the initial build lacked `-Dportable=true`, so scrcpy resolved `scrcpy-server` at
  the MSYS2 prefix (`C:/msys64/mingw64/share/scrcpy/scrcpy-server`) and failed on a clean machine with
  *"does not exist … Server connection failed"*. Rebuilt **portable**; verified non-intrusively with
  `scrcpy.exe --list-encoders` (starts the server, no mirror window) and owner-confirmed live mirror.
- 2026-07-23 — **M3.5a: the from-source scrcpy Windows build is proven (gate PASSED; Antigravity).**
  Built the *unmodified* scrcpy client from the `SCRCPY/Default` submodule via MSYS2/MINGW64
  (client-only, using the bundled prebuilt server, so no Java/Android SDK); `./run x --version`
  reported scrcpy 3.3.4. `SCRCPY/Default` kept pristine (git clean, no commits). Two snags resolved
  in-bounds: the `pkg-config`/`pkgconf` conflict, and `-Dprebuilt_server` needing a **relative** path.
  The full working recipe + the DLL-version nuance (the build links avcodec-62/SDL 2.32, newer than
  M3's avcodec-61 release DLLs) are recorded in `SCRCPY-WINDOW-SPEC.md`. This unblocks M3.5b (the
  actual window work).
- 2026-07-23 — **M3: scrcpy vendored + bundled + the mirror window branded (D-041 revised; Antigravity).**
  `Genymobile/scrcpy` added as a git submodule at `SCRCPY/Default` (pinned **v3.3.4**) for upstream
  tracking; the official prebuilt Windows binaries (scrcpy.exe, scrcpy-server, SDL2/FFmpeg/libusb DLLs,
  adb) bundled under `SCRCPY/Custom/bin/` and shipped with the desktop via `.csproj` Content links;
  `SCRCPY/NOTICE.md` + a Linc `.ico` added. `ToolLocator`/`MirrorService` now prefer the bundled
  scrcpy and launch it with `--window-borderless --window-title "Linc"` + `SCRCPY_ICON_PATH` — a
  **frameless, Linc-branded** mirror with **no scrcpy source edited or built** (interim look; M3.5
  supersedes it). Both apps build clean; bundled `scrcpy.exe --version` = 3.3.4; files confirmed in the
  build output. *(No protocol change.)* Loose ends: a stray `adb.exe.bak` sits in the bundle;
  `mirrorsim` couldn't run in Antigravity's environment (display-dependent — not an M3 regression); the
  frameless-look **manual UI check is owed to the owner**.

### Changed
- 2026-07-23 — **M2c: the onboarding wizard is now the app's own full-window UI** (owner feedback;
  first Antigravity session). The first-run wizard was a centered `ContentDialog`; it's now a
  **full-window in-app overlay** — new `Views/OnboardingView.xaml` (UserControl) hosts the step UI
  styled with `MaterialExpressive` tokens to match `SettingsPage`, layered over the whole
  `MainWindow` root grid and shown/hidden via an `IsOnboarding` flag on `AppShellViewModel`;
  `MainWindow` / `HomePage` open the overlay instead of the dialog; **`OnboardingDialog` removed**.
  `OnboardingViewModel` step logic unchanged. Desktop + Android build clean; `apkprobe` /
  `onboardsim` / `devicesim` green; **owner-checked, looks good.** *(No protocol change.)* Cleanup
  owed: it left `task.md` / `walkthrough.md` / `verify-ui.ps1` scratch files in the repo root.

### Added
- 2026-07-23 — **M2b: guided first-run onboarding + the desktop carries the companion APK.** A
  stepped onboarding wizard (`OnboardingDialog` / `OnboardingViewModel`) — Intro → enable USB +
  wireless debugging → detect over ADB → install the bundled companion → "You're all set" — opened
  from the tab-strip `+` and reachable from the zero-device first-run state. The desktop now
  **bundles the companion APK and injects it** during onboarding (no manual APK handling).
  `apkprobe` / `onboardsim` harnesses added and green. *(No protocol change.)* **Owner feedback: the
  wizard is a centered `ContentDialog`; it should use the app's own UI, full-screen — queued as M2c.**

### Fixed
- 2026-07-23 — **M2b: onboarding crash + false-success, both fixed.** The wizard crashed the app on
  open — `OnboardingDialog.xaml` bound a **string** (`UsbStatusText`) straight to `Visibility`, and
  WinUI `x:Bind` implicitly converts `bool`→`Visibility` but **not** `string`, so realizing the
  dialog threw `E_INVALIDARG` (`0xc000027b` STOWED_EXCEPTION in `Microsoft.UI.Xaml.dll`). Fixed with a
  bool `HasUsbStatus`. Also removed a false-success shortcut where `OnUsbDeviceSeen` treated an
  already-connected phone as a completed pairing and jumped straight to "You're all set"; success now
  comes only from a genuine `StateChanged → Connected` transition. Verified via UI-Automation
  InvokePattern (no cursor moved, phone untouched); builds + harnesses green.
- 2026-07-22 — **M2a: device management actually works, and the Files page reaches the whole
  phone.** M1 made the tab strip visible but its actions were never clicked; hands-on testing found
  `+`, close-tab, and switching all did nothing. Root cause of the dead `+`: pairing was gated by
  `IsDisconnected`, so the QR/code card was hidden whenever a phone was connected. Fixes:
  `DeviceViewModel.ShowPairing => PairingActive` (+ `ShowDisconnectedArea`/`ShowConnectedArea`), so
  pairing shows over a live link; `MainWindow.xaml.cs` `+` navigates to the Device page and runs
  `StartPairingCommand`; `DeviceTabsViewModel.CloseTab` dropped the `Count<=1` guard (closing the
  last tab hides it; the phone stays paired per D-033; closing the active tab switches to a
  neighbour first). Files page: `UpAsync` climbs to `/`, a denied directory degrades gracefully with
  a plain-language note (a shell `ls` probe surfaces `Permission denied`, which the sync protocol
  otherwise returns as an empty list), and downloads now land in **`Downloads/Linc`**. Verified with
  real DPI-aware clicks on the Pixel 7 + `devicesim`/`filesim` green.
- 2026-07-22 — **M1: the desktop Files page lists the phone's directory again.** Root cause: the
  Files load was edge-triggered on the supervisor's `StateChanged → Connected` transition, but
  `FilesViewModel` is created lazily on first navigation to the page — after `AppShellViewModel`
  has already connected at launch — so that transition had already fired and been missed, leaving
  the listing blank. Fix (`FilesViewModel.cs`): also load once in the constructor when the link is
  already `Connected`. Verified via UI Automation against the Pixel 7: opening Files while already
  connected now populates the listing. Added **`tools/filesim`**, a regression compiling the real
  `TlsFileService`/`FileServiceRouter` + `Framing` — covers the Direct-TLS files channel and the
  router's `HasAdb` path selection.

### Changed
- 2026-07-23 — **M2a: Settings › Devices management + a Files "Go to" menu.** A new Settings
  "Devices" card lists every paired phone with **Set active** (which also un-hides a closed tab) and
  **Forget** (clears that device's cache + unpairs). The Files page's single-select storage dropdown
  became a **"Go to"** menu (Phone root `/`, Internal storage, SD cards) rebuilt live on open so
  every pick navigates. `tools/devicesim` gained a close-last-tab scenario. *(No protocol change.)*
- 2026-07-22 — **M1: the device tab strip is always visible once a phone is paired.**
  `DeviceTabsViewModel.IsStripVisible` went from `Tabs.Count > 1` to `>= 1`, so a single-device
  owner finally sees their tab and the "+" add button (previously the strip hid itself for anyone
  with one phone). Verified via UI Automation: the "Pixel 7" tab renders with one paired device.
- 2026-07-22 — **Documentation reorganized; a fresh roadmap for the next phase.** Added
  **`Documentation/`**, the formal human-facing documentation of the whole project (master overview +
  vision, history, architecture, the Desktop app, the Android app, connectivity & protocol, the
  feature catalog, build/test/deploy, decisions, glossary). Archived the Era 1/2 planning docs
  (BRAIN, CHANGELOG, ROADMAP, FEATURES, PIPELINE, EXTEND-MODE, DESCRIPTION, VISION, task.txt) into
  **`v2.docs.old/`** and rewrote `gemini-docs/ROADMAP.md`, `FEATURES.md`, `BRAIN.md`, `CHANGELOG.md`, and
  `task.txt` clean for the new **Convergence** phase. Kept the living technical references
  (`PROTOCOL.md`, `DECISIONS.md`, `TECH_STACK.md`, `THEME.md`, `ARCHITECTURE.md`, `AGENT-GUIDE.md`,
  `CONTRIBUTING.md`) in place. Added `gemini-docs/REQUIREMENTS-ANALYSIS.md` (each new requirement mapped to
  Implemented/Polish/New/Bug and to the code to reuse) and **decisions D-041…D-048** for the new
  work. No source code changed in this pass; the previous session's uncommitted M07 Sync-page split
  remains in the working tree.

### Planned next
- **M2c — redesign the onboarding as the app's own full-screen UI** (owner feedback): replace the
  centered `ContentDialog` with a full-window, in-app-styled first-run experience (Material
  Expressive brushes, same control styles as the other pages); keep the working `OnboardingViewModel`
  step logic. **First Antigravity session.**
- **M3 — vendor scrcpy** (`SCRCPY/Default` + `SCRCPY/Custom`) as the foundation for the
  screen-feature milestones. See `gemini-docs/ROADMAP.md`.
- **Known limitation (M2b):** opening the wizard to add a *second* phone while the first is actively
  connected won't complete the new connect — the supervisor holds a single active link by design
  (D-037). The real first-run path (zero devices → wizard) is unaffected.
- **Known caveat from M2a:** a `devicesim` run (killed mid-way by a piped command before its restore
  step) overwrote the real Pixel 7 pairing with test devices; it was cleaned up and the phone
  re-paired over USB, but the device's **sync-lane / folder-sync config may need re-setting**.
- **Known caveat from M2a:** a `devicesim` run (killed mid-way by a piped command before its restore
  step) overwrote the real Pixel 7 pairing with test devices; it was cleaned up and the phone
  re-paired over USB, but the device's **sync-lane / folder-sync config may need re-setting**.
