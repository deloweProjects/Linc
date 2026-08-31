# TASK M4c — the phone as a media remote and a slideshow clicker

> **Agent: Claude Code 2.** Read first: `Vibe/agent/AGENTS.md`,
> `Vibe/agent/claude-code-2/GUIDE.md`, `Vibe/agent/GUARDRAILS.md`.
> Live docs: `Vibe/agent/opencode-docs/` (D-056). **Read `BRAIN.md`'s POSITION block first** — it now
> carries M15's findings, including the port-5038 change and the `packagesim` staleness trap below.
>
> **FOUR PARTS (0, A, B, C), ONE SESSION, SELF-CONTINUED.** Finish a part, re-read this file from the
> top, continue.
>
> **📄 REPORT TO `Vibe/agent/reports/M4c.md`** as well as printing it.
>
> **Report only what a file contains NOW.** Never claim you did *not* do something. **Do not delegate
> to `nemo`** (the repo-root `CLAUDE.md` says to; your guide §0.5 overrides — do not stop to ask).

---

## 0. FIRST STEPS

**0.1 — Inspect with last-write times**, and report `git rev-parse --abbrev-ref HEAD`,
`git log --oneline -3` and full `git status --short`.
**Expected:** branch `m13-hotspot-links`, HEAD `d64a1b7`, `main` three behind at `e23fbc3`, and
**M15a/M15b's work uncommitted on top** (`DeviceAdmission.cs` untracked, ~14 modified files). Report
any difference. **Do not commit, branch or merge.**

**0.2 — Build both sides before writing anything.** MSBuild x64 (kill `Linc.Desktop.exe` first);
`gradlew assembleDebug testDebugUnitTest`. **Expected baseline: 0 errors, 2 warnings (the duplicated
`CS9113` at `HomeViewModel.cs:166`), 117 Android tests.**

**0.3 — Two traps M15a paid for, so you don't:**
- **`packagesim` fails after ANY `DESKTOP/` source edit** until the Release profile is re-published
  (M12i's staleness guard doing its job). Re-publish; never touch the check. **Not a regression.**
- **Linc now runs its own ADB server on port 5038.** To talk to it by hand, set
  `$env:ANDROID_ADB_SERVER_PORT=5038` first, or you will be querying the wrong server.

---

## 1. PART 0 — kill the `syncsim` flake (poll, don't sleep)

`syncsim` has flaked since M12g's temp-root rewrite. Every harness that gained temp-root or
process-launch behaviour in M12f/M12g developed a timing flake; `cleanroomsim` had the same disease
and M4b Part 0 cured it by polling.

**Master-read:** `tools\syncsim\Program.cs:249-253` runs the reconcile six times with a fixed
`await Task.Delay(120)` between passes. **That is the same shape of bug** — a fixed wait standing in
for a condition.

**Replace it with a bounded poll:** loop until the state the assertions need is actually true, or a
ceiling (**30 s**, `cleanroomsim`'s number) elapses, checking every ~100 ms. On timeout **fail
loudly** with what was and was not true — a harness that silently gives up is worse than one that
flakes. Then run `syncsim` **five times in a row** and report every exit code.

**If it still flakes after the fix, say so and stop chasing it** — report what you observed rather
than tuning numbers until it goes green.

---

## 2. PART A — media transport controls on the phone's Tools page

### What already exists — PLANNER'S FINDING, verify the phone half before implementing
**Master-read on the desktop side:** `Services/PcMediaService.cs` already implements
**`pc.media.control`** with actions **`play` / `pause` / `next` / `prev`**, routes each to whichever
player the phone is showing, falls back to `WinampRemote`, and pushes `pc.media.state` back
(`:255-285`, doc comment at `:12-17`). It dates from Era 1 (M19, D-028) and is driven today from the
**phone's Home media widget**.

**I did not read the phone side** — the bridge was down. **A1: read it first**
(`service/MediaBridge.kt`, `service/OutgoingActions.kt`, `ui/HomeScreen.kt`) and report what is
already wired. **If the Tools page can reach this with no new plumbing, say so and do not invent
any.** The whole point of M4c is that the wire does not move.

### A2 — surface it on Tools
The Tools page (`ui/ToolsScreen.kt`, from M4a/M4b) gets a **Media** block: play/pause, next,
previous, and volume up/down/mute.

- **Transport buttons reuse `pc.media.control`.** No new message type.
- **Volume reuses `pc.control`** (v17, M4b) — `PcControlService`/`PcControlValidator` already carry
  volume, and `canBrightness`/`canSleep`/`canShutdown` already teach the phone to **disable, never
  hide**. Follow that pattern exactly: a control the PC cannot honour is disabled with a reason, not
  missing.
- **Show what is playing** if `pc.media.state` already gives it to you for free. **If it does not,
  skip it** — do not add a fetch.

---

## 3. PART B — the slideshow clicker

A **Presentation** block on the same Tools page: **next slide**, **previous slide**, **start**
(F5), **end** (Esc), **black screen** (B).

**Reuse `pc.input`** — the v14 message M4a proved works with **no mirror session open**
(`MirrorControl` calls `CompanionOutbox.trySend(envelope, requiredVersion = 14)` and nothing
references a streaming flag or channel 5). Send real key events: Right/Down/Space for next,
Left/Up for previous, F5, Escape, B.

- **Put the key mapping in a pure object** (the `KeyboardMapping.kt` pattern M4a established) so the
  Android unit tests cover it with no `Context`.
- **Reuse `KeyboardMapping` if it already carries these keys** — read it before writing a second
  mapping. Two drifting copies is exactly what M4a merged away.
- **No app detection, no PowerPoint integration.** These are key presses to whatever has focus. Say
  that in the UI in one short line, so nobody expects it to know what a slide is.

---

## 4. PART C — two stale doc comments (one-line fold-in)

`ViewModels\HomeViewModel.cs:319` and `ViewModels\SyncViewModel.cs:173` still describe the offline
banner as *"Phone disconnected — showing what was last synced at {time}."* M15b removed that prefix.
**Comments only — no rendered string says it.** Correct both to what the code now produces.

---

## 5. Acceptance

1. Both sides build clean; Android tests green; counts against §0.2. **New Android tests expected for
   the key mapping — say how many.**
2. **Both protocol constants still 18.** Paste both greps (`Protocol\Envelope.cs:12`,
   `Protocol.kt:12`). **No new message types.** If you believe a bump is needed, **stop and report** —
   do not bump it.
3. **A1's finding:** what the phone side already had, and what you therefore did *not* build.
4. `syncsim` **five consecutive exit codes**, plus the poll ceiling you chose.
5. Regression green: `packagesim` (re-publish first — §0.3), `cleanroomsim`, `pccontrolsim`,
   `hotspotsim`, `presencesim`, `startupsim`, `applaunchsim`, `mirrorsettingssim`, `appssim`,
   `homelayoutsim`, `displaysim`, `homecachesim`, `storesim`, `synccachesim`, `outboxsim`,
   `apkinstallsim`, `autostartsim`, `syncsim`. `desktopsim`/`blescan` honest either way.
6. **Two negative proofs against PRODUCTION source** (never a harness's own copy of the logic — the
   M9b lesson): (a) break one key in the presentation mapping → its unit test fails → restore →
   green; (b) make a PC-unsupported control render enabled instead of disabled → its check fails →
   restore → green. Paste the failing output for both.
7. **The real `%LOCALAPPDATA%\Linc/settings.json` — report a HASH before and after**, not just size
   and mtime. *(M15a's own suggestion: size and mtime cannot detect a one-byte change. Adopted.)*
8. **No negative proof may run through a real-data path.**

---

## 6. Report — `Vibe/agent/reports/M4c.md`

§0.1 with mtimes, branch, HEAD, full `git status` · §0.2 baselines · **A1's finding** · what you
built vs what already existed · the `syncsim` ceiling and five exit codes · files changed ·
build/test/harness output · both negative proofs with failing output · **a numbered manual test**
(play something on the PC, drive it from the phone with the mirror window closed; open any slide
deck and click through it) · **what you could NOT verify** · **anything in this file that was wrong.**

Write a one-line note into the report as you finish each part.

---

## 7. Out of scope

**Any protocol change** — the wire stays at 18. Camera → PC webcam (its own milestone, D-042). PC
brightness beyond what M4b already shipped. Album art over the bulk channel (phone-served only —
there is no reverse path; see `PcMediaService`'s doc comment). The dynamic-island status surface. The
`Linc/Files/` rewrite. Committing, branching or merging. Editing any project `.md` except your report.
Moving the real cursor. Tapping or scripting the phone.
