# Roadmap

> **Fresh start (2026-07-22).** The historical milestone numbering (Era 1 `M0–M19`, Era 2
> `M00–M07`) is complete and archived — the settled record is `Documentation/02-History.md`, the raw old
> planning docs are in `v2.docs.old/`. This is a **new milestone series (M1…)**; decision IDs
> (`D-041…`) continue in `gemini-docs/DECISIONS.md`. **Revised 2026-07-22 after owner corrections** (see
> `REQUIREMENTS-ANALYSIS.md` › "Owner corrections").

A milestone is "done" when its features work reliably, are covered by tests/harnesses where
practical, and the docs reflect reality. Standing testing policy is unchanged (**D-031**):
self-verify everything automatable and hand the owner only physically-unautomatable checks. Every
new message type bumps the protocol version in `PROTOCOL.md` first.

---

## 📍 POSITION AS OF 2026-08-24 — read this first; it supersedes the 08-12 block below

- **🔴 The commits are NOT on `main`.** HEAD is **`d64a1b7`** *(M14)* on branch **`m13-hotspot-links`**;
  **`main` is three commits behind at `e23fbc3`**. `536ac28`, `01d7504` and `d64a1b7` — all of M13 and
  M14 — are unmerged. The 08-12 block below says "committed" without saying where. Verified by reading
  `.git/HEAD`, `.git/refs/heads/*` and `.git/logs/HEAD`.
- **🔴 M15's first session was lost and left nothing on disk.** `tasks/M15.md` was handed over on
  2026-08-12; there is no `reports/M15.md`, and no file under `Services/`, `Views/` or `ViewModels/` has
  an mtime later than 2026-08-12 08:02 UTC (which is M14's own work).
- **M15 is re-issued and split: `tasks/M15a.md` → `tasks/M15b.md`, one paste, chained** — each writes its
  own report before the next begins. `M15.md` stays on disk untouched.
- **M15 is NO LONGER the "dynamic island."** The owner's bug reports displaced it on 2026-08-12. M15 is
  now six defects: adb calls that never name their device (so a second phone cannot be added), the
  USB-disconnect delay (**to be measured**, not patched on top of M13f), a manual transport override,
  the several disconnected/error strings, the blurred wallpaper, and Apps-off leaving a gap where
  Notifications should expand. **The live status surface / island moves to a later milestone** — M14
  deliberately left the Phone card as one composable seam for it.
- **Wallpaper, corrected:** the image behind the widgets is the phone's **pre-blurred thumbnail**
  (`ThemeSyncService.cs:22-23`, `HomeViewModel.cs:1624`), fetched via
  `FetchBulkAsync("wallpaper", id, …)` (`ThemeSyncService.cs:158`) and produced by
  `WallpaperProvider.kt`. **Unblurring is a two-sided change**, without a protocol bump.
- **Active coding agent from 2026-08-24: Claude Code 2** (rules `AGENTS.md` + `claude-code-2/GUIDE.md` +
  `GUARDRAILS.md`; live docs unchanged, D-056).

---

## 📍 POSITION AS OF 2026-08-12 — read this before the milestone text below

**Protocol is shipped v18 on both sides.** The narrative further down was written on 2026-07-22 and
describes v14; **where it disagrees with this block, this block wins.**

**DONE: M1, M2, M3, M3.5, M5, M6, M7, M8, M9, M10, M11, M12, M4, M13, M14.** Everything in the
Convergence series has landed. Commits: `76c754b` (M12d–h), `e23fbc3` (M4a/M4b/M12i), `536ac28` +
`01d7504` (M13a–e), plus M13f and M14.

**Milestones that were split after this file was written:**
- **M12** → M12a–c (packaging) · **M12d** scrcpy rename · **M12e** tray + start-on-sign-in ·
  **M12f** clean-room harness + `PRI249` · **M12g** `--data-root`, completing D-057 · **M12h** startup
  toggle diagnosis (no defect) · **M12i** publish staleness guard + `canSleep`.
- **M4** → **M4a** keyboard + trackpad (**no protocol change** — `pc.input` already covered it) ·
  **M4b** protocol **v17** quick controls · **M4c** multimedia + slideshow (**not started**) ·
  camera→PC-webcam **deferred to its own milestone**, it needs a Windows virtual-camera device.
- **M13** → **M13a** role rule + address resolution · **M13b** protocol **v18** announce path ·
  **M13c** self-arming + race + health loop · **M13d/M13e** planner rulings and the promotion redial ·
  **M13f** the defects the live test exposed.
- **M14** (new) — black-and-white default theme + the new Linc mark on both apps + the Phone section.

**M13's outcome, measured on hardware 2026-08-11:**
- ✅ **Phone hosts, PC joins: WORKS. 514 ms end to end**, no discovery of any kind — `DecideRole` picked
  CLIENT, dialled the gateway, `adb connect` → `get-state = device`.
- ✅ PC hosts, phone joins: works, owner-confirmed, **not snappy** — worth a look if it annoys.
- 🔴 ***Wireless debugging* is greyed out while the phone hosts a hotspot** ("wifi disconnected"),
  exactly as this file predicted in July. **Self-arming via `adb_wifi_enabled` is dead in that role.**
  The shipping answer is `adb tcpip 5555` armed once over the cable, which survives until reboot.
  **Do not spend another session trying to beat this.**

**WHAT IS ACTUALLY LEFT:**
1. **Owner visual checks from M14** — tray icon at 16 px on light *and* dark taskbars, mirror window
   icon, Android launcher, Home in both states, the 3-dots flyout.
2. **M15 — the live status surface ("dynamic island") in the Phone section.** Design is **open**; M14
   deliberately left the status area as one composable element with its own view-model state so it can
   drop in. **Needs the owner's answer on shape before it can be specced.**
3. **M4c** — multimedia transport keys + slideshow remote. Reuses `pc.media.control` and `pc.input`,
   **no protocol bump**. Carries the `syncsim` poll fix as Part 0.
4. **Camera → PC webcam** — its own milestone, heavier than it looks (D-042).
5. **`Linc/Documentation/` is 20 days and 13 milestones stale** — see the banner in that folder.

---

**Theme — Convergence.** The phone and PC become one control surface: a proper guided front door,
each device driving the other, phone apps opening as windows on the PC, and the link + its memory
solid enough to feel effortless.

---

## Baseline & important correction

Shipped and working: pairing, discovery, reconnection, three transports with failover (**USB
prioritized**), Direct TLS, file manager, two-way mirror, clipboard, rich notifications + media,
the Sync page, offline device memory, and BLE presence (`Documentation/07-Feature-Catalog.md`).

**But two "implemented" things the owner has never actually seen and wants brought in:**
- **Device tabs** exist but the strip is coded to **hide itself when only one phone is paired** —
  so a single-phone user never sees it. It must be **always visible once a device is paired**.
- **Preemptive auto-connect / standing presence** is coded but unverified in practice — confirm it
  actually connects on its own and surfaces status, or repair it.

Also note: the desktop does **not yet bundle the companion APK** (dev builds resolve it from the
repo). The onboarding milestone (M2) requires bundling it — "the desktop carries the companion and
injects it."

---

## M1 — Files page fix, visible device tabs, verify the rest — DONE 2026-07-22
*(Files page loads again; tab strip always visible; `tools/filesim` added; USB/picker/BLE/preemptive verified. Hands-on testing then found the tab actions don't work and some Files polish — folded into M2a below.)*
- **Fix the desktop Files page** — it should list the connected phone's directory and doesn't.
  Diagnose first (active-device wiring after the M03 tabs refactor, `HasAdb`/router path,
  no-refresh on the `Connected` fan-out, or a swallowed load exception), then fix minimally, and
  **add a `FileService`/router regression** to `tools/`.
- **Bring in the device tabs:** make the top tab strip **always visible once ≥1 device is paired**
  (today it hides for single-device users), with the "+" add button present. Confirm it renders and
  switches.
- **Verify** USB priority, the connection-type picker, and BLE presence actually work and are
  visible; note any gaps. Sanity-check preemptive auto-connect (full repair can wait for M12 if it's
  more than a small fix).
- No protocol change.

## M2 — The guided front door: onboarding + bundled companion + device management
> **Delivered in two sessions.** **M2a — DONE 2026-07-23:** device management works (`+` shows
> pairing over a live link, close-tab, device-switch), **Settings › Devices** (list / Set active /
> Forget), Files-page navigate-to-`/` + graceful denied-dir note + downloads to `Downloads/Linc`;
> confirmed Sync/Mirror VMs don't share the M1 lazy-load bug. **M2b — DONE 2026-07-23:** the stepped
> onboarding wizard (`OnboardingDialog`/`OnboardingViewModel`) + the desktop bundling/injecting the
> companion APK, reachable from `+` and the zero-device state; fixed a crash (string→Visibility
> x:Bind) and a false-success shortcut. **M2c — DONE 2026-07-23 (first Antigravity session):** the
> wizard is now the **app's own full-window UI** — a `MaterialExpressive`-styled `OnboardingView`
> overlay replaced the `ContentDialog` (removed), shown via an `IsOnboarding` flag; VM step logic
> untouched; owner-checked. Known limitation: adding a 2nd phone while the first is connected won't
> complete (D-037 single active link). **M2 is complete.**

The first-run flow the owner specified, end to end:
- First launch → **Home** detects no paired device and directs the user to the Device page.
- Device page prompts **"click + to add a device"** → a guided flow walks the user through
  developer options + **USB and wireless ADB** with clear steps.
- On "done," **detect the phone over ADB** and confirm ("connection set").
- **Inject the bundled companion APK** (so the desktop must now **bundle the APK**), which
  **auto-starts the companion service** with no phone taps (M01 machinery already does the install
  + grants + `am start-foreground-service`).
- Detect the companion, connect, and **save the device permanently**: stats stored, appears on the
  **top tab**, and its notifications/messages/sync **cached under its device profile**.
- **"+"** repeats for another device; **Settings › Devices** lists/forgets/manages paired devices.
- Verify: a from-scratch pair on the Pixel 7 with zero phone taps beyond the ADB toggles + QR; a
  second device adds as a new tab; forgetting a device clears its profile.

## M3 — Vendor + bundle scrcpy; brand the window via flags — D-041 (revised) — DONE 2026-07-23
- Vendor upstream **source** as a git submodule at **`SCRCPY/Default`** (pristine, upstream-trackable;
  keep `LICENSE`). Bundle the **official prebuilt Windows binaries** (matched `scrcpy.exe` +
  `scrcpy-server` + DLLs + adb) under **`SCRCPY/Custom/bin/`** and ship them with the desktop
  (satisfies D-023 "install nothing"). Add `SCRCPY/NOTICE.md` + a Linc `.ico`.
- **Brand at runtime, not from source:** `MirrorService`/`ToolLocator` prefer the bundled scrcpy and
  launch it with `--window-borderless --window-title "Linc"` + `SCRCPY_ICON_PATH`. **No scrcpy source
  is edited or built.** This is the **interim frameless look**, superseded by M3.5.
- Verify: bundled `scrcpy.exe --version` correct; `mirrorsim` green; submodule tracks upstream.
  Owner check: mirror opens frameless + Linc-branded.
- **Foundation for M3.5, M7 (Apps windows), M8 (Desktop Mode).**

## M3.5 — Custom scrcpy window (iPhone-Mirroring style, from source) — D-049 *(highest risk)*
- Redesign the mirror window to match Apple's **iPhone Mirroring**: bare video at rest, a
  **hover-reveal top bar** (close/minimize only) that is the **drag zone** except on the buttons,
  subtle **rounded corners**, and a **drop shadow**. Full spec + mechanism: **`SCRCPY-WINDOW-SPEC.md`**.
- **Requires the from-source scrcpy build on Windows.** **Step 1 (hard gate):** stand up the build
  and compile **unmodified** scrcpy from `SCRCPY/Default`, confirm it runs — if the environment can't
  be stood up cleanly, **STOP**. Only then apply the window edits (`WM_NCCALCSIZE`/`WM_NCHITTEST`,
  `TrackMouseEvent`, SDL-drawn buttons, `DwmExtendFrameIntoClientArea`, `DWMWA_WINDOW_CORNER_PREFERENCE`)
  in the isolated `SCRCPY/Custom` copy, and bundle the custom build in place of M3's prebuilt.
- Scope: only window-creation/event/render code — **never** capture/input/protocol. Supersedes M3's
  interim flag look. Most-trusted agent; small, checkpointed steps.

> **M3.5 CLOSED 2026-07-27 (D-052).** Frameless + rounded + draggable + resizable + maximizable all
> shipped and owner-verified. The hover **control bar is deferred to the Polish backlog** (b-2c-3
> whole-strip reveal) — it never became reachable because only the 30-physical-px button cluster
> triggers the reveal while the rest of the strip is `HTCAPTION` (no SDL mouse events). Window ops
> stay reachable via Aero-snap / Win+↑ / double-click caption / Alt+F4.

> **ORDER CHANGE 2026-07-27 (D-052): M8 (Desktop Mode) takes M4's slot and is worked NEXT; M4 moves
> into M8's old slot.** M8 is unblocked (needs only M3's Custom scrcpy); M4 needs a protocol bump, so
> deferring it keeps the wire format frozen. The two are independent — no dependency risk.

## M4 — Phone as a remote for the PC (the Tools suite) — D-042 *(moved later — see D-052)*
A phone-side **Tools** surface + Home widgets that drive the PC (reuses `pc.input`; a new
`pc.control` message, protocol bump; Windows injection needs no shell UID, so it works over any
transport):
- **Quick controls:** lock, sleep, shutdown, restart (destructive ones **confirmed**), volume
  up/down/mute; **PC sound + brightness** control.
- **Tools page:** remote **keyboard**, **trackpad**, **multimedia controls**, **slideshow /
  presentation remote** (next/prev), and a **telephony notifier** (surface incoming calls as a PC
  tool — overlaps existing `call.incoming`).
- **Phone camera → PC webcam** — included but flagged as a **heavier sub-feature** (needs a Windows
  virtual-camera device); implement as its own step, don't assume it's trivial.
- Verify: each control fires on hardware (human check for destructive + webcam feel).

## M5 — PC controls the phone + Device-page Tools + scrcpy settings — D-043 — **IN PROGRESS**
Split into three gated sub-sessions, ordered so the wire format is touched last:
- **M5a ✅ (2026-07-29)** — **Device page "Tools" grouping** + the scroll host. `DevicePage.xaml`
  wrapped in a `ScrollViewer` (the page had none, so the expanded Desktop Mode settings were clipped
  and unreachable); Screen-mirroring + Desktop-Mode blocks moved into one "Tools" card. No protocol,
  no behaviour change. Apps / scrcpy-settings entries join the same card as they land.
- **M5b ✅ (2026-07-29)** — **Editable scrcpy settings** for the normal mirror (max size, bit rate,
  max FPS, crop, stay-awake / turn-screen-off / show-touches), persisted per device on
  `KnownDevice.Mirror`, edited from a "Mirror settings" expander in the Tools card. **D-054:** the
  preset combo became a shortcut that writes into that record, which is the single source of truth.
  M5a's duplicate labels cleaned up. Two planner-found defects (broken `devicesim`/`blescan` csprojs,
  the self-clearing save confirmation) are fixed at the head of M5c.
- **M5c** — **PC drives the phone's display:** force landscape / auto-rotate / brightness, via
  **companion protocol messages** the phone applies through `Settings.System` (**D-001, D-055**; no raw
  ADB). This is the **v14 → v15 bump** — `PROTOCOL.md` v15 and D-055 were written by the planner
  **before** any code, per the standing rule. Split at a hard STOP because it is the first wire change
  in this phase and it touches both apps:
  - **M5c-1 (next)** — **phone side only.** `Protocol.kt` types, `SocketServer` handlers gated on
    negotiated ≥ 15, a new `DisplayControl.kt` doing the `Settings.System` writes + 0–100 brightness
    scaling, the `WRITE_SETTINGS` appop check answering `error` `not-granted`, the three new `status`
    fields, and Android unit tests. **No desktop UI** — nothing sends these yet. Also carries the two
    M5b defect fixes.
  - **M5c-2** — **desktop side.** Send the messages, the widgets in the Device-page Tools card,
    surface the `not-granted` refusal in plain language, disable the widgets when negotiated < 15,
    and a harness.

## M6 — Home layout overhaul + the Apps section *(some parts nice-to-have)* — **IN PROGRESS**
Split into gated sub-sessions; the owner's stated priority is remove/re-add and flip, with pop-out
only "if it isn't a headache", so pop-out is deliberately last and droppable.
- **M6a (in flight)** — the six widget cards become **removable and restorable**, persisted per device
  on `KnownDevice.Home` (`HomeLayout` record, hidden-by-absence so a legacy file shows everything).
  UI is a single **"Sections" flyout** with six toggles — chosen over per-card ✕ buttons to keep churn
  out of a 651-line XAML file. Task file: `Vibe/agent/tasks/M6a.md`.
- **M6b** — **flip left/right:** swap the widgets pane and the tabbed panel between grid columns 0 and
  2. The `HomeLayout.PanesSwapped` field is already persisted by M6a, unused, so this needs no second
  `KnownDevice` migration.
- **M6c (next, Claude Code)** — the **Apps section**, end to end. **Protocol v16 (D-058):**
  `apps.get` → `apps` returns the phone's **launchable** packages; icons reuse the **existing** bulk
  `appIcon` kind from v5, so no new icon machinery. Desktop caches per device under
  `<root>/cache/<serial>/`, **rooted at the same injected path `DeviceRegistry` uses** so harnesses
  can't reach the real cache (extends D-057). Apps becomes a seventh Home section, visible by default.
  **Clicking an app does nothing yet** — that's M7. Task file: `Vibe/agent/tasks/M6c.md`.
- **M6d — pop-out panels: DEFERRED to the Polish backlog (planner's call, 2026-07-31).** The owner
  flagged it as "only if it isn't a headache" and it is: a floating panel with a small custom top-bar
  and no Windows chrome is **the same mechanism M7 must build properly** for per-app scrcpy windows.
  Building it first risks a throwaway. Revisit after M7, when the window machinery exists and pop-out
  becomes a re-use rather than a new capability.

- **M6c-3 (owner correction #2, after using M6c-2)** — "scroll down to reach Apps" is rejected. The
  right column becomes **tabs panel → draggable horizontal splitter → Apps**, both always visible,
  each scrolling internally, split persisted per device on **`HomeLayout`** (not `KnownDevice` — a
  `HomeLayout` field costs no csproj changes). This **deletes** M6c-2's `TabsPane` height-sync, which
  a bounded grid row now handles structurally. Task file: `Vibe/agent/tasks/M6c-3.md`. **Run before M7a.**
- **M6c-2 (owner correction #1)** — M6c put Apps in the **widgets** pane; the owner wanted it
  as **its own section in the right column, directly below the notifications/messages/calls panel**,
  rendered as an **icon + name grid** (`ItemsRepeater` + `UniformGridLayout`), growing to fit with the
  column scrolling. **The risk is `TabsPane`:** its `ListView`s scroll internally only because their
  height is bounded, so the new column scroller must pin `TabsPane` to the viewport height rather than
  letting it be measured unbounded. Task file: `Vibe/agent/tasks/M6c-2.md`.

**M6 is otherwise COMPLETE (a ✅ b ✅ c ✅).** Then **M7, in exactly two stages:**
- **M7a — the launch path, delivered as a working feature.** Click an app tile → that app opens in its
  own PC window. **`scrcpy --new-display --start-app=+<package>` (D-059)** — our vendored scrcpy 3.3.4
  supports `--start-app` natively, so **the old `am start --display <id> -n <pkg>/<activity>` plan is
  superseded**: no ADB shell in the launch path (better for D-001 and for Direct-TLS-only links), no
  activity resolution, and no display ids to leak — scrcpy owns the display's lifetime. One process
  per app keyed by package; relaunch focuses rather than duplicating; disconnect closes every window.
  **This is where the per-app windowing goal D-053 took off M8 finally lands.**
  Task file: `Vibe/agent/tasks/M7a.md`.
- **M7b — harden it.** Virtualize the grid, make icon fetching genuinely lazy (the current
  `ItemsRepeater` realizes every tile — see the BRAIN note), decide what per-app window state is worth
  persisting, and multi-window management.

- Add a new **Apps** section beside the widgets and the phone-stuff tabs (calls/notifications/etc).
- Sections become **removable** with a button on Home to **add them back**; **flippable
  left/right**; and **optionally pop-out** into a floating panel with a **small custom embedded
  top-bar** (close / return-to-Home — **no Windows title bar**). Per the owner: **do the pop-out
  only if it isn't a headache**; the remove/re-add/flip is the priority.
- Verify: sections hide/restore and swap sides without state bleed; layout persists per device.

## M7 — Apps windows: phone apps as PC windows — D-047
- On first connect, the desktop **loads the phone's installed apps** (icon + name), lists them in
  the **Apps** section, **caches them under the device profile**, and on later connects only
  **checks for new/removed** ones.
- Clicking an app opens it in **its own Custom-scrcpy window at a default ratio** (per-app
  `--new-display` + `am start --display <id> -n <pkg>/<activity>`); closing tears the display down.
- Verify: the Apps list populates instantly from cache and reconciles in the background; two apps
  open as independent windows.

## M8 — Desktop Mode — D-046 — **DONE 2026-07-28, as a SECOND SCREEN only (D-053)**
> Built and owner-accepted. The **freeform/windowing half below was DROPPED (D-053)** — the platform
> doesn't declare `android.software.freeform_window_management`, so apps run fullscreen on the
> virtual display and no settings key changes that. Real per-app windowing is now **M7's** job.
> The rest (launch path, first-run ADB setup + reboot handling, per-device settings panel,
> auto-tuned geometry, `tools/desktopsim`) shipped. Original scope kept below for the record:

Implement per **`gemini-docs/DESKTOP-MODE-SPEC.md`** (the owner's detailed spec):
- A **Desktop Mode widget/toggle** launches the phone's native desktop shell via
  `scrcpy --new-display`, tuned: explicit `--new-display=WxH/DPI`, `--mouse=uhid --keyboard=uhid`,
  and the one-time ADB flags (`force_resizable_activities`, `enable_freeform_support`,
  `force_desktop_mode_on_external_displays`, `desktop_mode`) with **reboot handling**.
- A **first-run setup screen** (idempotent) and a **settings panel** (resolution/DPI, aspect-ratio
  mode — "full size" = **match the monitor**, aspect = match-phone/custom, freeform/maximized,
  resizable, default window state, **auto-fullscreen apps via `am start --windowingMode`**).
- **Auto-tune per device** from `wm size` / `wm density`, with a "reset to recommended" button.
- Uses M3's Custom scrcpy. Verify is mostly human (real desktop-mode session).

## M9 — SQLite data layer + notification history + offline queue — D-044, D-045 — **IN PROGRESS**
Split four ways so the schema lands with zero regression risk before anything depends on it:
- **M9a (in flight, opencode)** — **`LincStore` foundation only.** SQLite file at
  `<root>/linc.db` on the injected `DeviceRegistry.RootPath` (D-057/D-058), all five table shapes
  created up front so later sessions never migrate, a **versioned** `schema_version` path from day one,
  idempotent import of `KnownDevice`s, corrupt-file tolerance. **`settings.json` stays authoritative;
  nothing reads the store yet** — that is what makes this session risk-free. Task: `tasks/M9a.md`.
- **M9b** — notification history: opt-in, **default off** (D-045's scoped reversal of D-032), retention
  setting, clear action, icon/art blobs stored by hash to bound growth.
- **M9c** — the offline action/share queue: queue while disconnected, **flush on reconnect, idempotent
  and ordered**.
- **M9d** — cache-first Sync page: SMS conversations + call log persisted per device so the page is not
  blank offline. Extend `devicesim`.

_Original scope, for reference:_
- Introduce **`LincStore` (SQLite)**: device profiles, per-device offline cache, and the tables the
  rest needs. **Migrate incrementally** from `settings.json` (import on first run).
- **Notification history** kept **days/months** — opt-in (**default off**, a scoped reversal of
  D-032), with a retention setting + clear action.
- **Offline action/share queue:** share (or SMS send, or push) while disconnected and **flush on
  reconnect** (idempotent, ordered).
- **Cache-first Sync page:** persist SMS conversations + call log per device so it isn't blank
  offline. Update `devicesim`.

## M10 — Sharing & install
- **Android share-sheet file target** (`*/*`, multiple) → reuses `share.item kind:file`.
- **Install-APK widget (PC → phone):** pick an APK on the desktop → guided → `adb install` via
  `PhoneSetupService`'s primitive, plain-language progress/errors.
- **Move Share into the phone Home** (a scrollable share section) and **remove the Share bottom-nav
  tab**.

## M11 — Routing (audio) — D-048
- **PC as speaker for the phone:** the **audio channel (4)** via the Custom scrcpy's audio capture
  → play on the PC (ADB-bound).
- **Phone as speaker/mic for the PC:** the harder direction, staged after the above. (Phone camera
  → PC webcam lives in M4 unless deferred here.)

## M12 — Presence polish + packaging/beta
- **Preemptive mutual start:** opening either app fires an immediate connect; optionally the **PC
  also advertises a BLE beacon** so the phone can scan for the PC. (Finish any repair deferred from
  M1.)
- **Packaging:** desktop **installer/updater**, a **Play-ready Android build** (SMS/call lanes
  compiled out, D-016), **crash reporting + opt-in telemetry + a feedback channel**. (Companion-APK
  bundling already landed in M2.)

## M13 — Hotspot links: connect with no shared network, in either direction

**Goal: two devices and no Wi-Fi network between them should still be a working Linc link.** Today
every wireless path assumes the phone and the PC already sit on the same LAN. When they don't —
travelling, a hotel, a locked-down office network, a router that isolates clients — the only fallback
is the USB cable. This milestone makes the two devices form their own network.

**Two directions, both in scope:**
- **Phone hotspots the PC** (the common case). The PC joins the phone's hotspot, landing on the
  phone's subnet with the phone as gateway (typically `192.168.43.1` / `192.168.x.1`).
- **PC hotspots the phone.** Windows Mobile Hotspot (`NetworkOperatorTetheringManager`, WinRT) puts
  the phone on the PC's subnet instead. Useful when the phone needs to keep its own Wi-Fi for
  internet, or when the phone's hotspot blocks the debugging path below.

**What is expected to break, and is the actual work:**
- **Wireless ADB is the likely casualty.** Android's *Wireless debugging* toggle generally expects
  the phone to be a Wi-Fi **client**; while its radio is serving a hotspot the toggle is often
  unavailable. If confirmed, the answer is not to fight it — **prefer Direct TLS on a hotspot link**
  and let the transport ranking (D-022, USB > wireless ADB > Direct TLS) degrade to it cleanly.
- **mDNS discovery over the hotspot interface is the second question mark.** Android's hotspot is
  inconsistent about forwarding multicast. Fallbacks, in order of preference: dial the **gateway
  address** directly (the phone's hotspot IP is deterministic and the PC already knows its own
  default gateway); reuse the existing **candidate-endpoint list** the phone already keeps for
  channel dial-back; and only then consider a manual address entry.
- **Reachability is genuinely easier here than on a normal LAN** — no AP client isolation, no router
  firewall between the two, and the gateway address is knowable without discovery. The hard part is
  the debugging transport and discovery, not routing.

**Do this first (a diagnostic session, before any design is committed):** on real hardware, bring up
the phone's hotspot, join the PC to it, and record — is *Wireless debugging* still togglable? what
does `adb devices` report? does the desktop's mDNS discovery see `_linc._tcp`? does Direct TLS
connect? Then repeat with Windows Mobile Hotspot in the other direction. **Write the findings into
`BRAIN.md` and a decision before writing any feature code** — this milestone is mostly unknowns, and
guessing at them is how M8 lost two sessions (D-053).

**Verify:** with the PC's Wi-Fi disconnected from every normal network and the USB cable unplugged,
Linc connects, browses files, and mirrors — in both hotspot directions.

**Cost note:** a phone hotspot may carry the phone's mobile data. Linc's own traffic stays local (it
never leaves the subnet), but **say so in the UI** — users will reasonably assume mirroring over a
hotspot is burning their data plan, and it isn't.

---

## Dependencies at a glance

```
M1 (Files fix + visible tabs) ── first, independent
M2 (onboarding + bundled companion) ── the front door; needs APK bundling
M3 (vendor scrcpy, Default/Custom) ── foundation ─┬─► M7 (Apps windows)
                                                  ├─► M8 (Desktop Mode)
                                                  └─► M11 (audio routing) + M5 (scrcpy settings)
M4 (phone→PC Tools) ── independent (reuses pc.input); webcam is its own step
M6 (Home layout) ──► M7 (Apps section hosts the Apps windows)
M9 (SQLite) ── underpins notif history + offline queue + app-cache
M12 (presence + packaging) ── last
M13 (hotspot links) ── independent; rides D-022's Direct TLS + the transport ranking.
                       Schedule its diagnostic session any time; the feature work after M12.
```

## Deferred / unscheduled
- Google account integration + FCM push-wake (D-007); mirror phase 2 (`linc-agent.dex`, D-015);
  call audio via BT HFP (D-017); MediaProjection fallback mirror; macOS/Linux ports —
  `Files/07` "Future ideas."
