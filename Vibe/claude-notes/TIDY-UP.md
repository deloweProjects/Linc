# Code that needs tidying (review of 2026-10-20, read-only)

Much of 1.0.0.12 to 1.0.0.17 was written quickly by parallel helpers without a local compiler. It
builds in CI, and the owner says it works, but these corners should be cleaned before features pile
on top. ✔ = re-checked by hand. Line numbers drift; search for the names.

**Order:** the cheap, safe items first. The big refactors only with the UI test plus the
sims/unit tests green before and after, and **never mixed with feature work**.

## Phone app (Linc/ANDROID/app/src/main/java/app/linc/android/)

| # | What | Where | Fix | Risk |
|---|---|---|---|---|
| A1 | Terminal copies the **whole scrollback every frame**, under the emulator lock, on the UI thread ✔. It also allocates `substring` per run | `ui/TerminalScreen.kt` ~394-418 | Copy only `first until first+rows`; check blanks in place | low |
| A2 | 189 `runCatching` calls ✔, only ~11 of them logging failures. Empty catches swallow errors (that hid the MediaSession Looper bug) | e.g. `SyncedAudio.kt` ~95, `PhoneSource.kt` ~309/316, `PcTools.kt` ~483, `MirrorReceiver.kt` ~223, `AudioEverywhere.kt` ~334 | A `runLogged("what") { }` helper that writes WARN to LogStore | low |
| A3 | Protocol versions as magic literals: ~40 `canSend(N)`, ~45 `requiredVersion = N`, ~60 `negotiated >= N` | everywhere (e.g. `PcTools.kt` ~187/228, `SocketServer.kt` ~629) | Named constants in `protocol/Protocol.kt` (`V_PC_TOOLS = 27`, `V_BTAUDIO = 29`, `V_SYNC = 30`, `V_MIRROR_SOURCE = 31`) | low |
| A4 | The same "setting object" written 4 times; `getSharedPreferences("linc")` open-coded in 17 places | `ui/theme/WallpaperBackground.kt`, `FloatingNav.kt`, `AppFonts.kt`, `service/Explanations.kt` | One `PrefFlow<T>` helper plus a `LincPrefs` accessor. **Keep the keys identical** | low |
| A5 | Clock probe duplicated: `CLOCK_MAGIC` in two places, two probe loops with different framing | `PhoneSource.kt` ~66/298, `SyncedAudio.kt` ~170/194 | A shared `protocol/ClockProbe`. The wire format must match the PC (`PcAudioStreamer` 16/24 bytes) | medium |
| A6 | Channel numbers private to SocketServer, hard-coded in comments elsewhere | `SocketServer.kt` ~821-833 | Move them to `Protocol.kt` | low |
| A7 | `SocketServer.serveControl` is one ~510-line function with a giant `when` | `SocketServer.kt` ~274-783 | A table of message → (minVersion, handler), or per-feature handlers | medium |
| A8 | Global mutable singletons (SyncedAudio ~15 vars, PhoneSource 10, AudioEverywhere 8); scopes never cancelled; `PhoneReach` borrows `AudioEverywhere.appContext` | `PcPlaybackSession.kt` ~40, `ShareStore.kt` ~39, `UpdateCheck.kt` ~36, `ClipboardHistoryStore.kt` ~130 | One app-level scope and context holder | medium |
| A9 | SoundScreen queries AudioManager and prefs **during composition**, so toggles don't redraw | `ui/SoundScreen.kt` ~280; the screen itself is ~300 lines | Expose outputs and per-output settings as state | low-med |
| A10 | Frosted blur hook placed inside AlertDialog's **confirm button slot** to reach the window | `ui/kit/Overlays.kt` ~43, `ui/theme/WallpaperBackground.kt` ~195 | `Dialog` + `DialogWindowProvider`, or a `DisposableEffect` at the content root | low (check on Android 12+) |
| A11 | MirrorActivity is 1039 lines; its dictation code duplicates RemoteScreen's | `MirrorActivity.kt` ~847-895 vs `ui/RemoteScreen.kt` ~636-697 | Extract a `Dictation` class; move the touch maths into a pure, tested helper | medium |
| A12 | Terminal gesture state machine inline, with integer modes 0-3 | `ui/TerminalScreen.kt` ~343-388 | A sealed mode type, extracted and unit-tested like `TrackpadGestures` | low-med |
| A13 | `@OptIn` 45× in 17 files; `@Suppress("UNREACHABLE_CODE")` after `while(true)`; magic numbers (700 KB, 2560 px, 480 px, 20.dp, 10_000L) | `PcTools.kt` ~523-534 etc. | Module-wide `optIn` in `app/build.gradle.kts`; named constants | trivial |

**Not leftovers (don't delete):**
- `PhoneSource` (MediaProjection): still used for v28 `phone.sound.ask` and the "Record it instead"
  fallback.
- `pc.btaudio.*`: still the message names for "phone sound on PC" (v29).

**Tests missing:**
- PcTools payload parsers (v27–v31). The cheapest, highest-value target.
- Sync/SyncRemote (v30).
- PhoneSource / NativeRoute delay maths.
- SocketServer version gating.
- ShareStore.
- The settings objects.

## PC app (Linc/DESKTOP/Linc.Desktop/)

| # | What | Where | Fix | Risk |
|---|---|---|---|---|
| B1 | **PhoneSoundOnPc leaks the scrcpy process handle on Stop** ✔. `Stop` nulls `_process` before `Kill`, so `OnExited` returns early without `Dispose` or `Changed`. It only catches `InvalidOperationException` | `Services/PhoneSoundOnPc.cs` ~114-150 | Dispose on every path, as `PhoneMirrorSource` does; also catch `Win32Exception` | low |
| B2 | **scrcpy and adb can outlive Linc** (crash, `Environment.Exit`). No Job Object; `MirrorService.Dispose` is fire-and-forget | `Services/MirrorService.cs` ~108, `App.xaml.cs` ~476 | A `ChildProcessJob` with `KILL_ON_JOB_CLOSE`, used by MirrorService, PhoneMirrorSource and PhoneSoundOnPc | medium |
| B3 | Events raised while holding a lock (deadlock risk if a subscriber calls back in) | `Services/MirrorService.cs` ~232-280, `PhoneSoundOnPc.cs` ~129 | Record the outcome under the lock, invoke after it | low |
| B4 | `MainWindow.ApplyBackdrop` is a ~165-line method full of closures (mask brush, wallpaper sampling, Reload). Lambdas on singletons are never removed. The taskbar poll (150 ms timer + P/Invoke) lives in the same file | `MainWindow.xaml.cs` ~140-305, ~829-910 | Classes `WallpaperBackdrop` (IDisposable), `TaskbarWatcher`, `SurfaceToning` | medium (visual; uishots) |
| B5 | Surface and overlay toning split three ways: XAML defaults, ThemeSync colours, MainWindow opacities. Writing a local `TintColor` replaces the ThemeResource | `Themes/MaterialExpressive.xaml` ~97-112, `Services/ThemeSyncService.cs` ~451-466, `MainWindow.ToneSurfaces` | One `SurfaceTone` service; use `TryGetValue` for resource lookups | low-med |
| B6 | Settings record with **28 positional fields**; `Persist` passes them positionally (neighbouring `bool?` values can swap silently); non-atomic write that only catches `IOException` | `Services/DeviceRegistry.cs` ~304-353, ~891-899 | DTO class or named arguments; write to a temp file and replace. **Old settings files must still load** (`tools/storesim`) | medium |
| B7 | One `AppearanceChanged` for five settings; four subscribers redo everything (the wallpaper is re-decoded when only the clock style changed) | `DeviceRegistry.cs` ~770-797; `MainWindow.xaml.cs` ~98/301, `Views/AppFonts.cs` ~85, `Views/HomePage.xaml.cs` ~94 | Pass which setting changed | low |
| B8 | Live font re-targeting walks only the window tree (it misses popups and pages not loaded); setting Default pins a local value on every control; global static state | `Views/AppFonts.cs` ~88-125 | Set the resource and reload the page; or keep the walk but clear local values | low |
| B9 | Home layout constants must agree across FitScale and HomePage (1130/0.8 vs 820/700/320/360/48+14); size-change feedback near the thresholds; an orphaned doc comment | `Views/FitScale.cs` ~28-37, `Views/HomePage.xaml.cs` ~331-520 | One `HomeLayoutMetrics`, plus a pure function covered by `tools/homelayoutsim` | medium |
| B10 | 22 `async void` methods (two can crash: `HomeViewModel.SeekTo` ~1889, `PcMediaService.Start` ~31); ~119 unobserved `_ = XAsync()` | many | A `.Forget(log, "what")` extension; try/catch in every `async void` | low |
| B11 | PcAudioEverywhere flags shared across threads without lock or volatile; `MarginMs` read under `_groupLock`, written under `_lock` | `Services/PcAudioEverywhere.cs` ~100, ~160-215, ~541-686 | Document which lock guards what; volatile or Interlocked flags | medium (audio timing) |
| B12 | HomeViewModel (2086 lines) and SyncViewModel duplicate the cache and lane code | `HomeViewModel.cs` ~1236/1329/1430 vs `SyncViewModel.cs` ~485/566/673 | A shared `LaneCacheService`, then split HomeViewModel into child view models | medium |
| B13 | Decide on keeping or deleting: `PcBluetoothAudio` (registered; PhoneSoundOnPc's fallback on Direct-TLS links) and the v27 phone-source path (still reachable). Two pragma suppressions | `App.xaml.cs` ~130, `PcAudioEverywhere.cs` ~180-260 | Owner's call; both still serve old phones or links | low |
| B14 | Tool csprojs list shared source files by hand (LogService in 16, FolderPair in 14, DeviceRegistry and friends in 13). One new dependency breaks 13 tools (it happened with FolderPair.cs) | `Linc/tools/*/*.csproj` | `tools/Shared.Registry.props` imported by those projects, or a `Linc.Core` class library | low |
| B15 | uishots finds things by English text and barely asserts: a missing element only adds a note; fixed sleeps; it hand-copies DeviceRegistry's JSON names | `Linc/tools/uishots/Program.cs` ~136-200 | `AutomationProperties.AutomationId`; fail when a page is missing; use the shared settings type | low |

**Not covered by CI:**
- Mirror encoding, Bluetooth, the hotspot.
- The ConnectionSupervisor state machine, PcAudioEverywhere, the scrcpy services, AppFonts and
  ThemeSync.

`build.yml` runs most tools in `Linc/tools`. It skips probe, desktopsim, blescan, hotspotprobe,
mirrorsim and uishots; uishots runs in `ui-test.yml`.

## Process, not code

- **Make the UI test actually fail.** Today it passes unless the app or the fake phone dies.
  Requiring every page screenshot, and failing on "couldn't find", would have caught the Clipboard
  overflow the first time.
- **Add the uncovered screenshots:**
  - Settings › Appearance on both apps, which the run never scrolls to.
  - PC with no phone, maximised, dark theme, wallpaper off.
  - Phone with the wallpaper background on.
