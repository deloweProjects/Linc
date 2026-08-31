# TASK M7a — open a phone app in its own PC window (the launch path, end to end)

**Agent for this session: Claude Code.** Read this whole file before editing anything. Design
decisions marked **decided** are settled. Where it says *your judgement*, it genuinely is yours.

This session delivers a **working, hand-testable feature**: click an app tile on Home, that app opens
in its own window on the PC. M7b afterwards hardens it (virtualized grid, real lazy icons, per-app
window settings, multi-window management).

---

## 0. Orientation and standing rules

- Project root for every relative path below: **`Linc`**.
- **Live docs (D-056):** `Vibe/agent/opencode-docs/`. Read **`DECISIONS.md`
  › D-059** (the decision you are implementing), **D-053** (why this milestone exists), and
  `BRAIN.md`'s "Recurring gotchas". `Vibe/agent/claude-docs/` project docs are DEAD; only
  `claude-docs/CLAUDE.md` is live. Also `Vibe/agent/AGENTS.md`, `GUARDRAILS.md`.
- **Do not delegate to nemo. Never move the real cursor. Never tap the phone. Do not install the APK.**
- **Code + tests only. Do not edit any `.md` file.**
- **No protocol work.** v16 is correct and shipped both sides. This milestone adds **no** wire
  messages — everything rides scrcpy, locally.
- Desktop build: kill any running `Linc.Desktop`, then
  `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
- `git status` works from `Linc/`. Harnesses are safe (D-057) — do not reintroduce devicesim warnings.

---

## 1. The key fact — read D-059 before you plan anything

The roadmap's long-standing plan was per-app `--new-display` **plus** `am start --display <id> -n
<pkg>/<activity>` over ADB. **That plan is obsolete and you must not build it.**

**Our vendored scrcpy 3.3.4 supports `--start-app` natively.** Its own help shows exactly our case:
`scrcpy --new-display --start-app=+org.mozilla.firefox`. A leading `+` force-stops first. We always
know the exact package (v16's `apps.get` gives it), so we always pass `+<package>` — never the `?`
fuzzy form.

This means: **no `am start`, no ADB shell in the launch path, no activity resolution, and no display
ids to track or clean up.** scrcpy creates the virtual display, starts the app on it, and tears the
display down when its window closes. Confirm the flag exists in `SCRCPY/Custom/src/app/src/cli.c`
before building on it — do not take this file's word for it.

---

## 2. Decided design

**2.1 One `scrcpy` process per app window, keyed by package.** A new
`Services/AppLaunchService.cs`, modelled closely on the existing **`DesktopLaunchService`** — read
that first; it already solves process ownership, exit watching and plain-language error surfacing for
exactly this shape of problem. Differences: it holds a **dictionary** of package → process rather than
a single session, and it must stay correct when several apps are open at once.

**2.2 Launching an app that is already open focuses the existing window rather than starting a
second one.** **Decided.** Two windows of one app on one phone is not a feature, it is a bug that
looks like a feature. *Your judgement* on how to raise the window (`SetForegroundWindow` on the
process's main window handle is the obvious route) — and if you cannot do it reliably, **do nothing
and say so** rather than launching a duplicate.

**2.3 Geometry: reuse the M8 pixel-budget rule, phone-aspect.** `DesktopModeSettings` already has a
tested `ComputeRecommendedGeometry` that caps on a 1,440,000-pixel budget preserving aspect, applies a
640 floor, forces even dimensions and derives DPI as `round(160 * height / 900)` clamped 120–320. An
app window should look like a phone, so use the **MatchPhone** shape derived from the device's real
`wm size` / `wm density`. **Reuse that code — do not write a second geometry routine.** If it needs a
small refactor to be callable from here, do that rather than duplicating it.

**2.4 Per-app windows are NOT persisted this session.** No new `KnownDevice` field, no per-app
settings. M7b decides what is worth remembering. **Decided** — do not add a schema field "while you
are in there"; that is what forces every `tools/*.csproj` to change.

**2.5 The window uses our Custom scrcpy**, the same binary `MirrorService`/`DesktopLaunchService`
resolve through `ToolLocator` — so app windows inherit the frameless, rounded, draggable window from
M3.5. Pass the window title as the **app's label**, not its package.

**2.6 Failure is plain language.** scrcpy not found, phone disconnected, app refuses to start — each
surfaces as a readable sentence, never raw scrcpy output (the D-009 shell box is the only exception in
this product). Follow `DesktopLaunchService`'s existing error style.

**2.7 Teardown.** Closing the window ends the process and the virtual display with it (D-059). The
service must notice the exit and drop the entry so relaunching works. **Disconnecting the phone must
close every app window** — do not leave orphan windows pointed at a dead device; `MirrorViewModel`
already does this for the mirror on `StateChanged`, follow that precedent.

---

## 3. The work

1. **`Services/AppLaunchService.cs`** (new) — per 2.1/2.2/2.7. Keep the **argument construction in a
   pure static method** (the `DisplayPayload` / `MirrorService.BuildScrcpyArgs` pattern) so a harness
   can assert the exact command line without spawning anything.
2. **Geometry** — per 2.3, reusing the existing routine.
3. **`ViewModels/HomeViewModel.cs`** — a launch command on the app tiles. Guard it: no launch when
   disconnected. Surface `AppLaunchService` errors into a visible message on the Apps section.
   **Mind the established traps:** raise any composed visibility property at every source's change
   site (M6a), and do not let a status refresh re-trigger a launch (the echo class of bug — three
   occurrences so far).
4. **`Views/HomePage.xaml`** — the Apps tiles become **interactive**: remove
   `IsHitTestVisible="False"`, give each tile a click path to the command, and add a hover affordance
   so it reads as clickable. Keep the icon+name-only tile design and the `ItemsRepeater` /
   `UniformGridLayout` from M6c-2. **Do not** restructure the section or touch the `TabsPane`
   height-sync — that sync is load-bearing (see the comment in the XAML).
5. **`tools/applaunchsim`** (new; temp root per D-057) — assert the built command line exactly:
   contains `--new-display=WxH/DPI` with geometry from the shared routine, `--start-app=+<package>`
   (with the `+`, never `?`), the label as window title, the serial, and **no `am start` and no `adb
   shell` anywhere**. Also: a second launch of the same package does not build a second command;
   geometry matches `ComputeRecommendedGeometry`'s MatchPhone output for a Pixel-7-shaped device; and
   **UI wiring checks** that the tile binds the launch command and that `IsHitTestVisible="False"` is
   gone. Keep the crude-check comments.

---

## 4. Acceptance

1. Desktop MSBuild x64 → **0 errors**, no new warnings in files you touched.
2. `tools/applaunchsim` green, exit 0 — paste full output.
3. `appssim`, `homelayoutsim`, `mirrorsettingssim`, `displaysim` green. `desktopsim`'s ADB section
   fails with no phone — say so; **do not weaken it**.
4. **Two negative proofs**, broken → FAIL → restored → green, outputs pasted:
   - Change `--start-app=+<pkg>` to the bare package (no `+`) → `applaunchsim` must fail.
   - Re-add `IsHitTestVisible="False"` to the tiles → `applaunchsim` must fail.
   Rebuild afterwards; confirm both restores.

---

## 5. Report

Per-part result lines written as you go. Files changed with one-line reasons. **Confirmation that you
verified `--start-app` exists in the vendored `cli.c`**, and the exact command line your code builds
for one example app. How you handled the already-open case (2.2), including honestly if you could not
make focusing work. How disconnect tears windows down. Verbatim commands and results. Both negative
proofs. `git status` from `Linc/`. A numbered manual test script covering: launching one app,
launching a second app at the same time, relaunching an already-open app, closing a window, and
unplugging the phone with windows open. **What you could not verify and why.** Anything ambiguous or
wrong in this file — that is feedback the planner wants.

**Out of scope:** virtualizing the grid, real lazy icon fetching, per-app persisted window settings,
multi-window layout memory (all M7b); pop-out panels (deferred); any protocol change; any
`KnownDevice` schema change; restyling the widgets pane.
