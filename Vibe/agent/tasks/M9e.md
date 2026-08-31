# TASK M9e — the four defects from the 2026-08-06 phone test

> **Agent: Claude Code 2.** Read `Vibe/agent/claude-code-2/GUIDE.md` first.
>
> **ONE SESSION, TWO PARTS, SELF-CONTINUED.** Finish **Part A** — code, build, harness, negative proofs
> — then **re-read this file from the top and begin Part B as if freshly handed it.** Do not report
> until both are done; give **one** report. Do not pause between parts to ask. The answer is continue.
>
> Part A's diagnosis is already done and is **prescriptive — implement it exactly.** Part B is a
> **diagnose-then-fix**: my hypothesis is stated, but you must measure before you change anything, and
> if the measurement contradicts me, **follow the measurement and say so.**

---

## 0. FIRST TWO STEPS — before any work, and again at the start of Part B

**0.1 — Inspect what is already on disk and report it** for the files that part names. Never assume you
are starting from zero.

**0.2 — Build immediately, before writing anything, and again after each part.** Never leave the tree
unbuildable.

---

## 1. Standing rules

- Workspace **yellow**; code **Linc** (`Linc`).
- Live docs `Vibe/agent/opencode-docs/`. Read **D-032** (the UI never blanks on disconnect), **D-044**,
  **D-045**, **D-053**, **D-059**.
- **Code + tests only. Do not edit any `.md` file. NO PROTOCOL WORK — v16 stays as is.**
- **Never move the real cursor. Never tap the phone. Do not install the APK.**
- Desktop build: `Stop-Process` any `Linc.Desktop`, then
  `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
- Harness CWD = `Linc`. **D-057:** explicit root at every `LincStore`/
  `DeviceRegistry` construction under `tools/`.
- **A phone may be attached this session.** If `desktopsim` passes 7/7 because a device is present, that
  is the honest result — report it, do not force a failure.

---
---

# PART A — the caches are written but never read on a cold start

## A1. The diagnosis (already done by the planner — verify, then implement)

The owner: *"even with history on, notifications are not cached, nothing is… messages send and queue ok
but when I completely close and open the app again, it's like a new connection, no cache."*

**Root cause, located.** In `ViewModels/HomeViewModel.cs`, `LoadCachedLaneWidgetsAsync()` is invoked from
**exactly one place** — line ~1181, inside the **not-connected branch of `RefreshLaneWidgets()`**. And
`RefreshLaneWidgets()` runs only from the supervisor's `StateChanged` handler.

**So on a cold launch with no phone there is no state *transition*, `StateChanged` never fires,
`RefreshLaneWidgets()` never runs, and the cache is never read.** The rows are on disk; nothing asks for
them. M9d-1's harness passes because it calls `LincStore` directly — **it proves the store, never the
wiring.** That is the real lesson of this defect and it is why A3.4 exists.

**Check `SyncViewModel` for the same shape** — its M9d-2 read-through was specified as "on construction
and on transition", so it may already be correct. **Report which of the two it is; do not assume.**

## A2. Decided design

**A2.1 The read-through runs unconditionally during construction**, in both `HomeViewModel` and
`SyncViewModel` — not only from a state-transition handler. It must run whether the app starts
connected, disconnected, or with no device paired at all.

**A2.2 It must not fight a live connection.** If the app starts already connected, the cache load may
still run (it is cheap and the lists are empty at that moment), but a subsequent live load **must
overwrite it**, never merge into it or duplicate rows. Live data always wins.

**A2.3 Keep it off the UI thread and non-throwing.** Same stance as M9a/M9b/M9c: fire-and-forget with a
catch-all, marshalled back through the dispatcher to touch the collections.

**A2.4 Notification history (defect 3) — find out why nothing is stored, then fix the cause.** The guard
at `Services\NotificationSyncService.cs:182` is
`if (registry.NotificationHistoryEnabled && registry.PairedSerial is { } serial)`. **My hypothesis:
`PairedSerial` is null at the moment a notification arrives**, so the second clause fails silently and
no row is written even with the toggle on. **Verify this before changing it** — add temporary
instrumentation if you must, then remove it. If `PairedSerial` is genuinely unavailable there, source
the serial from wherever the notification's own device context comes from; if it is available and the
real cause is different, **fix the real cause and say what it was.**

**A2.5 Apps caching "sometimes" (defect 4) — reproduce before you touch it.** The owner reports the Apps
list caches inconsistently. Apps uses M6c's own per-device **file** cache (`AppCatalog`), not
`sync_cache`. **Investigate and report what you find. Only fix it if you can state the cause in one
sentence.** If you cannot reproduce it, say so plainly and leave it alone — a speculative fix to an
unreproduced bug is worse than the bug. **Do not migrate Apps onto `sync_cache`.**

## A3. The work

1. **`ViewModels/HomeViewModel.cs`** — call the cache read-through from the constructor path (A2.1),
   keeping the existing transition call. Ensure live loads replace rather than merge (A2.2).
2. **`ViewModels/SyncViewModel.cs`** — the same, if and only if it has the same defect. Report either way.
3. **`Services/NotificationSyncService.cs`** — the A2.4 fix, once the cause is established.
4. **`tools/homecachesim`** — extend. **The check that would have caught this:** a crude source-text
   check that fails if `LoadCachedLaneWidgetsAsync` (and Sync's equivalent) appears **only** inside a
   state-transition handler and not on the constructor path. Comment it as crude and say exactly what it
   is protecting against — a future session must not be able to re-break this silently.
5. **`tools/storesim`** — extend with the equivalent guard for the notification-history serial path once
   A2.4's cause is known.

## A4. Part A acceptance

1. MSBuild x64 → 0 errors, no new warnings in files you touched (`CS9113` on `HomeViewModel.cs:166` is
   pre-existing).
2. `homecachesim`, `storesim`, `synccachesim` green, exit 0.
3. **Two negative proofs**, each breaking a **PRODUCTION** file, broken → FAIL → restored → green,
   **naming the file broken**: remove the constructor-path read-through call; and re-break whatever
   A2.4 turned out to be.
4. Temp-rooted stores only. No negative proof may write to the real store.

**When Part A is green: re-read this file from the top, then start Part B. Do not pause to ask.**

---
---

# PART B — app windows die when several are open

## B1. What the owner saw

*"Opening several apps makes them all close — scrcpy crashes, I think. And they seem to open in Desktop
Mode when they open, weird. Maybe they should open using scrcpy default, maybe that's why they crash."*

## B2. My hypothesis — measure it before you act on it

M7a launches each app window with `--new-display=WxH/DPI` (D-059), which asks scrcpy to create **a new
Android virtual display per app.** Open N apps and you demand N simultaneous virtual displays. **I
believe that is what falls over**, and it also explains the "opens in Desktop Mode" feel — a virtual
display is exactly what Desktop Mode (M8) uses, so each app window is effectively its own desktop.

**But this is a hypothesis, not a finding.** The last time this project enshrined an unmeasured guess as
fact it cost a milestone of wrong planning (`BRAIN.md`'s corrected `ItemsRepeater` entry). **Measure
first:**

- Read the vendored scrcpy source in `SCRCPY/Custom/src` for any documented limit on concurrent
  `--new-display` instances.
- Read `Services/AppLaunchService.cs` and confirm exactly what flags each window is launched with.
- **You may launch scrcpy processes yourself to observe behaviour** — that is not driving the cursor and
  not tapping the phone. Capture stderr from each. **Do not tap or swipe the phone.**
- Establish: does the crash correlate with a count? Is it the *newest* window that dies or *all* of them?
  Does the phone log (`adb logcat`, read-only) say anything at the moment of death?

**Report what you measured before you report what you changed.**

## B3. The fix — pick from these two, based on what you measured

**B3.1 If the virtual-display count is the cause (my expectation):** per-app windows should stop creating
one each. Launch them mirroring the phone's existing display and use `--start-app` alone, so N windows
means N views of one display rather than N displays. **Consequence you must state plainly in the report:
this changes the feature's character** — the apps will share the phone's screen rather than each having
an independent one. That may be the right trade or it may not, and **the owner decides after reading your
report, not you.** Implement the safe version; describe what was lost.

**B3.2 If the measurement says otherwise**, implement what the measurement actually supports, and state
the reasoning. If the honest answer is "this needs a cap on concurrent windows," implement the cap with
a plain-language message when it is reached.

**B3.3 Either way: a scrcpy process dying must not take the others with it.** Whatever the root cause,
each window's process should be independently supervised, and one dying must leave the rest alone and
update the open-window count correctly (M7b's counter must not drift).

## B4. Part B acceptance

1. MSBuild x64 → 0 errors.
2. `applaunchsim` extended with whatever the fix makes checkable — at minimum that the launch arguments
   match the decided design, via the existing pure `BuildScrcpyArgs`. Green, exit 0.
3. **One negative proof against a PRODUCTION file**: revert the argument change → the harness check
   fails → restore → green.
4. `appssim`, `homelayoutsim`, `mirrorsettingssim`, `displaysim`, `homecachesim`, `storesim`,
   `synccachesim`, `outboxsim`, `apkinstallsim` still green.

---
---

## 5. One report, at the end, covering both parts

- Result line per part **written as you finish it.**
- **What you found on disk (§0.1)** and **both §0.2 baseline builds.**
- **Part A:** confirmation of the root cause; whether `SyncViewModel` had the same defect; **what A2.4's
  real cause turned out to be**; and what you found for A2.5's Apps inconsistency — including "could not
  reproduce" if that is the truth.
- **Part B: what you measured, before what you changed.** If the measurement contradicts my hypothesis,
  say so directly — that is the most valuable line in the report.
- Files changed, one line of reason each. Verbatim build/harness output. All negative proofs, each
  naming the production file broken. `git status` from `Linc/`.
- **A numbered manual test script**, with the cold-start test first: connect, let data arrive,
  disconnect, **fully quit and relaunch, confirm everything is still there.**
- **What you could NOT verify and why.**
- Anything in this file that was ambiguous, contradictory or wrong.

## 6. Out of scope

New features of any kind; protocol changes; `KnownDevice` changes; migrating Apps onto `sync_cache`;
the M9c crash window (needs v17); restyling anything; M10's install or share work.
