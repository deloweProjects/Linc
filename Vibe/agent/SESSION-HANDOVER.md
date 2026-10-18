# Session handover — 2026-10-18 (Play everywhere, PC tools, Device page)

Paste everything below the line into a new Claude Code session on `deloweProjects/Linc`.

---

You are continuing work on **Linc** (Android phone ↔ Windows PC companion; Kotlin/Compose app in
`Linc/ANDROID`, WinUI 3 / .NET 8 desktop in `Linc/DESKTOP/Linc.Desktop`). Read `CLAUDE.md` and
`Vibe/START-HERE.md` first, then `Vibe/agent/opencode-docs/PROTOCOL.md` (sections v26 and v27) and
`Vibe/agent/opencode-docs/DECISIONS.md` (D-096, D-097). The owner talks directly to you, informally
and briefly; they want features finished, built, and sent to Telegram for testing.

## Where things stand

- **Branch:** all work is on `ccr-2424cb9c-c1cvcg` (11 commits ahead of `main`, not merged, no PR,
  no release). Protocol is **v27** on both sides. Version numbers unchanged (1.0.0.16, versionCode 17).
- **On the owner's devices now:** test build `test-build/everywhere-tools-1` (commit `46ca963`),
  APK + desktop zip posted to Telegram. They are testing it. Expect bug reports and logs next.
- **Last commit** `e4c50e1` (a small perf fix in `PcToolsService`) is NOT in that test build.

## What this branch added (newest first)

1. **Desktop Device page redesign** (`Views/DevicePage.xaml`): hero card + stat tiles, phone controls
   (media, find, DND, screenshot, ring/vibrate/silent, display), mirror + Desktop Mode, install app,
   connection. Binds Home's commands via `Home` (HomeViewModel). New theme tokens in
   `Themes/MaterialExpressive.xaml` (HeroCard, StatTile, SectionLabel, StatValue, IconTonalButton,
   QuietButton, ActionTile). Settings → Show explanations now honoured on Device/Home/Sync/Details/
   Settings via `views:Explain.IsHelp="True"`.
2. **Phone PC tools** (`PcToolActivity` hosts Sound, PC, Terminal, Files; shortcuts row on Tools):
   - PC look/info/apps/open: `ui/PcScreen.kt`, desktop `Services/PcToolsService.cs` (`PcLook`, `PcInfo`).
   - Terminal: ConPTY in `Services/PcTerminal.cs` (channel 9), phone VT screen
     `service/TerminalEmulator.kt` (+ `TerminalEmulatorTest`), UI `ui/TerminalScreen.kt`.
   - Files: `PcFilesRemote` (desktop), transfers on channel 10, phone `service/PcTools.kt`,
     UI `ui/PcFilesScreen.kt` (downloads go to Download/Linc via MediaStore).
3. **Play everywhere** (PC's sound on PC speakers + PC Bluetooth + phone outputs, in step):
   - Core algorithm (pure, tested): desktop `Services/AudioSync.cs` ↔ phone `service/AudioSync.kt`
     (PlayoutController/PlayoutEngine identical; ClockEstimator, TrackClock, VideoLatency,
     ChirpDetector incl. `findPair`, CalibrationMath, NativeRoute). Pinned by
     `tools/audiosyncsim` (run it!) and `AudioSyncTest`.
   - Desktop: `PcAudioEverywhere.cs` (orchestrator, settings `audio-everywhere.json`, group delay,
     calibration, cable pinned to 100% volume, PC volume = endpoint volume, phone-source mode on
     channel 8), `PcRenderSink.cs` (WASAPI render + endpoint volume), `PcAudioStreamer.cs` (QPC
     timestamps, PCM tap, clock probes on channel 6), wiring in `PcMirrorService.cs`.
   - Phone: `SyncedAudio.kt` (clocked receive, PhoneOutput per output, `PlayoutTimeline`),
     `AudioEverywhere.kt` (mixer state, reports, mic calibration), `PhoneSource.kt` (phone as the
     source: AudioPlaybackCapture + `CaptureConsentActivity`, FGS type mediaProjection),
     `PcPlaybackSession.kt` ("Playing from your PC" media session + audio focus), UI `ui/SoundScreen.kt`.
   - PC Remote lip-sync: `pc.mirror.stats.epochUs` → `MirrorSession.videoLatency` →
     `AudioEverywhere.targetDelayUs`.
4. Smaller: 100 MB share limit removed; PC Remote Pointer buttons reflect the mode; click ring in
   Touch mode; `.github/workflows/test-build.yml` (test builds to Telegram, no release).

## How to build and verify in a cloud session (none of this is preinstalled)

```bash
S=<your scratchpad>
# Android SDK (≈2 min): commandlinetools from dl.google.com, then
#   sdkmanager --sdk_root=$S/sdk "platforms;android-35" "build-tools;35.0.0" "platform-tools"
export ANDROID_HOME=$S/sdk
cd Linc/ANDROID && bash ./gradlew --no-daemon -q assembleDebug testDebugUnitTest   # gradlew has no +x; 233 tests
# .NET: curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --install-dir $S/dotnet
#   (9.0 needed: the MVVM Toolkit's partial-property generator; 8.0 for the net8 harnesses)
cd Linc/tools/audiosyncsim && $S/dotnet/dotnet run -c Release    # must print ALL PASS
```
- The WinUI app can't build on Linux (XAML compiler is Windows-only). To type-check desktop C#, make a
  scratch project that `<Compile Include>`s `Services/**`, `Protocol/**`, `ViewModels/**`,
  `QuickShare/**` (+ the `Protobuf` item and Google.Protobuf/Grpc.Tools packages), with
  `EnableWindowsTargeting`, `TargetFramework net8.0-windows10.0.19041.0`, `RuntimeIdentifier win-x64`,
  `WindowsSdkPackageVersion 10.0.19041.56`, the csproj's PackageReferences (minus H.NotifyIcon), and
  a stub `static class App { Services; MainWindowInstance }`; run `dotnet msbuild -t:Compile`.
- XAML is only validated by Windows CI: every push runs `.github/workflows/build.yml`.
- `tools/mirrorsim` compiles `PcAudioStreamer.cs`/`PcMirrorSource.cs`, so it now also includes
  `AudioSync.cs`; keep that in mind when moving types.

## Sending a test build to Telegram

The git proxy only lets you push your session branch (tags and other branches are refused). So:
commit + push the session branch, then create a branch named `test-build/<name>` from it with the
GitHub MCP `create_branch` tool. `test-build.yml` builds the APK (signed with the owner's key from
`ANDROID_DEBUG_KEYSTORE`, checked against SHA-256 `8182d316…d969`), publishes the self-contained
desktop zip, and posts both to the channel. Check the run with `actions_list` / `get_job_logs`
(look for `sent dist/…`). Never use `release.yml` for tests: from a non-main branch `release.ps1`
merges to main and `update.json` offers the build to every installed copy.

## Not verified on hardware (watch for these in the owner's reports)

- WASAPI rendering, endpoint volume, cable pinning, Bluetooth on Windows; per-device hidden latency.
- `setPreferredDevice` for two simultaneous phone outputs (some phones refuse; status "rerouted").
- Mic calibration (PC mode and phone-source two-peak mode); AudioRecord timestamps.
- Phone-source: playback capture behaviour across apps; PC can only keep up when the phone's own
  output is the slow one (Bluetooth).
- ConPTY terminal (startup, resize, exit), file up/download over channel 10, wallpaper/accent
  registry writes, SHFileOperation Recycle Bin delete.
- The new Device page layout (built in CI, never seen).

## Owner preferences learned

- Wants "send to telegram" for every testable change; wants logs to be useful (all features log).
- Wants a simple surface with advanced knobs tucked away; Material 3 Expressive look on both apps.
- Be honest about what was and wasn't tested; ask only when a choice is genuinely theirs.
