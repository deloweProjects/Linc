# Changelog

Chronological record of significant features, fixes, refactors, and architectural changes. Newest
first.

> **History before 2026-07-22** (all of Era 1 `M0–M19` and Era 2 `M00–M07`) is captured formally
> in `Documentation/02-History.md`, and the raw old changelog is preserved at
> `v2.docs.old/CHANGELOG.md`. This file starts fresh for the **Convergence** phase (`claude-docs/ROADMAP.md`).

## [Unreleased]

### Fixed
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
  **`v2.docs.old/`** and rewrote `claude-docs/ROADMAP.md`, `FEATURES.md`, `BRAIN.md`, `CHANGELOG.md`, and
  `task.txt` clean for the new **Convergence** phase. Kept the living technical references
  (`PROTOCOL.md`, `DECISIONS.md`, `TECH_STACK.md`, `THEME.md`, `ARCHITECTURE.md`, `CLAUDE.md`,
  `CONTRIBUTING.md`) in place. Added `claude-docs/REQUIREMENTS-ANALYSIS.md` (each new requirement mapped to
  Implemented/Polish/New/Bug and to the code to reuse) and **decisions D-041…D-048** for the new
  work. No source code changed in this pass; the previous session's uncommitted M07 Sync-page split
  remains in the working tree.

### Planned next
- **M2b — the guided onboarding wizard**: the first-run flow (Home "no device" → Device page → `+`
  → guided dev-options/USB+wireless ADB → detect → **bundle + inject the companion APK** →
  auto-start → save device). Must keep the add-device entry reachable **at zero devices** (M2a's
  close-last-tab hides the strip *and* the `+`). Then **M3 — vendor scrcpy** (`SCRCPY/Default` +
  `SCRCPY/Custom`) as the foundation for the screen-feature milestones. See `claude-docs/ROADMAP.md`.
- **Known caveat from M2a:** a `devicesim` run (killed mid-way by a piped command before its restore
  step) overwrote the real Pixel 7 pairing with test devices; it was cleaned up and the phone
  re-paired over USB, but the device's **sync-lane / folder-sync config may need re-setting**.
