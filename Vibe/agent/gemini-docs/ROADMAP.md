# Roadmap

> **Fresh start (2026-07-22).** The historical milestone numbering (Era 1 `M0–M19`, Era 2
> `M00–M07`) is complete and archived — the settled record is `Documentation/02-History.md`, the raw old
> planning docs are in `v2.docs.old/`. This is a **new milestone series (M1…)**; decision IDs
> (`D-041…`) continue in `gemini-docs/DECISIONS.md`. **Revised 2026-07-22 after owner corrections** (see
> `REQUIREMENTS-ANALYSIS.md` › "Owner corrections").

A milestone is "done" when its features work reliably, are covered by tests/harnesses where
practical, and the docs reflect reality. Standing testing policy is unchanged (**D-031**):
self-verify everything automatable and hand the owner only physically-unautomatable checks. Every
new message type bumps the protocol version (shipped **v14**) in `PROTOCOL.md` first.

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

## M5 — PC controls the phone + Device-page Tools + scrcpy settings — D-043
- **Desktop widgets:** force landscape, auto-rotate, brightness — via **companion protocol
  messages** the phone applies through `Settings.System` (honors D-001; no raw ADB in the UI).
- **Device page "Tools" grouping:** move mirror / desktop-mode / apps / scrcpy-settings under it.
- **Editable scrcpy settings** (quality/bitrate/resolution/crop/flags), persisted per device.

## M6 — Home layout overhaul + the Apps section *(some parts nice-to-have)*
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

## M8 — Desktop Mode — D-046 — **NEXT (promoted into M4's slot, D-052)**
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

## M9 — SQLite data layer + notification history + offline queue — D-044, D-045
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
```

## Deferred / unscheduled
- Google account integration + FCM push-wake (D-007); mirror phase 2 (`linc-agent.dex`, D-015);
  call audio via BT HFP (D-017); MediaProjection fallback mirror; macOS/Linux ports —
  `Files/07` "Future ideas."
