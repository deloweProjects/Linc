# Features

Checklist for the current phase. Full shipped catalog: `Documentation/07-Feature-Catalog.md`. This tracks
the roadmap (`ROADMAP.md`) + analysis (`REQUIREMENTS-ANALYSIS.md`, incl. the owner corrections).

Legend: `[x]` done · `[~]` in code but not visible/verified to the owner · `[ ]` planned · `[bug]`
broken, to fix.

## 📍 CONVERGENCE — STATE AS OF 2026-08-12 (this block supersedes anything below it)

Protocol **v18** both sides. The whole M1–M14 series has landed.

- [x] **M1** Files fix + always-visible device tabs · **M2** guided onboarding + bundled companion APK
- [x] **M3 / M3.5** scrcpy vendored, built from source, frameless rounded draggable window
- [x] **M5** PC-controls-phone widgets, Device Tools, editable scrcpy settings, **v15** display control
- [x] **M6** Home layout overhaul + Apps section (**v16** `apps.get`)
- [x] **M7** per-app windows (`--start-app`), lazy icons, remembered geometry
- [x] **M8** Desktop Mode — ships as a **second screen**; real windowing dropped (D-053)
- [x] **M9** SQLite `LincStore`, opt-in notification history (default **off**), offline outbox,
      cache-first Home + Sync
- [x] **M10** install-APK from the PC + share sheet accepts files
- [x] **M11** audio routing as an explicit per-device setting
- [x] **M12** presence polish + packaging — self-contained publish, app-local VC++ runtime, crash log,
      `tools/cleanroomsim`, publish **staleness guard**, tray icon fix, **start on sign-in** (opt-in,
      default off)
- [x] **M4a** phone Tools: remote **keyboard + trackpad** (no protocol change)
- [x] **M4b** **v17** quick controls — lock / sleep / shutdown / restart / volume / PC brightness,
      destructive actions confirmed **on the wire**
- [x] **M13** **v18** hotspot ADB link-up — **measured 514 ms, phone-AP direction, zero discovery**
- [x] **M14** black-and-white default theme + the new Linc mark on both apps + Phone section

**Owner checks outstanding:** M14's visual list (tray icon at 16 px light/dark, mirror icon, Android
launcher, Home connected/disconnected, 3-dots flyout); M4a's cursor test; M4b's quick controls
(**save your work — the destructive ones have never been executed once**); a Direct-TLS-only link
with the cable out.

**Not started:** `[ ]` **M15** live status surface in the Phone section (design open) · `[ ]` **M4c**
multimedia + slideshow remote (no protocol bump) · `[ ]` camera → PC webcam (its own milestone).

**Known, deliberate limits:** *Wireless debugging* cannot be toggled while the phone hosts a hotspot,
so hotspot ADB relies on `adb tcpip 5555` armed once over the cable; PC brightness is WMI-only, no
DDC/CI; the ephemeral-port sweep is deferred (44–175 s on-device).

## Shipped baseline (Files/07)
- [x] Pairing, discovery, reconnection; multi-transport + failover with **USB prioritized**; Direct TLS
- [x] File manager, two-way share, folder/photo sync · phone→PC + reverse mirror
- [x] Clipboard, rich notifications + inline reply, phone media + PC media
- [x] Messages + Calls lanes (sideload), quick actions, live theme sync
- [~] **Device tabs** — coded but hidden for single-device users → make always-visible (M1)
- [~] **Preemptive auto-connect** — coded but unverified in practice → verify/repair (M1/M12)
- [ ] **Bundle the companion APK** into the desktop (needed by onboarding) — not done yet

## New roadmap (planned)

### M1 — Files fix + visible tabs + verify — DONE 2026-07-22
- [x] Files page lists the phone dir (was edge-triggered load missed by the lazy VM)
- [x] Device tab strip always visible once ≥1 device paired (+ regression harness `tools/filesim`)
- [x] Verified USB priority, connection-type picker, BLE presence, preemptive connect (USB path)

### M2a — Device management works + hands-on fixes — DONE 2026-07-23
- [x] `+` (pairing now shows over a live link), close-tab, and device-switch all work
- [x] **Settings › Devices** — list, Set active (un-hides a closed tab), Forget (clears cache + unpairs)
- [x] **Files page:** navigate up to `/`; denied dirs show a plain-language note; downloads → `Downloads/Linc`
- [x] Confirmed `SyncViewModel`/`MirrorViewModel` don't share the M1 lazy-load bug (no change needed)
- Wrinkle for M2b: closing the last tab hides the strip **and** the `+` — the add-device entry must
  stay reachable at zero devices.

### M2b — Guided front door (onboarding) — DONE 2026-07-23
- [x] Stepped wizard: enable debugging → detect over ADB → inject bundled companion → done
- [x] Desktop bundles + injects the companion APK (`apkprobe`/`onboardsim` green)
- [x] Reachable from the tab-strip `+` and the zero-device first-run state
- [x] Fixed a crash (string→Visibility x:Bind) and a false-success shortcut
- Known limitation: adding a 2nd phone while the first is connected won't complete (D-037 single link)

### M2c — Onboarding as the app's own full-screen UI — DONE 2026-07-23
- [x] Full-window `OnboardingView` overlay replaces the `ContentDialog` (removed)
- [x] Styled with MaterialExpressive to match `SettingsPage`; `IsOnboarding` flag on AppShellVM
- [x] `OnboardingViewModel` step logic untouched; builds + harnesses green; owner-checked
- Cleanup owed: stray `task.md` / `walkthrough.md` / `verify-ui.ps1` left in repo root (remove next session)

### M3 — Vendor + bundle scrcpy; brand via flags (D-041 revised) — DONE 2026-07-23
- [x] `SCRCPY/Default` submodule (v3.3.4) + bundled prebuilt binaries in `SCRCPY/Custom/bin/`
- [x] `LICENSE` kept + `SCRCPY/NOTICE.md` + Linc `.ico`; binaries shipped with the desktop build
- [x] `MirrorService`/`ToolLocator` prefer bundled scrcpy; launch `--window-borderless --window-title
  "Linc"` + `SCRCPY_ICON_PATH` (interim frameless look; no source build). Owner UI check owed.
- Loose ends: stray `adb.exe.bak` in the bundle; `mirrorsim` not runnable in Antigravity's env

### M3.5 — Custom scrcpy window, iPhone-Mirroring style, from source (D-049) — *highest risk*
- [x] **Gate (M3.5a):** from-source Windows build of *unmodified* scrcpy proven
- [x] **M3.5b-1:** ship OUR from-source build standalone (own `scrcpy.exe` + 98 mingw64 DLLs); built **portable** (D-050); mirror confirmed
- [ ] **M3.5b-2a:** frameless shape — `WM_NCCALCSIZE` + `WM_NCHITTEST` drag strip + tunable rounded-region corners + DWM shadow
- [ ] **M3.5b-2b:** hover-reveal top bar (close/minimize) = drag zone; fade; bare video at rest
- [ ] `WM_NCCALCSIZE`/`WM_NCHITTEST`/`TrackMouseEvent` + SDL-drawn buttons + DWM shadow/corners — see `SCRCPY-WINDOW-SPEC.md`
- [ ] Window/event/render code only; bundle the custom build; supersedes M3's flag look

### M4 — Phone as a remote for the PC (D-042)
- [ ] `pc.control`: lock/sleep/shutdown/restart (confirmed) + volume + **PC sound & brightness**
- [ ] Phone Home control widgets
- [ ] Phone **Tools page**: keyboard, trackpad, multimedia, **slideshow/presentation remote**, telephony notifier
- [ ] **Phone camera → PC webcam** (heavier; own step — needs a Windows virtual camera)

### M5 — PC controls the phone + Device Tools + scrcpy settings (D-043)
- [ ] Desktop widgets: force landscape, auto-rotate, brightness (via companion protocol, not raw ADB)
- [ ] Device page **Tools** grouping (mirror/desktop-mode/apps/scrcpy under it)
- [ ] Editable **scrcpy settings**, persisted per device

### M6 — Home layout overhaul + Apps section
- [ ] New **Apps** section beside widgets + phone-stuff tabs
- [ ] Sections removable + re-addable (Home button), flippable left/right
- [ ] *(nice-to-have)* pop-out panel with small custom embedded top-bar (no Windows title bar)

### M7 — Apps windows (D-047)
- [ ] Load phone apps (icon+name) on first connect; cache per device profile; diff on reconnect
- [ ] Click app → its own Custom-scrcpy window at default ratio; teardown on close

### M8 — Desktop Mode (D-046, see DESKTOP-MODE-SPEC.md)
- [ ] Desktop Mode widget → `scrcpy --new-display` tuned (`WxH/DPI`, `--mouse/--keyboard=uhid`)
- [ ] First-run ADB setup (flags + reboot handling, idempotent)
- [ ] Settings panel (resolution/DPI, aspect mode=match-monitor default, freeform/resizable, auto-fullscreen via windowingMode)
- [ ] Per-device auto-tune (wm size/density) + "reset to recommended"

### M9 — SQLite + notification history + offline queue (D-044, D-045)
- [ ] `LincStore` (SQLite); incremental migration from settings.json; update devicesim
- [ ] **Notification history** days/months, opt-in (default off), retention + clear
- [ ] **Offline action/share queue** flushes on reconnect; cache-first Sync page

### M10 — Sharing & install
- [ ] Android share-sheet **file** target (`*/*`, multiple)
- [ ] **Install-APK widget (PC → phone)** via adb, guided
- [ ] Move Share into phone Home; remove Share tab

### M11 — Routing (D-048)
- [ ] Audio channel (4): phone audio → PC speaker (Custom scrcpy)
- [ ] Phone as speaker/mic for the PC (staged)

### M12 — Presence polish + packaging/beta
- [ ] Preemptive mutual start; optional PC-side BLE advert
- [ ] Installer/updater; Play-ready build; crash reporting + feedback

## Deferred (unchanged)
- [ ] Google account + FCM push-wake (D-007); mirror phase 2 (D-015); call audio via BT HFP (D-017);
  MediaProjection fallback mirror; macOS/Linux ports
