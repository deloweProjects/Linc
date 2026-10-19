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

## Still pending, not a concern

- Sync folder pairs (v30) + Mirror PC in the Your PC card: done, CI green at `ad283a1`, **not sent
  to Telegram** — the owner said wait.
