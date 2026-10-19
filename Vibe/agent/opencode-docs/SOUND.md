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
  phones; don't build on it.

## 3. Mirror screen's sound (scrcpy)

`MirrorService` adds `--audio-dup` at launch when audio is on and no source is set, so the phone
keeps its sound while the PC plays it. It's added outside `BuildScrcpyArgs` so the saved-settings
byte-identity check (tools/mirrorsettingssim) is unchanged.

## 4. PC Remote's sound (v20, channel 6 without `clock`)

The PC's sound while PC Remote shows the PC's screen: same channel 6, config without `clock`, played
by `AudioReceiver` as soon as it arrives (lip-sync with the picture, not with other speakers).

## 5. What "Now playing" means

- Phone → PC: `media.state` (topic `media`) from `MediaBridge` — needs notification access; it
  now retries when access is granted (`LincNotificationListener.onListenerConnected`) and when
  the PC subscribes. Linc's own "Playing from your PC" session is ignored.
- PC → phone: `pc.media.state` (Winamp API / Windows media sessions).
