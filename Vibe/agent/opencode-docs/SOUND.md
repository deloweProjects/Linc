# How sound moves in Linc

Read this before changing anything that plays, records or carries sound. Update it in the same
commit as the code. (The owner, 2026-10-19: "always write down protocols and how they work so you
don't get lost.")

## 1. The PC's sound on the phone — Play everywhere (v26+, channel 6)

- **Who starts it:** the phone (Sound page switch → `audio.everywhere.set`) or the PC's Device
  page. The PC answers `audio.everywhere.state`.
- **PC side:** `PcSpeakerMode.Enable()` makes the **virtual cable** (VB-Cable / Linc's cable) the
  Windows default output, so every app plays into it. `PcAudioEverywhere` reads the cable and
  plays it to each PC output (the person's own speakers are `PcSpeakerMode.OwnOutputId`), delayed
  so every speaker is in step. `PcMirrorService.StartAudioAsync` opens channel 6 to the phone with
  `OpenPhoneDialledChannelAsync` (`channel.open` → the phone dials back over TLS, through
  `adb reverse` on USB / wireless debugging, else the PC's LAN address), writes the config frame
  `{sampleRate, channels, clock:true}`, then `PcAudioStreamer` sends AAC frames stamped on the
  PC's clock.
- **Clock:** the phone sends 16-byte probes ("LCLK" magic, phone time) on the channel's back
  direction (`SyncedAudio.probe`); the PC answers each with a 24-byte config frame (sent, arrived,
  leaving) from `PcAudioStreamer.ClockLoop`. Until a few answers arrive the phone's clock isn't
  ready and the page says "Aligning…".
- **Phone side:** `SocketServer.audioReceive` → `SyncedAudio.receive` decodes AAC and hands
  blocks to one `PhoneOutput` per output (phone speaker, Bluetooth buds…), each played at
  `firstHeard = PC time + group delay`. `PcPlaybackSession` shows "Playing from your PC" in the
  media controls and takes audio focus — **it must never be able to stop the stream** (it threw
  on the channel thread before 2026-10-19 and closed every stream ~100 ms in).
- **Volume while on:** PC volume keys / the phone's PC-volume slider change the person's own
  output (`PcControlService.HeardOutputId`), never the cable (Windows refuses those).
- **Off:** either side; the PC puts the person's own output back as the default
  (`PcSpeakerMode.Disable`, also on a crash via `RestorePending`).

## 1a. The phone's sound everywhere — "Whose sound: This phone's" (v31)

- **Who starts it:** the phone's Sound page, "Whose sound: This phone's", when the PC speaks v31
  (`AudioEverywhere.setMirrorSource` → `audio.everywhere.set {source: "mirror", on: true}`).
  Older PCs: v29/v30 → `pc.btaudio.set` (section 2, not in step); v27/v28 → the recording route.
- **The idea:** the same as section 1, with the phone's sound arriving at the PC like any app's.
  `PcAudioEverywhere.EnableMirrorSourceAsync` turns Play everywhere on (if it isn't), then
  `PhoneMirrorSource` runs scrcpy `--no-video --no-window --no-control --audio-source=playback
  --require-audio --audio-buffer=40`, **without `--audio-dup`**: the phone's media apps go quiet
  on the phone, and scrcpy (SDL, default device) plays them into the **cable**, which is the
  default output while Play everywhere is on. Linc captures the cable as always and plays it on
  every PC speaker and back to the phone on channel 6, all on the PC's clock. Nothing new on the
  wire beyond the `source` value. The PC's own sounds still play too (they go into the cable).
- **No echo:** scrcpy's capture takes `USAGE_MEDIA` players and, without `--audio-dup`, ignores
  an app's capture opt-out. So while `AudioEverywhere.phoneIsSource` is set the phone's
  `PhoneOutput` tracks use `USAGE_GAME` (left alone by the capture), and `SyncedAudio` skips
  `PcPlaybackSession` (its audio focus would pause the app being played). Switching source with a
  stream running reopens the tracks.
- **Needs:** an adb route (USB or wireless debugging) — else `error: needs-adb`, nothing changes,
  and the page offers "Record it instead" (the v27 route); Android 13+ (`needs-android-13`).
  scrcpy ending on its own turns Play everywhere off (`mirror-stopped`).
- **Back to the PC's sound:** `{source: "pc", on: true}` stops scrcpy, keeps the pipeline. Off,
  the phone going away, speaker mode off or Linc closing all stop scrcpy too (`Disable`).
- **Timing knobs, Advanced only:** per-speaker offset −400…+400 ms in 10 ms steps
  (`device.manualMs`, saved per device in `audio-everywhere.json`), Re-sync
  (`{resync: true}`: PC speakers re-align, group delay re-measured; the phone re-learns the
  clock, `SyncedAudio.resync`), Smooth / Tight (`marginMs` 150 / 0). These apply to the PC's own
  sound too.
- **Expected delay:** scrcpy (~40 ms buffer + encode/adb) plus the group delay, about 0.2 s from
  an app pressing play to sound, everything in step.

## 2. The phone's sound on the PC (v29 messages, scrcpy underneath)

- **Messages:** `pc.btaudio.set {on, names}`, `pc.btaudio.get`, `pc.btaudio.state {on,
  supported, device?, error?}` (names kept from v29; no protocol change).
- **PC side:** `PhoneSoundOnPc` runs the bundled scrcpy (3.3.4) with
  `-s <serial> --no-video --no-window --no-control --audio-dup`: the same sound path as Mirror
  screen, no picture, and **the phone keeps playing** (`--audio-dup`, Android 13+; scrcpy uses
  `--audio-source=playback` for it). Needs an adb route (USB or wireless debugging). Without one,
  it falls back to `PcBluetoothAudio` (Windows as the phone's Bluetooth speaker).
- **Errors:** `needs-android-13`, `stopped`, `failed`, plus Bluetooth's (`not-paired`,
  `unsupported`, Windows' status) on the fallback.
- **Retired:** `PhoneSource` (MediaProjection recording, `phone.sound.ask`) stays only for v28
  phones and as the fallback when there is no adb route (section 1a); don't build on it.
- This is the phone's sound on the PC **outside** Play everywhere (the "This phone's sound on the
  PC" switch, the Device page): it keeps `--audio-dup` and is not delayed. Section 1a is the one
  that silences the phone and plays in step. The switch is disabled while 1a runs.

## 3. Mirror screen's sound (scrcpy), and Mirror PC's (1.0.0.18)

- **The owner's rule: one device plays while mirroring; both at once is Play everywhere.**
- **PC mirrors the phone:** Device › Phone screen › **Sound** has two options.
  - *On this PC* (default): `AudioEnabled` on, `MutePhone` on. There is no `--audio-dup`, so the
    phone goes quiet.
  - *On the phone*: `AudioEnabled` off, so scrcpy runs with `--no-audio` and the phone keeps its
    own sound.
  - A saved `MutePhone = false` (from a 1.0.0.18 pre-release) still adds `--audio-dup`, which plays
    on both.
  - The flag is added in `MirrorService.LaunchLocked`, outside `BuildScrcpyArgs`, so the
    saved-settings byte-identity check (`tools/mirrorsettingssim`) is unchanged.
- **Phone mirrors the PC** (PC Remote): the panel's **"Mute the PC while here"** (`mirror_mute_pc`,
  off by default) asks the PC for speaker mode (`pc.speaker.set`, v25) instead of the plain
  `pc.audio.start`. The PC's sound then moves onto the phone through the virtual cable.
  - Leaving PC Remote turns speaker mode off **only if PC Remote turned it on**
    (`MirrorActivity.speakerByRemote`). Speaker mode from the Sound page, or Play everywhere, is
    left alone.
  - Turning the switch off waits for `pc.speaker.state {on:false}` before asking for the plain
    stream. The PC handles `pc.speaker.set` on a background task, so the two requests could
    otherwise cross.
  - With no cable (`error: no-cable`), it falls back to the plain stream (sound on both) and says
    why.

## 4. PC Remote's sound (v20, channel 6 without `clock`)

The PC's sound while PC Remote shows the PC's screen: same channel 6, config without `clock`, played
by `AudioReceiver` as soon as it arrives (lip-sync with the picture, not with other speakers).

## 5. What "Now playing" means

- Phone → PC: `media.state` (topic `media`) from `MediaBridge` — needs notification access; it
  now retries when access is granted (`LincNotificationListener.onListenerConnected`) and when
  the PC subscribes. Linc's own "Playing from your PC" session is ignored.
- PC → phone: `pc.media.state` (Winamp API / Windows media sessions).
