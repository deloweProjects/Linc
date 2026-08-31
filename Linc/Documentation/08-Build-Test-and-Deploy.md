# Build, Test & Deploy

## Building the desktop app (`DESKTOP/`)

Requires Visual Studio with the WinUI / Windows App SDK workload.

**Plain `dotnet build` cannot build a WinUI app** — it lacks the PRI resource-packaging tasks that
ship only with Visual Studio. Build from the IDE (open `Linc.Desktop.sln`, set platform to
**x64**, F5), or from the command line with **VS MSBuild**:

```powershell
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -find MSBuild\**\Bin\MSBuild.exe
& $msbuild DESKTOP\Linc.Desktop\Linc.Desktop.csproj -restore -p:Configuration=Debug -p:Platform=x64
```

- **Kill any running `Linc.Desktop` process first**, or the build fails copying a locked exe
  (MSB3027).
- MVVM Toolkit partial properties require `<LangVersion>preview</LangVersion>`; partial properties
  can't have initializers — set defaults in constructors.

## Building the Android app (`ANDROID/`)

Requires Android Studio (or JDK 17 + Android SDK, compileSdk 35). Point `JAVA_HOME` at Android
Studio's bundled JBR, then:

```
cd ANDROID
gradlew.bat assembleDebug testDebugUnitTest --no-daemon
```

- The heap is pinned to 1 GB in `gradle.properties` for the dev machine's memory limit; a JVM
  error 1455 means "close Chrome / free memory," not a code problem.
- Android 11+ is required (wireless debugging with in-band pairing).

## The verification harnesses (`tools/`)

Self-verification is standing policy (D-031): exhaust everything a machine can check before
handing anything to a human. The harnesses that do it are **committed** (D-036) rather than living
in a scratchpad — earlier they kept getting lost, so every milestone rebuilt them. They are all
plain `net8.0` consoles, so they build with the ordinary `dotnet` CLI (the VS-MSBuild rule applies
only to the WinUI app):

| Harness | What it proves | Run |
|---|---|---|
| **`tools/probe`** | A desktop-emulating ADB probe that forwards `localabstract:linc` exactly as the desktop does and exercises every protocol lane. **Never** probes `sms.send` / `call.dial` (they'd text or ring a real contact). | `dotnet run --project tools/probe -- --write` |
| **`tools/syncsim`** | Compiles the **real** `SyncEngine.cs` against fakes and runs the folder/photo sync scenarios under **both** ADB and Direct-TLS semantics; backs up the real `sync-state.json`. | `dotnet run --project tools/syncsim` |
| **`tools/filesim`** | Compiles the **real** `TlsFileService.cs` (+ `FileServiceRouter`) and the real `Framing` codec against fakes: drives the Direct-TLS files channel over a loopback server speaking the real protocol (list/pull/push, the `not-granted` message, NeedsAdb guidance) and asserts the router's transport selection by `HasAdb` (the M1 Files-page bug). The ADB-sync transport itself needs a device, so that stays covered by `tools/probe` + on-device checks. | `dotnet run --project tools/filesim` |
| **`tools/devicesim`** | Links the **real** `DeviceRegistry.cs` and asserts the multi-device semantics — per-device settings not bleeding, restart persistence, pre-M03 migration, switch-event rules. Backs up and briefly deletes the real `settings.json`, so **don't run it with the desktop running**. | `dotnet run --project tools/devicesim` |
| **`tools/blescan`** | Links the real `BlePresenceService` and reports whether the phone's beacon is heard *and* recognised — deliberately distinguishing "nothing advertising" from "advertising but the id doesn't match." | `dotnet run --project tools/blescan` |
| **`tools/mirrorsim`** | The reverse-mirror pipeline: real hardware capture, a decodable Annex-B H.264 stream, framing round-trips, coordinate mapping (incl. multi-monitor), and delivery pacing/latency. | `dotnet run --project tools/mirrorsim` |

**What only a human can check.** After the harnesses pass, the remaining checks are genuinely
physical: Wi-Fi range and cables, real calls/SMS to real contacts, subjective feel (latency,
animation, the felt balance of a layout), **and any check that requires driving the real mouse
cursor** — a genuine click/drag via `SetCursorPos` + `mouse_event` — because that hijacks the
pointer on a PC the owner is actively using. Hand those to the owner as a **detailed, numbered
manual test script** (what to click, what to expect); never drive the cursor autonomously while the
desktop may be in use (the PC-side twin of D-031's phone-tap rule). Non-intrusive automated checks
stay fair game and should be exhausted first: builds, the `tools/` harnesses, and UI-Automation
*tree enumeration* / `InvokePattern.Invoke` (which don't move the pointer). Two testing gotchas
worth knowing:

- The desktop is an **unpackaged exe the screenshot tooling can't target** — but it drives fine
  headlessly through **UI Automation** from PowerShell (`System.Windows.Automation`): enumerate the
  tree to prove elements rendered, then send a *real* mouse click (`SetCursorPos` + `mouse_event`)
  and compare against `InvokePattern.Invoke`. Invoke bypasses hit-testing; a real click doesn't —
  so when Invoke works and a real click doesn't, something is swallowing the click. (The first
  click on an unfocused window only activates it — click twice.)
- `adb input tap` does **not** reach the mirror's `TextureView` touch listener, so cursor-landing
  on the reverse mirror is a real-finger check; video is confirmable from the phone Logs and, over
  wireless, `screencap` does capture the TextureView.

## Environment facts (the dev machine)

These bit hard enough to be worth recording:

- **125% display scaling silently changes what the display APIs report.** A DPI-unaware process is
  told 1536×864 by `GetSystemMetrics` *and* DXGI's output description; a PerMonitorV2 process is
  told the real 1920×1080. Both `Linc.Desktop` and `mirrorsim` declare **PerMonitorV2** so capture
  space, virtual-desktop space, and `SendInput`'s space all agree on physical pixels — that is what
  collapses the reverse mirror's coordinate mapping to a single conversion. Any new console that
  touches display geometry needs that manifest too.
- **Capture sizes come from the acquired texture, never the output description** —
  `CopyResource` between mismatched textures fails *silently*, so a disagreement would stream black
  frames with nothing in any log.
- **adb:** `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`. **scrcpy v3.3.4 on PATH.** Test
  phone: **Pixel 7** (serial `<your-device-serial>`), USB serial == `ro.serialno`.
- **BLE and GSMTC media APIs work from an unpackaged .NET process** — no packaging or capability
  declaration needed.
- **H.NotifyIcon.WinUI is pinned to 2.2.0** (2.4+ requires net10).
- Icon regeneration: `DESKTOP/tools/generate-icon.ps1` / `generate-tray-icon.ps1`.

## Packaging & distribution (the open work)

Packaging is the Polish/Beta milestone work, still open:

- **Bundle adb + scrcpy binaries** with the desktop so end users install nothing (D-023). Today
  dev builds resolve them from PATH / winget / the repo.
- **Bundle the companion APK** with the desktop so onboarding installs it directly (today dev
  builds resolve it from `ANDROID/app/build/outputs/apk/debug/`).
- **Desktop installer / updater** (MSIX or Inno Setup + auto-update — decided at packaging time).
- **Play-ready Android build** with the SMS/call lanes compiled out (D-016); the full-feature
  build stays sideload-only.
- **Crash reporting + opt-in telemetry + a feedback channel** for the public beta.

## Licensing of bundled binaries

- **Android platform-tools (adb)** — Apache 2.0; redistribution permitted with the license notice.
- **scrcpy** — Apache 2.0 / MIT; redistribution permitted with the license notice. (A vendored,
  modified scrcpy build is planned for the next phase — see `docs/ROADMAP.md` — and must ship the
  original `LICENSE` plus a `NOTICE` describing the modifications.)
- **AdvancedSharpAdbClient** — MIT.

Bundled binaries are pinned to specific versions and updated deliberately, recorded in the
changelog.
