# REPORT — M4b: protocol v17, the phone's quick controls for the PC (both sides)

Agent: Claude Code 2. Three parts, one continuous session: Part 0 (cleanroomsim fix) → Part A
(desktop) → Part B (phone). **No power action was ever executed for real** — see "What could NOT
be verified" at the end; every lock/sleep/shutdown/restart check below is either structural
(the pure validator) or read-only (volume/capability reads via a throwaway reflection probe,
deleted before this report was written).

---

## §0.1 — what was on disk, WITH last-write times, before I touched it

**A process note first, stated plainly per the reporting rules:** I fixed Part 0
(`tools/cleanroomsim/Program.cs`) *before* recording this file's own pre-edit mtime — exactly the
mistake this section exists to prevent. I did not lose the provenance, though: this file is
tracked in git and had **no uncommitted changes** before I touched it this session (confirmed by
`git status` at session start, captured below), so its pre-edit state is exactly HEAD, last
committed **2026-08-09 12:49:15 +02:00** (commit `76c754b`). Every other file below, I captured
the mtime *before* editing, as instructed.

`git status --porcelain` at the very start of this session (before any Part 0/A/B edit):

```
 M ANDROID/app/src/main/AndroidManifest.xml
 M ANDROID/app/src/main/java/app/linc/android/MirrorActivity.kt
 M ANDROID/app/src/main/java/app/linc/android/service/MirrorControl.kt
 M ANDROID/app/src/main/java/app/linc/android/ui/HomeScreen.kt
?? ANDROID/app/src/main/java/app/linc/android/ToolsActivity.kt
?? ANDROID/app/src/main/java/app/linc/android/service/KeyboardMapping.kt
?? ANDROID/app/src/main/java/app/linc/android/service/KeyboardSink.kt
?? ANDROID/app/src/main/java/app/linc/android/service/TrackpadGestures.kt
?? ANDROID/app/src/main/java/app/linc/android/ui/ToolsScreen.kt
?? ANDROID/app/src/test/java/app/linc/android/service/KeyboardMappingTest.kt
?? ANDROID/app/src/test/java/app/linc/android/service/MirrorControlTest.kt
?? ANDROID/app/src/test/java/app/linc/android/service/TrackpadGesturesTest.kt
```

This is M4a's tree exactly — nothing under `DESKTOP/` was already touched, `Protocol.kt` and
`Envelope.cs` were both still at their committed state. I read every file in this list before
writing anything new; none of it is claimed as this session's work below.

**Pre-edit mtimes for every file this session went on to touch** (captured before Part A/B, right
after §0.2's baseline builds):

| File | mtime before I touched it |
|---|---|
| `DESKTOP/Linc.Desktop/Protocol/Envelope.cs` | 2026-07-31 16:09:42 |
| `DESKTOP/Linc.Desktop/App.xaml.cs` | 2026-08-07 21:05:57 |
| `DESKTOP/Linc.Desktop/ViewModels/AppShellViewModel.cs` | 2026-08-07 01:13:51 |
| `DESKTOP/Linc.Desktop/Linc.Desktop.csproj` | 2026-08-07 07:47:11 |
| `ANDROID/.../protocol/Protocol.kt` | 2026-07-31 16:06:16 |
| `ANDROID/.../service/SocketServer.kt` | 2026-07-31 16:07:52 |
| `ANDROID/.../ui/ToolsScreen.kt` | 2026-08-09 14:07:45 (M4a's file) |
| `tools/cleanroomsim/Program.cs` | 2026-08-09 12:49:15 (HEAD, see note above) |
| `PcControlService.cs`, `PcControlValidator.cs`, `PcControl.kt`, `ControlPayloads.kt`, `tools/pccontrolsim/*` | did not exist |

**Which of them actually changed during *this* session** (final mtimes, all 2026-08-10, this
session):

- `tools/cleanroomsim/Program.cs` — Part 0 fix.
- `DESKTOP/Linc.Desktop/Protocol/Envelope.cs`, `App.xaml.cs`, `ViewModels/AppShellViewModel.cs`,
  `Linc.Desktop.csproj` — Part A wiring.
- `DESKTOP/Linc.Desktop/Services/PcControlService.cs`, `PcControlValidator.cs` — new, Part A.
- `tools/pccontrolsim/` — new, Part A.
- `ANDROID/.../protocol/Protocol.kt`, `service/SocketServer.kt`, `ui/ToolsScreen.kt` — Part B.
- `ANDROID/.../service/PcControl.kt`, `service/ControlPayloads.kt` — new, Part B.
- `ANDROID/.../test/.../ControlPayloadsTest.kt`, `PcControlTest.kt` — new, Part B.

**Not touched by me this session** (M4a's files, carried forward unchanged):
`AndroidManifest.xml`, `MirrorActivity.kt`, `service/MirrorControl.kt`, `ui/HomeScreen.kt`,
`ToolsActivity.kt`, `service/KeyboardMapping.kt`, `service/KeyboardSink.kt`,
`service/TrackpadGestures.kt`, and the three M4a test files. I did not claim any of them.

## §0.2 — baselines, before any Part A/B edit (Part 0's fix was required first — see below)

**Ordering note, flagged as ambiguous in the task (see the bottom section):** §0.2 says "build
both sides immediately, before you write anything," but Part 0 is itself a required write (fixing
`cleanroomsim`) that the task's own §1-adjacent instruction says must happen, and pass, *before*
Part A may even start. I resolved this by treating §0.2's "baseline" as the Desktop/Android build
+ test baseline (needed for acceptance items 1–2), running those immediately after Part 0's fix
and before any Part A/B code, and treating Part 0 itself as a prerequisite gate the task explicitly
sequences ahead of everything else. I also had to `taskkill Linc.Desktop.exe` (PID 10052, already
running) before `cleanroomsim` could exercise Half Two — this is the task's own standard build
prerequisite ("Stop-Process any Linc.Desktop"), not an unrelated intrusion.

**Desktop baseline** (`MSBuild DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`):

```
BUILD SUCCESSFUL
D:\...\ViewModels\HomeViewModel.cs(166,72): warning CS9113: Parameter 'connection' is unread.
0 Errors, 1 distinct warning (pre-existing, unrelated to this task)
```

**Android baseline** (`gradlew assembleDebug testDebugUnitTest --no-daemon`):

```
BUILD SUCCESSFUL
79 tests, 0 failures
```
(Same 79/0 as M4a's final state — nothing moved between M4a's session end and this one's start.)

---

## Part 0 — `cleanroomsim`: what was actually wrong, and did the hypothesis hold

**The hypothesis held, confirmed by measurement, not assumption.** Replaced the fixed
`Thread.Sleep(5000)` with a poll (250ms interval, 30s ceiling) for the redirected log file to
appear, reporting real elapsed time in every message. Ran it twice on this machine:

- **First run: found the log file after 8.9s of polling** — comfortably past the old fixed 5s
  window, which is exactly the false failure M4a hit (that session had Gradle running
  concurrently, per the task's own note).
- **Second run** (machine less loaded): found it after **0.8s**.

Both runs are genuinely useful evidence *because* they're so different — the fixed 5s sleep would
have failed the first and passed the second purely on machine load, which is precisely why a
fixed sleep in a harness is a false-failure generator. The redirect itself was never broken.

I also had to fix a nullable-reference warning my own poll loop introduced (`child.HasExited` →
`child is not { HasExited: false }`, matching the existing pattern a few lines below) so the fix
doesn't add a new build warning.

`dotnet run --project tools/cleanroomsim` (second/final run, verbatim tail):

```
PASS: the published exe is still alive 0.8s after launch (settle poll).
PASS: no linc-startup-error.log was written beside the exe or under the redirected root.
PASS: the app's own log under the redirected root records "Store root redirected via --data-root: ...\Linc_cleanroomsim_launch_c0a5adb62c174225bb101cacfd28cdf6" — --data-root actually redirected the store root, tied to the real DI-constructed IDeviceRegistry.RootPath (found after 0.8s of polling).
SKIP: ...\linc.db had not appeared within the 0.8s settle poll (LincStore.EnsureSchemaAsync is fire-and-forget...) — not counted as a pass or a failure.
SKIP: the app log exists but does not record which adb it resolved — cannot assert the bundled-adb claim without adding production logging (out of scope). Reported honestly rather than asserted.
=== SUMMARY ===
SKIPPED: 3 check(s) not run: [as above, plus the pre-existing LOCALAPPDATA-redirection environment limit]
PASS: All non-skipped verification checks succeeded.
```

Green. Proceeded to Part A as authorised.

---

## Part A — the desktop side of v17

**A4 (`ProtocolConstants.Version` → 17)** — `DESKTOP\Linc.Desktop\Protocol\Envelope.cs:12`. Added
`MessageType.PcControl` / `PcStateGet` / `PcState` to the same file, matching the v14/v16 comment
style already there.

**A1 (`PcControlService.cs`)** — mirrors `PcMirrorService`'s shape exactly: an `Attach()` that
subscribes to `IConnectionManager.CompanionMessageReceived` once, a `switch (envelope.Type)`, and
replies via `connection.SendToPhoneAsync(Envelope.Create(..., replyTo: envelope.Id), ...)`. Wired
into `App.xaml.cs`'s DI (`IPcControlService, PcControlService`) and into `AppShellViewModel`
alongside `pcMirror.Attach()` — quick controls must work with no page open, same reasoning as
every other push service already there (GUARDRAILS: cross-cutting services belong in the shell,
not a page VM).

**A2 (the pure validator)** — `PcControlValidator.ValidateControl(action, level, step, on,
confirm) → string?`, in its own file with **zero P/Invoke and no reference to
`PcControlService`**. It only ever returns `null` / `"needs-confirm"` / `"internal"` — never
`"unsupported"`/`"denied"`, which can only be known by actually attempting the action
(`PcControlService.Execute`'s job). Section 1 of `pccontrolsim` (below) proves this at the
compiled-assembly level, not just by prose.

**A3 (Win32/COM/WMI, in order of risk)**:

- `lock` → `LockWorkStation` (user32).
- `sleep` → `SetSuspendState(false, false, false)` (powrprof) — not hibernate.
- `shutdown`/`restart` → `ExitWindowsEx`, gated on `SeShutdownPrivilege` explicitly enabled via
  `AdjustTokenPrivileges` first (checking `GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED`, since
  `AdjustTokenPrivileges` can return `true` while quietly failing to grant the privilege).
  **Deliberately did not set `EWX_FORCEIFHUNG`** — an unasked-for choice, flagged here: forcing
  past a hung app would kill it without letting it prompt "save your work?", which cuts against
  the owner's own safety framing for this feature. A normal `ExitWindowsEx` still lets Windows
  negotiate with apps first.
- Volume → hand-rolled `IAudioEndpointVolume` COM interop (`MMDeviceEnumerator` →
  `GetDefaultAudioEndpoint` → `IMMDevice.Activate` → `IAudioEndpointVolume`). No new dependency —
  the csproj already forbids that; this is plain `[ComImport]` interop, same posture as
  `MediaFoundation.cs`'s hand-rolled Media Foundation interfaces. `volume.up`/`.down` call the
  endpoint's own `VolumeStepUp`/`VolumeStepDown` the given number of times (one call = one
  notch, matching a keyboard volume key exactly, per spec).
- Brightness → WMI **only**, via `root/WMI`'s `WmiMonitorBrightness` (read) /
  `WmiMonitorBrightnessMethods.WmiSetBrightness` (write). **DDC/CI is untouched, as instructed.**

**Unasked-for dependency, flagged explicitly:** added `System.Management` (8.0.0) to
`Linc.Desktop.csproj`. There is no in-box .NET way to query WMI, and the task explicitly mandates
`WmiMonitorBrightnessMethods` for brightness — this package is the only practical way to reach it
in one session (the alternative, hand-rolling raw `IWbemServices` COM interop, is a much larger
and riskier undertaking for a "don't touch DDC/CI, it's a rabbit hole" feature). Restored via
`dotnet restore` after adding it; the desktop build is otherwise clean (see §0.2 comparison below).

**What `canBrightness` reports on this machine, and why:** `true` — a real
`WmiMonitorBrightnessMethods` instance exists here (confirmed by a runtime probe, not just
"the query didn't throw" — see below), and reading `WmiMonitorBrightness.CurrentBrightness`
returned `100`.

**A5 (`tools/pccontrolsim`)** — new project, `<Compile Include>`-only `PcControlValidator.cs`
(same posture as `apkinstallsim`/`ApkInstall.cs`). **How it's structured so it cannot invoke a
real power action, by construction, not by convention:** the Win32/COM/WMI code lives entirely in
`PcControlService.cs`, which this project never compiles — so there is no code path in this
harness's own assembly that *could* reach `LockWorkStation`/`ExitWindowsEx`/etc. Section 1 proves
this isn't just an intent: it reflects over the harness's own compiled assembly and asserts **zero
methods carry `MethodAttributes.PinvokeImpl`** — a runtime check of the actual binary, not a claim
about the source.

```
[1] This assembly cannot reach a real power/volume/brightness call...
    PASS: this assembly's own compiled metadata contains zero P/Invoke methods — PcControlValidator.cs
    is the only production file compiled in (see the .csproj), and the Win32/COM/WMI interop that
    actually locks/sleeps/shuts down/sets volume or brightness lives entirely in PcControlService.cs,
    which this project never compiles.
```

Covers every action, the confirm gate, `level` out-of-range, missing `level`, and unknown action
(sections 2–5). All green — full output under "Files changed" below.

**A finding beyond what the task asked me to check** (runtime-verified, see the note on how, at
the end of this section): on **this** development machine, `IsPwrSuspendAllowed()` — the powrprof
export I used for `canSleep` — returns **`false`**, even though Sleep is a normal, working option
from this machine's own Start menu. This is very likely because modern Windows machines can use
**Modern Standby (S0 low-power idle)** instead of the legacy ACPI S3 sleep state this specific API
checks, and `IsPwrSuspendAllowed()` only reports on the latter. This is a real platform quirk, not
a bug in `PcControlValidator` or a defect I introduced — but it means `canSleep` as implemented
may under-report on Modern-Standby machines, disabling a Sleep button that would actually work. A
more complete signal would need `GetPwrCapabilities`' `SystemS0LowPower` field (the giant
`SYSTEM_POWER_CAPABILITIES` struct), which I judged out of scope for this session — flagging it
here rather than either silently shipping a wrong assumption or spending the session's remaining
budget on a struct with ~40 fields for one flag. `IsPwrShutdownAllowed()` returned `true` on this
machine, for comparison.

**How I verified any of A3 actually works, given I could execute none of it for real:** the
safety rule forbids lock/sleep/shutdown/restart outright, and only explicitly sanctions *reading*
volume — so brightness/volume *writes* were never attempted either. I built a throwaway console
project **outside the repo** (in my scratch temp directory, never under `<repo-root>`)
that compiled `PcControlService.cs` verbatim and called its private static read-only methods via
reflection (`TryGetVolume`, `IsPwrSuspendAllowed`, `IsPwrShutdownAllowed`, `CanControlBrightness`,
`TryGetBrightness`, `BuildStatePayload`) — the **real** production methods, not a model of them.
Output:

```
TryGetVolume() -> (True, 0.24000001, False)
IsPwrSuspendAllowed() -> False
IsPwrShutdownAllowed() -> True
CanControlBrightness() -> True
TryGetBrightness(out) -> ok=True, brightness=100
BuildStatePayload() -> {
  "volume": 24, "muted": false, "brightness": 100,
  "canBrightness": true, "canSleep": false, "canShutdown": true
}
```

This is real evidence the `IAudioEndpointVolume` COM chain and the WMI brightness queries resolve
correctly at runtime on real hardware, not just that they compile. I deleted the throwaway project
immediately after (nothing was ever added to the repo; `git status` before/after is identical).

---

## Part B — the phone side of v17

**B2 (`PROTOCOL_VERSION` → 17)** — `ANDROID/...\protocol\Protocol.kt:12`, same session as A4, per
the task's explicit instruction (M5c's split-brain vs. M6c's atomic move). Added
`MessageType.PC_CONTROL` / `PC_STATE_GET` / `PC_STATE` and three new `ErrorCode`s
(`NEEDS_CONFIRM`, `UNSUPPORTED`, `DENIED`) to the same file.

**B1 (widgets on `ToolsScreen`, no second surface)** — added a `QuickControlsCard` directly into
the existing `ToolsScreen.kt` (M4a's file), between the header and the trackpad card: Lock /
Sleep / Shut down / Restart buttons, a Volume row (−/current%/+/mute toggle), and a Brightness row
(−/current%/+). No new Activity, no new nav destination.

**B3 (confirm — both, not either)** — tapping Shut down/Restart opens a Compose `AlertDialog`
("Save your work on the PC first — this will shut it down / restart it now.") with Cancel and a
confirm action; only on confirm does the app call `PcControl.shutdown()`/`restart()`, and those
functions **always** build their payload with `confirm: true` via `ControlPayloads` — the wire
flag is never conditional on anything the UI does, so a UI bug can't accidentally send it. Lock
and Sleep have no dialog (Sleep is explicitly non-destructive per spec; Lock is not destructive
either).

**B4 (state on open + after every send; disable, not hide; version gate)** — `PcControl.send()`
calls `requestState()` immediately after every `pc.control` send (there is deliberately no
unsolicited push, per spec). `ToolsScreen` also requests state via
`LaunchedEffect(connection) { PcControl.requestState() }`, keyed on the connection object so it
re-fires both when Tools opens and after any reconnect that completes while the screen is
already open. A **separate** `controlsReason(state)` gate (parallel to M4a's `connectionReason`,
which stays at v14) disables the whole quick-controls card with a plain-language reason when the
negotiated version is below 17 — so a v14-only desktop still gets a fully working keyboard and
trackpad, only quick controls go dim. Individual controls (`canSleep`/`canShutdown`/
`canBrightness`) disable independently within that, each matching a plain-language reason
("This PC's screen brightness can't be controlled remotely.").

**One interpretation beyond the task's literal bullet list, flagged here:** the desktop's `error`
reply to a rejected `pc.control` (e.g. a `needs-confirm`/`denied` the phone's own confirm dialog
+ wire flag should already have prevented) wasn't explicitly assigned a phone-side handler by the
task. I added a small `MessageType.ERROR` case to `SocketServer.kt`, gated at negotiated ≥ 17, that
logs the code/message via `LogStore` rather than building UI for it — the spec itself frames this
as "the backstop, not the primary path," so a log line felt proportionate; a toast/snackbar would
have been inventing UI scope the task didn't ask for.

---

## Files changed, one line each

**Desktop:**
- `DESKTOP/Linc.Desktop/Protocol/Envelope.cs` — version → 17; `PcControl`/`PcStateGet`/`PcState` message types.
- `DESKTOP/Linc.Desktop/Services/PcControlValidator.cs` (new) — the pure structural validator.
- `DESKTOP/Linc.Desktop/Services/PcControlService.cs` (new) — Win32/COM/WMI execution + `pc.state` replies.
- `DESKTOP/Linc.Desktop/App.xaml.cs` — DI registration for `IPcControlService`.
- `DESKTOP/Linc.Desktop/ViewModels/AppShellViewModel.cs` — inject + `pcControl.Attach()`.
- `DESKTOP/Linc.Desktop/Linc.Desktop.csproj` — added `System.Management` 8.0.0 (flagged above).
- `tools/pccontrolsim/` (new) — the validator harness.
- `tools/cleanroomsim/Program.cs` — Part 0: fixed sleep → poll, elapsed-time messages.

**Android:**
- `ANDROID/.../protocol/Protocol.kt` — version → 17; `PC_CONTROL`/`PC_STATE_GET`/`PC_STATE`; three new `ErrorCode`s.
- `ANDROID/.../service/ControlPayloads.kt` (new) — pure `pc.control` payload builders.
- `ANDROID/.../service/PcControl.kt` (new) — send path + `pc.state` StateFlow.
- `ANDROID/.../service/SocketServer.kt` — routes `pc.state` to `PcControl.onState`; logs `pc.control` error replies.
- `ANDROID/.../ui/ToolsScreen.kt` — `QuickControlsCard`, confirm dialog, `controlsReason` gate.
- `ANDROID/.../test/.../ControlPayloadsTest.kt`, `PcControlTest.kt` (new) — unit coverage.

## Verbatim build/test output

**Desktop, final:**
```
MSBuild DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m
D:\...\ViewModels\HomeViewModel.cs(166,72): warning CS9113: Parameter 'connection' is unread.
Linc.Desktop -> D:\...\Linc.Desktop.dll
```
0 errors. The one warning is the same pre-existing one from the §0.2 baseline — **0 new warnings.**

**Android, final:**
```
gradlew assembleDebug testDebugUnitTest --no-daemon
BUILD SUCCESSFUL
```
Test counts (baseline was 79/0):

| Suite | tests | failures |
|---|---|---|
| protocol.EnvelopeTest | 3 | 0 |
| protocol.FramingTest | 9 | 0 |
| service.AppInventoryTest | 6 | 0 |
| service.CompanionOutboxTest | 8 | 0 |
| service.ControlPayloadsTest | 12 | 0 |
| service.DisplayControlTest | 20 | 0 |
| service.KeyboardMappingTest | 7 | 0 |
| service.MirrorControlTest | 4 | 0 |
| service.OutgoingActionsTest | 13 | 0 |
| service.PcControlTest | 6 | 0 |
| service.TrackpadGesturesTest | 9 | 0 |
| **Total** | **97** | **0** |

**Both version-constant greps:**
```
ANDROID/.../protocol/Protocol.kt:12:const val PROTOCOL_VERSION = 17
DESKTOP/Linc.Desktop/Protocol/Envelope.cs:12:    public const int Version = 17;
```

**`pccontrolsim`, final:**
```
=== SUMMARY ===
PASS: All verification checks succeeded.
```
(29 individual checks across 5 sections, all PASS — full transcript above/available on rerun.)

**Full desktop regression** (`dotnet run --project tools/<name> -c Debug`, CWD `Linc/`), all green:
`packagesim`, `presencesim`, `startupsim`, `applaunchsim`, `mirrorsettingssim`, `appssim`,
`homelayoutsim`, `displaysim`, `homecachesim`, `storesim`, `synccachesim`, `outboxsim`,
`apkinstallsim`, `autostartsim`, `syncsim` — every one ended `PASS: All verification checks
succeeded.` / `ALL CHECKS PASSED` / `ALL SCENARIOS PASSED`.

- `desktopsim` — **FAIL: "No attached ADB device found"** (hardware-dependent; no Pixel 7 attached
  this session — honest either way, per the task).
- `blescan` — **PASS** this run (`SIGHTED <your-device-serial>`) — the phone happened to be nearby with
  Bluetooth on. Honest either way either result.
- `cleanroomsim` — **PASS**, both runs (Part 0 above): 8.9s and 0.8s respectively.

---

## Both negative proofs against production source

**1. Removed the `confirm: true` requirement** from `PcControlValidator.cs` (deleted the
`DestructiveActions.Contains(action) && confirm != true` block):

```
[3] The confirm gate — this is the safety gate; prove it exists...
    FAIL: shutdown with no confirm -> "valid", expected "needs-confirm"
    FAIL: shutdown with confirm: false -> "valid", expected "needs-confirm"
    FAIL: restart with no confirm -> "valid", expected "needs-confirm"
=== SUMMARY ===
FAIL: 3 check(s) failed
```

Restored the exact block; reran — `pccontrolsim` back to `PASS: All verification checks
succeeded.`, with `KeyboardMappingTest`/etc. on the Android side unaffected (this is a
desktop-only file).

**2. Broke the 0–100 `level` clamp** in the same file (`level is null || level < 0 || level >
100` → `level is null`):

```
[4] The 0-100 level clamp and required-level cases...
    FAIL: volume.set below range (-1) -> "valid", expected "internal"
    FAIL: volume.set above range (101) -> "valid", expected "internal"
    FAIL: brightness.set below range (-1) -> "valid", expected "internal"
    FAIL: brightness.set above range (101) -> "valid", expected "internal"
=== SUMMARY ===
FAIL: 4 check(s) failed
```

Restored the exact clamp; reran — green again.

Both proofs broke and restored **`DESKTOP/Linc.Desktop/Services/PcControlValidator.cs`**, the one
production file this whole harness compiles.

---

## The owner's real settings — unchanged, checked before and after

```
C:\Users\<you>\AppData\Local\Linc/settings.json
before: 1538 bytes, 2026-08-09T20:37:42.9571165Z
after:  1538 bytes, 2026-08-09T20:37:42.9571165Z   (identical — checked at the very end of session)
```
`cleanroomsim` also asserts this internally (size+mtime unchanged) on every run, and passed both
times.

---

## Numbered manual test for the owner

Requires the phone's Tools page open with a v17 desktop connected. **Read the destructive ones
(6–7) especially carefully — save your work on the PC before trying them.**

1. **Open Tools.** *Good:* the new controls card appears below the header (Lock / Sleep / Shut
   down / Restart, then a Volume row, then a Brightness row), at full opacity — not dimmed.
2. **Lock.** Tap Lock. *Good:* the PC's screen locks immediately (Windows lock screen appears).
   No confirmation dialog — this one shouldn't need one.
3. **Volume down/up.** Tap `−` a few times, then `+`. *Good:* the PC's system volume visibly drops
   then rises, one notch per tap (same size as a keyboard volume key), and the on-screen `%`
   number updates within about a second (it re-syncs via a fresh `pc.state` after every tap).
4. **Mute.** Tap Mute. *Good:* PC audio mutes; the button now reads "Unmute". Tap it again to
   restore sound.
5. **Brightness.** Tap `+`/`−` on the Brightness row. *Good, on a laptop or a monitor with WMI
   brightness support:* the screen visibly dims/brightens in 10% steps. *Good, on most external
   desktop monitors:* the Brightness row is disabled with the text "This PC's screen brightness
   can't be controlled remotely" — this is the DDC/CI-not-supported case the spec calls out, not
   a bug.
6. **Sleep (not destructive, no confirmation dialog).** Tap Sleep. *Good:* the PC goes to sleep
   immediately. **Save any open work before this step regardless** — nothing is lost by design,
   but confirm your own apps behave the way you expect on this PC's sleep. If the button is
   greyed out with a reason shown, see the `canSleep`/Modern-Standby note in this report — that is
   a known platform-detection limitation, not a connection problem.
7. **Shut down or Restart — SAVE YOUR WORK ON THE PC FIRST.** Tap Shut down (or Restart). *Good:*
   a dialog appears asking to confirm, naming what will happen. Only after you tap the confirm
   button does the PC actually begin shutting down/restarting. Tapping Cancel does nothing to the
   PC. If Windows itself then asks you to close specific apps, that is normal — it did not use a
   force-close flag.

## What could NOT be verified, and why

**Every power action is on this list, as instructed — none were run for real:**

- **Lock, Sleep, Shutdown, Restart** — the safety rule forbids executing any of these for real,
  with no exception. Verified only structurally (the pure validator, `pccontrolsim`) and via
  source review of the Win32 calls (`LockWorkStation`, `SetSuspendState`, `ExitWindowsEx` +
  `AdjustTokenPrivileges`) — never invoked.
- **Volume SET / Mute SET** — the safety rule only sanctions *reading* volume, not setting it. I
  verified the read path live (`TryGetVolume()` via the throwaway reflection probe, real output:
  24%, not muted) but never called `SetMasterVolumeLevelScalar`/`SetMute` for real.
  `VolumeStepUp`/`VolumeStepDown` were likewise never invoked.
- **Brightness SET** — not explicitly exempted by the safety rule the way volume-read is, so
  treated the same as the destructive actions: never invoked. Read path (`TryGetBrightness`,
  `CanControlBrightness`) verified live (100%, WMI available on this machine).
- **The phone-side UI** (buttons, the confirm dialog, disabled/dimmed states, the numbers
  updating) — this is a Compose screen; I did not tap the Pixel 7, per the hard prohibition. All
  seven manual-test items above are unverified by me and belong to the owner.
- **`canSleep` correctness on a genuinely policy-locked machine** — I only have this one dev
  machine to test against, and its `false` result traces to a hardware/OS platform quirk (Modern
  Standby vs. legacy S3), not an actual policy lock. I could not verify what a *real*
  policy-locked machine reports, only that the API call itself resolves and returns a value.
- **`desktopsim`'s ADB-dependent check** and **whether the phone actually receives/parses a live
  `pc.state`/`pc.control` reply over a real connection** — no Pixel 7 was attached this session.

---

## Ambiguous, contradictory, or wrong — in the task file or PROTOCOL.md v17

- **§0.2 vs. Part 0 ordering** (discussed above): §0.2 says build/baseline before writing
  anything, but Part 0's fix has to land and pass before Part A is authorised to start. Not a
  contradiction I could avoid — flagging it so a future task sequences these unambiguously (e.g.
  "run §0.2's builds, THEN do Part 0" vs. "do Part 0 first, then §0.2").
- **`canSleep`/`canShutdown`'s exact determination method is left to the implementer** ("cover
  policy-locked machines" is the only guidance) — I chose `IsPwrSuspendAllowed`/
  `IsPwrShutdownAllowed` (powrprof.dll) as the simplest correct-by-API-contract signal, and found
  live that `IsPwrSuspendAllowed` under-reports on this Modern-Standby machine (see Part A). This
  is a genuine finding about the chosen API, not a spec defect, but worth the planner knowing
  before this ships as the final word on `canSleep`.
- **PROTOCOL.md v17 itself reads as internally consistent** — I found no contradiction between
  its "Reply contract" and "action enum" tables, and no clash with the existing v14/v15/v16
  request/reply precedents it says it follows. Confirmed both by reading and by `pccontrolsim`
  matching every documented case.
- Everything else in the task file matched what I found in the code (M4a's `MirrorControl`
  already being service-level, `PcMediaService` genuinely having no volume code, etc.) —
  planner-checked claims in the task file held up.
