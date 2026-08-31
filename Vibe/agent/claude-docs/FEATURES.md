# Features

Checklist for the current phase. Full shipped catalog: `Documentation/07-Feature-Catalog.md`. This tracks
the roadmap (`ROADMAP.md`) + analysis (`REQUIREMENTS-ANALYSIS.md`, incl. the owner corrections).

Legend: `[x]` done · `[~]` in code but not visible/verified to the owner · `[ ]` planned · `[bug]`
broken, to fix.

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

### M2b — Guided front door (onboarding) *(session after)*
- [ ] First-run: Home → "no device" → Device page → "click +" → guided dev-options/USB+wireless ADB
- [ ] Detect over ADB → **inject the bundled companion APK** → auto-start service → connect
- [ ] Save device permanently: top tab, stats stored, notifications/messages/sync cached per profile
- [ ] "+" runs the guided flow; **Settings › Devices** management (built in M2a)
- [ ] Bundle the companion APK into the desktop

### M3 — Vendor scrcpy (D-041)
- [ ] `SCRCPY/Default` (pristine upstream, don't corrupt) + `SCRCPY/Custom` (Linc logo, custom top bar, name)
- [ ] `LICENSE` + `NOTICE.md`; build & bundle **Custom** as Linc's scrcpy (replaces PATH/winget)

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
