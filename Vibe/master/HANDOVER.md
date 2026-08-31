# HANDOVER.md — paste-ready resume prompt for a fresh master session

> **What this file is.** When the owner says "handoff," they mean: start a **fresh copy of the
> master/orchestrator session** to reset a bloated context. They will tell the new session to read
> this file. It must therefore be **complete, self-contained and current at all times** — not a
> template to be filled in later.
>
> **RULE FOR THE MASTER: update the "STATE AS OF THIS HANDOVER" block at the END of every session,**
> as part of reconciling STATUS and the ledger. A stale handover file is worse than none, because the
> resuming session will trust it. If you have just QA'd a report or written a task file and have not
> touched this file, you are not finished.
>
> _Last updated: **2026-08-24** — see **STATE AS OF THIS HANDOVER — 2026-08-24** below; it supersedes
> every state block in this file. Headline: **M1–M14 done, protocol v18, but the commits sit on the
> unmerged branch `m13-hotspot-links`; the M15 session was lost and left nothing on disk; the active
> agent is Claude Code again; M15 is re-issued as `M15a.md` → `M15b.md`.**_
>
> _(previous) Last updated: **2026-08-12** — **THE WHOLE CONVERGENCE SERIES M1–M14 IS DONE AND COMMITTED.**
> Protocol **v18** both sides. Hotspot ADB link-up is **measured working on hardware — 514 ms, no
> discovery**. Nothing is in flight; no task file is queued._
>
> _**Active agent: the opencode slot, model `DeepSeek V4` on OpenCode Zen (free)** — a forced switch on
> 2026-08-11 when the owner's org disabled Claude subscription access for Claude Code. Its first two
> sessions (M13f, M14) were accepted; it diagnosed rather than guessed, and it caught a stale commit
> claim in a task file. Rules: `Vibe/agent/AGENTS.md` + `OPENCODE.md` + `GUARDRAILS.md`._
>
> _**Docs were reconciled in one pass on 2026-08-12:** `CHANGELOG.md`, `ROADMAP.md` (with a POSITION
> block that supersedes the July narrative) and `FEATURES.md` are current. **`Linc/Documentation/` was NOT
> rewritten** — it carries a prominent staleness banner in `00-` and a protocol warning in `06-`
> instead. Those eleven narrative chapters are closer to a rewrite than an edit and remain owed._
>
> _**What is actually left:** the owner's M14 visual checks · **M15** the live status surface
> ("dynamic island") in the Phone section — **design open, needs the owner's answer before it can be
> specced** · **M4c** multimedia + slideshow (no protocol bump, carries the `syncsim` poll fix) ·
> camera→PC-webcam as its own milestone · the `Documentation/` rewrite. The resuming master **does not wait to
> be asked** (owner directive, step 5)._
>
> _**Note for the resuming master, 2026-08-07:** the owner asked whether the master session could drive
> Claude Code 2 itself overnight, unattended. **It cannot** — the master has no way to type into a
> terminal, and its shell is a Linux sandbox that cannot run MSBuild x64, the WinUI toolchain or any
> `tools/*sim` harness. Writing code it cannot compile is the exact failure this project has fought since
> M9c. **The master's honest overnight work is QA, doc reconciliation and queuing task files** — do not
> promise an autonomous build loop._

---

## THE PROMPT — everything below this line is what the resuming session reads

You are the **technical planner / orchestrator ("master")** for the **Linc** project, resuming in a
fresh session to cap context. Everything you need is on disk under `<repo-root>`.

**FIRST, before anything else:**

1. Read `Vibe/master/INDEX.md` → `Vibe/master/ORCHESTRATOR.md` → `Vibe/master/STATUS.md` →
   `Vibe/master/AGENTS-REGISTRY.md`, then the live agent docs at
   `Vibe/agent/opencode-docs/BRAIN.md`. **Trust them over any memory.**
2. There is **ONE live doc set for every agent: `Vibe/agent/opencode-docs/`** (D-056). Read it as
   "the live docs", not "opencode's docs". The project-reality copies in `gemini-docs/` and
   `claude-docs/` are **DEAD** — stale by whole milestones, never read or update them. Only the small
   per-agent behavioural guides remain (`OPENCODE.md`, `claude-docs/CLAUDE.md`, `GEMINI.md`). An agent
   switch is one ledger line, not a doc merge.
3. Pay particular attention to ORCHESTRATOR.md → **"VERIFY, DON'T TRUST"** and the rules directly
   above its paste block. **Agent reports are claims, not evidence.** Every defect and every
   fabrication this project has caught was found by reading the actual files, diffs and timestamps —
   never by reading a green report. Recent proof:
   - a "Part 0 produced zero file edits" claim that timestamps disproved;
   - a completely dead UI sitting behind a clean build and a green harness;
   - a BRAIN "gotcha" the planner had written that turned out to be unmeasured folklore;
   - and, newest (M9b), **a negative proof that broke the harness's own copy of a guard instead of the
     production one** — so it proved nothing while reporting green.
4. **Do NOT ask which agent is resuming, and do not ask permission to write prompts.** STATUS names
   the active agent. The owner is a **courier**: you decide, write the task file, and tell them to run
   it. Deliverables are **task files** at `Vibe/agent/tasks/<ID>.md` — complete, self-contained,
   engineering pre-solved, with acceptance commands, a required report format, and an explicit
   out-of-scope list. **A task file is immutable once handed over**; mid-flight changes go in
   `<ID>-amend.md`.
5. **DO NOT WAIT TO BE ASKED. Owner directive, 2026-08-06.** Read the docs, give a **three-line**
   summary of where things stand, and then **immediately write the next task file and hand over the
   kickoff line.** Do not ask which milestone is next, do not ask whether to proceed, do not ask which
   agent — STATUS names it and the roadmap names the milestone. **You are the master: decide and
   deliver.** The owner is a courier who wants a task to paste, not a conversation.
   The only thing that ever stops you is a genuine product/UX decision that is theirs to make — and then
   you ask once, with a real question, and carry on.

### STATE — 2026-08-27 (NEWEST OF ALL) — FIRST PRODUCTION BUILD EXISTS

- **✅ M17a, M17b and M18 all DONE and master-QA'd.** `prod/` holds
  `Linc-Desktop-1.0.0-beta.1.zip` (170,988,255 B, extracted and verified),
  `Linc-Companion-1.0.0-beta.1.apk` (25,928,213 B, **debug-signed — no keystore exists**),
  `README.txt`, `CHECKSUMS.txt`. Both apps stamped **1.0.0-beta.1**; `prod/` is gitignored.
- **🔴 THE LESSON OF THE SESSION: the Pixel attached mid-M18 and immediately exposed three defects
  source review had missed** (worst: "Restart" rendering off-screen as an unreachable sliver).
  `desktopsim` passed for the first time. **Never treat a green build as evidence again.**
- **🔴 THE BETA GATE — do not lose these three, they are the agent's own words:**
  1. The **forced-update path can brick a user's app** and its blocking screen has never rendered.
     Ship with the manifest URL empty (proven inert).
  2. **47 font sizes were moved on desktop pages nobody has ever looked at**, `HomePage` most of all.
  3. **The phone↔PC link was never exercised** — the companion service was off all session, so
     **M16 step 4 (resume a paused player) remains unobserved.**
- **🟡 OPEN QUESTION FOR THE OWNER — dynamic colour is one-directional now.** `Theme.kt:80` renders
  the phone with the fixed Linc schemes (M17a removed the Material You path); the desktop **did** get
  `UsePhoneColours` (default ON). So the PC borrows the phone's wallpaper colours and the phone always
  wears the Linc palette. **The owner asked for "dynamic colours both sides" — one line from them
  settles whether the phone should follow its wallpaper too. Do not guess.**
- **Next, in order:** (1) the owner runs the published zip and looks at Home in both themes — that is
  the highest-value 2 minutes available; (2) the phone↔PC hand-test with the companion service ON;
  (3) the dynamic-colour ruling; (4) a real keystore if this ever goes beyond sideloading; (5) camera →
  PC webcam and the `Linc/Documentation/` rewrite.
- **Uncommitted again:** M17a/M17b/M18's work sits on `main` unmerged-to-nothing but uncommitted —
  the owner committed and merged before M17. **Commit the release work.**

### STATE — 2026-08-26, LATE (NEWEST OF ALL)

- **✅ M16 DONE and master-QA'd.** The phone can now **resume a paused PC player** (routing and
  publishing share one rule — `PcMediaRouting.PickTarget`, called at both sites); the Tools page has a
  **scroll host** and the trackpad a **280 dp minimum**; the Release publish is proven shippable
  (859 files, 104-file scrcpy bundle, `.pri` + `System.Management.dll`, `packagesim` 11/11 against
  that publish, and `publish/Assets/companion.apk` byte-identical to the fresh debug APK).
- **The planner was corrected twice, and both corrections are worth keeping:** there is **no `present`
  field** in the media payload (the phone greys buttons only on `none:true`), and on the Tools page
  **only the trackpad carried a weight**, so every card added came out of the pad.
- **🔴 THREE HAND-TESTS ARE NOW OWED, none observed:** M16's 8 steps (resume-from-paused is step 4;
  trackpad comfort is step 7), M15c's 8 steps (needs a **second Android phone**; step 8 is the
  cable-pull timing vs the old 19 s), and M4c's 20 steps. **A defect any of them finds outranks new
  work.**
- **🔴 STILL UNCOMMITTED: M15a/b/c, M4c and M16 — 32+ working-tree entries on `m13-hotspot-links`,
  with `main` three commits behind at `e23fbc3`.** The owner has the exact git commands; this is the
  largest single risk in the project right now and it is one minute of work.
- **Nothing is queued.** Remaining before v1, owner's call: the merge, camera → PC webcam (its own
  milestone, D-042), and the `Linc/Documentation/` rewrite (13+ milestones stale, banner-flagged). Cosmetic
  glitches are deferred by the owner to a bug-fix pass after v1.

### STATE — 2026-08-26 (NEWEST; supersedes every block below)

- **✅ M15c DONE and master-QA'd** (report 139 lines — the 150-line cap held first time). The one-phone
  limit is now a **choice** (Cancel / "Disconnect X and connect Y") and is stated in three places
  before the collision; a pulled USB cable is noticed in **~3-6 s instead of 19**.
- **🔴 HARDWARE FACT, MEASURED: a USB row vanishes from `adb devices` in 329 ms** — no stale entry, no
  `offline`. **This contradicts the comment at `ConnectionSupervisor.cs:360-363` (M13f's), which says
  adb's claim outlives the cable.** The comment was left in place, deliberately, and may still be true
  for a **wireless** `host:port` peer — untested. **Settle it before anyone touches M13f's ranking
  again; do not let it become folklore** (BRAIN's own rule about unmeasured gotchas).
- **🔴 OWNER HAND-TEST OWED, 8 steps at the end of the M15c report.** Steps 3-7 need a **second Android
  phone**; step 8 is the cable-pull timing to compare against the old 19 s. **The new drop path has
  never fired on hardware** — the 3-6 s number is derived from the measured poll interval, not observed.
  M4c's 20-step test is also still owed (media/slideshow, and the Tools column may be cramped).
- **Nothing is queued.** Next candidates, owner's call: merge `m13-hotspot-links` into `main` (three
  commits plus everything since — M15a/b/c and M4c are all uncommitted); the paused-SMTC-player bug
  (`PcMediaService` only routes to a *playing* session); camera → PC webcam; the `Linc/Documentation/` rewrite
  before release. The owner has deferred remaining cosmetic glitches to a bug-fix pass after v1.

### STATE — 2026-08-25 (NEWEST; supersedes every block below)

- **✅ M4c DONE and master-QA'd.** Report `Vibe/agent/reports/M4c.md`. Media transport + volume and a
  slideshow clicker on the phone's Tools page, `syncsim`'s flake killed (5/5 clean), two stale doc
  comments fixed. **Wire still 18.** Part A was mostly *not built* because the phone half already
  existed — the right outcome.
- **🔴 OWNER HAND-TEST OWED (20 steps, end of the M4c report).** Nothing hardware-tested. **Layout risk
  worth naming: the Tools `Column` is not scrollable and now holds four cards on `weight(1f)`** — the
  trackpad area may be noticeably smaller.
- **Fold into a future desktop milestone:** `PcMediaService` routes to SMTC only when `IsPlaying`, so a
  **paused** player cannot be resumed from the phone. Pre-existing; same on Home's widget.
- **`Vibe/agent/tasks/M15c.md` IS WRITTEN AND HANDED OVER (2026-08-25).** Part A: the one-phone limit
  stated *before* the collision — a Cancel / "Disconnect X and connect Y" choice plus a standing line
  wherever a phone can be added (owner's own words after hand-testing M15a: *"I forgot I had to
  disconnect the Pixel"*). Part B: **owner measured 19 s to notice a pulled cable**; the hypothesis is
  that `UsbWatcherService`'s existing 3 s `adb devices` poll is a ~15 s cheaper death signal that
  nothing consumes today — two consecutive misses → ~6 s worst case. **`HealthInterval` must not move.**
- **STANDING RULE ADDED: agent reports are capped at 150 lines** and agents are told to conserve their
  own tokens. The owner never pastes a report; the master reads it off disk.
- **Owner's M15 hand-test verdict:** blocks 3, 4, 5 green. Block 1 was not a bug — the one-phone limit
  simply was not announced (→ M15c A). Block 2 measured 19 s / 1 s / 20 s (→ M15c B). Remaining
  glitches are deferred by the owner to a bug-fix pass **after the first release**.

### STATE AS OF THIS HANDOVER — 2026-08-24, LATE (NEWEST OF ALL)

- **✅ M15a + M15b DONE and master-QA'd** (Claude Code 2, one session, both reports on disk at
  `Vibe/agent/reports/`). The chained-one-paste format worked: M15a's report was written before M15b
  opened. Full verdict in `STATUS.md`'s top section.
- **🔴 NOTHING IN M15 HAS MET A PHONE.** No device was attached for the whole session. The
  second-phone flow, the cable-pull timing, the sharp wallpaper, scrim legibility at `0.0`, and the
  Apps-off reflow are **all unobserved**. Both reports end with numbered manual scripts. **The one
  number to bring back is the log line "Connection dropped … Detection took NNNN ms …"** — that is
  Part B stage 2 measured on real hardware, which the agent could not obtain.
- **🔴 OWNER CONSEQUENCE OF A4: Linc now runs its own ADB server on port 5038** (`AdbServerHost.
  PrivateServerPort`, set in `App.xaml.cs:42` **above** the ServiceCollection — ordering is
  load-bearing). A wireless device already connected on the old 5037 server is not inherited, so the
  first run after this change may need one reconnect. Do not "fix" that as a regression.
- **Two planner lessons, both earned this session and both worth carrying:**
  1. **My Part A hypothesis was wrong for the sixth time** — the adb calls were already targeted. The
     real bug was two silent filters. `grep` locates; it never concludes.
  2. **My D1 scoping was the bug that hid this complaint three times.** I scoped the search to
     `Views/` and `ViewModels/`; the worst offender lived in `Services/HomeCacheFormat.cs`. **Scope
     enumeration tasks by behaviour, not by folder.**
- **Adopt in every future acceptance list:** "size and mtime before and after" cannot detect a
  one-byte edit — **ask for a hash.** And **`packagesim`'s M12i staleness guard fires after any
  `DESKTOP/` source edit** until the Release profile is re-published; name it so it is not read as a
  regression.
- **Cosmetic drift to fold into the next desktop task (master-found):** `ViewModels\HomeViewModel.cs:319`
  and `ViewModels\SyncViewModel.cs:173` still describe the old banner text in XML doc comments.
- **`Vibe/agent/tasks/M4c.md` IS WRITTEN AND PASTE-READY** (2026-08-24). Four parts: **Part 0** the
  `syncsim` poll fix (master-read: `tools\syncsim\Program.cs:249-253` is a fixed `Task.Delay(120)` in a
  six-pass loop — the same shape `cleanroomsim` had); **Part A** media transport + volume on the phone's
  Tools page, **reusing `pc.media.control`** (master-read: `PcMediaService.cs:255-285` already implements
  play/pause/next/prev from Era 1 — the phone half is UNVERIFIED and A1 makes the agent read it before
  building anything); **Part B** the slideshow clicker over `pc.input`, which M4a proved works with no
  mirror window; **Part C** the two stale doc comments. **The wire stays at 18.**
- **The owner's hand-test is `Vibe/master/HANDTEST-M15.md`** — five blocks, ~15 min. **A defect it finds
  outranks M4c**; the two can run in parallel because M4c touches neither the connection path nor Home.
- **Then:** camera → PC webcam as its own milestone (D-042), and the `Linc/Documentation/` rewrite.
- **Still true: the commits are on `m13-hotspot-links`, `main` is three behind at `e23fbc3`**, and
  M15's work is uncommitted on top of that. Raise the merge once; it is the owner's call.

### STATE AS OF THIS HANDOVER — 2026-08-24 (superseded by the block above)

- **M1–M14 are done. Protocol v18 both sides.** M13's hotspot link-up is measured on hardware at
  **514 ms**, no discovery. M14 shipped the black-and-white theme and the new Linc mark.
- **🔴 THE COMMITS ARE ON A BRANCH, NOT `main`.** Master-verified by reading `Linc/.git` directly:
  **HEAD = `d64a1b7` on `m13-hotspot-links`**; **`main` = `e23fbc3`, three commits behind.** All of M13
  and M14 are unmerged. `ROADMAP.md`/`CHANGELOG.md` say "committed" without saying where. Raise the
  merge with the owner once — it is their call. *(`git status` itself is NOT verified: the device shell
  was unavailable; the next agent's §0.1 reports it.)*
- **🔴 THE M15 SESSION WAS LOST AND PRODUCED NOTHING.** `tasks/M15.md` was handed to the opencode slot
  on 2026-08-12 19:30 local. There is no `reports/M15.md`, and **no file under `Services/`, `Views/` or
  `ViewModels/` has an mtime later than 2026-08-12 08:02 UTC** (which is M14's own work). Twelve days,
  no code.
- **AGENT SWITCH 2026-08-24: back to Claude Code 2** (owner's call). Rules `AGENTS.md` +
  `claude-code-2/GUIDE.md` + `GUARDRAILS.md`; live docs unchanged. **Pre-empt in the kickoff:** the
  repo-root `CLAUDE.md` used to auto-load and tell it to delegate to `nemo` (removed in M19-amend; the
  nemo protocol now lives only in `Vibe/agent/NEMO.md`) — its guide §0.5 overrides anyway, so say
  "do not delegate, do not ask"; restate the out-of-scope list in the kickoff, not only in the file.
- **M15 IS RE-ISSUED AND SPLIT: `Vibe/agent/tasks/M15a.md` → `M15b.md`, ONE PASTE, chained M13-style —
  each writes its own report before the next begins.** `M15.md` is left untouched on disk (immutable
  once handed over). **M15a** = adb targeting + `unauthorized` surfaced + the private adb port +
  `DecideDeviceSwitch`; **measure** the USB-disconnect delay rather than patching on top of M13f; a
  manual wireless override. **M15b** = enumerate every disconnected/error string and reduce to one;
  unblur the wallpaper; Apps-off collapses its row and gives the space to Notifications.
- **🔴 PLANNER CORRECTION ALREADY BAKED INTO `M15b.md`:** `M15.md` §5 claimed unblurring needs "no new
  plumbing, no new source." **Wrong.** `ThemeSyncService.cs:22-23` and `HomeViewModel.cs:1624` both
  document the image as the phone's **pre-blurred thumbnail**, fetched by
  `FetchBulkAsync("wallpaper", id, …)` (`ThemeSyncService.cs:158`); the producer is
  `WallpaperProvider.kt`. It is a **two-sided** change, no protocol bump. Marked as a hypothesis to
  verify, because the master read the three desktop sites but could not open the Kotlin file.
- **M15 is no longer the "dynamic island."** `ROADMAP.md` still names it that; the owner's bug reports
  displaced it. The island stays a later milestone — M14 left the Phone card as one composable seam.
- **Still owed:** the owner's six M14 visual checks; **M4c** (multimedia + slideshow, no bump, carries
  the `syncsim` poll fix as Part 0); **camera → PC webcam** as its own milestone; the `Linc/Documentation/`
  rewrite (13 milestones stale, banner-flagged).
- **Doc debt closed this session:** `STATUS.md` had no M13/M14 sections and the ledger had no rows for
  M13f/M14 — both now carry a dated position block. `BRAIN.md`'s narrative still stops around M5a; a
  POSITION block was added at its top rather than rewriting it.

### STATE AS OF THIS HANDOVER — 2026-08-03

- **Done:** M1–M3.5, M5, M6, M7, M8, M9a, M9b. Protocol **v16 both sides**. M9 is split a/b/c/d.
- **M9b (notification history — opt-in, default off) was QA'd and ACCEPTED.** Privacy posture is
  master-verified: one guarded persist at `NotificationSyncService.cs:182`, checked *before* the row is
  built; defaults `false` in both the initialiser and the load fallback; the toggle is `TwoWay` to a
  real save path (**not** the M5c-2 dead-UI shape); no `Log(` call in any touched file names a body.
  Three findings are recorded in STATUS: **a hollow negative proof** (fixed by M9c Part 0), a
  retention-widen message that claims a prune it never performed, and **a third occurrence of
  opencode's invented-narrative failure mode** ("live-verified previously in M13" — M13 has never run;
  as always, its file-content claims were accurate and only the narrative was invented).
### STATE — 2026-08-06 (supersedes everything above in this block)

- **M1 → M12c are DONE. Protocol v16 both sides. Everything is COMMITTED** — `d0ac417` on `main`,
  after twelve milestones with no recovery point. That risk is closed; keep it closed.
- **The app is packaged and RUNS ON A SECOND PC** — owner-confirmed, self-contained, no VC++ redist and
  no Android SDK required. Getting there cost three sessions and exposed three real defects, all of the
  same family: **the dev machine was quietly supplying things the package did not** (a missing `.pri`,
  an adb lookup that never checked Linc's own bundle, and a dev-tree path depth wrong since M3).
- **✅ M12d DONE + MASTER-QA'd (2026-08-07).** The `SCRCPY/Custom` → `SCRCPY/Linc.scrcpy` rename is
  followed through in all 7 files; the publish output carries 104 files incl. `adb.exe`/`scrcpy.exe`/
  `scrcpy-server` plus `Linc.Desktop.pri`. **The rename was already committed in `d0ac417` — my task
  file's "the owner renamed by hand, git will show a delete-plus-add" was wrong and the agent checked
  it.** Left behind: a new **`PRI249`** publish warning caused by the `.` in `Linc.scrcpy` (noise, output
  verified correct) → M12f Part B.
- **THEN `Vibe/agent/tasks/M12e.md` — WRITTEN AND QUEUED BEHIND M12d (2026-08-07, owner-requested).**
  Two parts, one session: **Part A** the tray-icon `ToSmallIcon` exception (non-fatal, invisible for the
  project's whole life until M12b's crash logging exposed it) — **diagnose from the real stack trace
  first**; the planner's hypothesis is the `ms-appx:///Assets/linc.png` `IconSource` at
  `MainWindow.xaml:158` in an unpackaged app, explicitly marked unverified. **Part B** "start Linc when
  I sign in" — HKCU Run key, opt-in default OFF, `StartupRegistration` with an injectable subkey (the
  D-057 pattern, so `autostartsim` can never write the real Run key), `--startup` activates then hides
  to tray (**must still `Activate()`** — M9e proved the view models are built synchronously inside it).
  **Do not run M12e before M12d has landed** — both touch `Linc.Desktop.csproj`.
- **THEN `Vibe/agent/tasks/M12f.md` — WRITTEN AND QUEUED (2026-08-07). This one is for the owner's
  patience, not the product.** They are out of patience with carrying builds to a second PC, and they are
  right to be. Part A builds `tools/cleanroomsim`: run the real `ToolLocator` lookups and then the
  published exe itself with `PATH` cut to `system32`, `ANDROID_HOME`/`ANDROID_SDK_ROOT` unset and
  `LOCALAPPDATA` redirected to a temp root — which reproduces **two of the three** historical second-PC
  defects locally and keeps the child out of the real store. Part B is the `PRI249` warning, **with
  explicit permission to give up** rather than ship a build hack. **Renaming `Linc.scrcpy` is not an
  option.**
- **THEN, in order:** **M4** (phone-as-remote Tools suite — the last real feature, needs a protocol
  bump) → **M13** (hotspot links; **starts with a hardware diagnostic session, not a design** — nothing
  about it is documented or tested).
- **`Vibe/agent/tasks/M12g.md` — WRITTEN (2026-08-07), unblocks M12f's launch half.** Adds a
  `--data-root <path>` switch (pure `ParseDataRoot`, DI factory at `App.xaml.cs:37`, `null` in every
  normal launch so production is byte-identical), then finishes `cleanroomsim` Half Two on top of it.
  **Reason: `LOCALAPPDATA` redirection does not work on Windows — see the 🔴 entry at the top of BRAIN.**
- **✅ M12 IS COMPLETE — M12d, M12e, M12f, M12g all DONE and master-QA'd (2026-08-07).** `D-057 is finally
  whole` (all four bypassing services now take `IDeviceRegistry.RootPath`; a `cleanroomsim` guard asserts
  the exact call count at each of the three sanctioned sites), `PRI249` is gone, the clean-room harness
  launches for real via `--data-root`, and `syncsim`'s real-store footgun is dead.
- **✅ M12h closed the tray/startup loop: the tray icon is owner-confirmed good and the startup toggle
  was proven working end to end (registry ON/OFF + restart survival). There was no defect** — the
  toggle had simply never been flipped. Reports now live in `Vibe/agent/reports/`.
- **✅ COMMITTED 2026-08-08 — `76c754b` on `main`**, M12d→M12h in one commit (28 files, +1277/-101),
  working tree clean. The recovery-point risk is closed again; keep it closed.
- **M4 IS SPLIT, AND `Vibe/agent/tasks/M4a.md` IS WRITTEN AND PASTE-READY.**
  **M4a — remote keyboard + trackpad — needs NO protocol change, and this was verified in both
  implementations before the spec was written:** `PROTOCOL.md:323`'s `pc.input` already carries
  `key`/`text`/`move`/`scroll` with `mode:"trackpad"` on the **control channel**, and
  `PcInputService.cs:35,67-71` treats `trackpad` as **relative** dx/dy applied with `absolute: false` —
  so it needs no video frame of reference. `MirrorControl.kt` is already service-level, not stuck in the
  Activity. **The wire stays frozen at v16 for M4a.** *(Planner correction: an earlier note in this file
  said M4's first step was the v17 spec. That was wrong — it is M4b's first step.)*
- **✅ M4a DONE (2026-08-09), OWNER HAND-TEST OWED** — Tools page with remote keyboard + trackpad, wire
  still v16. The phone was away the night it landed; the test is: open Tools with the PC connected and
  drive the cursor **with no mirror window open**.
- **`Vibe/agent/tasks/M4b.md` IS WRITTEN AND PASTE-READY, and v17 IS ALREADY SPECCED IN `PROTOCOL.md`**
  (2026-08-09, planner work — `pc.control` request/reply with an action enum, plus `pc.state.get` →
  `pc.state`). Three parts, one session, **both version constants move together** (M6c's pattern, not
  M5c's split-brain). **Part 0 fixes `cleanroomsim`'s deterministic failure first** — poll instead of a
  fixed 5 s sleep; if polling to 30 s still fails, `--data-root` is genuinely regressed and the session
  must stop there.
- **🛑 M4b IMPLEMENTS SHUTDOWN/RESTART/SLEEP. The task file forbids the agent from ever executing any of
  them** — the harness tests a pure validator that cannot reach a Win32 power call, and every power
  action goes to the owner as a manual test. **Keep that rule in any follow-up.** Confirmation is
  enforced **on the wire** (`confirm: true`) as well as in the phone UI, deliberately: a UI-only gate
  means one stray frame can power off the owner's PC.
- **✅ M4b DONE (2026-08-10) — protocol v17 both sides, moved atomically.** `Envelope.cs:12` and
  `Protocol.kt:12` both 17 (master-verified). **OWNER HAND-TEST OWED**, and the destructive actions are
  in it — the agent was forbidden from running any of them, so lock/sleep/shutdown/restart have **never
  been executed once**.
- **✅ M12i DONE (2026-08-10).** The publish is fresh (master-verified 05:11:50, newer than the newest
  desktop source), carries `System.Management.dll` and the `.pri`, and `packagesim` is **10/10** with a
  staleness guard that fails when the publish is older than the code. `canSleep` now uses
  `GetPwrCapabilities` (S3 **or** Modern Standby). **A second-PC copy is safe again.**
- **🟡 `syncsim` FLAKES — and it is probably NOT pre-existing.** It failed 3 checks and passed on
  rerun; the agent inferred "pre-existing", but `tools/syncsim/*.cs` are dated **2026-08-07 20:27:27 =
  M12g's rewrite**. **Pattern: every harness that gained temp-root or process-launch behaviour in
  M12f/M12g has since developed a timing flake** (`cleanroomsim` did, and M4b Part 0 fixed it by
  polling). **Give `syncsim` the same treatment as M4c Part 0.**
- **✅ COMMITTED `e23fbc3`** (M4a + M4b + M12i, 29 files, +2035). Tree clean at that point.
- **🔗 M13 IS WRITTEN AS A SELF-CONTINUING CHAIN: `M13a.md` → `M13b.md` → `M13c.md`, ONE PASTE.**
  Owner directive 2026-08-10: they want to paste once and get one combined report. **Each file writes
  its own report to `Vibe/agent/reports/` BEFORE moving to the next**, so a session that dies at M13b
  does not lose M13a. **Each file opens with a hard GATE that can stop the chain** — that is what makes
  one paste safe on a milestone where nothing has ever been measured.
  - **M13a** — role rule (`DecideRole`), address resolution with the **same-subnet filter**, connect
    path with hardcoded 5555, `tools/hotspotprobe` (the owner's one-command diagnostic) + `hotspotsim`.
    **No protocol change.**
  - **M13b** — **v18** (`adb.announce` / `adb.down` / `adb.arm` / `adb.ack`), already specced in
    `PROTOCOL.md`. **The `gen` counter is built first** and the liveness gate is mandatory.
  - **M13c** — self-arming via `WRITE_SECURE_SETTINGS` **granted inside M2b's existing onboarding ADB
    flow, so D-001 is not violated**, plus the speculative race and health loop.
- **🔴 THE HONEST LIMIT OF THE WHOLE CHAIN: the agent cannot bring up a hotspot** (phone taps are
  forbidden, and it would drag the owner's PC off its network). **So none of M13 is proven until the
  owner runs the manual script.** Every task file says so and forbids claiming otherwise. `hotspotprobe`
  exists precisely to turn that hand-test into data.
- **THEN M4c** (multimedia transport keys + slideshow remote — reuses `pc.media.control` and `pc.input`,
  **no bump needed**; carries the `syncsim` poll fix as Part 0), then **camera→PC-webcam as its own
  milestone** — it needs a Windows virtual-camera device and D-042 already flags it as heavier.
- **🟡 KNOWN FLAKE, FIX ON NEXT TOUCH:** `cleanroomsim` has a 5 s fixed-sleep log-settle race in the
  `--data-root` path and fails intermittently. **Poll for the log line instead of sleeping.** A flaky
  harness erodes the regime everything else depends on.
- **THEN M4 — and the first step is PLANNER work, not an agent session.** M4 needs a protocol bump, and
  the standing rule is that `PROTOCOL.md` gets the new version written **before any code**. So the master
  writes the **v17 spec** first, then splits M4 into gated sub-sessions. M4's scope is broad (keyboard /
  trackpad / multimedia / slideshow / sound / brightness, plus camera→PC-webcam as its own step) — **pick
  a first slice with the owner rather than speccing all of it.**
- **✅ THE HARDWARE BACKLOG IS CLEARED.** Every feature has now met the phone. The one hand-test session
  that cleared it found **four defects every harness had passed over.** **Schedule a hand-test at the end
  of every milestone group** — never let a backlog of unobserved features build again.
- `MaxConcurrentWindows = 6` in `AppLaunchService` is a precaution, **never a measured ceiling** (8
  windows survived M9e's testing). One line to remove when the owner is ready.

### HOW THIS OWNER WANTS TO WORK — learned the hard way, honour it

- **They are a courier and they are impatient in a productive way.** Give a task file, not a discussion.
  "Be the master" is a direct quote.
- **One session per milestone, both parts, self-continued.** Every task file says: finish Part A, then
  re-read the file from the top and start Part B without pausing, then one report at the end.
- **Every task file opens with §0.1 "inspect what is on disk and report it" and §0.2 "build immediately,
  before writing anything."** Both were earned: three sessions in a row left code that had never
  compiled, and one honest agent looked like a fabricator because a task file's description had gone
  stale within two hours.
- **The agent is good and has repeatedly been right when the planner was wrong.** It disproved two
  planner hypotheses by measuring them, caught a false pass in its own harness, and refused a pointless
  rename. **Mark your diagnoses "planner's hypothesis — verify before implementing" unless you have read
  every line.** `grep` locates; it never concludes.
- **M10 DONE** (install-APK from the PC; share sheet takes files; Share moved into the phone's Home).
- **M9e DONE — and it disproved BOTH planner hypotheses by measuring them.** The cache bug was **not**
  missing wiring (my diagnosis, from an incomplete trace) but a **startup race on `LincStore.IsAvailable`**,
  proven with instrumented timestamps. The app-window crash was **not** virtual-display count. Read
  `BRAIN.md`'s two M9e entries before planning anything in this area. **This is the standard: when the
  planner hands down a hypothesis, the agent measures it, and sometimes the planner is wrong.**
- **M9 IS COMPLETE (a/b/c/d-1/d-2), 2026-08-06.** SQLite store, opt-in notification history, the offline
  outbox, and cache-first Home + Sync with the queued-message affordance. **M9d-2 was owner hand-tested.**
  Next milestone is **M10** (sharing & install-APK) — but see the hardware backlog below first.
- **TWO RULES ADDED THAT EVERY TASK FILE NOW OPENS WITH** (§0.1 / §0.2, both earned the hard way):
  *inspect what is already on disk and report it before editing*, and *build immediately, before writing
  anything, then after each part*. Three consecutive sessions left production code that had never
  compiled, and one session's honest "this was already done" report looked like a fabrication until
  timestamps cleared it. Both rules have since paid off — keep them.
- _(historical)_ **M9c WAS IN FLIGHT and PARTLY LANDED.** `Vibe/agent/tasks/M9c.md` (the offline outbox) plus
  `M9c-amend.md` are both live. On disk: Part 0's storesim source-text check, the retention-message
  fix, `LincStore` outbox methods, `OutboxService.cs`, the `AppShellViewModel` wiring and
  `SyncViewModel`'s queue branch are all **done**. **Missing: `tools/outboxsim/Program.cs` (only the
  csproj exists), amendment A1, the build, and all four negative proofs.** The DLL is still M9b's.
- **AGENT SWITCH, 2026-08-03: the active agent is now `Claude Code 2`**, guide
  `Vibe/agent/claude-code-2/GUIDE.md`. The owner stood it up mid-M9c after the previous session emitted the
  **same implementation plan three times without writing the file**. Its guide leads with the anti-loop
  rule. **Its temperament notes and ledger result row are still owed** — fill them in from its first
  completed report.
- **Standing constraints:** the harness→real-data footgun is dead (**D-057**) and the old "never run
  devicesim" warning is **retired** — do not reinstate it. Agent rules live in AGENTS-REGISTRY
  (opencode: ~40 req/min, wait-and-retry, one tool call at a time, never claim what it did or did not
  do). **Every task file must forbid editing `.md` files, driving the real cursor, and tapping the
  phone.**

### THE OPEN ITEM TO KEEP VISIBLE

**Seven features are built and none has met the phone** — v15 display control, M6b's cards and
splitter, the Apps list, two rounds of right-column layout, and app windows. All structurally verified,
none observed. STATUS carries the consolidated checklist under the hardware-backlog heading. One phone
session with a freshly built APK clears the lot. **Raise it once when it is relevant; do not nag.**

### HOUSE STYLE

Concise and direct. **Before every task file, give the owner a short plain-language brief** of what the
agent will do and how they would tell whether it did the right thing. **In QA verdicts, say plainly
what you verified yourself versus what you are taking on trust** — the owner decides what to hand-test
based on that distinction.
