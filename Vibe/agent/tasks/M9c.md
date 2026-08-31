# TASK M9c — the offline outbox: queue a text while disconnected, flush it in order on reconnect

> **Agent for this session: Claude Code.** **Read this whole file before editing anything.** It is the
> complete specification. Every design decision is already made — implement it exactly. If something
> here is impossible or self-contradictory, **stop and report that**; do not improvise an alternative.
> Where this file is wrong, say so in the report — the last three sessions each caught a planner error
> and each catch was worth more than the feature.

---

## 0. Standing rules (non-negotiable)

- Workspace/project = **yellow** (`<repo-root>`). Product/code = **Linc**, at
  **`Linc`** — every relative path below hangs off that `Linc/` root.
- **Live docs (D-056):** `Vibe/agent/opencode-docs/`. Read **`DECISIONS.md`
  › D-044** (the store), **D-032** (offline device memory — the UI never blanks on disconnect), and
  `BRAIN.md`'s "Recurring gotchas" plus the new **M9b** gotcha about harnesses that re-implement the
  thing they test. Your behavioural guide: `Vibe/agent/claude-docs/CLAUDE.md`. Your rules:
  `Vibe/agent\{AGENTS,GUARDRAILS}.md`. Nothing auto-loads.
- **Code + tests only. Do not edit any `.md` file.** The planner owns all documentation.
- **Never move the real cursor. Never tap the phone. Do not install the APK.**
- **NO PROTOCOL WORK. v16 stays exactly as it is** on both sides. This session sends the *same*
  `sms.send` message the desktop already sends — it only changes *when*. If you conclude the feature
  cannot be done correctly without a protocol bump, **stop and report that** rather than bumping.
- Desktop build: kill any running `Linc.Desktop` (it is single-instance and **hides to the tray** on
  close — use `Stop-Process`, do not wait for exit), then
  `& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m`
- **Run every harness with CWD = `Linc`**, not `yellow/`.
- `git status` works from `Linc/` (**`yellow/` itself is not a repo** — a previous session got this
  backwards and skipped the check).

---

## Part 0 — close the M9b gap (do this first, it is small)

M9b's second negative proof was hollow and you are fixing the cause, not just the symptom.

**0.1 — the problem.** `tools/storesim` §8 proves "toggle off means an empty table" by writing its own
copy of the guard:

```csharp
var historyEnabled = reg8.NotificationHistoryEnabled;
if (historyEnabled) { await store8.InsertNotificationAsync(...); }
```

That is a faithful *model* of the real guard at `Services\NotificationSyncService.cs:182`, but only a
model. **Delete the production guard and §8 stays green.** The harness cannot call into
`NotificationSyncService` (it has WinUI-side dependencies), so the model is not itself wrong — what is
missing is any check that the real file still contains the real guard.

**0.2 — the fix.** Add a **crude source-text check** to `storesim` §8, in the style `displaysim` already
uses (M5c-3). Locate `DESKTOP/Linc.Desktop/Services/NotificationSyncService.cs` via the existing
`FindRepoRoot` helper and fail if **either** of these is absent:

- the identifier `NotificationHistoryEnabled` appears on a **non-comment** line in that file, and
- that same line also contains `InsertNotificationAsync` **or** is within 5 lines above a line that does.

Comment it as deliberately crude, per house style, and say in the comment **why** it exists: the harness
models the guard, this check proves the modelled guard is still the one production runs.

**0.3 — the retention message defect.** `ViewModels/SettingsViewModel.cs` → `SetRetention` sets
`StatusMessage = $"History kept for {days} days. Older entries were removed."` unconditionally, so
widening 30 → 90 claims a prune that never happened. Make the second sentence conditional on the prune
actually having been issued (`days < oldDays`). Widening should read
`$"History kept for {days} days."` and nothing more.

**0.4 — negative proof for Part 0.** Delete the guard from **`NotificationSyncService.cs`** (the
production file, not the harness), run `storesim`, show the new check **FAIL**, restore it, show green.
This is the proof M9b was supposed to produce. Paste both outputs.

---

## 1. What M9c is

Today, sending a text from the PC while the phone is not connected simply fails: `SyncViewModel.SendReplyAsync`
calls `_connection.SmsSendAsync(...)`, a `LincException` comes back, and the user gets a lane message. The
text is lost.

**M9c makes that text wait instead.** It goes into the `outbox` table M9a already created, and it is sent
when the phone comes back — **in the order it was queued**, one flush at a time.

This is deliberately the *smallest* useful queue. See §5 for what is explicitly not in it.

---

## 2. Decided design — implement exactly

**2.1 One queued action kind this session: `sms.send`.** The `outbox.kind` column exists so later
milestones can add kinds; you are adding exactly one, and the code must read as though a second kind is
coming (switch on `kind`, do not hardcode a single path). Use the literal kind string `"sms.send"`.

**2.2 Queue only when actually disconnected — never as a retry-on-error.** The decision point is
`supervisor.State != LinkState.Connected`, checked **before** attempting the send. If the state says
Connected and the send then throws, that is a **failure, not a queue** — surface it the way the code
does today. Rationale: a send that fails while the link is up has a real reason (no SIM, permission
refused, malformed address) and silently retrying it forever would hide a bug from the user and could
double-send. **Do not add a catch-and-queue fallback anywhere.**

**2.3 Ordered: flush by `id` ASC, per serial.** `id` is `AUTOINCREMENT`, so insertion order is queue
order. Flush the rows for the currently-paired serial only.

**2.4 One flush at a time, ever.** A reconnect can fire `StateChanged` more than once, and a slow flush
must never overlap itself. Guard with a single in-flight flag (the house pattern — see
`_appsRefreshInFlight` in `HomeViewModel`, added in M6c). A second trigger while a flush is running is a
**no-op**, not a queued second flush.

**2.5 Stop the flush at the first failure.** If sending row *N* throws, **stop** — do not skip it and
carry on to *N+1*. Skipping would reorder the user's messages, which is worse than delaying them. The
remaining rows stay queued for the next reconnect.

**2.6 Delete a row only after the send returns successfully.** `CompanionClient.SmsSendAsync` awaits an
`Ok` reply, so a returned task means the phone accepted it.

**2.7 Attempts are capped at 5.** On a failed attempt, increment `attempts` for that row. A row that has
already reached 5 is **dropped** at the head of the next flush (not attempted again), and the user is
told, in plain language, that a queued text could not be sent. Do not let one poisoned row block the
queue forever.

**2.8 The user must be able to tell a text is queued rather than sent.** In `SyncViewModel.SendReplyAsync`,
when the text is queued instead of sent, set `LaneMessage` to plain language along the lines of
*"Not connected — this text will send when your phone reconnects."* Keep appending the message to the
conversation as the code does today, so the user sees what they typed. **Do not** build a queue-management
UI, a pending-items list, or a cancel button this session.

**2.9 The flush must run regardless of which page is open.** Per `BRAIN.md`: anything that must run
independent of navigation belongs in `AppShellViewModel`, never a page's view model. The supervisor once
lived in the Device page and the app never connected unless you opened it — do not repeat that. So:

- New **`Services/OutboxService.cs`**, started from `AppShellViewModel` alongside `clipboardSync.Start()`
  and `share.Start()`. It subscribes to `IConnectionSupervisor.StateChanged` and flushes on a transition
  **into** `Connected`.
- `SyncViewModel` **enqueues** through this service; it does not flush.

**2.10 Nothing in this path may throw into the UI or block it.** Same stance as M9a/M9b: the flush is
fire-and-forget with a catch-all, and every `LincStore` method degrades rather than throwing. A phone that
reconnects must never be able to crash the app because the queue is malformed.

**2.11 Never log a message body.** `BRAIN.md`'s standing rule, and M9b's §2.9. Log that a text was queued
or flushed, and the row count — **never the text**. The `payload_json` on disk is the same consented
category as a stored notification body; a log line is not.

---

## 3. Known limitation you must NOT try to solve — name it in the report

**There is a crash window between the phone accepting a text and the row being deleted.** If the desktop
dies in that window, the next flush re-sends that text and the user's contact gets it twice. Closing this
properly needs a client-generated dedupe id carried on the wire and remembered by the phone — **a v17
protocol change, which is out of scope (§0).**

Do not invent a desktop-only workaround. Do not pretend the queue is idempotent. **Implement
delete-after-ack, and state the residual window plainly in your report** so the planner can scope the v17
bump. An honest limitation is a useful result; a false claim of idempotency is not.

---

## 4. The work

1. **`Services/LincStore.cs`** — typed methods over the existing `outbox` table: enqueue
   (serial, kind, payload json); list pending for a serial ordered by `id` ASC; increment attempts for a
   row; delete a row by id; count pending for a serial. Same lock + `TryOpen` + degrade-on-failure
   pattern as the notification methods. Keep them callable from a plain `net8.0` harness.
2. **`Services/OutboxService.cs`** — new. `Start()`, an `EnqueueSmsAsync(address, body)`, and the
   reconnect-triggered flush per §§2.3–2.7, §2.10. **Put the payload shape and the flush *decision* logic
   in pure static methods** (e.g. building and parsing the `sms.send` payload json, and deciding
   drop-vs-attempt from an attempts count) so the harness can test them directly rather than modelling
   them. This is the M9b lesson: **the more of this a harness can call, the less of it a harness has to
   re-implement.**
3. **`ViewModels/AppShellViewModel.cs`** — construct and `Start()` the service alongside the other
   eagerly-started services.
4. **`ViewModels/SyncViewModel.cs`** — the §2.2 decision point and the §2.8 lane message. This is the
   only view-model change.
5. **`tools/outboxsim`** — a new plain `net8.0` console harness, following `storesim`'s conventions
   exactly (numbered sections, `Check`/`CheckSub`, temp roots, exit 0/1, `FindRepoRoot` that handles both
   the `yellow/` and `Linc/` CWDs). It must `<Compile Include>` the real `LincStore.cs` and any pure
   logic file, and **name an explicit root at every `DeviceRegistry`/`LincStore` construction site
   (D-057)** — `homelayoutsim`'s `[10/10]` scan will check this, so run it.
   Cover: enqueue-then-list round-trip; **ordering by `id` across at least 3 rows, including after one
   is deleted from the middle**; attempts increment persists; a row at 5 attempts is dropped, not
   attempted; per-serial isolation (device A's queue is invisible to device B); delete-by-id removes
   exactly one row; and the pure payload build/parse round-trip.
   **Plus two crude source-text checks, in the Part 0 style, that read the production files:**
   `SyncViewModel.cs` contains a `LinkState.Connected` comparison in the send path, and
   `AppShellViewModel.cs` really starts the outbox service. Comment both as crude and say why.

---

## 5. Out of scope — do not build

- **File share queueing.** A queued file can be moved, renamed or deleted before the flush, which is a
  real design problem (copy to a spool? fail late? tell the user what?) and it belongs to **M10**, which
  owns sharing. Queue text only.
- Clipboard push, notification replies, notification actions, or call dialling while offline.
- Any protocol change, including a dedupe id (§3).
- A queue-management UI: no pending list, no cancel, no retry button, no badge.
- Cache-first Sync (**M9d**), a notification history *viewer* (**M9d**), icon/art blob storage.
- Touching `KnownDevice`, `settings.json` ownership, or the notification history feature beyond §0.3.

---

## 6. Acceptance — all must pass

1. Desktop MSBuild x64 → **0 errors**, no new warnings in files you touched. The `CS9113` on
   `HomeViewModel.cs:165` is pre-existing and expected.
2. `tools/outboxsim` green, exit 0 — paste full output.
3. `storesim` green **including the new Part 0 check**, exit 0 — paste full output.
4. `appssim`, `applaunchsim`, `homelayoutsim`, `mirrorsettingssim`, `displaysim` still green.
   `desktopsim`'s ADB section fails with no phone attached — **say so; do not weaken it.**
5. **Launch the built exe, confirm it stays alive ~10 s, then `Stop-Process` it.** Confirm the real
   `linc.db` still reports `schema_version = 1` and that **`outbox` is empty** — you never queued
   anything in production. Report the row count.
6. **Three negative proofs**, each broken → FAIL → restored → green, all outputs pasted. **Every one of
   these must break a PRODUCTION source file, not the harness's model of it** (this is the M9b lesson and
   it is the single thing most likely to go wrong in this session):
   - Part 0's: delete the history guard from `NotificationSyncService.cs` → storesim's new check fails.
   - Remove the outbox `Start()` call from `AppShellViewModel.cs` → outboxsim's start check fails.
   - Change the flush ordering from `ORDER BY id ASC` to `DESC` in `LincStore.cs` → outboxsim's ordering
     check fails.
   **Assert against temp-rooted stores only — no negative proof may write to the real store.** Rebuild
   after the restores and confirm the final build is clean.

---

## 7. Report format

- One result line per part, **written as you finish it**, not reconstructed at the end.
- Files changed, one line of reason each.
- **The §3 crash window, stated plainly** — what it is, when it fires, why you did not close it.
- Verbatim commands and results for the build, every harness, and the launch check.
- Explicit confirmation that **no code path logs a message body** (§2.11).
- All three negative proofs, and for each one, **name the production file you broke.**
- `git status` from `Linc/`.
- A numbered manual test script for the owner covering: send a text while connected (unchanged
  behaviour), disconnect and send two texts (both queued, lane message shown), reconnect (both arrive
  **in order**), and confirm nothing double-sends on a second reconnect.
- **What you could NOT verify and why.** Be specific — "no phone was attached" is the expected answer for
  the end-to-end path, and saying so is worth more than a confident guess.
- Anything in this task file that was ambiguous, contradictory or wrong. The planner wants this.
