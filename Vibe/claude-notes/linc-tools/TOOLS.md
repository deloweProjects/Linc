# Linc Tools: every tool, exactly (spec for the next session)

How to read each entry:
- **Surface** = what the owner sees first.
- **Hidden** = what goes in ⋯ More or Advanced.
- **Cmd** = the exact command (always `adb -s <serial> shell …` unless it starts with `adb`).
- **Parse** = how the output becomes UI.
- **Tier** = S safe / C confirm / D destructive.
- **Gate** = when it's greyed out, and the reason shown.

All commands go through `IAdbShell` (PLAN.md §1.1). All user text goes through `AdbQuote.Arg`.
Commands marked (verify) should be checked on a real phone before relying on them.

---

## 1. Type: "Type on your phone" (the hero)

**The case it exists for:** the phone's screen is dead or black, and you need to wake it and type
your PIN, or answer a prompt, from the laptop keyboard.

**Surface:**
- **A big text box:** "Type here, Enter sends", with a toggle chip "Live keys".
  - Live keys **off** (default): type a line, press Enter or **Send**. The text goes to whatever
    field has focus on the phone, then Enter is pressed if "Press Enter after" is on (hidden, default
    on).
  - Live keys **on**: the box captures every keystroke (letters, Backspace, Enter, Tab, arrows, Esc)
    and sends each one straight away, like a remote keyboard. A soft glow on the box shows it's live.
- **A row of round buttons under the box:** **Wake** · **Unlock swipe** · **Home** · **Back** ·
  **Recents**.
- **A compact D-pad:** ↑ ↓ ← → OK, for TVs or phones whose touch is broken.

**Hidden (⋯):**
- Media keys (play/pause, next, previous, volume ±, mute).
- Power (sleep); screenshot key; notifications shade; Paste; Select all / Copy / Cut (A13+
  `keycombination`).
- "Press Enter after" (on); "Unlock swipe direction" (up).

**Commands:**

| Action | Cmd |
|---|---|
| Wake | `input keyevent KEYCODE_WAKEUP` (224) |
| Unlock swipe | `input swipe {w/2} {h*0.8} {w/2} {h*0.25} 250` (size from `wm size`) |
| Line of text | `input text <escaped>` (spaces → `%s`; escape `\ ' " & ; ( ) < > \| * ? $ ~` and backtick; chunks of ~100 chars) then `input keyevent 66` |
| Home / Back / Recents | `input keyevent 3` / `4` / `187` |
| D-pad | `19 20 21 22 23` |
| Media | `85 87 88 24 25 164` |
| Sleep | `223` |
| Notifications | `cmd statusbar expand-notifications` |
| Copy / Paste / Select all | `input keycombination KEYCODE_CTRL_LEFT KEYCODE_C\|V\|A` (Android 13+; below that use `279` for paste) |
| Backspace / Del / Tab / Esc / Enter | `67` / `112` / `61` / `111` / `66` |

**Latency:**
- Each `input` call starts a JVM, about 100–300 ms. Live keys must use **one persistent shell**:
  `IInputSession` keeps `adb shell` open and writes `input keyevent X\n` lines to its stdin, which
  cuts the per-key cost.
- If scrcpy is running for Mirror, inject through scrcpy instead, under 10 ms: the next session
  should check whether `MirrorService` exposes the control socket.
- **Fallback:** batch keys typed within 40 ms into one `input text`.

**Non-ASCII** (é, emoji, Arabic, Chinese): `input text` can't send them.
- If the Linc app is running, send the text with the existing clipboard sync, then
  `input keyevent 279` (paste). Mark it "via clipboard".
- Without the app, show "This character needs the Linc app" and send the rest.
- The scrcpy `--keyboard=uhid` path uses the phone's own layout and works for any language. Consider
  it for Live keys when Mirror is open.

**Lock screen:**
- `input text` / keyevents do type into the PIN or password field once the screen is awake and the
  bouncer is showing. That is the rescue flow: Wake → Unlock swipe → type PIN → Enter.
- The owner types their own PIN; Linc never stores it.
- **Privacy:** the box is a password-style field while the phone is on its lock screen
  (`dumpsys window | grep mDreamingLockscreen|isKeyguardShowing`, or `dumpsys power` — verify), and
  typed text is never logged.

**Tier:** S. **Gate:** needs ADB (USB or wireless). The phone must have been authorised for debugging
before the screen died; say so on the Rescue card.

## 2. Rescue (new): "Screen broken or black?"

**Surface:** one card with three big numbered steps, each a single button.
1. **See it:** opens Mirror with the phone's own screen kept off (`--turn-screen-off --stay-awake`),
   so the phone's screen shows on the PC. Reuse `MirrorService`; add the flags as an option.
2. **Get in:** Wake → Unlock swipe → a PIN box ("Type your PIN", Enter sends). Uses §1.
3. **Save your stuff:** "Back up photos and files to this PC". Pulls `DCIM`, `Pictures`, `Download`,
   `Documents`, `Movies`, `Music` from `/sdcard` into `Documents\Linc Backup\<model> <date>`, with a
   progress bar.
   - Option: also save the app list (§3 Backup).
   - Uses `PullAsync` with progress; skips files already copied (same size and mtime).

**Hidden:** keep awake while charging (`svc power stayon usb`), lower brightness to save the panel.

**Tier:** S (backup writes only on the PC). **Gate:** needs ADB. When the phone isn't authorised:
"USB debugging wasn't allowed before the screen broke — Linc can't reach it", with a link to what
else can help (OTG mouse).

## 3. Apps

**Surface:**
- A search box, filter chips **Yours · System · Disabled**, and a list: icon, name, version, size.
- Click a row to open a **side sheet** with big buttons **Open · Stop · Uninstall** and secondary
  **Disable · Clear data · Save APK**, then the **Permissions** toggles.

**Hidden (sheet ⋯):**
- AppOps (Run in background, Display over other apps).
- Battery optimisation (unrestricted / optimised).
- Set as default (browser/SMS/dialer/home roles).
- App info screen on the phone; copy the package name.

**List-level ⋯:**
- **Backup app list** (JSON) and **Restore**.
- **Clean up phone (debloat)**, with tiers and undo; see below.
- **Install APK** (drag and drop an .apk or .apks onto the page).

**Commands:**
- **List:** `pm list packages -f -U --show-versioncode` plus `-3` / `-s` / `-d`. Lines look like
  `package:<apk>=<pkg> versionCode:<n> uid:<n>`.
- **Labels, icons, sizes:** from the Linc app when it's running (`apps.get` already exists, with
  icons). Without the app:
  - label = the package name prettified;
  - icon = a generic tile;
  - sizes from `dumpsys diskstats` (parallel arrays `Package Names` / `App Sizes` / `App Data Sizes`
    / `Cache Sizes`).
- **Details:** `dumpsys package <pkg>` gives `versionName`, `firstInstallTime`, `lastUpdateTime`,
  `targetSdk`, `installerPackageName`, and lines `android.permission.X: granted=true|false`.

**Actions:**

| Action | Cmd | Tier | Undo |
|---|---|---|---|
| Open | `monkey -p <pkg> -c android.intent.category.LAUNCHER 1` | S | — |
| Stop | `am force-stop <pkg>` | S | — |
| Uninstall (user app) | `pm uninstall <pkg>` | D | none: offer "Save APK first" |
| Remove system app | `pm uninstall -k --user 0 <pkg>` | C | `cmd package install-existing <pkg>` |
| Disable / Enable | `pm disable-user --user 0 <pkg>` / `pm enable <pkg>` | C | enable |
| Clear data | `pm clear <pkg>` | D | — |
| Save APK | `pm path <pkg>` (one line per split), then `PullAsync` each into `<Downloads>\<label> <ver>\` | S | — |
| Install | `adb install -r [-d] [-g] app.apk` / `install-multiple` (A14+: targetSdk <23 is blocked; offer `--bypass-low-target-sdk-block` under Advanced) | C | uninstall |
| Permission toggle | `pm grant/revoke <pkg> <perm>` (runtime permissions only; others shown read-only) | C | toggle back |
| AppOps | `appops get <pkg>` / `appops set <pkg> <OP> allow\|ignore\|default` | C | default |
| Battery | `dumpsys deviceidle whitelist +<pkg>` / `-<pkg>` | C | reverse |
| Default app | `cmd role get-role-holders <role>` / `cmd role add-role-holder --user 0 <role> <pkg>` | C | previous holder |

**Clean up phone (debloat):**
- Uses a bundled list in the Universal Android Debloater style, tiers **Recommended / Advanced /
  Expert**. **Unsafe** entries are never shown.
- Only ever `--user 0`.
- Every change goes in a log (`%LocalAppData%\Linc\debloat-<serial>.json`) with **Restore all**.
- Always protected (never removable): `com.android.systemui`, `com.android.settings`, the package
  installer, `com.android.phone` and the telephony providers, the current launcher,
  `com.google.android.gms`, and Linc itself.

## 4. Capture

**Surface:** two big tiles, **Screenshot** and **Record**, and the last capture as a preview with
**Open · Copy · Show in folder**.
- Screenshot: one click, saved to `Pictures\Linc\Screenshots` and copied to the clipboard.
- Record: click to start, a timer counts, click to stop.

**Hidden:**
- **Clean status bar** (demo mode: 12:00, full battery, full signal, no notifications).
- Record quality (720p / 1080p / max), Show touches while recording, Record sound (uses scrcpy).

**Commands:**
- Screenshot: `adb exec-out screencap -p` via `ExecOutAsync` (binary-safe; `-d <id>` for other
  displays).
- Record without sound: `screenrecord --size 1280x720 --bit-rate 8000000 /sdcard/linc-rec.mp4`,
  limited to 180 s. Segment and join for longer, or switch to scrcpy. Stop by cancelling the stream
  (sends SIGINT), then `PullAsync` and `rm`.
- Record with sound (Android 11+): `scrcpy --no-window --record <file>.mp4 [--no-video]`; reuse
  `ToolLocator.FindScrcpy`.
- Show touches: `settings put system show_touches 1` (restore after).
- **Demo mode:**
  1. `settings put global sysui_demo_allowed 1`
  2. `am broadcast -a com.android.systemui.demo -e command enter`
  3. `… -e command clock -e hhmm 1200`
  4. `… -e command battery -e level 100 -e plugged false`
  5. `… -e command network -e wifi show -e level 4`
  6. `… -e command notifications -e visible false`
  - Exit with `… -e command exit`. Always exit when leaving the page or disconnecting.

**Tier:** S. **Gate:** FLAG_SECURE apps (banking) capture black. Say "This app blocks screenshots"
when the image is all black.

## 5. Health

**Surface** (refreshes every 3 s while open; pause when hidden):
- **Battery:** a big ring with level %, status (Charging · 🔌 USB/AC/Wireless), temperature, and
  charging speed in W.
- **Phone temperature:** the hottest sensor, with a colour state (cool / warm / hot).
- **Storage:** a bar (used/free) that links to Files.
- **CPU and RAM:** two small live line graphs (60 s).
- **Busy apps:** the top 5 by CPU, each with a **Stop** button.

**Hidden (⋯):**
- Device card (model, Android version, security patch, chip, uptime, resolution, serial) with
  **Copy all**.
- Battery details (voltage, health, cycle count if readable, charge counter).
- All temperatures table; network (Wi-Fi name, signal dBm, link speed, live data rate).
- Per-app battery use since the last charge; **Bug report** (C, warns about personal data).

**Commands:**
- **Battery:** `dumpsys battery`. Key:value lines: `level`, `temperature` (tenths of °C), `voltage`
  (mV), `status` (2 charging, 5 full), `health`, `AC|USB|Wireless powered`.
- **Charging speed:** `cat /sys/class/power_supply/battery/current_now` (µA; sign and units vary by
  OEM; may be denied, in which case hide the number).
  - Speed W ≈ |µA| × mV ÷ 10⁹.
  - Also try `cycle_count` and `charge_counter`; hide if unreadable.
- **Temperatures:** `dumpsys thermalservice` lines
  `Temperature{mValue=41.2, mType=…, mName=…, mStatus=…}`. Colour by status 0–6.
- **CPU:** sample `/proc/stat` twice to get a % graph. Top apps from
  `top -b -n 1 -m 10 -o PID,%CPU,RES,NAME -s 2`, mapping process names to packages (Stop =
  `am force-stop`).
- **RAM:** `/proc/meminfo` (MemTotal, MemAvailable) for the gauge; `dumpsys meminfo` "Total PSS by
  process" for the hidden table.
- **Storage:** `df /data` and `df /sdcard` (parse 1K-blocks); `sm list-volumes` for an SD card.
- **Network:** `cmd wifi status` and `dumpsys wifi` (`RSSI:`, `Link speed`, SSID, which may be
  redacted); `/proc/net/dev` deltas.
- **Device card:** `getprop` (`ro.product.manufacturer`, `ro.product.model`,
  `ro.build.version.release`, `ro.build.version.sdk`, `ro.build.version.security_patch`,
  `ro.soc.model` (12+)), `wm size`, `uptime`.
- **Per-app battery:** `dumpsys batterystats --charged` (parse the "Estimated power use" section;
  verify the format per version).
- **Bug report:** `adb bugreport <path>.zip` via `AdbAsync`; stream progress; it takes 1–5 min.

**Tier:** S (bug report C). **Gate:** values the phone doesn't share are simply hidden, with no
errors.

## 6. System

**Surface:** six large toggle or step tiles (two rows of three):

| Tile | Control | Cmd |
|---|---|---|
| **Dark mode** | toggle | `cmd uimode night yes\|no` |
| **Stay awake** (while charging) | toggle | `settings put global stay_on_while_plugged_in 7\|0` |
| **Animations** | Off · 0.5× · 1× | `settings put global window_animation_scale\|transition_animation_scale\|animator_duration_scale <v>` |
| **Screen timeout** | 30 s · 1 m · 5 m · 10 m · 30 m | `settings put system screen_off_timeout <ms>` |
| **Brightness** | slider + Auto chip | `settings put system screen_brightness_mode 0\|1`, `screen_brightness 0-255` (or `cmd display set-brightness 0..1`) |
| **Text size** | Small · Default · Large · Larger | `settings put system font_scale 0.85\|1\|1.15\|1.3` |

Read the current values on open with `settings get …`.

**Hidden (⋯ More):**
- Wi-Fi, Mobile data, Bluetooth, Airplane, Location, Battery saver, Night light. Wi-Fi and Airplane
  warn on a wireless link.
- Commands for those, in the same order:
  - Wi-Fi: `cmd wifi set-wifi-enabled enabled|disabled`
  - Mobile data: `svc data enable|disable`
  - Bluetooth: `cmd bluetooth_manager enable|disable` (12+) or `svc bluetooth` (11)
  - Airplane: `cmd connectivity airplane-mode enable|disable`
  - Location: `cmd location set-location-enabled true|false`
  - Battery saver: `cmd power set-mode 1|0`
  - Night light: `settings put secure night_display_activated 1|0`
- **Display size (DPI) and resolution** (C, with a **15-second auto-revert dialog**: "Keep these
  settings?"):
  - `wm density <dpi>|reset`, `wm size <WxH>|reset`;
  - presets Compact / Default / Large, plus a custom value.
- **Desktop windows:** `settings put global enable_freeform_support 1`,
  `force_resizable_activities 1` (C; ties into Desktop mode, which may already set these, so check
  `DesktopModeService`).
- **Accessibility:** show touches, pointer location (demos), colour inversion, colour correction
  (`settings put secure accessibility_display_inversion_enabled 1`, `…daltonizer_enabled`).
- **Quick Settings:** open the shade (`cmd statusbar expand-settings`).
- **Restart** (C; Linc reconnects): **Restart**, **Recovery** (C), **Bootloader** (Advanced only,
  C), **Power off** (`adb reboot`, `adb reboot recovery`, `adb reboot bootloader`,
  `svc power shutdown`). Safe mode needs root, so say "Hold Power off on the phone".

**Tier:** S unless noted. **Gate:** SDK checks per row (e.g. Bluetooth command A12+).

## 7. Inspector (Advanced)

**Surface:** the phone's screen (a screenshot) at the left, with a thin outline on every element.
- **Hover:** highlight, plus a tooltip (text, id, type).
- **Click:** taps that element on the phone, then refreshes.
- **Refresh** button and an **Auto** toggle (refresh after each tap).

**Hidden:** the element tree panel; "Copy selector" (for Macros); show only clickable; long-press
instead of tap.

**Commands:** `uiautomator dump /sdcard/linc-ui.xml && cat /sdcard/linc-ui.xml` (or `exec-out
uiautomator dump /dev/tty`) plus `exec-out screencap -p`.
- **Parse:** each `<node>` gives `bounds="[x1,y1][x2,y2]"`, `text`, `resource-id`, `content-desc`,
  `class`, `clickable`, `package`. Scale from the device size to the image.
- **Tap:** `input tap cx cy`.

**Limits** (show as a hint, not an error):
- "Screen is busy" (`could not get idle state`): retry, or suggest turning off animations.
- Secure screens are black. Games and web views have no elements.
- Each dump takes 1–3 s. One UiAutomation client at a time.

**Tier:** S. **Gate:** needs ADB.

## 8. Logs (Logcat; Advanced)

**Surface:**
- A live list (virtualised), with time, level dot (V grey, D blue, I green, W amber, E red), tag
  and message.
- At the top: an **app picker** ("All apps" or one app) and **level chips** (All · Info · Warnings ·
  Errors).
- Buttons: Pause/Resume, Clear view, Save to file.

**Hidden:** the buffers (main / system / crash / events); a regex filter; "Clear phone log" (C); wrap
lines.

**Commands:**
- `logcat -v threadtime -T 500 [*:W] [--pid=<pid>]`, with the PID from `pidof <pkg>`, re-resolved
  when the app restarts (like pidcat).
- Parse `MM-DD HH:MM:SS.mmm PID TID L TAG: msg`.
- Cap the view at 20 000 lines (ring buffer).

**Tier:** S. **Gate:** needs ADB.

## 9. Macros (Advanced)

**Surface:** a grid of the owner's saved macro buttons. Click to run; a small progress ring shows
step n/m. A "+ New macro" tile.

**Builder** (side sheet): a list of steps, each picked from a menu:
- **Key**, **Type text**, **Tap element** (from the Inspector's selector), **Tap point**, **Swipe**
- **Open app**, **Open link**, **Open settings screen**
- **Wait** (seconds), **Wait until** (app on screen, text on screen, screen on)
- **Shell command** (C when run)

Then **Name**, **Icon**, **Test run**.

**Example macros to ship as templates:**
- "Wake & unlock" (Wake, Unlock swipe, Type PIN *asked each run, never saved*, Enter)
- "Clean screenshot" (demo on, screenshot, demo off)
- "Night mode" (dark mode on, brightness 20 %, Do Not Disturb if the app is there)

**Wait conditions:**
- `dumpsys activity activities | grep topResumedActivity` (foreground app)
- `dumpsys power | grep mWakefulness` (screen on)
- uiautomator text match

**Storage:** `%LocalAppData%\Linc\macros.json`. Macros can be pinned to Home's quick controls
(hidden option).

**Tier:** each step's own tier. **Gate:** needs ADB.

## 10. Console (Advanced)

**Surface:** a dark monospace terminal pane, a prompt line, and history (↑/↓). Output is plain
text, selectable, with **Copy output**.

**Hidden:** clear; save session.

**Autocomplete** (Tab), with sources loaded once per device:
- `cmd -l`, `service list` (services)
- `pm list packages` (packages)
- `settings list system|secure|global` (settings keys)
- `getprop` keys, `ls /system/bin` (binaries)
- `cmd <svc> help` parsed lazily for subcommands

**Risky verbs** (`rm`, `pm clear`, `uninstall`, `reboot`, `wm`, `settings put`, `svc`) are
highlighted amber, and the first use per session asks for confirmation.

Replaces the raw-shell box in Settings (`SettingsViewModel.RunShellCommandAsync`); keep one
implementation.

**Tier:** C on risky verbs. **Gate:** needs ADB.

## 11. Connection helpers (inside Tools, top-right chip)

- A chip: "Pixel 7 · USB" / "· Wi-Fi" / "· Not allowed". Clicking it opens:
  - **Pair over Wi-Fi** (the existing QR + code UI from DevicePage);
  - **Reconnect**;
  - **Switch device** (with several).
- **Hidden:** "Use Wi-Fi without pairing (old way)" (`adb tcpip 5555`, C, with **Turn off**:
  `adb usb`).
- Note for the owner: Android forgets debug authorisations after 7 days unused unless "Disable adb
  authorization timeout" is on in Developer options. Show this as a one-time tip.

## 12. Files (no new section): improvements to the existing Files page

- An optional **two-pane** view (PC left, phone right), with drag and drop both ways, via
  `PullAsync` / `PushAsync` with progress.
- Listing via `stat -c '%F|%s|%Y|%A|%n' <dir>/*` (stable), not `ls -la`.
- Delete on the phone (D) moves to a PC-side Trash first (pull, then `rm`), so it can be undone.
- After a push into a media folder, rescan:
  `am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file://<path>` (verify on
  14/15; fallback: the Linc app's existing `MediaScannerConnection` scan).

## 13. Never offer (weakens the phone's security, or bricks it)

- Changing package verification or Play Protect (`verifier_verify_adb_installs`,
  `package_verifier_enable`).
- `locksettings` (changing or clearing the lock credential).
- `dpm set-device-owner` / `set-profile-owner`.
- `hidden_api_policy`.
- Granting accessibility, notification-listener, device-admin, `WRITE_SECURE_SETTINGS`,
  install-packages or overlay powers to **other** apps. Linc may grant its own components only, as
  it already does in setup.
- Leaving `adb tcpip` permanently on.
- Removing the protected packages in §3.
- Bootloader unlock flows.
