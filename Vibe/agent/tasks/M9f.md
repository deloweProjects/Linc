# TASK M9f — bare app windows, and stop leaking virtual displays

> **Agent: Claude Code 2.** Read `Vibe/agent/claude-code-2/GUIDE.md` first.
>
> **ONE SESSION, TWO PARTS, SELF-CONTINUED.** Finish **Part A** — code, build, harness, negative proof —
> then **re-read this file from the top and begin Part B as if freshly handed it.** One report at the
> end, covering both. Do not pause between parts. The answer is always continue.
>
> **M9e's measurements are the foundation of this task and they were excellent.** Do not redo them.
> Build on them.

---

## 0. FIRST TWO STEPS — before any work, and again at the start of Part B

**0.1 — Inspect what is already on disk and report it** for the files that part names.
**0.2 — Build immediately, before writing anything, and again after each part.**

---

## 1. Standing rules

- Workspace **yellow**; code **Linc** (`Linc`).
- Live docs `Vibe/agent/opencode-docs/`. Read `BRAIN.md`'s **two new M9e entries** (the virtual-display
  measurements and the system-decorations finding) and **D-053**, **D-059**.
- **Code + tests only. Do not edit any `.md` file. NO PROTOCOL WORK. Do not edit scrcpy's C source**
  (`SCRCPY/Custom/src`) — this project's history says that is the highest-risk surface in the tree, and
  nothing here needs it. You may **read** it, and you must (§A2.1).
- **Never move the real cursor. Never tap the phone. Do not install the APK.** You **may** launch scrcpy
  processes and run read-only `adb` (`dumpsys`, `logcat`, `shell wm`) — M9e established that is fine.
- Desktop build: `Stop-Process` any `Linc.Desktop`, then
  `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
- Harness CWD = `Linc`. **D-057:** explicit root at every construction site.
- **A phone may or may not be attached.** Report the honest result either way; never force a harness to
  match an assumption.

---
---

# PART A — app windows stop looking like Desktop Mode

## A1. Why

The owner: *"they seem to open in desktop mode when they open, weird."* M9e explained it — `dumpsys
window displays` shows a `StatusBar` on every virtual display because **system decorations are on by
default** and the launch args never turn them off. Not a bug, but not what the owner wants either.

## A2. Decided design

**A2.1 VERIFY THE FLAG EXISTS IN THE VENDORED BUILD BEFORE USING IT.** Read
`SCRCPY/Custom/src/app/src/cli.c` and confirm `--no-vd-system-decorations` is present in *this* vendored
version, and quote the line number in your report. **M7a set this precedent for `--start-app` and it was
the right instinct** — a flag that exists upstream but not in our build fails at runtime, not at compile
time, and the harness would never see it. **If the flag is absent, stop and report that** — do not
substitute a different flag.

**A2.2 Add it to the per-app window launch path only.** `Services/AppLaunchService.cs`'s
`BuildScrcpyArgs`. **Do not touch Desktop Mode's launch path** (`DesktopLaunchService`) — Desktop Mode is
*supposed* to look like a desktop; that is the whole feature (D-053). This flag is for per-app windows,
where the chrome is noise.

**A2.3 Nothing else about the args changes.** Not the geometry, not `--start-app`, not the uhid flags.
One flag added, everything else byte-identical.

## A3. The work

1. **`Services/AppLaunchService.cs`** — the flag in `BuildScrcpyArgs` (the existing pure static).
2. **`tools/applaunchsim`** — extend: assert `BuildScrcpyArgs` emits `--no-vd-system-decorations`, and
   assert **Desktop Mode's args do not** (guarding A2.2's boundary — a future session must not
   "unify" them).

## A4. Part A acceptance

1. MSBuild x64 → 0 errors, no new warnings in files you touched.
2. `applaunchsim` green, exit 0.
3. **One negative proof against a PRODUCTION file:** remove the flag → the check fails → restore → green.
4. **The `cli.c` line number for the flag, quoted in the report.**

**When Part A is green: re-read this file from the top, then start Part B.**

---
---

# PART B — stop leaking virtual displays on the phone

## B1. What M9e measured

`dumpsys SurfaceFlinger --display-id` showed **orphaned virtual displays (ids 60, 74, 75) still alive on
the phone after their owning Windows processes were confirmed gone.** scrcpy's own docs say the virtual
display is destroyed on exit — **true for a clean exit; a forced kill skips it.**

**This is the strongest candidate for the owner's original "open several apps and they all close"
defect**, which M9e could not reproduce at 4, 8 or 5-simultaneous windows. A leak compounds across a
session: open and close apps repeatedly and dead displays accumulate until something on the phone gives.
**That is a hypothesis, not a finding — treat it the way M9e treated mine, and measure.**

## B2. Decided design

**B2.1 Close windows gracefully; kill only as a fallback.** Find every place Linc terminates an app
window's scrcpy process — start from `AppLaunchService`'s close path and `CloseAllAsync`. If it calls
`Process.Kill()`, that is the leak. Replace with: **`CloseMainWindow()` → wait with a bounded timeout →
`Kill()` only if it is still alive.** A **3-second** timeout; if it expires, kill and **log that the
graceful close timed out** so a future session can see it happening.

**B2.2 Beware the house trap.** `BRAIN.md`: `Linc.Desktop` itself does **not** exit on `CloseMainWindow`
— it hides to the tray. **That is about Linc's own window, not scrcpy's**, and scrcpy is an ordinary
SDL app that does exit on `WM_CLOSE`. **Confirm this empirically** — launch one, `CloseMainWindow()` it,
and observe whether the process ends and the display is released. Do not assume either way.

**B2.3 Measure the leak before and after.** Same rigour as M9e. Record
`adb shell dumpsys SurfaceFlinger --display-id` before launching, after opening N windows, after closing
them the **old** way, and after closing them the **new** way. **Paste the actual output.** If graceful
close does not release the display, **say so** — that is a finding, and it redirects the fix rather than
wasting a session pretending.

**B2.4 If the leak is fixed, re-test the owner's original defect.** Open, close and reopen app windows
several times over — the pattern a real user produces, which M9e's steady-state test did not. **Report
whether the crash appears.** If it does not, say so plainly; if it does, capture `logcat` at the moment.

**B2.5 The cap of 6 stays for now.** Do **not** remove `MaxConcurrentWindows`. It is cheap insurance
until the owner has hand-tested the leak fix. **Recommend in your report whether it should go**, based
on what you measure — the owner decides, not you.

**B2.6 Do not attempt to destroy orphaned displays that already exist.** Cleaning up another process's
leaked display is out of scope and risky. **Report whether they persist across a phone reboot** if you
can determine it cheaply; otherwise say it is unknown.

## B3. The work

1. **`Services/AppLaunchService.cs`** — the graceful-close path (B2.1). **Put the decision logic in a
   pure static** (e.g. "given exited-within-timeout, should we kill?") so the harness calls it rather
   than modelling it.
2. **`tools/applaunchsim`** — extend: the pure static's boundary cases, and a check through the **real
   service** that closing a tracked window attempts the graceful path before any kill. Crude source-text
   check that `Process.Kill()` is not the *first* action on the close path; comment it as crude.

## B4. Part B acceptance

1. MSBuild x64 → 0 errors.
2. `applaunchsim` green, exit 0, with both Part A and Part B checks.
3. **One negative proof against a PRODUCTION file:** make the close path call `Kill()` immediately →
   the check fails → restore → green.
4. Full regression: `appssim`, `homelayoutsim`, `mirrorsettingssim`, `displaysim`, `homecachesim`,
   `storesim`, `synccachesim`, `outboxsim`, `apkinstallsim` all green.

---
---

## 5. One report, covering both parts

- Result line per part **as you finish it.** §0.1 findings and both §0.2 baselines.
- **The `cli.c` line number proving the flag exists** (A2.1).
- **Part B: the before/after `dumpsys` output, pasted.** Whether graceful close actually releases the
  display. Whether the owner's crash reappears under open/close/reopen cycling (B2.4). Your
  recommendation on the cap (B2.5), with the reasoning.
- Files changed, one line each. Verbatim build/harness output. Both negative proofs, each naming the
  production file broken. `git status` from `Linc/`.
- **A numbered manual test script**, leading with: open several apps, close them all, reopen several —
  repeat three times — and confirm nothing dies and the windows have no status bar.
- **What you could NOT verify and why.**
- Anything in this file that was ambiguous, contradictory or wrong. **M9e disproved both of my
  hypotheses by measuring them, and that was the most useful thing it did. Do the same here.**

## 6. Out of scope

Editing scrcpy's C source; destroying pre-existing orphaned displays; removing the window cap; the
one-off app cross-wire M9e observed (still unreproduced — leave it); Desktop Mode's launch path;
protocol changes; new features of any kind.
