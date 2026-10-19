# Owner concerns — 2026-10-19 (go through these first, in this order)

The owner's words, then what is known and what to do. Do not start new features before these.

## The rule behind all of it

> "Why are you complicating stuff … always write down protocols and how they work so you don't get lost."

- **Prefer the simple path that already exists** over a new subsystem. Before building something,
  check whether the app already moves that data (mirror already carries phone audio; the first
  Play everywhere build already played PC sound on the phone).
- **Write down how each feature works on the wire and in the code before changing it**, and update
  it in the same commit: `Vibe/agent/opencode-docs/PROTOCOL.md` for messages, and a "how it works"
  section per feature (sources, channels, who starts what, which files) — start with sound (below).
- When something that worked breaks, **diff against the build that worked** before adding code.

## 1. PC → phone sound (Play everywhere) worked, now it doesn't

> "at first you wrote a code that actually played the sound from PC to phone now it's all broken"

- **Worked:** test build `test-build/play-everywhere-1` = commit `b28d636` (posted 2026-10-18 14:57 UTC).
- **Changed since** (sound path), oldest first:
  - `9f39fb9` Play everywhere: lip-sync in PC Remote, no scratch on other apps' sounds, real volumes
  - `376df9d` Play everywhere from the phone; a simpler Media sheet and a Sound page
  - `6c16170` WIP: Tools rail, Sound/PC/Files/Terminal redesign, v28, …
  - `c03b21f` diagnostics only (logs where the PC's sound channel breaks)
- Files: desktop `Services/PcAudioEverywhere.cs` (+177 lines since), `PcAudioStreamer.cs`,
  `AudioSync.cs`; phone `service/AudioEverywhere.kt`, `SyncedAudio.kt` (+69), `PhoneSource.kt` (new),
  `ui/SoundScreen.kt` (new).
- Known symptom from the owner's logs: the PC→phone channel aborts about 1 s in.
- **Do:** `git diff b28d636 HEAD` on those files; find what broke the phone playing the PC's
  sound; restore the working behaviour first (revert the breaking piece if needed), keep the
  diagnostics. Verify with the UI test's fake PC if it can stream, else ask the owner for logs.

## 2. Phone → PC sound: use the mirror's audio, not Bluetooth or recording

> "Bluetooth to play sound from phone on pc why bro mirror phone screen adds sound to mirror just
> take sound from there and align it you can try slow on phone so it aligns with pc"

- Mirror (scrcpy, `Services/MirrorService.cs`) **already forwards phone audio** to the PC by
  default (Android 11+, `AudioEnabled = true`; no extra prompt beyond scrcpy's own).
- So "phone sound on the PC" = start scrcpy **audio only** (`--no-video` / `--no-window`, keep
  audio) instead of the v29 Bluetooth sink (`PcBluetoothAudio.cs`, `pc.btaudio.*`) or the
  MediaProjection recording route (`PhoneSource.kt`, `phone.sound.ask`).
- Alignment: the PC copy lags. Delay the phone's own playback to match ("slow on phone"), using the
  Align/latency machinery Play everywhere already has, or let scrcpy's `--audio-buffer` set the PC side.
- **Do:** check scrcpy flags in the bundled version (`--no-video`, `--no-video-playback`,
  `--audio-buffer`, `--audio-source=output`), build "Play on PC" on that, and then decide with the
  owner whether to remove the Bluetooth and recording routes (don't delete before asking).

## 3. PC Clipboard page loads slower

> "Clipboard page on pc loads slower why"

- Not investigated yet. Suspects: `ViewModels/ClipboardViewModel.cs` building all groups/items on
  the UI thread when the page is created; image thumbnails decoded synchronously; the
  `ItemContainerTransitions` in `Views/ClipboardPage.xaml` animating every row on first load.
- **Do:** time page construction (log ms in the ctor and Loaded), load history off the UI thread,
  show the first screenful then the rest, and drop the per-row entrance transition on first load.
  Compare with the UI test's `04-page-clipboard.png` timing.

## 4. Phone Terminal: long output breaks up; make it read like Termux

> (screenshot of `tree` in cmd, lines split across rows) "this screen should make sure it doesn't
> break like this n user can see properly like how termux does"

- How it works now (v27, channel 9): `ui/TerminalScreen.kt` measures cols/rows from the screen and
  the cell size, opens with `PcTools.openTerminal(shell, cols, rows)` and sends a resize (frame
  type 2, two big-endian int16) when they change; desktop `Services/PcTerminal.cs` runs a real
  ConPTY at that size (`CreatePseudoConsole` / `ResizePseudoConsole`); the phone parses the
  output in `service/TerminalEmulator.kt`.
- So the PC already wraps at the phone's width; what breaks is the rest:
  - **No reflow on resize**: `TerminalEmulator.Line.resize` only cuts or pads, so lines drawn
    before the keyboard or rotation changed the size stay broken. Termux re-wraps (reflows)
    wrapped lines. Track "this row continues the next" (soft wrap) and reflow scrollback.
  - **Keyboard open/close shouldn't change cols**: only rows; check nothing else nudges width.
  - **Too few columns for real output** (~40 on a phone): add pinch-to-zoom font size like Termux,
    which changes cols and resizes the ConPTY; remember the size.
  - Check box-drawing and wide (CJK/emoji) characters take the right number of cells.
- **Do:** reproduce with the UI test's fake PC (send a `tree`-like long-line output on channel 9),
  then fix reflow, then pinch-zoom. Write the channel-9 frame format into PROTOCOL.md if missing.

## 5. Pictures sent from the PC don't show in the phone's Gallery

> "Pics shared from PC to phone don't reflect in phone gallery"

- How it works now: desktop `Services/ShareService.cs` pushes the file with
  `files.PushAsync(localPath, "/sdcard/Download/Linc", …)` (adb push, or the TLS files channel
  served by phone `service/FileChannelServer.kt`). Nothing tells Android's media index about the
  new file, so Gallery/Photos never see it until some later rescan.
- **Do:** after a file lands, have the phone index it: `MediaScannerConnection.scanFile` on the
  written path (in `FileChannelServer` for the TLS route; for adb, the PC sends a small message,
  or `share.item` already announces it, and the phone scans on receipt). Better still for images
  and videos: write them through MediaStore into `Pictures/Linc` / `Movies/Linc` (as
  `PcTools.kt` already does for Downloads with `RELATIVE_PATH` + `IS_PENDING`), so they appear at
  once in their own album. Same check for Control → Files downloads and for Sync's pulled files.

## 6. Phone Share page: open the received folder, select several, send

> "In shared add jump to received folder on phone n allow multi select n send"

- How it works now: `ui/ShareScreen.kt`, section "Received from your PC", lists the newest 20 of
  `ShareStore.received` (files in `Download/Linc`); each row opens one file or shares one file
  (`open()`, `shareOn()`). No way to open the folder, nothing selectable.
- **Do:**
  - A "Open folder" action on that header: open `Download/Linc` in the phone's Files app
    (`DocumentsContract` folder URI via `ACTION_VIEW`, fall back to the system Files app on
    Downloads); also "Show all" past 20.
  - Long-press a row to start selecting (checkboxes, a select-all, a count in the bar), like
    Gallery.
  - With a selection: **Send to PC** (the existing Linc share path, several files at once),
    **Share** (one `ACTION_SEND_MULTIPLE` with all the URIs, so Quick Share / any app), and
    **Delete**.
  - Ask the owner if "send" means back to the PC, to another app, or both — default to both.

## 7. PC app look and behaviour (owner's 8 screenshots, maximised 1920×1080, light theme)

The owner asked for (their words, then the ask):
- "changed to dark n bro the blur to strong" — the backdrop blur is far too strong (and check
  whether the theme switches to dark by itself; ask if unclear).
- **Maximised background**: the phone's portrait wallpaper in the centre, a *light* blur, and
  ambient colours from the wallpaper (its dynamic palette) filling the sides, semi-colourful,
  toned for light or dark theme. Not a flat wash.
- **Now playing**: the phone plays music and the PC Device page says "Nothing playing". Wait for
  the owner's logs; check `media.state` reaches the PC and the Device page's binding.
- **Floating dock**: the band of page cut off above the dock is far too tall (pages stop ~100 px
  above it). Shrink it to the dock's height plus a small gap. Make the dock aware of the Windows
  taskbar: when an auto-hide taskbar shows (or the window is under it), lift the dock so nothing
  is covered (`SHAppBarMessage(ABM_GETTASKBARPOS / ABM_GETSTATE)` + the window's work area).
- **Device page red error stays too long**: "Linc can't reach the companion app…" was still shown
  while connected 5m32s. Clear it on the next good message/reconnect, or time it out.
- **Home stage** (clock + wallpaper) a bit bigger.
- **Music widget always on Home**, even when nothing plays (fills the empty space; shows the last
  track or "Nothing playing" with play controls).

Also seen in the same screenshots (fix with the above):
- **Pages don't line up**: Settings and Details hug the left, Share is centred, Clipboard and
  Device are pushed right with an empty ~380–700 px column on the left. One layout rule for all
  pages (centred column with a max width, or full width), checked at 1920 and 1280.
- **Home with no phone**: a plain white panel fills the page (no backdrop) and the "Pair a phone"
  button's text is dark on black — unreadable.
- **Add a phone**: plain white, no app backdrop, card stuck to the left, no dock.
- **Device with no phone**: "Link your phone" card floats at the top right over an empty page.
- **Details**: two small cards on the left, values "—" with no hint why; the rest empty.
- **Settings**: cards stop at half width with the switches mid-card; "Notification history" has
  a switch with no label.
- Add these states to the UI test (no phone; maximised 1920×1080; Add a phone; dark theme) so
  they're checked before every build.

## 8. Phone Control page: thinner side panel, and a button that says "there's a panel"

> "Sidepanel of control page is too large thin it, then 3 dot doesn't let user know therez a side
> panel replace with something that'll make user know"

- How it works now: `ui/ToolsScreen.kt` — the panel slides in from the right at
  `RAIL_EXPANDED = 240.dp` (collapsed 80.dp); it opens from the ⋮ (`MoreVert`, "Control menu")
  that `LincScaffold` (`ui/kit/Kit.kt`) puts last in the top bar.
- **Do:**
  - Thinner panel: about 180–200 dp expanded (icon + label), less padding per row.
  - Replace the ⋮ with something that reads as "pages": the current page's icon + name +
    a ▾/❯ as a pill in the top bar ("🔊 Sound ▾"), or a panel icon (`menu_open` / "view sidebar")
    on the right edge. Also a small grab handle on the right screen edge, and swipe-from-right to
    open. Keep the UI test's "Control menu" label (android.py taps it) or update the test.

## 9. PC Home: no red bar; phone app gets an optional frosted-wallpaper background

> "I said remove that red bar and put that error showing whatever on the section with mirror
> phone, also phone app too dark or white to me, add blurred frosted wallpaper as its background
> but as a setting that is normally off"

- **The owner already asked for this once** — the full-width red banner ("Lost the connection to
  your phone…") across the top of PC Home must go. Every link message (lost, connecting, can't
  reach the companion app, last synced at …) shows inside the frosted phone card, next to
  "Connecting…" and the Mirror phone button, with an icon and a short line; the details go in a
  tooltip / the connection details. Find every place that sets that banner (Home and Device) and
  route it to the card. Same for the Device page's red line (item 7).
- **Phone app background**: a Settings switch, **off by default**, "Wallpaper background": the
  phone's own wallpaper, blurred and frosted (scrim toned for light/dark), behind every screen;
  cards become slightly translucent over it. Off = today's plain surface. Needs the wallpaper
  read on the phone (`WallpaperProvider.kt` already reads it for the PC) and `RenderEffect` blur
  (Android 12+; on older, a pre-blurred downscaled bitmap).

Also in that screenshot (PC Home, disconnected):
- Clipboard rows show mojibake: "Linc PC log â€" 2026-…" — an em dash decoded as Windows-1252
  somewhere (phone → PC clipboard or the history store). Find it and read/write UTF-8.
- The Shared tab says "Nothing shared from your phone yet" right under "Sent 9 of 9 to your
  phone" — the empty text should be about what was received, and say both directions plainly.
- The ⋯ button floats alone above the quick-controls card; a big empty area under Clipboard.
- The phone card shows "—" for battery and Wi-Fi while offline; show the last known values greyed.

## 10. PC Ring / Vibrate / Silent: all three end up selected

> (Device page screenshot, dark) "Look there on ring vibrate etc I managed to click everything"

- Cause: they are three `ToggleButton`s (`Views/DevicePage.xaml` ~line 315, same on
  `Views/HomePage.xaml` ~245) with `IsChecked="{x:Bind Home.IsSoundX, Mode=OneWay}"`. A
  ToggleButton checks itself on click; when the phone refuses (here: no Do Not Disturb access)
  `SoundMode` (`ViewModels/HomeViewModel.cs` ~1650) never changes, nothing re-notifies, and every
  clicked one stays checked.
- **Do:** make it a real single choice — a segmented control of RadioButtons (one group), or
  keep the buttons but always re-raise `IsSoundRing/Vibrate/Silent` after `SetSoundAsync`
  (success or failure) so they snap back to the phone's real mode. Disable the three (with the
  "needs Do Not Disturb access" line and a "Fix on phone" action) while the phone lacks access.
  Check every other ToggleButton group in the app for the same pattern (Home quick controls).

## Still pending, not a concern

- Sync folder pairs (v30) + Mirror PC in the Your PC card: done, CI green at `ad283a1`, **not sent
  to Telegram** — the owner said wait.
