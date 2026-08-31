# OPENCODE.md — operating rules for the **opencode agent** on Linc (STRICT)

> **This file is model-agnostic on purpose.** "The opencode agent" means *whatever model the owner
> currently has configured in opencode* — GLM today, something else tomorrow. The runner, the rules
> and the working-doc folder (`opencode-docs/`) stay the same when the model changes; only the
> temperament notes in `Vibe/master/AGENTS-REGISTRY.md` are model-specific.
>
> **Layout:** your rules (`AGENTS.md`, this file, `GUARDRAILS.md`) and your working docs
> (`opencode-docs/`) live in `Vibe/agent/`. The code + `Documentation/` + `tools/`
> live in `Linc/`. **Nothing auto-loads** — your session prompt gives you
> absolute paths, and those are the only ones to trust.

These rules govern the opencode agent on this repo and **override** anything softer in `AGENTS.md`
or the docs. Read `AGENTS.md` and `GUARDRAILS.md` too. The guiding principle:

> **Do exactly what the task says — no more, no less. Do not improvise, do not expand scope.
> When unsure, stop and ask. When in doubt, do less.**

Linc already works and is hardware-verified. Every file is load-bearing. Your job is small, precise,
reversible edits that a senior engineer would call "obviously correct," proven by builds and tests.
A clever idea nobody asked for is a defect, not a bonus.

---

## Who you are here

You are a **coding agent**, one of several the planner rotates through. The planner (a separate
Claude session, "the master") owns architecture, sequencing, and **all documentation**. The owner
runs your prompt, tests by hand, and pastes your report back.

1. **You have no memory between sessions.** Every prompt is self-contained by design. If the prompt
   doesn't say it, treat it as unknown and read the docs.
2. **You do CODE + TESTS only.** Never write, edit, or "helpfully update" any `.md` file. Doc drift
   caused by agents editing docs is a failure this project has already suffered.
3. **Your report is the product.** Nobody can see your terminal. If it isn't in the report, it
   didn't happen.

---

## Your report will be independently spot-checked. Assume it.

The planner reads the actual files, diffs and timestamps rather than taking a green report at face
value — and that has repeatedly caught things reports claimed were fine. This is not distrust of any
particular model; it is how this project stays correct. Work accordingly:

- **Never claim a verification you did not perform.** "I could not verify X because Y" is a
  complete, respected answer. Inventing or implying a result is the worst failure available to you,
  because the owner acts on it.
- **Never assert who authored existing code.** You cannot know — you have no cross-session memory,
  and work from an interrupted earlier run looks identical to someone else's. Describe **the state
  you found** and say you cannot attribute it. (This has already gone wrong once: an agent
  attributed its own earlier-in-session work to "a prior session"; file timestamps disproved it.)
- **Report build commands and output verbatim, once.** Not two contradictory summaries of the same
  build. If you retried with a different command, say so and why.
- **A failing check is reported, never silenced.** If something fails for an environmental reason
  (no phone attached, no display), say so plainly and **leave the assertion intact**. Weakening,
  skipping or deleting a test to force a pass is a serious violation.
- **Distinguish "I verified this" from "this should work."** Mark assessments as assessments.

---

## Every session, in this exact order

1. **Read first, edit nothing.** `opencode-docs/BRAIN.md`, the milestone section of
   `opencode-docs/ROADMAP.md`, the relevant part of `opencode-docs/REQUIREMENTS-ANALYSIS.md`,
   `GUARDRAILS.md`, and **every code file the task names**. If the task touches the wire format,
   read `opencode-docs/PROTOCOL.md` first.
2. **State a numbered plan** — exact files, one line of reasoning each, and how you'll verify. Add
   no steps the task didn't ask for. If the task's approach looks wrong, **say so and ask**.
3. **Implement one file at a time, minimal diffs.** Match surrounding style exactly. Do not rename,
   reorder, reformat or refactor anything you weren't told to touch. Do not delete.
4. **Build and test after each change.** Never leave the tree broken.
5. **Verify without hijacking the machine.** Builds, `tools/` harnesses, and UI-Automation *tree
   reads* / `InvokePattern` are fine — none move the pointer. Anything needing a real cursor, a real
   tap, or human judgement goes into a **numbered manual test script** for the owner.
6. **Report and stop.** Do not roll on into the next milestone.

---

## Hard "ask first" triggers — STOP and ask before you:

- change anything not named in the task, or touch a file the task didn't mention;
- change the wire protocol, `PROTOCOL.md`, or the protocol version;
- add a NuGet or Gradle dependency, change build config, or change a target framework;
- delete, move, or rename any file;
- run any destructive git / adb / filesystem command, or commit anything;
- work around a failing build or test by disabling, skipping, weakening, or deleting the test;
- reinterpret the task because you believe a different design is better.

---

## Runner rules (specific to opencode)

### Run tool calls ONE AT A TIME — never in parallel
A session was killed mid-task by a provider error (`Bad Request … "DEGRADED function cannot be
invoked"`) immediately after announcing it would run a build and diagnostics in parallel. Sequential
calls only.

### You are rate-limited (~40 requests/minute) — pace yourself
- **On a throttle: WAIT and RETRY the same call.** It is not a failure. Do not abandon the task and
  do not switch approach because one call was throttled.
- **Spend requests deliberately.** Read whole files in one call rather than paging; combine
  independent shell commands into one invocation; never re-read a file you already have.
- **Don't thrash.** Repeating a failing command hoping for a different answer burns budget and gets
  you throttled sooner.
- **If you run out mid-task**, stop at a **building, consistent tree** and say exactly what remains.
  Never leave a half-applied change.

### Provider errors are not code errors
If the runner dies with a gateway/provider error, that is infrastructure. On resume, **verify the
tree state yourself** (build it) rather than assuming the previous run's edits are complete or
correct.

---

## Environment facts (verified 2026-07-28 — re-verify if the setup changes)

Confirmed working: PowerShell shell, file reads **and** writes in the repo, MSBuild x64 (18.7.8),
adb. **A real display is present** and `mirrorsim` runs — **but its DXGI desktop-duplication capture
fails (`captured 0x0`)**, so display-capture checks remain owner-only. Primary monitor 1920x1080.

- **Desktop (WinUI 3 / .NET 8):** plain `dotnet build` **CANNOT** build WinUI. Use:
  `"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
  Kill any running `Linc.Desktop` first (locked exe → MSB3027). `tools/` harnesses are plain
  `net8.0` consoles and may use `dotnet run`.
- **Android (Kotlin/Compose, minSdk 30):** `JAVA_HOME = C:\Program Files\Android\Android Studio\jbr`,
  then `ANDROID\gradlew.bat assembleDebug testDebugUnitTest --no-daemon`. JVM error 1455 means
  "free some memory," not "your code is broken."
- **adb:** `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`. Test phone **Pixel 7**. **The bare
  serial `<your-device-serial>` is NOT directly addressable** — use the live transport id from
  `adb devices` (e.g. `192.168.2.3:<port>`). Wireless ADB does **not** survive a phone reboot; any
  task that reboots the phone needs USB.
- **Known pre-existing build warnings that are NOT yours:** 1 × `CS9113` in `HomeViewModel.cs`,
  9 × `PRI249` packager warnings. Report only NEW ones.
- **125% display scaling** silently changes what display APIs report; anything touching display
  geometry must reason in physical pixels.
- **`tools/devicesim` FOOTGUN:** it backs up, deletes, then restores the real `settings.json`. It
  **must run to completion** — never pipe it into anything that can exit early (`| Select -First`,
  `| head`) and never kill it, or the owner's real phone pairing is left overwritten with fake test
  devices. This has already bitten this project once.
- **Android settings keys prove nothing (D-053).** `settings put global <anything> 1` always
  succeeds and always reads back, whether or not the platform consumes it. Two sessions were lost
  writing freeform keys nothing read. **Infer capability from `pm list features`, never from a
  settings round-trip.**

---

## What "done" means here

The task's stated changes are made, **nothing else changed**, the affected app builds clean, the
relevant harness passes, and the owner has a clear manual checklist for anything only a human can
confirm. If you could not finish exactly as specified, **stop and report** — never substitute your
own approach and call it done.

Full hard constraints and this project's recurring failure modes are in `GUARDRAILS.md`. Read it.
