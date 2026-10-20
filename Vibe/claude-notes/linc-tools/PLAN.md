# Linc Tools: the build plan (designed 2026-10-20, to be built by the next session)

**What it is:** a new PC page, **Tools**, that turns ADB into simple, good-looking controls for the
owner's own phone. It's "ADB, but with a face". The tool-by-tool spec is in `TOOLS.md` next to this
file. The design mockup the owner sees is linked in `HANDOFF.md`, if approved.

**The owner's brief (2026-10-20), in short:**
- "a new page on PC that goes real hard"
- "simple surface, hide extra knobs"
- "good UI, less text and fields, clear minimal style"
- the flagship case: "the phone screen is dead and you need to get in, use your laptop keyboard and
  enter the pass"
- and: **"Linc should connect just by ADB pairing; no need to start Linc or even open it [on the
  phone]. PC → phone should work with only ADB."**

Read `../HANDOFF.md` and `../WORKING-WITH-THE-OWNER.md` first.

**Golden rule: don't break anything.** Ship behind the existing pages; every phase passes the UI test
and the build before the next one starts.

---

## 0. Ground truth (from the code, 2026-10-20)

- **The PC app** is WinUI 3, .NET 8, C#, CommunityToolkit.Mvvm, x:Bind (`Linc/DESKTOP/Linc.Desktop`).
- **ADB transport:**
  - **AdvancedSharpAdbClient 3.6.16** (`Linc.Desktop.csproj` ~76), talking to Linc's **private adb
    server on port 5038** (`Services/AdbServerHost.cs`; `ANDROID_ADB_SERVER_PORT` is set in
    `App.xaml.cs` ~39-42 before any client is created).
  - The bundled adb and scrcpy are found by `Services/ToolLocator.cs`.
  - `adb.exe` is spawned only by `AdbProcess.RunAsync` (`Services/HotspotConnect.cs` ~60-102: 20 s
    timeout, never throws).
- **There is no shared shell wrapper today.** About eight services each do
  `new AdbClient()` + `ExecuteRemoteCommandAsync(cmd, device, new ConsoleOutputReceiver(), ct)` with
  no timeout:
  - `ConnectionManager.cs` ~161, `PhoneSetupService.cs` ~82/275, `UsbWatcherService.cs` ~225,
    `PairingService.cs` ~20, `FileService.cs` ~38, `DesktopModeService.cs` ~56/355,
    `SettingsViewModel.cs` ~69/655, `PhoneExtrasViewModel.cs` ~41/273.
- **Pairing already exists.**
  - `PairingService`: QR `WIFI:T:ADB;S:linc-…;P:…;;` (~22) and the 6-digit code `PairAsync` (~29).
  - `DiscoveryService`: Zeroconf `_adb-tls-pairing` / `_adb-tls-connect`.
  - UI in `Views/DevicePage.xaml` ~121-142 and `OnboardingView.xaml` ~171-177.
- **Device tracking:**
  - `UsbWatcherService` polls `GetDevicesAsync` every 3 s (seen / unusable / gone).
  - `LinkPolicy.cs`: `LinkTransport { AdbUsb, AdbWireless, DirectTls }`.
  - `ConnectionSupervisor` serialises connects with one `SemaphoreSlim _gate` (~130).
- **What already runs over ADB or scrcpy alone:**
  - Mirror (`MirrorService`), Desktop mode (`DesktopLaunchService`, `DesktopModeService`), app
    windows (`AppLaunchService`).
  - Phone sound on PC (`PhoneSoundOnPc`, `PhoneMirrorSource`).
  - Files (`FileService` when `HasAdb`), screenshot (`FileService.cs` ~187), install APK
    (`PhoneSetupService.InstallApkAsync`).
  - Phone theme (`PhoneExtrasViewModel`), a raw shell box in Settings
    (`SettingsViewModel.RunShellCommandAsync` ~643), and adb forward/reverse.
- **But "connected" needs the companion app today.**
  - `ConnectionManager.FinishConnectAsync` (~280-420) waits for the app's `hello`. It tries
    `am start-foreground-service` once (`TryStartCompanionAsync` ~690-713), then throws.
  - The supervisor (`ConnectionSupervisor.cs` ~1015-1029) stays at **Searching** with "can't reach
    the Linc app". So with ADB up and no app, the PC never says connected.

## 1. Architecture

### 1.1 `IAdbShell`: one command layer (Phase 1)

New `Services/AdbShell.cs`, registered once in `App.xaml.cs` (near ~45). Every Tools feature uses only
this; later, move the eight private `ShellAsync` copies onto it.

```csharp
public interface IAdbShell
{
    // One-shot command. Never throws for device errors: returns ok=false + text.
    Task<ShellResult> RunAsync(string serial, string command, TimeSpan? timeout = null, CancellationToken ct = default);
    // Streaming (logcat, top, screenrecord progress): one callback per line until ct cancels.
    Task StreamAsync(string serial, string command, Action<string> onLine, CancellationToken ct);
    // Binary-safe stdout (screencap -p, uiautomator dump to /dev/tty).
    Task<byte[]?> ExecOutAsync(string serial, string command, TimeSpan? timeout = null, CancellationToken ct = default);
    Task<bool> PullAsync(string serial, string remote, string local, IProgress<double>? progress, CancellationToken ct);
    Task<bool> PushAsync(string serial, string local, string remote, IProgress<double>? progress, CancellationToken ct);
    Task<ShellResult> AdbAsync(string[] args, TimeSpan? timeout = null, CancellationToken ct = default); // adb.exe passthrough (install, pair, reboot, bugreport) via AdbProcess
    IInputSession OpenInput(string serial); // persistent shell for live typing (see TOOLS.md §1)
}
public sealed record ShellResult(bool Ok, string Text, int? ExitCode, string Command);
```

- **Implementation:**
  - One `AdbClient` against port 5038.
  - **Per-device concurrency limit of 3**, so a logcat stream can't starve a button press.
  - Default timeout 10 s; the console uses none.
  - Append `; echo "__rc=$?"` to read exit codes.
- **Every call is logged** at Info with the command (tokens and text from the Input tool redacted),
  so the owner's logs show what happened.
- **`ShellResult.Command`** feeds the "⌘ adb shell …" caption under each action (design rule 2).
- **Escaping:** one `AdbQuote.Arg(string)` helper (single quotes, `'` → `'\''`). Never build commands
  by concatenating user text without it.
- **Fake for tests:** `FakeAdbShell` answers canned outputs from a JSON script. It's switched on by a
  hidden `--fake-adb <script.json>` argument next to `--data-root` in `App.xaml.cs`, so uishots can
  screenshot every Tools section with realistic data (see §5).

### 1.2 ADB-only connection (Phase 2): the owner's "just ADB pairing" ask

**Goal:** if a phone is connected over USB or wireless debugging, the PC shows it as connected, even
when the Linc app is not installed or not running. Everything that needs only ADB works. Anything
that needs the app shows one calm card: **"Start Linc on the phone"** (or "Install"), never "not
connected".

Changes:
1. **`ConnectedDevice` gains `Mode { Full, AdbOnly }`**, with `Companion` nullable. New
   `ConnectionManager.AdoptAdbOnlyAsync(DeviceData)`:
   - read getprop (model, SDK, serial);
   - skip the forward and the hello;
   - `Install(companion: null, RawDevice: device)`.
2. **ConnectionSupervisor** (~1015-1029 catch): when the companion is missing or the hello fails,
   **adopt AdbOnly instead of failing**.
   - In AdbOnly the health loop probes ADB (`get-state` plus a cheap `getprop`) instead of the
     companion's `status.get`.
   - Keep retrying the companion in the background: every 30 s, and immediately when the package
     appears via `pm list packages app.linc.android`.
   - **Upgrade in place, make-before-break**, as `Install` already does.
3. **The ~30 `_companion ?? throw` sites** (`ConnectionManager.cs` ~437-628) and the page gates
   (`Companion.V >= n`: `HomeViewModel` ~1014/1750, `PhoneExtrasViewModel` ~76,
   `DisplayControlViewModel` ~259-269, `SyncRemote` ~71, `ClipboardSyncService` ~171/216).
   - Add one `Capability` check: `NeedsApp`, `NeedsAdb`, `NeedsAndroid(n)`.
   - A missing capability shows the reason, never "not connected".
4. **Replace the install dialog** (`MainWindow.xaml.cs` ~436-458) with a persistent card on
   Home/Device: "Linc isn't running on the phone · **Start** / **Install**". Reuse
   `ConfirmInstallAsync`, `TryStartCompanionAsync` and `ApkResolver`.
5. **Adopt any authorised adb device**, not only `registry.PairedSerial`
   (`OnUsbDeviceSeen` ~619-660; wireless ~1252).
   - With several devices, show a picker in the title tab.
   - **Unauthorised** shows "Allow USB debugging on the phone" with a picture.
6. **Status in AdbOnly mode** comes from ADB: battery (`dumpsys battery`), Wi-Fi (`cmd wifi status`),
   storage (`df`), model (`getprop`). Home's phone card shows them, so Home isn't empty.
7. **Protocol:** none needed; AdbOnly is PC-side only. Document the mode in PROTOCOL.md (§ connection
   lifecycle) and FEATURES.md.

**Risk:** this touches the connection core, the most hardware-verified part of Linc.
- Ship it on a `test-build/adb-only` branch first.
- The owner must test four cases:
  - app running (Full);
  - app force-stopped (AdbOnly → upgrades when started);
  - app uninstalled (AdbOnly + Install card);
  - wireless debugging only.
- **Full mode must behave exactly as today.**

### 1.3 The page

- **New nav item:**
  - `MainWindow.xaml` ~241-280: `NavigationViewItem Content="Tools" Tag="Tools"`, glyph `E90F`
    (Repair; alternative `E756`).
  - The tag map is at `MainWindow.xaml.cs` ~921-932. The dock builds itself from Nav (`BuildDock`
    ~669-716).
  - Place it after Device.
- **New files:**
  - `Views/ToolsPage.xaml(.cs)`
  - `ViewModels/Tools/*ViewModel.cs`, one per section
  - `Services/Tools/*` for the parsers, which are pure and testable: `PackageListParser`,
    `BatteryParser`, `TopParser`, `UiDumpParser`, `LogcatParser`, `StatParser`, `ThermalParser`
- **Layout:**
  - A **left rail inside the page**: a narrow vertical list of section icons plus names, collapsing
    to icons under 900 px. It looks like the main side rail but is lighter.
  - The content uses `PageLayout.Adaptive` (max 1200).
  - Sections load lazily: nothing runs until the section is opened, and streams stop when you leave.
- **Remember the last open section** (registry setting `ToolsSection`, added at the END of the
  Settings record with a default, as usual).

## 2. Design rules (the owner's, plus ours)

1. **Simple surface.** Each section opens on **one primary thing**, a big control or a list, with at
   most 3 secondary buttons. Everything else sits in a **"More" ⋯ menu** or an **Advanced expander**,
   collapsed by default.
2. **Parse, don't dump.** Tables, toggles, gauges and graphs. Raw text only in Console and Logcat.
3. **Show the command.** A small caption under the last action:
   - `⌘ adb shell settings put system screen_off_timeout 600000`
   - `CaptionText`, opacity 0.6, monospace, copy on click.
   - It can be hidden with **Settings › Tools › Show commands** (default on).
4. **Danger tiers** (every action is tagged in `TOOLS.md`):
   - **Safe:** one click.
   - **Confirm:** a ContentDialog with `DefaultButton = Close`, as in `FilesPage.xaml.cs` ~140-153.
     Say what happens and how to undo it.
   - **Destructive** (clear data, uninstall a user app, delete files, factory-level): the dialog's
     primary button is red and enables after 1.5 s. No type-to-confirm (the owner wants minimal).
   - **Undo** wherever one exists: a toast with Undo for 8 s (install-existing, enable, `wm` reset,
     demo exit, battery reset).
5. **Capability detection.**
   - On connect, cache `ro.build.version.sdk`, the OEM, the transport (USB/wireless), `HasApp`, and
     probe results (e.g. `current_now` readable).
   - Unavailable controls are **greyed with a one-line reason** ("Needs Android 13", "Needs USB",
     "This phone doesn't share this"). Never a failed command.
6. **Never cut our own line.**
   - Wi-Fi off and airplane mode on a **wireless** link: warn "This will disconnect Linc", Confirm
     tier.
   - Reboot: "Linc reconnects when the phone is back" (wait-for-device, then reconnect).
7. **Looks:**
   - Same as the rest of the app: frosted cards (`FrostedCard`), `ShapeLarge`.
   - Typography: `TitleText` / `BodyText` / `CaptionText` / `LabelText`, `SectionLabel` headers.
   - Buttons: `FilledButton` for the one primary action, `TonalButton` / `QuietButton` for the rest;
     `ChoiceButton.IsChoice` for single choices; `StatTile` for numbers.
   - Fluent glyph icons. Short labels (1–3 words); help text uses `Explain.IsHelp` so it can be
     turned off.
8. **Privacy:**
   - The Input tool never logs typed text.
   - Bug reports warn that they contain personal data.
   - Nothing leaves the PC.
9. **"Do not offer" list** (`TOOLS.md` §13): nothing that weakens the phone's security.

## 3. Sections, in rail order (full spec in TOOLS.md)

Primary (always visible):

| # | Section | One-line surface |
|---|---|---|
| 1 | **Type** (Input) | One big "Type on your phone" box plus live keyboard mode; arrow pad; media keys. The dead-screen hero |
| 2 | **Rescue** *(new)* | "Phone screen broken or black?": a 3-step guided card: see it (mirror with screen off), get in (type), save files (one-click backup) |
| 3 | **Apps** | Searchable list with icons; tap an app for a detail sheet with actions. Filter chips: Yours · System · Disabled |
| 4 | **Capture** | Two big buttons: Screenshot / Record. Clean-status-bar toggle (demo mode) |
| 5 | **Health** | Battery gauge, temperature, charging speed, storage bar, live CPU/RAM graphs, top apps |
| 6 | **System** | 6 tiles (Dark mode, Stay awake, Animations, Screen timeout, Brightness, Text size) plus "More" |

Advanced (below a divider in the rail, "Advanced" label):

| # | Section | One-line surface |
|---|---|---|
| 7 | **Inspector** | Screenshot with element boxes; click a box to tap it; hover for details |
| 8 | **Logs** (Logcat) | Live colour-coded log, one app picker plus level chips, pause/export |
| 9 | **Macros** | Saved one-click buttons; a step builder in a side sheet |
| 10 | **Console** | Raw shell with autocomplete and history |

Files: Linc already has a Files page (adb-backed). Don't duplicate it. Tools adds, in Health, a
"Storage" card that links there, and the two-pane PC ⇄ phone view is a Files-page enhancement (see
TOOLS.md §3).

## 4. Build order (each phase is shippable on its own)

1. **Phase 1: foundation.**
   - Build `IAdbShell` + `AdbQuote` + `FakeAdbShell` + `--fake-adb`.
   - Add the Tools page shell with the rail and empty sections, the capability cache, the command
     caption, and the danger-tier dialog helper.
   - Unit tests for `AdbQuote` (in a new `tools/toolsim`, like the other sims, run by `build.yml`).
2. **Phase 2: ADB-only connection** (§1.2) on `test-build/adb-only`. The owner tests the four cases.
3. **Phase 3: Type + Rescue** (the hero). Persistent input session, live mode, PIN-friendly typing
   on the lock screen, clipboard paste for non-ASCII when the app is there.
4. **Phase 4: Apps + Capture.**
5. **Phase 5: Health + System.**
6. **Phase 6: Inspector + Logs.**
7. **Phase 7: Macros + Console.** Then retire the Settings › raw-shell box into Tools › Console.
8. After each phase:
   - update FEATURES.md and the claude-notes HANDOFF;
   - add uishots pages `11-tools-type.png` … (with `--fake-adb`);
   - put a test build to Telegram **only when the owner says "send"**.

## 5. Testing

- **Pure parsers** go in `tools/toolsim`, with real captured outputs from Android 11/13/14/15 as
  fixtures: `pm list packages`, `dumpsys battery`, `top`, `uiautomator` XML, `logcat` threadtime,
  `stat`, `dumpsys thermalservice`.
  - The next session can't run adb, so take fixtures from the AOSP sources and docs, then refresh
    them from the owner's phone (Console › "Copy output").
- **uishots:**
  - Add `"Tools"` to `pages` (`Linc/tools/uishots/Program.cs` ~61), and one shot per section by
    clicking the rail items.
  - Launch the app with `--fake-adb tools/uishots/fake-adb.json`.
  - The fake phone dials in over Direct TLS (`FakePhone.cs`), so `HasAdb` is false there. The fake
    shell must also make the device report `HasAdb = true` / AdbOnly.
- **Make uishots fail** when a Tools section is missing, so it can't go green with blank pages
  (CONCERNS.md).
- **Hardware:** the owner, on their phone, phase by phase. Give them a short checklist each time.

## 6. Done means

- Every section in TOOLS.md works on Android 11–15, or greys out with a reason.
- Full mode is unchanged; AdbOnly shows the phone as connected with no app on it.
- uishots shows every section in light and dark.
- FEATURES.md, PROTOCOL.md (connection lifecycle) and SOUND.md (if touched) are updated in the same
  commits.
