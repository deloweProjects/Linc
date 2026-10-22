# Linc features (as of 1.0.0.17)

What Linc does, where each thing lives on the phone and the PC, how it works in a line or two, and
which settings change it (with defaults). Written for the owner and for any agent picking up the
project. This guide covers what the apps do; for how things travel on the wire read
[PROTOCOL.md](PROTOCOL.md), and for anything that plays, records or carries sound read
[SOUND.md](SOUND.md).

> This file used to be the milestone checklist (M1–M14, last updated 2026-08-12). That history is
> in git (`git log -p -- Vibe/agent/opencode-docs/FEATURES.md`), and the plan lives in
> `ROADMAP.md` and `CHANGELOG.md`.

**Versions.** 1.0.0.17 ("1.0.0-beta.17", 2026-10-20). Both apps report the same four-part version,
and the protocol is **v31** on both sides. Older peers keep working: each feature appears only when
both sides support it (see PROTOCOL.md).

**The two apps.**
- **Linc for Android** (`Linc/ANDROID`, Kotlin + Jetpack Compose, Material 3 Expressive). Four tabs
  (**Home, Control, Clipboard, Share**) and Settings behind the gear. Android 11+ (wireless
  debugging needs it).
- **Linc for Windows** (`Linc/DESKTOP/Linc.Desktop`, C# WinUI 3). One tab per paired phone, pages
  **Home, Device, Tools, Details, Files, Sync, Clipboard, Share, Logs**, plus Settings. Lives in the
  notification area (tray) when closed.

**No account, no server.** The phone and the PC talk directly, over the USB cable (ADB), wireless
debugging, or Linc's own Direct TLS link on your Wi-Fi. The only thing Linc fetches from the
internet is the update manifest (`Releases/update.json` in this repo) and, when you ask for it,
VB-CABLE's installer from vb-audio.com.

---

## 1. Connecting

| What | Where | How it works | Settings (default) |
|---|---|---|---|
| **First setup** | PC: Add a phone (the `+` by the tabs) · phone: the setup pages on first launch | The PC finds the phone over USB or wireless debugging (QR code or pairing code), installs the bundled phone app if needed (or "Linc is already on my phone — don't install it"), and pins each side's certificate for Direct TLS. Setup explains ADB pairing and ticks off when it worked. | Phone: Settings › Run setup again |
| **Links and handover** | Automatic; PC: Device › Connection shows the link, "Use this link now" pins one | USB is preferred; plugging the cable in switches to it without a drop, unplugging goes back to Wi-Fi. The phone reconnects when it rejoins Wi-Fi, the screen comes on or Linc opens, trying the PC's last address first. The PC also recognises the phone by its Wi-Fi address. | PC: Settings › Connection › Allow USB connections (on), Background connection (on) · phone: Settings › Connection › Background connection (on) |
| **Stay connected in the background** | Phone: Settings › Connection; Home shows a warning card when it matters | Asks Android to exempt Linc from battery optimisation, and opens the phone maker's own auto-start screen (itel, Tecno, Infinix, Xiaomi, Oppo, Vivo). Without it many phones freeze Linc with the screen off and the Wi-Fi link drops. | Off until you allow it |
| **Hotspot quick link** | Phone: Settings › Connection | When the phone shares a hotspot, the PC joins over ADB in about half a second (measured 514 ms), no discovery needed. | See the row's explanation |
| **Tap to connect (NFC)** | Phone: Settings | Writes `linc://tap` to a cheap NFC sticker; tapping the phone on it starts Linc and dials the PC at once. | Optional |
| **Several PCs** | Phone: Connection sheet › PCs | The phone remembers every PC it paired with. Pick one, or "Any of my PCs". Linc is with one PC at a time; another PC is told the phone is busy and by whom. Forget removes a PC. | "Any of my PCs" |
| **Several phones** | PC: one tab per phone; Settings › Devices | Each paired phone has a tab. Closing a tab asks whether to disconnect (stays paired) or remove it; one phone is active at a time. | — |
| **New connection engine (LincLink)** | PC: Settings › Connection › New connection engine · phone: Settings › Connection | One session that survives the route changing (cable out, Wi-Fi in) with nothing lost and nothing shown for a drop under ~12 s; every route raced at once and the winner remembered per network; the PC dials the phone (TCP 46002) as well as the phone dialling the PC; a private UDP beacon (46003) finds the phone on any shared Wi-Fi; "Connected" as soon as anything reaches the phone, with a separate **ADB** chip on PC Home that turns green when ADB is up too; Linc on the phone is started and checked for over ADB until it answers; the phone starts Linc after a restart, an update and when Linc is opened (unless stopped by hand). Protocol v32; off = v31 exactly. Design: `Vibe/agent/design/linclink.md`. | On (test builds) |
| **Pair without a cable (QR)** | PC: Add a phone › Wi-Fi · phone: its camera | The PC shows Linc's own code above the wireless-debugging steps; pointing the phone's camera at it opens Linc, which asks, then pairs over the network — no ADB, no debugging. Works with the PC's firewall closed (the PC can dial the phone). | New engine on |
| **Firewall rule** | PC installer | A checked task lets Linc through Windows Firewall on private networks (Windows asks for permission once); removed on uninstall. Without it the PC still reaches the phone by dialling out. | On |
| **Offline cache** | PC: Home, Sync | The PC keeps the phone's last apps, photos, messages, battery and Wi-Fi (shown greyed while it is away) in a local SQLite store, so pages are useful without the phone. | PC: Settings › Connection › Clear saved phone data |

## 2. Home

**Phone › Home.** The hero card for the PC (connected / connecting, with the Connection sheet
behind it), **Your PC** (the PC's battery and network, volume and brightness sliders, quick
controls, **Mirror PC**), **Now playing** on the PC, shortcuts into Control, the latest clipboard
item, and one-time cards: update available, background warning, setup, and the Telegram channel
(Join / Not now).

**PC › Home.** The phone's wallpaper with a clock (like its lock screen), the frosted phone card
(battery, Wi-Fi, connection notes, **Mirror phone**), **Now playing** on the phone (album art as the
background, play controls), **Quick controls** (Ring / Vibrate / Silent, find phone, and more),
**Apps** (open any phone app in its own window), **Recent photos**, **Clipboard**, **Shared**
(sent / received), **Messages** and **Calls**. Sections can be hidden and swapped ("Sections",
"Swap sides"). Home shrinks to fit smaller windows.

Settings (PC › Settings › Appearance): Clock on Home (on), Clock style (Classic; also Bold,
Stacked, Serif, Minimal), Wallpaper as background (on), Use my phone's colours (on).

## 3. Screens

| What | Where | How it works | Settings (default) |
|---|---|---|---|
| **Mirror phone** (phone screen on the PC) | PC: Home › Mirror phone; Device › Phone screen on this PC | Linc's build of **scrcpy** (bundled, `Linc/SCRCPY`) in a frameless, rounded Linc window. Sound comes too and stays on the phone as well (`--audio-dup`, Android 13+) unless **Silence the phone while mirroring** is on. **Screen shape › Fill this monitor** (1.0.0.18) sets the phone's `wm size` to the primary monitor's resolution in the phone's own orientation (long side capped at 2560, even numbers) and opens the mirror `--fullscreen` at native size, so landscape is exactly the monitor's shape; "phone turned sideways" also sets `accelerometer_rotation 0` / `user_rotation 1`. The values before are saved to `%LOCALAPPDATA%\Linc\mirror-display-restore.json` first and put back when the mirror stops, at Exit/update, or at the next connection (wm size survives a reboot). scrcpy runs AboveNormal priority; its output goes to the log as `scrcpy: ...`. | PC: Device › Phone screen on this PC: Quality, Screen shape (phone's own), Silence the phone (off) on the card; bit rate, crop, audio under Mirror settings; all apply at the next start |
| **Per-app windows** | PC: Home › Apps | Opens one phone app in its own window (scrcpy `--start-app`), with lazy-loaded icons and remembered window size and position. | — |
| **Desktop Mode** | PC: Device › Desktop Mode | The phone's desktop as a second screen on the PC, for apps that support it. | Sound off by default (it costs performance) |
| **Mirror PC** (PC Remote, the PC on the phone) | Phone: Home › Your PC › Mirror PC | The PC's screen as H.264 on the phone, with the PC's pointer drawn on the phone, Touch or Mouse (trackpad) modes, right click, smooth scrolling, the PC's sound, and the keyboard popping up on PC text fields. A status bubble holds the keyboard, Tablet mode, Voice Access and the microphone. A ring and a buzz confirm each click at once. | Phone: Settings › Mirror PC › Picture quality (Balanced; Smooth, Sharp), Pointer (Touch), Keyboard on text fields (on) · PC: Settings › Mirror PC › Use software encoding (off) |
| **Tablet mode** | Phone: the Mirror PC bubble | Makes a PC without a touch screen finger-friendly: the mirrored display's scale goes up two steps and File Explorer gets touch spacing. Put back when you switch it off or leave, and after a crash. | Off |

## 4. Control (phone tab)

Control is a set of full-screen pages; the **Pages** button or the edge handle opens the side panel.

| Page | What it does |
|---|---|
| **Remote** | A full-screen trackpad for the PC (tap to click, two-finger tap for right click, two-finger scroll, tap-then-drag) with Left/Right buttons, and sheets for the keyboard (Ctrl/Alt/Shift/Win, dictation), media and volume, presentations, screen brightness, **Voice Access** and **which microphone the PC hears**. Works without Mirror PC. |
| **Sound** | Play everywhere, the phone's sound on the PC, the phone as the PC's speaker or microphone. See section 6. |
| **PC** | The PC's performance (CPU, memory), running apps, **Open on the PC** (a link or an app), and **Personalise** (set the PC's wallpaper from a phone photo, accent colour, light or dark, transparency effects, "Match phone"). |
| **Files** | Browse the PC's drives from the phone: download to the phone, upload, open on the PC, rename, delete. |
| **Sync** | Messages and Calls lanes, camera photos to the PC, and **Sync folders** (section 7). |
| **Terminal** | A real shell on the PC (Command Prompt, PowerShell, Bash from Git for Windows, or WSL Linux) through a ConPTY. Long lines stay whole (swipe sideways to read them), pinch to zoom. |

**Quick controls** (phone Home › Your PC, and the Remote sheets): lock, sleep, restart, shut down
(the destructive ones are confirmed), volume and the PC's brightness.

## 5. Clipboard

| What | Where | How it works | Settings (default) |
|---|---|---|---|
| **Clipboard sync** | Automatic both ways | Copy on one device, paste on the other. Phone → PC works in the background once the PC has connected over USB or wireless debugging (it grants what that needs); otherwise while Linc is open on the phone (an Android rule). | Phone: Settings › Features shared with your PC › Clipboard sync (on) · PC: Settings › Sync › Clipboard sync (on) |
| **Clipboard pages** | Phone: Clipboard tab · PC: Clipboard page | Search first; a searchable history grouped by day with pin, copy and delete; **Sync now**; sending text to the other side is behind "Send to PC" / "Send to phone". | Keep history (on), History size (500; 100 or 2,000) on both |
| **Sensitive copies** | Both | Passwords and codes that apps mark as sensitive are never saved or sent; a Windows clip excluded from clipboard history is never sent. | Phone: Don't save sensitive copies (on) |

## 6. Sound (details: [SOUND.md](SOUND.md))

| What | Where | How it works | Defaults |
|---|---|---|---|
| **Play everywhere** | Phone: Control › Sound · PC: Device page | The PC's sound on the phone and on every PC speaker at once, in step on the PC's clock. Volume keys work while it's on. "Aligning…" shows until the clocks agree. | Off |
| **Whose sound: This phone** | Phone: Control › Sound › Play everywhere | The phone's own sound plays on the PC's speakers and the phone together, in step (scrcpy audio). Needs USB or wireless debugging and Android 13+; otherwise "Record it instead". | PC's sound |
| **Timing (Advanced)** | Phone: Control › Sound › Timing | Per-speaker offset −400…+400 ms (10 ms steps, saved per device), Re-sync, Smooth / Tight buffer, test clicks. | 0 ms offsets; no extra margin (Tight) |
| **This phone's sound on the PC** | Phone: Control › Sound · PC: Device › Phone sound on this PC › Play here | Uses the mirror's sound path (scrcpy, no picture): no Bluetooth pairing, and the phone keeps playing. Falls back to Windows as a Bluetooth speaker when there is no adb route. | Off |
| **Phone as PC speaker** | Phone: Control › Sound | The PC's sound moves to the phone instead of playing on both; the PC's speakers come back when you stop or disconnect. Uses the virtual cable. | Off |
| **Phone as the PC's microphone** | Phone: Control › Sound and Remote › microphone sheet · PC: Settings › Phone as microphone | Any PC app hears the phone's mic, through VB-CABLE (VB-Audio's free virtual cable, downloaded from vb-audio.com only after you agree, checked against a pinned SHA-256). Automatic in Mirror PC with "Phone" chosen. Your own default microphone is put back after each session, on exit and after a crash. | Off; PC: Set up / Remove |
| **Voice Access** | Phone: Remote › Voice Access, the Mirror PC bubble | Starts Windows Voice Access (Windows 11 22H2+), hearing through the phone's microphone or the PC's. | Phone mic |
| **Mirror screen's sound** | PC: Device › Phone screen on this PC › Sound | One device plays, so there's no echo: **On this PC** (the phone stays quiet; scrcpy without `--audio-dup`) or **On the phone** (`--no-audio`). Both at once is Play everywhere. | On this PC |
| **Mute PC (Mirror PC)** | Phone: Mirror PC › Control panel › PC row, next to Sound | The PC's sound plays only on the phone while you're in Mirror PC (speaker mode, v25, through the virtual cable); the PC's speakers come back when you leave. Tapping it while the sound is off turns the sound on. Without the virtual cable it explains and the sound plays on both. | Off |

## 7. Files, sharing and sync

| What | Where | How it works | Settings (default) |
|---|---|---|---|
| **Files** | PC: Files page | Browse the phone's storage, transfer both ways, delete, rename, new folder, screenshot. Over ADB or the Direct TLS files channel. | Phone: All files access (asked) |
| **Share** | Phone: Share tab · PC: Share page, tray menu, Explorer | Send files, text and links. Each side says whether it sends over Linc (fast, no prompts) or with **Quick Share** when Linc isn't connected, and picks by itself; progress and retry. On the PC: drop files on the Share page, the tray's **Send to phone**, Explorer right-click → **Send to phone (Linc)** / **Send with Quick Share**, or Send to → **Phone (Linc)** / **Quick Share (Linc)**. A link opens on the phone; text lands on its clipboard. | PC: the Explorer entries (on), "Accept from my connected phone without asking", "Name phones see", Visible / Hidden — all on the Share page |
| **Received files** | Phone: Share › Received from your PC · PC: Share › Received | Files from the PC land in `Download/Linc` and pictures show in the Gallery. On the phone: Open folder, long-press to select several, then Send, Share or Delete. | — |
| **Quick Share on the PC** | PC: Share page | Send to and receive from nearby Android devices with Google's Quick Share protocol. | Visible / Hidden |
| **Send to PC from any app** | Phone: Android share sheet (Linc, and the PC as a Direct Share target) | Shares a file or link to the PC; a notification shows sending, then "Sent". | — |
| **Install an app** | PC: Device › Install an app | Installs an APK on the phone from the PC; the phone may ask you to allow it. | — |
| **Sync folders** | Phone: Control › Sync · PC: Sync › Folders | Pick a folder on the PC and one on the phone and keep them the same: both ways (the newer copy wins), PC → phone, or phone → PC. Deletes are never copied; top-level files only; syncs by itself while connected. | None until you add one |
| **Camera photos** | Phone: Control › Sync · PC: Sync › Photos | New camera photos copy to the PC. | Off |

## 8. Phone on the PC

| What | Where | How it works | Settings (default) |
|---|---|---|---|
| **Notifications** | PC: toasts, Home | The phone's notifications on the PC with their actions, inline reply and dismiss. | Phone: notification access (asked) · PC: Settings › Sync › Notification relay (on) |
| **Notification history** | PC: Settings › Notification history | Keeps notification bodies on this PC for 7, 30 or 90 days. | **Off**; 30 days when on |
| **Messages** | PC: Home › Messages, Sync › Messages | Read and reply to texts from the PC (sideload builds; no default-SMS-app needed). | Phone: Messages permission (asked); Sync lane (off) |
| **Calls** | PC: Home › Calls, Sync › Calls | Call log, dial from the PC, decline an incoming call. Call audio stays on the phone. | Phone: Calls permission (asked); Sync lane (off) |
| **Now playing** | PC: Home, Device | The phone's media with play controls (needs notification access). The phone shows the PC's media too. | — |
| **Phone controls** | PC: Device › Phone controls, Home › Quick controls | Find phone (full-volume ring), Do not disturb, screenshot, Ring / Vibrate / Silent, rotation, adaptive brightness and brightness. Some need a grant on the phone; the PC says so and greys the control. | Phone: DND access, Modify system settings (asked) |
| **Wallpaper and colours** | PC: Device › Wallpaper and colours | Set the phone's wallpaper from the PC; the PC follows the phone's Material You colours. | PC: Use my phone's colours (on) |
| **Screen time** | PC: Device › Screen time | Today's screen time on the phone (needs usage access, which the PC can turn on over USB debugging). | — |
| **Details** | PC: Details page | Battery, storage, CPU load, memory, uptime, Wi-Fi signal, screen mirroring and this session. | — |
| **Battery alert** | PC: Windows toast | Once when the phone drops to 15 %, re-armed when it charges or climbs above 20 %. | — |

## 9. Look and feel

- **Phone › Settings › Appearance:** Show explanations (on), Wallpaper background (off; frosted,
  colour-tinted sheets and menus over the phone's wallpaper), Floating navigation bar (off), Font
  (default; Manrope, DM Sans, Outfit, Playfair Display). Follows Material You, light and dark,
  large text and predictive back.
- **PC › Settings › Appearance:** Navigation (Floating bar; or Side rail), Font (Default = Segoe
  UI; Inter, Plus Jakarta Sans, Manrope, Outfit), Wallpaper as background (on), Clock on Home (on),
  Clock style (Classic), Use my phone's colours (on), Show explanations (on). The floating bar only
  rises while the Windows taskbar is showing.

## 10. App life

| What | Where | Settings (default) |
|---|---|---|
| **Updates** | Both: Settings › Updates. Both apps read `Releases/update.json`; an optional update can be skipped, `minimumSupported` forces one; a download whose SHA-256 doesn't match is refused. The phone shows progress and hands the APK to Android's installer. | Keep Linc up to date (on) |
| **Installer** | `Linc-Setup-<version>.exe` (Inno Setup, no admin). Uninstalling puts back the microphone and display scale, removes the Explorer menus and autostart, and asks whether to delete Linc's data. VB-CABLE stays installed. | — |
| **Start on sign-in** | PC: Settings › Startup › Start Linc when I sign in (opens quietly in the tray). | Off |
| **Tray** | Closing the window keeps Linc in the tray with a Send to phone menu; the icon shows when the phone isn't connected. A new Linc takes over from an older copy still running. | — |
| **Logs** | Phone: Settings › Help & diagnostics › Logs / Copy log · PC: Logs page (Copy log, Open log folder, Clear) | Logs hold connection and sync events, never clipboard text or message contents. **Copy log** makes a plain-text report to paste to whoever is helping (the phone's says why Android ended Linc recently). Both start with the line "Linc <version> by deloweProjects — https://github.com/deloweProjects/Linc". |
| **ADB tools** | PC: Settings › ADB status (devices, port forwards, run a shell command, restart the ADB server) | — |
| **About** | Both: Settings › About: version, developer, Telegram channel (t.me/LincApp), source on GitHub, credits and licences. | — |

---

## 11. Tools (PC page; ADB with a face)

Works on any phone adb can see over USB or wireless debugging, **even when the Linc app isn't
running on the phone** (it finds phones straight from adb; Linc's own connection is untouched).
Every action shows the command it ran in a small caption (click copies it). Steps that change the
phone ask first; ones that can't be undone get a red button that wakes after a moment. Code:
`Services/Tools` (one command layer, `IAdbShell`; pure parsers in `Pure/`, tested by
`tools/toolsim`), `ViewModels/Tools`, `Views/ToolsPage.xaml` + `Views/Tools/*Section.xaml`. Design
and spec: `Vibe/claude-notes/linc-tools/`.

| Section | What it does |
|---|---|
| **Type** | Type on the phone from the PC keyboard (a line at a time, or Live keys: each key straight away); Wake, Unlock swipe, Home, Back, Recents, arrow pad; ⋯ media keys, Sleep, Notifications, Paste. Typed text is never logged. Characters `input text` can't send (é, emoji) need the Linc app. |
| **Rescue** | Screen broken or black: 1 See it (mirror with the phone's screen off), 2 Get in (wake, swipe, type your PIN; never saved), 3 Save your stuff (DCIM, Pictures, Download, Documents, Movies, Music into Documents\Linc Backup\<model> <date>, skipping files already copied). Needs USB debugging allowed before the screen died. |
| **Apps** | Search and filter (Yours · System · Turned off); open, stop, uninstall, remove a system app for this user (with Put back), turn off/on, clear data, save the APK, runtime permissions. Core apps and Linc are protected. |
| **Capture** | Screenshot to Pictures\Linc\Screenshots (Open · Copy · Show in folder); Record up to 3 min to Videos\Linc; More: clean status bar, show touches, quality. |
| **Health** | Battery (level, charging, watts, temperature), phone temperature, storage, live CPU and memory graphs, busy apps with Stop, Phone details (Copy all). Refreshes every 3 s while open. |
| **System** | Dark mode, Stay awake, Animations, Screen timeout, Brightness, Text size; More: Wi-Fi, mobile data, Bluetooth, airplane, location, battery saver, night light (Wi-Fi off / airplane on ask first over wireless); Restart, Recovery, Power off. |
| **Commands** | Search 331 everyday adb commands by what they do ("install app", "screen size", "wifi"), fill in the parameters, see the exact command, then Run or Copy. Values are quoted or checked; deletes only inside the phone's storage; nothing that weakens the phone's security. |
| **Console** (Advanced) | Type adb shell commands yourself: history, Copy output, a warning and one confirm for risky commands. |

## Provenance (who made Linc, and how anyone can check)

Linc is made by **deloweProjects** (<https://github.com/deloweProjects/Linc>), released under the
MIT License (`LICENSE`), with third-party credits in `NOTICE`. To make the authorship easy to see
and hard to strip by accident, both apps carry the same small, open set of constants:

- Phone: `Linc/ANDROID/app/src/main/java/app/linc/android/Provenance.kt` (`object Provenance`)
- PC: `Linc/DESKTOP/Linc.Desktop/Provenance.cs` (`static class Provenance`)

| Constant | Value |
|---|---|
| Product | `Linc` |
| Author | `deloweProjects` |
| Source | `https://github.com/deloweProjects/Linc` |
| Copyright | `© 2026 deloweProjects` |
| Statement | `Linc by deloweProjects — https://github.com/deloweProjects/Linc` |
| Fingerprint | `93b660d11f6717cfe67f72a92881210977685b4954a673cadf67e2e11358f281` |

**Where it shows.** The first log line of every run and the header of **Copy log** on both apps
("Linc 1.0.0.17 by deloweProjects — https://github.com/deloweProjects/Linc (© 2026
deloweProjects)"); an optional `author: "deloweProjects"` field in both `hello` messages of the
handshake (PROTOCOL.md › Handshake; older peers ignore it); the Windows exe's file properties
(Company, Authors, Copyright in `Linc.Desktop.csproj`) and the installer's publisher; the Android
manifest (`<meta-data android:name="app.linc.author" android:value="deloweProjects"/>`) and
`BuildConfig.AUTHOR`.

**What it is not.** It is not hidden, encrypted or obfuscated, it phones nowhere, tracks nothing,
checks nothing and switches nothing off. Changing the values changes nothing about how Linc works.
It is a signature, like the copyright line in a book: a fork that keeps it credits the author, and
one that strips it has visibly removed it.

**How to verify the fingerprint.** It is the SHA-256 of the UTF-8 statement, with no newline:

```sh
printf '%s' 'Linc by deloweProjects — https://github.com/deloweProjects/Linc' | sha256sum
# 93b660d11f6717cfe67f72a92881210977685b4954a673cadf67e2e11358f281
```

```powershell
# PowerShell 7
[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('Linc by deloweProjects — https://github.com/deloweProjects/Linc'))).ToLower()
```

The phone's unit test `ProvenanceTest` checks the same thing, and that `BuildConfig.AUTHOR`
matches. To see it in a build: open Logs and look at the first line, check the exe's Properties ›
Details, or run `aapt dump xmltree <apk> AndroidManifest.xml | grep -A1 app.linc.author`.

Keep the two Provenance files identical, and don't change the statement: the fingerprint is only
useful if it stays the same.
