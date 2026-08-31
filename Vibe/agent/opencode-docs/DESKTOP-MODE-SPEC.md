# Linc Desktop Mode — Implementation Spec

> Owner-authored spec for roadmap **M8** (`ROADMAP.md`). Depends on the vendored Custom scrcpy
> (M3, D-041). Decision: **D-046**.

## Goal

Linc's PC-side app already bundles scrcpy and has ADB permissions. Add a **"Desktop Mode"** feature
that launches the phone's native Android desktop-mode shell via `scrcpy --new-display`, tuned to fix
the rough edges of the default experience — proper window sizing, resizability, aspect ratio, and
sane defaults — with a settings panel to control all of it. It is surfaced as a **Desktop Mode
widget/toggle**; clicking it brings up the phone mirror with the phone in desktop mode.

## Background context for the implementing agent

- `scrcpy --new-display` alone can trigger Android's native desktop-mode shell (taskbar + freeform
  windows) if the right hidden settings are already enabled on-device.
- Required one-time ADB settings (some require a reboot to take effect):

  ```bash
  adb shell settings put global force_resizable_activities 1
  adb shell settings put global enable_freeform_support 1
  adb shell settings put global force_desktop_mode_on_external_displays 1
  adb shell settings put secure desktop_mode 1
  ```

- `--new-display` accepts an explicit resolution/DPI, e.g. `--new-display=1920x1080/240` — this is
  the main lever for fixing sizing/aspect-ratio issues, since the default virtual display often
  doesn't match the phone's native aspect ratio and looks stretched or oddly scaled.
- scrcpy also supports `--start-app` / window-focus flags and keyboard shortcuts. **Note two
  different fullscreen concepts** and do not conflate them: scrcpy's own `Alt+F` fullscreens the
  *scrcpy client window*; the owner's "apps open fullscreen" means the *Android app's window state
  inside desktop mode*.
- Input handling matters: pass **`--mouse=uhid --keyboard=uhid`** — without these, freeform windows
  may not be draggable/resizable properly because the pointer isn't released into the virtual
  display correctly.

## Feature requirements

### 1. First-run setup screen
- Triggered the first time the user clicks "Desktop Mode."
- Runs the one-time ADB settings commands above.
- Detects if a reboot is required (some flags need it, some apply live) — if so, show a clear
  **"Reboot phone to finish setup"** step with a button that runs `adb reboot` and waits for the
  device to reconnect before proceeding.
- Must be **idempotent** — safe to re-run if the user reinstalls Linc or switches phones.

### 2. Desktop Mode settings panel
Expose these as user-configurable options (not hardcoded):
- Virtual display **resolution/DPI** (sensible default, **not** the phone's raw native resolution —
  1:1 native tends to look oversized/awkward).
- **Aspect-ratio mode:** "match monitor" (full size, default) vs "match phone" vs custom.
- Toggle for **floating/freeform** windows vs standard maximized layout.
- Toggle for **resizable** windows.
- **Default window state** on launch: maximized / freeform / remember last.
- Toggle + shortcut for **"launch apps fullscreen by default"** — implement as **Android-side
  window state** (`am start --windowingMode` or equivalent), **not** scrcpy's own fullscreen toggle.

### 3. Auto-tuning across devices
- Account for different phones reporting different native resolutions/DPIs/aspect ratios; build a
  lookup or dynamic calculation so the virtual-display resolution scales sensibly regardless of the
  source device — not a single hardcoded resolution that only looks right on one phone.
- Expose a **"reset to recommended"** button that recalculates good defaults from the connected
  device's reported display metrics (`adb shell wm size` / `wm density`).

### Suggested settings schema (example)

```
DesktopModeSettings {
  virtualDisplayWidth: int
  virtualDisplayHeight: int
  virtualDisplayDpi: int
  aspectRatioMode: enum [MatchMonitor, MatchPhone, Custom]
  defaultWindowMode: enum [Freeform, Maximized, RememberLast]
  resizableWindows: bool
  autoFullscreenApps: bool
  setupCompleted: bool
  rebootPending: bool
}
```

## Owner's answers to the open questions
- **"Full size, not native display"** = match the **PC monitor's resolution** (this is required),
  not just a larger fixed default than the phone's own resolution.
- **Reboot-required detection** — flag-based vs trial-and-error: **implement as best you can** (either
  is acceptable; trial-and-error = attempt without reboot, prompt only if the shell doesn't appear).
- **Auto-fullscreen** = the Android app's window state inside desktop mode (`windowingMode`), not the
  scrcpy client window.
