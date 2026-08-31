# M12c-amend — confirm before installing the companion APK

> Amendment to `M12c.md`, which stays authoritative for everything not named here. **Parts A and B are
> unchanged.** This adds a small **Part C**, to be done after Part B, in the same session and the same
> report.

## Why

The owner asked me to confirm that the desktop carries the companion app and offers to install it on a
fresh pairing. **I verified it does** — `ApkResolver` resolves `Assets/companion.apk` beside the exe,
`PhoneSetupService.IsInstalledAsync` runs `pm list packages`, `EnsureReadyAsync` returns early when the
app is present, and `OnboardingFlow` has an explicit `DetectPair → Install` step.

**But the owner's requirement was "ask and confirm with the user, of course", and one path does not.**
Inside the onboarding wizard the install is a visible step the user drives. `EnsureReadyAsync` is **also**
reached on an ordinary connect, outside the wizard, where it installs **silently**. Pushing an APK to
someone's phone without asking is not something Linc should ever do quietly.

---

# PART C — the non-wizard install path must ask first

## C1. Establish the truth before changing it

**Find every caller of `EnsureReadyAsync`** and report them. Determine, and state in your report:

- which paths reach it **during** the onboarding wizard (already user-driven — leave those alone), and
- which reach it on an **ordinary connect** with no wizard on screen.

**If it turns out every path is already user-driven, say so and change nothing.** That is a perfectly
good outcome and better than adding a prompt nobody sees. My reading of the code is a hypothesis.

## C2. Decided design

**C2.1 On a non-wizard path, ask before installing.** A plain-language confirmation naming what will
happen: that the Linc app is not on this phone, that Linc can install it now, and that the phone may
show its own prompt. Two choices — install, or not — with **not installing as a safe outcome** that
leaves the link in whatever state it can manage.

**C2.2 Use the house dialog pattern.** `SettingsPage`'s "Clear history" `ContentDialog` (M9b) is the
established shape for a confirmation. Follow it. **Do not restyle anything.**

**C2.3 Do not ask twice for the same phone in the same session.** If the user declines, respect it until
they reconnect or ask explicitly — a dialog that reappears on every reconnect attempt is worse than no
dialog. **Remember the decision in memory only; do not persist a refusal to disk.**

**C2.4 The wizard path is unchanged.** It already shows an Install step the user drives. **Do not add a
second confirmation on top of it** — that is exactly the kind of doubled prompt that makes software feel
hostile.

**C2.5 Never block the connection on the answer.** Same standing rule as M9: the prompt must not deadlock
the supervisor, and a declined install must not leave a half-connected state. Fire it on the UI thread
via the dispatcher; let the connection logic carry on.

**C2.6 Log the decision, not the payload.** "Companion install offered / declined / accepted" is fine.
No paths, no serials in the message beyond what is already logged elsewhere.

## C3. The work

1. **`Services/PhoneSetupService.cs`** and/or the calling view model — the confirmation gate on the
   non-wizard path only. **Put the "should we ask this phone?" decision in a pure static** (given:
   installed?, wizard active?, already declined this session? → ask / install / skip) so the harness
   calls the real logic rather than modelling it.
2. **`tools/apkinstallsim`** — extend with that pure static's truth table: already-installed → no ask;
   wizard active → no ask; declined-this-session → no ask; fresh non-wizard connect → ask. **Plus a
   crude source-text check** that the non-wizard path cannot reach the install call without passing the
   gate.

## C4. Part C acceptance

1. MSBuild x64 → 0 errors, no new warnings in files you touched.
2. `apkinstallsim` green, exit 0, with the new truth-table checks.
3. **One negative proof against a PRODUCTION file:** bypass the gate so the non-wizard path installs
   directly → the crude check fails → restore → green.
4. **Do not install anything on a real phone.** The end-to-end is the owner's manual step.

## C5. For the report

- **Every caller of `EnsureReadyAsync`**, classified wizard vs non-wizard.
- Whether C1 found a real silent path, or whether my reading was wrong.
- The truth table as implemented.
- A manual test line for the owner: with the companion app **uninstalled** from the phone, connect
  normally (not through the wizard) and confirm Linc **asks** before installing, and that declining
  leaves the app usable and does not re-prompt on the next reconnect in that session.

---

## Note on the `SCRCPY/Custom` rename — NOT in this session

The owner wants `SCRCPY/Custom` renamed, since it is Linc's own scrcpy and "Custom" no longer means
anything. **Agreed, and it is queued as its own task — deliberately not here.** That rename touches
`ToolLocator`, the csproj `Content` glob, the publish output layout, `.gitignore`, several harnesses and
the documented MSYS2 build recipe: **precisely the paths that have taken three sessions to stabilise and
that are still unconfirmed on the owner's second PC.** Renaming them before packaging is proven would
turn any new failure into an ambiguity. **Do not rename anything in this session.**
