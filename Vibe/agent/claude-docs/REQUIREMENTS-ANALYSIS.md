# Requirements Analysis — Next Phase

Analysis of the new requirement set against the shipped codebase (protocol **v14**, Era 1 + Era 2
complete through M05, M07 polish partway). For each requirement: **status** (Implemented / Polish
/ New / Bug), the **existing pieces to reuse**, and the **recommended approach bent to the current
architecture**. This document feeds `ROADMAP.md`; the milestone sequencing lives there.

Legend — **Status**: ✅ Implemented · 🔧 Polish (mostly there) · 🆕 New · 🐞 Bug.

---

## Group 1 — scrcpy foundation (unblocks everything screen-related)

### 1.1 Vendor scrcpy as an upstream-trackable fork + strip window chrome 🆕 *(detailed req 1)*
**Today:** the desktop mirror is scrcpy.exe run as a **child process**, located via `ToolLocator`
from a bundled dir / PATH / winget (`MirrorService.cs`). There is no vendored copy; D-023 already
committed to "bundle scrcpy's binaries at Polish," so this requirement makes that concrete *and*
adds a UI customization.
**Approach:**
- Add `Genymobile/scrcpy` (Apache-2.0) as a **git submodule** at `Linc/SCRCPY` pointing at the
  official repo, so `git fetch/merge` from upstream stays possible. If the submodule fights the
  build, fall back to a **fork** (our GitHub repo cloned from theirs, `git remote add upstream`).
  **Avoid a flat unzip/copy** — it kills future syncing.
- Keep the original `LICENSE`; add `NOTICE.md` stating this is a modified scrcpy build by
  Genymobile, changes = **window chrome only, engine untouched**.
- Touch **only the SDL window-presentation layer** (`app/src/screen.c` and the SDL window
  flag/title calls — confirm exact files against the pulled version). Remove the title text and
  icon; make the title bar blend away via `SDL_WINDOW_BORDERLESS` (weigh the drag/resize
  trade-off; if it hurts usability, use a transparent/matching-color bar instead). **Do not touch
  capture/encode/transport/input.**
- Keep the UI change as an isolated patch/diff so upstream merges rarely conflict.
- Build the modified client as Linc's bundled scrcpy, replacing the winget/PATH lookup.
**Bend to existing:** this is the D-023 "engulf scrcpy" work; `MirrorService` keeps launching a
child process, it just launches **our** bundled, chromeless build. New decision needed
(vendoring + patch strategy). **Foundational** — Desktop Mode (1.2), Apps windows (1.3), scrcpy
settings editing (5.3), and audio routing (4.x) all build on it.
**Risk:** building scrcpy's SDL client on Windows (meson/ninja, SDL2/FFmpeg deps) is real work;
budget for it. The server jar can stay the upstream prebuilt.

### 1.2 Android Desktop Mode → capture → stream → input back 🆕 *(the reborn "Extend")*
This is the capability M06 Extend was meant to give, achieved the **right** way: not a PC-side
IddCx virtual monitor, but **Android's own desktop windowing**, captured and streamed with the
scrcpy pipeline we already own.
**Approach:**
- Toggle native desktop mode via ADB settings (version-dependent: `settings put global
  force_desktop_mode_on_external_displays 1` on older builds; Android 15/16 desktop windowing on
  newer). The cleanest path is **scrcpy `--new-display`** (scrcpy 2.5+/3.x), which spins up a
  **virtual display** that Android renders in freeform/desktop mode — often without touching the
  global setting. Confirm flag support against the vendored scrcpy version (1.1).
- Stream that display with the **existing scrcpy pipeline** (mirror channel 1) into a desktop
  window; input flows back through scrcpy exactly as normal mirroring does.
- Toggle off tears down the virtual display and reverts the phone.
**Bend to existing:** reuses `MirrorService` + the vendored scrcpy; the "toggle" is a Device-page
control (fits 5.2's "Tools" grouping). Add capability detection + graceful fallback per Android
version. **Depends on 1.1.**
**Risk:** virtual-display + desktop-mode support varies by OEM/Android version; some apps refuse
virtual displays; secure surfaces (DRM) won't capture.

### 1.3 "Apps" widget — per-app windows 🆕 *(detailed Feature spec)*
A Home-page **Apps** section (device-profile area, beside the widgets) listing the phone's
installed apps; clicking one opens it as **its own live, resizable PC window** via a per-app
virtual display.
**Approach:**
- **First connect:** pull the app list (`pm list packages` + `PackageManager` labels/icons via a
  small companion query, icons over the **bulk channel**), cache under the device profile keyed by
  device id. **Later connects: diff only** (new/removed packages) so the widget is instant from
  cache.
- Click → new virtual display (scrcpy `--new-display`) + `am start --display <id> -n
  <pkg>/<activity>` → stream that display over the existing pipeline as a **standalone window**;
  closing the window tears down the display; the cached list persists.
**Bend to existing:** same transport/capture as 1.2, scoped to one app per virtual display; the
app-list cache slots into the persistence layer (3.x) as an app-list-per-device table; icons reuse
the bulk channel + `LargeIconCache`/`BulkResources`. **Depends on 1.1, 1.2.** Protocol: a small
`apps.list` control message (v-bump).
**Risk:** activity resolution (`am start` needs a launchable activity), multi-display input
routing, window lifecycle.

---

## Group 2 — Phone controls the PC

### 2.1 Quick PC controls from the phone: lock / sleep / restart / shutdown / volume 🆕
**Today:** the phone already sends `pc.input` (→ `SendInput`) and `pc.media.control`
(phone→desktop) — the exact precedent for a phone→desktop control message.
**Approach:** add a **`pc.control`** control message (phone→desktop) with an action enum
(`lock`, `sleep`, `shutdown`, `restart`, `volume_up`, `volume_down`, `mute`, `brightness`…).
Desktop executes via Win32: `LockWorkStation`, `SetSuspendState`, `InitiateShutdown`/`ExitWindowsEx`,
and the audio endpoint volume API (or `keybd_event` for the media/volume keys). **Destructive
actions (shutdown/restart/sleep) require a confirmation** — an overlooked-but-essential safety
gate. Protocol bump (→ v15).
**Bend to existing:** `PcInputService`/`PcMediaService` are the pattern; add a `PcControlService`.
Works over any transport (no shell UID needed on the Windows side).

### 2.2 Phone Home widgets to control the PC (+ PC brightness/volume/sound profile) 🆕
The user-facing surface for 2.1: control widgets on the phone's **Home** screen.
**Approach:** phone Home cards that emit `pc.control`; reflect PC volume/brightness state back to
the phone (extend `pc.media.state` or add `pc.state`). Reuses the phone's existing widget/card
pattern (`HomeScreen.kt`).

### 2.3 "Tools" page on the phone → remote keyboard + touchpad + buttons 🆕
A **Tools** entry on the phone Home opening a page with a **remote keyboard** (type to the PC), a
**touchpad**, and buttons — i.e. drive the PC **without** the video (headless remote control).
**Approach:** reuse the reverse-mirror input path — the phone already forwards typing and touch as
`pc.input`; the Tools page is that input surface **decoupled from the video** (`MirrorActivity`
already has the hidden-EditText keyboard + touch-to-`pc.input` logic to lift out). Works over any
transport (Windows input injection needs no shell UID). **Reuses `pc.input` — little/no new
protocol.**

---

## Group 3 — Persistence, offline & the Sync page

### 3.1 A proper database to remember the phone 🆕 (replaces JSON where it helps)
**Today:** persistence is `settings.json` (registry + per-device settings), `cache/<serial>/`
(offline memory), and `sync-state.json`. Serviceable, but the new asks (notification history,
offline queue, app-list cache) want real queries and retention.
**Approach:** introduce a **SQLite data layer** (e.g. `Microsoft.Data.Sqlite`) as `LincStore`,
holding: known devices, per-device offline cache, **notification history**, the **offline action
queue**, and the **app-list cache**. Migrate incrementally — keep `settings.json` read/write for
back-compat or do a one-time import (the registry already migrates a pre-M03 file, so a
JSON→SQLite import fits that pattern). `devicesim` must be updated to cover the new store.

### 3.2 Notification history — save up to days/months 🆕
**Today:** notifications are **in-memory/session-only**, and D-032 deliberately keeps notification
**bodies** off disk unless the user opts in.
**Approach:** an **opt-in** persistent notification history in SQLite (3.1) with a **retention
setting** (e.g. 7 days / 30 days / 90 days / off) and a **clear** action. This **must honor
D-032**: off by default; when on, the user has consented to bodies on disk. Consider storing icon
blobs by hash to bound growth.
**New decision needed** (persistent notification history as a scoped, opt-in reversal of D-032's
default).

### 3.3 Sync page not static; share while disconnected, sync on reconnect; cache device 🔧/🆕
**Today:** offline device memory (M02) already keeps Home from blanking, and the Chrome-style
**device tabs (M03)** already implement "upper tab strip showing the device, kept once paired, +
button to add a phone that shifts to the new tab." So the **tab requirement is essentially done** —
verify it matches the ask.
**What's new:** (a) make the **Sync page cache-first** too (persist last-known SMS conversations /
call log per device so it isn't blank offline), and (b) an **offline action/share queue**: queue a
share (or SMS send, or file push) while disconnected and **flush it on reconnect**, with
idempotency + ordering. Both land on the SQLite layer (3.1).
**Bend to existing:** the queue is a table + a "drain on `Connected`" hook in the supervisor's
connected fan-out; reuses `ShareService`/`FileService`.

---

## Group 4 — Routing (PC ⇄ phone as keyboard / speaker)

### 4.1 PC as speaker for the phone (phone audio → PC) 🆕
**Today:** the **audio channel (4)** is designed (scrcpy-server audio capture, Android 11+) but
not implemented.
**Approach:** implement channel 4 using the **vendored scrcpy** (1.1) audio capture → decode/play
on the PC. **Depends on 1.1.** ADB-bound (like the mirror).

### 4.2 Phone as keyboard/speaker/mic for the PC 🆕
- **Phone as keyboard for PC:** already delivered by 2.3 (`pc.input`).
- **Phone as speaker/mic for PC:** the hard direction — needs a PC audio-capture → phone-playback
  path (and mic the reverse). Stage this last; scope carefully.
**Bend to existing:** input routing reuses `pc.input`; audio routing is the genuinely new plumbing
and should be a dedicated milestone, not smuggled in.

---

## Group 5 — Device page & connection UX

### 5.1 Choose connection type ✅ (verify)
Done 2026-07-21: a per-device `ConnectionPreference` (Auto / USB-only / Wireless-only /
Direct-only) with a picker on the Device page. Just confirm it satisfies the ask.

### 5.2 "Tools" grouping; put screen-mirror stuff under Tools 🆕 (UI reorg)
Group mirror / desktop-mode / apps / scrcpy-settings under a **Tools** section on the Device page.
Pure UI reorganization over existing commands.

### 5.3 Edit scrcpy settings 🆕
Expose scrcpy options (quality, bitrate, resolution, crop, extra flags) in the UI and pass them to
the launch. **Bend to existing:** `MirrorService` already builds the scrcpy command line; surface
those args as bound settings (persisted per device in 3.1). **Depends on 1.1** for the full set.

### 5.4 Prioritize USB when connected ✅ (verify)
Done 2026-07-21: transports ranked **USB > wireless ADB > Direct TLS**, and USB **preempts**. Just
confirm.

---

## Group 6 — Sharing & install

### 6.1 App shows up in Android share sheet, for files 🔧
**Today:** `ShareReceiverActivity` handles `ACTION_SEND` **text/plain**; M19 added a `FileProvider`
for incoming files and the `share.item kind:file` path.
**Approach:** widen the share-sheet intent filter to `*/*` (and `ACTION_SEND_MULTIPLE`), stage the
file(s) in the outbox, and reuse the existing `share.item kind:file` → desktop sweep. Small.

### 6.2 Install-APK widget + guide the user, inject via ADB 🆕
**Today:** `PhoneSetupService` already `adb install`s the companion APK during onboarding — the
exact primitive.
**Approach:** a desktop **"Install APK"** action (Files page or a widget): pick an APK → guided
copy → `adb install` via the same primitive, with plain-language progress/errors. Covers both
"install-APK widget" and "PC guides then injects the APK."

### 6.3 Move Share to the phone Home; remove the Share tab 🆕 (Android UI reorg)
Fold the Share screen into **Home** (a share section reachable by scrolling), and drop the Share
bottom-nav destination. Reuses `ShareScreen.kt` content inside `HomeScreen.kt`.

---

## Group 7 — Presence & connection (mostly done)

### 7.1 Bluetooth scan to know the phone is close ✅ (M04)
Delivered: the phone advertises a BLE presence beacon; the desktop scans and reconnects faster. No
data over BLE. Verify it meets the ask; optional polish = surface it more clearly in the UI.

### 7.2 Preemptive mutual start; open one app → signal the other; background scan both ways 🔧
**Today:** standing presence (D-014) + the desktop auto-starting the companion (M01) + BLE presence
(M04) already do most of this — the phone dials out on sight, the desktop hunts continuously.
**What's left (polish):** make opening **either** app fire an immediate connect attempt, and
consider the **PC also advertising** a BLE beacon so the phone can scan for the PC (today only
phone→PC). Small, on top of existing machinery.

---

## Group 8 — Bugs

### 8.1 Desktop Files page doesn't show the connected phone's directory 🐞
**Today:** `FilesPage` + `FilesViewModel` + `FileService`/`FileServiceRouter` exist and worked
historically; the user reports it no longer lists the phone dir.
**Likely suspects to check first** (needs diagnosis, not yet root-caused): the M03 device-tabs
refactor (per-device resolution — is the active device wired to the Files VM?); the
`FileServiceRouter` picking the TLS path when `HasAdb` is false (rename/list edge cases); the page
not refreshing on the `Connected` fan-out (like the supervisor-in-page bug class); or an
unobserved exception swallowed on load (the "silent catch" pattern). **Fix + add a
`FileService`/router regression to the harnesses.** Good candidate for the **first** milestone —
concrete, self-contained, restores trust.

---

## Cross-cutting things easy to overlook (fold into the roadmap)

- **Protocol version bump + negotiation** for every new message type (`pc.control`, `apps.list`,
  audio, phone-setting writes). Keep unknown-type/field tolerance; gate on the negotiated version.
- **Safety confirmations** for destructive PC controls (shutdown/restart) and for installing
  arbitrary APKs.
- **Privacy:** persistent notification history is a scoped, opt-in reversal of D-032's default —
  decide it explicitly, default off, with retention + clear.
- **Capability/version detection** for Desktop Mode and virtual displays (varies by Android
  version/OEM); graceful fallback and plain-language "your phone doesn't support this."
- **Packaging still open (M07):** bundling adb + the vendored scrcpy + the companion APK so end
  users install nothing — 1.1 and 6.2 move this along.
- **Harness coverage** for every new surface: extend `devicesim` (SQLite store, offline queue),
  add an apps-list/desktop-mode harness, keep `mirrorsim` green through the scrcpy vendoring.
- **Secure-surface / DRM** limits on virtual-display capture; **multi-display input routing** for
  per-app windows.
- **`WRITE_SETTINGS`** (or shell) for phone-side setting writes (force landscape / auto-rotate /
  brightness) — prefer companion protocol messages over raw ADB to honor D-001.

---

# Owner corrections & clarifications (2026-07-22)

Recorded after the owner reviewed the first analysis. These **override** the sections above where
they conflict, and the ROADMAP was re-sequenced accordingly.

1. **Device tabs are NOT "done" from the owner's view.** They exist in code but the strip
   **hides itself when only one phone is paired**, so a single-device owner never sees them. Requirement:
   the top tab strip is **always visible once ≥1 device is paired**, with the "+" button. → M1.
2. **Preemptive auto-connect is unverified in practice** — the owner has never seen it work. Verify
   it actually connects on its own and surfaces status; repair if needed. → M1 sanity-check, full
   repair M12.
3. **Onboarding must be a specific guided front-door flow** (not the current posture): first launch →
   Home says no device is paired, directs to the Device page → Device page says "click + to add" →
   guided developer-options / USB + wireless ADB walkthrough → detect over ADB ("connection set") →
   **desktop injects the BUNDLED companion APK**, which auto-starts its service untouched → connect →
   **device saved permanently**, appears as a top tab, stats stored, notifications/messages/sync
   **cached under its device profile** → "+" repeats → **Settings › Devices** to list/forget/manage.
   Requires **bundling the companion APK into the desktop** ("the desktop carries the companion and
   simply injects it"). → **M2** (new, promoted to the front).
4. **scrcpy vendoring structure is `SCRCPY/Default` + `SCRCPY/Custom`**: `Default` = pristine
   upstream source (do **not** corrupt it), `Custom` = the working copy edited for **Linc logo, a
   custom branded top bar, and the Linc name**. Linc uses **Custom**. (This supersedes the
   "single isolated patch" phrasing; keeping Default as a submodule/clone still gives upstream
   tracking.) → M3.
5. **Home layout is a resizable, sectioned space.** Add a new **Apps** section beside widgets and
   the phone-stuff tabs. Sections must be **removable** (with a Home button to add them back),
   **flippable left/right**, and **optionally pop-out** into a floating panel with a **small custom
   embedded top-bar** (close / return-to-Home — **no Windows title bar**). The pop-out is
   **nice-to-have, only if it isn't a headache**; remove/re-add/flip is the priority. → M6.
6. **Apps section behavior:** on first connect the desktop **loads all the phone's apps** (icon +
   name), lists them, **caches under the device profile**, checks for new ones on later connects, and
   **clicking one opens a Custom-scrcpy window for that app at a default ratio**. → M7.
7. **Desktop Mode** is fully specified in **`DESKTOP-MODE-SPEC.md`** — a Desktop Mode widget →
   `scrcpy --new-display` tuned (explicit `WxH/DPI`, `--mouse=uhid --keyboard=uhid`, one-time ADB
   flags + reboot handling), a first-run setup screen, a settings panel, and per-device auto-tuning.
   "Full size" = **match the monitor**; auto-fullscreen = Android window state via `windowingMode`. → M8.
8. **Phone-as-remote is broader than "routing."** A phone **Tools** suite that controls the PC:
   remote **keyboard**, **trackpad**, **multimedia control**, **slideshow/presentation remote**, PC
   **sound + brightness**, a **telephony notifier**, and **phone camera → PC webcam** (the webcam is a
   heavier sub-feature — needs a Windows virtual-camera device — implement as its own step). → M4.
9. **Install-APK widget is on the PC** and installs a selected APK **to the phone via ADB**.
   Confirmed. → M10.
