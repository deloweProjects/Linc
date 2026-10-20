# Play everywhere sounds "a little weird" on the phone: causes and the fix plan

**Owner, 2026-10-20:** "it plays but on phone sounds a lil weird, not good quality". They said **no
fixing for now**, so this is the plan for a later session. Reviewed read-only on 2026-10-20. The key
lines were re-checked by hand (marked ✔).

How the path works: `Vibe/agent/opencode-docs/SOUND.md` §1. In one line:
1. The PC captures the virtual cable with WASAPI loopback (usually 48 kHz float).
2. It converts float to 16-bit, then encodes AAC-LC at **128 kb/s** (Windows' encoder, one
   1024-frame block per packet).
3. TLS/TCP carries it on channel 6.
4. On the phone, MediaCodec decodes it to 16-bit, and `AudioSync.kt`'s playout engine places each
   block on the PC's clock and corrects drift.
5. A float AudioTrack plays it at the stream's rate.

Abbreviations below:
- **AS** = `Linc/ANDROID/app/src/main/java/app/linc/android/service/AudioSync.kt`
- **SA** = `.../service/SyncedAudio.kt`
- **PS** = `Linc/DESKTOP/Linc.Desktop/Services/PcAudioStreamer.cs`

## Causes, most likely first

1. **Drift is corrected by dropping or repeating whole samples, up to 0.5 %.** ✔ This is the main
   suspect.
   - `copyWithSlip` (AS ~298) drops or repeats one frame whenever `slip` passes ±1. There is no
     interpolation.
   - The controller allows `MAX_PPM = 5000` with `KP = 0.5` ppm per µs of error (AS ~142-144) ✔.
   - So 2 ms of error means about 1000 ppm, a cut every 1000 samples. 10 ms of error means 5000 ppm,
     a cut about every 4 ms.
   - That sounds grainy or buzzy, especially on sustained tones and vocals. The comment saying "no
     one hears it" holds only around ≤100 ppm.
2. **The error fed to the controller is noisy, so it keeps steering.**
   - The clock fit re-estimates offset and slope on every probe (AS ~62-87). Each estimate can step
     by up to half the round trip (2–10 ms on Wi-Fi), at 5 probes a second for the first 5 s, then
     1 a second (SA ~186).
   - `TrackClock` re-anchors on every Android timestamp, with 5 % smoothing (AS ~349).
   - Error is sampled every 10 ms against a 50 ms time constant, so network jitter turns directly
     into ppm, and therefore into cause 1.
3. **Hard jumps click.**
   - A skip or silence jump fires in three cases:
     - on an underrun (AS ~227);
     - when block timestamps differ by more than `GAP_US = 2 ms` (AS ~283) ✔;
     - when the group delay changes by more than 2 ms (AS ~221).
   - There is only a 5 ms fade-in after the jump and no fade-out before it, so the waveform is cut
     mid-cycle.
4. **The PC pads part-filled blocks with zeros in the middle of a stream.**
   - `Flush()` zero-pads a short block (PS ~158). It runs on a >5 ms timestamp mismatch (PS ~179) or
     whenever the timeline falls 30 ms behind (PS ~200).
   - That can happen during continuous music: cable buffering, `WaitOne(5)` scheduling, or a GC
     pause.
   - The phone then sees a timestamp gap of more than 2 ms and jumps (cause 3). AAC also smears the
     edge between sound and zeros (pre-echo).
5. **Silent drops.**
   - The PC drops the oldest packet once more than 10 are queued (PS ~48, ~237).
   - The phone drops a frame if `dequeueInputBuffer(20 ms)` fails (SA ~113).
   - AAC frames overlap (MDCT), so a lost frame is an artifact, and it is also a gap, which triggers
     cause 3.
   - None of these drops are counted or logged.
6. **Codec.** 128 kb/s from Windows' built-in AAC encoder (PS:50 ✔) is soft on cymbals and stereo
   detail. The Media Foundation encoder only accepts 96, 128, 160 or 192 kb/s.
7. **Float to 16-bit without dither, with hard clipping.**
   - `(short)Math.Clamp((int)(f * 32767f), …)` (PS:588 ✔) truncates and clips anything over 0 dBFS.
   - `PinCable` forces the cable to 100 % (`PcAudioEverywhere.cs` ~384), so loud masters clip.
8. **Track setup (minor).**
   - The AudioTrack runs at the stream's rate (SA ~255), so a 44.1 kHz PC on a 48 kHz phone goes
     through Android's resampler on top of cause 1.
   - There is no `setPerformanceMode`, and no check of `PROPERTY_OUTPUT_SAMPLE_RATE`.
   - Bluetooth outputs re-encode (SBC/AAC/LDAC), which is a third lossy stage.
9. **Phone-as-source mode has extra stages.**
   - The chain is scrcpy (Opus 128k by default) → SDL → cable → AAC 128k → phone: two lossy codecs
     in a row.
   - `--audio-buffer=40` (`PhoneMirrorSource.cs` ~29) is tight over wireless adb. scrcpy fills gaps
     with silence, and those gaps then trigger causes 4 and 3.
10. **`USAGE_GAME`** (SA ~248) can bypass the phone's music effects or EQ. That changes the tone,
    not the quality.

## Fix plan, in order (each step is small and testable on its own)

1. **Prove it first.** Temporarily set `MAX_PPM = 0` in a test build.
   - If the "weird" sound goes away, causes 1 and 2 are confirmed. Drift then builds slowly and is
     only fixed by occasional jumps.
2. **Calm the loop.**
   - Set `KP` to about 0.02–0.05 and `MAX_PPM` to about 300–500.
   - Use a time constant of seconds, with a ±1–2 ms dead-band.
   - Slew the clock fit instead of stepping it (AS ~87).
3. **Real resampling instead of drop/repeat.**
   - Use a variable-ratio cubic (Hermite) or windowed-sinc/polyphase resampler in `copyWithSlip`.
   - Keep hard jumps only for errors over 40 ms.
   - The PC twin (`Services/AudioSync.cs` PlayoutEngine) needs the same change. Keep the two in step,
     since `tools/audiosyncsim` and `AudioSyncTest.kt` test both.
4. **No mid-stream zero padding on the PC.**
   - Raise the quiet-flush threshold (PS ~200) to about 100 ms, or base it on WASAPI padding.
   - Keep block timestamps contiguous.
5. **Clean jumps.**
   - Raise `GAP_US` to 5–10 ms.
   - Crossfade 5–10 ms (fade out, then fade in) on every jump.
   - Ramp group-delay changes through the resampler instead of jumping.
6. **Codec bitrate.**
   - Quick win: set `Bitrate` to 192 000 (PS:50).
   - Bigger win: Opus (libopus on the PC; Android 10+ decodes Opus with MediaCodec) at 128–192 kb/s
     with 10–20 ms frames and PLC/FEC.
   - Alternative: 16-bit PCM over USB/LAN, about 1.5 Mb/s.
7. **Dither and a soft limiter** before 16-bit, or carry float end to end.
8. **Native rate.** Open the track at `PROPERTY_OUTPUT_SAMPLE_RATE` and resample once, in Linc's
   resampler (from step 3).
9. **Phone-as-source.**
   - Use `--audio-codec=raw` (or flac) on USB, or `--audio-bit-rate=256K`.
   - Raise `--audio-buffer` to 80–120 ms; the group delay hides it.

**Don't break:** the owner confirmed PC → phone Play everywhere works and is in step. Every change
above must keep the sync. Run `tools/audiosyncsim` and the Android `AudioSyncTest` in CI after each
step, and ask the owner to A/B by ear.

## How to measure (do this before changing anything)

- **Counters.**
  - Phone: log `skippedFrames`, `underruns`, `silenceFrames`, `droppedBlocks`, decoder input drops,
    and mean/max |ppm| every 30 s. Most of these are already collected (SA ~307, AS ~184) but never
    logged.
  - PC: count queue drops (PS ~239), forced partial flushes (PS ~158/200), and clipped samples
    (PS ~588).
  - Put all of them in `PcAudioEverywhere.LogSummary` (~646) and in the phone's Copy log.
- **Sine test.**
  - Play a 1 kHz and a 10 kHz sine on the PC.
  - Record the phone with AudioPlaybackCapture, or its line-out into the PC.
  - Run an FFT for THD+N and look for sidebands from the drop/repeat pattern. A zero-crossing
    tracker shows the clicks.
  - Compare 44.1 vs 48 kHz on the PC, Wi-Fi vs USB, and Smooth vs Tight.
- **Unit test.** Extend `AudioSyncTest.kt` and `tools/audiosyncsim`.
  - Feed a pure sine through `PlayoutEngine` with a noisy clock (±5 ms steps).
  - Assert a bound on spectral purity and a maximum number of discontinuities.
