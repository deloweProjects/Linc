# M9c-amend — one correction before `tools/outboxsim/Program.cs` is written

> Amendment to `M9c.md`, which stays as written and remains authoritative for everything not named
> here. Read this alongside it, not instead of it.

## A1 — the flush must call `DecideDropVsAttempt`, not re-implement its comparison

**Found by the planner while the harness was still being written.**

`Services/OutboxService.cs` → `FlushAsync` currently decides the drop inline:

```csharp
if (row.Attempts >= MaxAttempts)
```

while the pure static exists separately:

```csharp
public static bool DecideDropVsAttempt(int attempts) => attempts >= MaxAttempts;
```

The two agree today, and that is the problem: **nothing keeps them agreeing.** A harness section that
exercises `DecideDropVsAttempt` at 0 / 4 / 5 / 6 is testing a helper the production flush never calls.
Change the flush's comparison to `> MaxAttempts` and every planned check stays green while the cap is
broken. **This is exactly the M9b failure — a correct test sitting next to, rather than underneath, the
behaviour it claims to cover.** M9c.md §4.2 asked for the pure static *so the harness would not have to
model the decision*; leaving the caller with its own copy re-creates the model in production.

**The change:** in `FlushAsync`, replace the inline comparison with a call to the static:

```csharp
if (DecideDropVsAttempt(row.Attempts))
```

Do not change `MaxAttempts`, the drop behaviour, the log line, or the static's body. There must be
**exactly one** expression of the cap rule in the file after this edit — grep `MaxAttempts` and confirm
the only remaining comparison against it lives inside `DecideDropVsAttempt`.

**Fold this into negative proof coverage:** with the flush calling the static, breaking the static
(`> MaxAttempts`) must now make a harness check fail. Add that as a **fourth** negative proof to
M9c.md §6.6, run the same way — break the production file, show FAIL, restore, show green, paste both.

## A2 — restating what §6.6 already requires, because this is the session's main risk

Every negative proof must break a **production** file. The planned §8 and §9 source-text checks are the
right tool for `SyncViewModel.cs` and `AppShellViewModel.cs` — the harness genuinely cannot call into
those. They are the wrong tool for anything the harness *can* call: ordering, attempts persistence,
per-serial isolation, delete-by-id and the drop decision must all be graded by real calls against a
temp-rooted store, so that breaking `LincStore.cs` or `OutboxService.cs` makes them fail.

**One specific trap in the ordering check (§2):** make the assertion compare the **full id sequence**,
not just the first element, and keep the delete-from-the-middle case in the same section. A loose
assertion can still pass under `ORDER BY id DESC` on some row shapes, which would silently void
negative proof 3.

## A3 — the build is stale; it is not optional

`Linc.Desktop.dll` is still dated **2026-08-02 20:05** — that is M9b's build. None of M9c's source has
been compiled yet. M9c.md §6.1 (clean MSBuild x64) and §6.5 (launch the built exe, confirm `outbox` is
empty in the real `linc.db`) are unmet and cannot be reported until after a real rebuild that
post-dates every restore.
