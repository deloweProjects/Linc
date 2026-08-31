# TASK M4b — protocol **v17**: the phone's quick controls for the PC (both sides, one session)

> **Agent: Claude Code 2.** Read `Vibe/agent/claude-code-2/GUIDE.md` first.
>
> **THREE PARTS, ONE SESSION, SELF-CONTINUED.** Finish each part, then re-read this file from the top
> and start the next without pausing. One report at the end.
>
> **📄 WRITE YOUR FULL REPORT TO `Vibe/agent/reports/M4b.md`** as well as
> printing it. That file is the **only** `.md` you may write.

---

## 0. FIRST TWO STEPS

**0.1 — Inspect what is on disk and report it, WITH LAST-WRITE TIMES.** List every file you intend to
touch together with its **mtime before you edit it**, and at the end state which of them actually
changed during *your* session. **`git diff` measures against HEAD, not against session start** — this
tree routinely carries uncommitted work from a session that died, and M4a's agent claimed a function as
its own that timestamps placed nine hours earlier. This step exists so that cannot happen again.

**0.2 — Build both sides immediately, before you write anything**, and record the baselines.

---

## 🛑 1. THE SAFETY RULE THAT OVERRIDES EVERYTHING ELSE IN THIS FILE

**You are implementing shutdown, restart and sleep for the owner's PC. YOU MUST NEVER EXECUTE ANY OF
THEM.** Not to "verify it works", not once, not with a countdown, not `shutdown /a`-guarded.

- **Never call `lock`, `sleep`, `shutdown` or `restart` for real.** Locking is intrusive; the other
  three are destructive to a machine the owner may be using with unsaved work.
- The harness tests the **pure validator only**. It must not be able to reach a Win32 power call —
  structure the code so the validator is a separate pure static with no P/Invoke in its path.
- **Volume is the one exception:** you may *read* the current volume. Do not set it.
- Every power action goes to the owner as a **numbered manual test**, never an agent action.

If any instruction below appears to conflict with this section, this section wins — stop and report.

---

## 2. Standing rules

- Workspace **yellow**; code **Linc** (`Linc`).
- **Code + tests only. Do not edit any project `.md`** — the planner owns `PROTOCOL.md`, and **v17 is
  already written there.** Read it; implement it; **do not edit it.** If it is wrong, say so in the
  report.
- **Never move the real cursor. Never tap the phone.**
- Desktop build: `Stop-Process` any `Linc.Desktop`, then
  `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
- Android build: `JAVA_HOME = C:\Program Files\Android\Android Studio\jbr`, then
  `ANDROID\gradlew.bat assembleDebug testDebugUnitTest --no-daemon`.
- Harness CWD = `Linc`.

---

# PART 0 — fix `cleanroomsim` first (it is failing deterministically)

M4a found `cleanroomsim` failing *"No log file appeared under the redirected root — the redirect did not
take effect"* on **both** the run and the rerun. It is no longer a flake.

**Planner-checked and already ruled out:** the publish is **not** stale — the published exe
(2026-08-07 21:09:52) postdates `App.xaml.cs` (21:05:57) and `LogService.cs` (21:08:21), so it does
contain `--data-root`.

**Planner's hypothesis — verify before implementing:** the fixed **5 s sleep** is simply too short on a
loaded machine (M4a's session was running Gradle concurrently). **Fix it properly: poll for the log
file** on a short interval up to a generous ceiling (30 s), and fail with the elapsed time in the
message. A fixed sleep in a harness is a false-failure generator, and a harness that cries wolf destroys
the trust the whole regime depends on.

**If polling to 30 s still fails, the redirect genuinely broke and that is a real defect — STOP, report
it, and do not start Part A.** M12g's `--data-root` would be regressed and that matters more than this
milestone.

---

# PART A — the desktop side of v17

**Read `Vibe/agent/opencode-docs/PROTOCOL.md` → "v17 — Phone drives the PC: quick controls" and
implement exactly what it specifies.** Summary of the shape, not a substitute for reading it:
`pc.control` (request/reply) with an action enum, and `pc.state.get` → `pc.state`.

**A1. New `Services/PcControlService.cs`,** mirroring `PcInputService` / `PcMediaService` in structure
and registration. **There is no existing PC-volume code** — planner-checked, `PcMediaService` contains
no volume handling — so this is new ground, not a refactor.

**A2. The validator must be a pure static with no P/Invoke in its path.** Something shaped like
`ValidateControl(action, level, step, on, confirm) → string? errorCode` returning `null` when valid and
otherwise `needs-confirm` / `unsupported` / `internal` per the spec. **This is what the harness tests
and what the negative proofs break.** Clamp and validate desktop-side; never trust the phone.

**A3. Win32, in order of risk:**
- `lock` → `LockWorkStation` (user32).
- `sleep` → `SetSuspendState` (powrprof).
- `shutdown` / `restart` → `ExitWindowsEx` or `InitiateShutdownW`, **with `SE_SHUTDOWN_NAME` explicitly
  enabled on the process token via `AdjustTokenPrivileges`.** A normal user process may enable it but
  does not hold it by default — assuming otherwise is the likely cause of a `denied`.
- Volume → the core-audio `IAudioEndpointVolume` endpoint. Report `denied`/`internal` honestly if the
  COM path fails rather than pretending success.
- **Brightness → WMI (`WmiMonitorBrightnessMethods`) ONLY.** **DDC/CI for external monitors is
  explicitly OUT OF SCOPE** — it is a rabbit hole and most desktop monitors will not answer. When WMI
  has no instance, report `canBrightness: false` and let the phone disable the control. **Do not spend
  session time trying to make an external monitor dim.**

**A4. `ProtocolConstants.Version` → 17.**

**A5. New `tools/pccontrolsim`** — the pure validator, every action, the confirm gate, `level`
out-of-range, missing `level`, unknown action. **It must not be able to invoke a real power action;**
say in the report how you structured it so that is true by construction.

---

# PART B — the phone side of v17

**B1. Quick-control widgets on the Tools surface M4a built.** Reuse `ToolsScreen`; do not create a
second surface.

**B2. `Protocol.kt`'s `PROTOCOL_VERSION` → 17, in this same session.** **Both constants move together —
that is the whole point of doing both sides at once.** M5c split them and shipped a split-brain window;
M6c moved both at once and did not. Follow M6c.

**B3. Destructive actions get a phone-side confirmation dialog AND `confirm: true` on the wire.** Both,
per the spec. A UI-only gate is not the contract.

**B4. Request `pc.state` when Tools opens and after any `pc.control` send**, and use
`canBrightness`/`canSleep`/`canShutdown` to **disable, not hide**, with a plain-language reason
(CONTRIBUTING.md — no jargon reaches the UI). Same for a negotiated version below 17.

**B5. Unit tests** for the payload builders and the confirm flag, in `testDebugUnitTest`.

---

## 3. Acceptance

1. Desktop MSBuild x64 Debug → 0 errors, no new warnings against the §0.2 baseline.
2. Android `assembleDebug testDebugUnitTest` → green; paste the test counts.
3. **Both version constants really are 17.** Paste both greps. A session that moves one and not the
   other has failed.
4. `pccontrolsim` green, exit 0.
5. **`cleanroomsim` green** (Part 0), with the elapsed-time message visible in its output.
6. Full regression green: `packagesim`, `presencesim`, `startupsim`, `applaunchsim`,
   `mirrorsettingssim`, `appssim`, `homelayoutsim`, `displaysim`, `homecachesim`, `storesim`,
   `synccachesim`, `outboxsim`, `apkinstallsim`, `autostartsim`, `syncsim`. `desktopsim` and `blescan`
   honest either way (**`blescan`'s rotating-id failure is pre-existing and hardware-dependent — do not
   chase it**).
7. **Two negative proofs against PRODUCTION source:**
   - Remove the `confirm: true` requirement from the validator → `pccontrolsim`'s destructive-action
     check fails → restore → green. **This is the safety gate; prove it exists.**
   - Break the 0–100 `level` clamp → the range check fails → restore → green.
8. **The owner's real `%LOCALAPPDATA%\Linc/settings.json` is unchanged** — size and mtime, before and
   after.

---

## 4. Report — to `Vibe/agent/reports/M4b.md` and printed

- §0.1 findings **with mtimes**, and which files changed during your session.
- Part 0: what was actually wrong with `cleanroomsim`, and whether my hypothesis held.
- How you structured `pccontrolsim` so it cannot reach a real power action.
- What `canBrightness` reports on this machine, and why.
- Files changed, one line each. Verbatim build, test and harness output. Both version-constant greps.
- Both negative proofs, naming the file you broke.
- **A numbered manual test for the owner**, covering every action — with the destructive ones **last**
  and clearly marked, and with "save your work first" stated in plain language.
- **What you could NOT verify and why.** **Every power action belongs on this list — you must not have
  run any of them.**
- Anything in this file, or in `PROTOCOL.md`'s v17 section, that was ambiguous, contradictory or wrong.

---

## 5. Out of scope

DDC/CI external-monitor brightness; multimedia transport keys and the slideshow remote (**M4c** — they
reuse `pc.media.control` and `pc.input`, no bump); the telephony notifier; **phone camera → PC webcam**
(its own milestone — it needs a Windows virtual-camera device); any change to `pc.input`, the mirror, or
what M4a shipped; an unsolicited PC-state push event (the spec rules it out deliberately); M13; editing
project `.md` files; committing anything.
