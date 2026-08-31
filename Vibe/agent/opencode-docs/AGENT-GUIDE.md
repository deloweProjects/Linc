# AGENT-GUIDE.md — how to work on Linc as GLM (via opencode)

> This is the **behavioural guide** inside your working-doc folder (`opencode-docs/`). Your enforced
> rules are `../OPENCODE.md` + `../AGENTS.md` + `../GUARDRAILS.md` — those win over anything here.
> This file is the practical "how do I actually get work done in this codebase" companion.

## The folder you're in

`opencode-docs/` is **your** copy of the project's working docs. Its project-reality content
(`BRAIN.md`, `ROADMAP.md`, `PROTOCOL.md`, `DECISIONS.md`, `FEATURES.md`,
`REQUIREMENTS-ANALYSIS.md`, the specs) is kept identical in meaning to the other agents' folders;
only the behavioural guides differ per agent. The planner syncs it. **You never edit it.**

Sibling folders you should know exist but must **not** write to:
- `../gemini-docs/` — Antigravity's folder (the previously active agent).
- `../claude-docs/` — Claude Code's folder.
- `../../Vibe/master/` — the planner's own memory. Off-limits.

Read order on a cold start: **`BRAIN.md` → the milestone in `ROADMAP.md` → the code files your
task names.** `BRAIN.md` is the canonical current state; if anything else disagrees with it,
BRAIN wins and you should flag the discrepancy in your report.

## The project in one paragraph

**Linc** links an Android phone to a Windows PC over ADB (wireless debugging or USB) plus a direct
app-to-app TLS channel: files, screen mirroring in both directions, clipboard, notifications,
media, messages, calls and live stats. The hard product rule is that **users never touch ADB or a
terminal** (the one deliberate exception is a power-user shell box in Settings). Two apps, **no
shared code**, one **versioned JSON protocol (shipped v14)**. Android 11+ only. The project is in
its "Convergence" phase — making the phone and PC drive each other.

- **Desktop:** `Linc/DESKTOP/Linc.Desktop/` — C# / .NET 8 / WinUI 3, MVVM Toolkit.
  `Services/`, `ViewModels/`, `Views/`, `Themes/`.
- **Android:** `Linc/ANDROID/app/src/main/java/app/linc/android/` — Kotlin / Compose.
  `service/`, `ui/`, `protocol/`.
- **Harnesses:** `Linc/tools/` — plain `net8.0` consoles (`probe`, `syncsim`, `filesim`,
  `devicesim`, `blescan`, `mirrorsim`, `apkprobe`, `onboardsim`, `desktopsim`). Run the relevant
  one before handing anything back.
- **Formal docs:** `Linc/Documentation/` — the settled human-facing documentation. Background reading.

## Conventions that are not optional

- **A silent catch is a bug factory.** If a path can fail and nobody awaits it, it must log. Several
  of this project's worst bugs were invisible fire-and-forget failures.
- **Every user-facing error is plain language.** Raw adb / scrcpy / exception text must never reach
  the UI. Log the technical detail; show the human a sentence they can act on.
- **Anything that must run regardless of the open page belongs in `AppShellViewModel`**, never in a
  page's view model. The connection supervisor once lived on the Device page, so the app never
  connected unless you opened that page.
- **Protocol changes bump the version in `PROTOCOL.md` first**, then gate behaviour on the
  negotiated version, and keep unknown-type/unknown-field tolerance. The protocol is sacred.
- **Never log clipboard contents or notification bodies** — only that an event occurred.
- **MVVM Toolkit partial properties** need `<LangVersion>preview</LangVersion>` and cannot have
  initializers; set defaults in constructors.
- **Not every file operation exists on every transport** — `TlsFileService` throws `NeedsAdb()` for
  delete/rename/mkdir/screenshot. Check `connection.HasAdb` or catch `LincException`.
- **Match the existing MaterialExpressive styling** when touching XAML. Copy a neighbouring
  control's brushes, corner radius and padding rather than inventing values.

## Verification etiquette (this project cares a lot about this)

Allowed freely: builds, unit tests, the `tools/` harnesses, UI-Automation **tree reads** and
`InvokePattern` — none of these move the pointer.

Not allowed while the owner may be using the PC: moving the real cursor (`SetCursorPos`,
`mouse_event`), or tapping the phone autonomously. Those become a **numbered manual test script**
in your report. Write that script for a human who is not you: name the button, say where it is,
say what should happen, and say what would count as failure.

A note on the desktop app: it is an unpackaged exe that screenshot tooling can't target, so it is
driven headlessly through UI Automation. A real click and `InvokePattern.Invoke` are not the same
test — a real click reveals swallowed clicks that `Invoke` hides. Also, the first click on a
window only activates it.

## Writing your end-of-session report

Structure it exactly as the task prompt asks. Absent other instruction:

1. Files changed, one line each, with what and why.
2. Build result — the exact command, and its real output, once.
3. Test/harness output in full.
4. `git status`, so the planner can see stray files.
5. The numbered manual test script for the owner.
6. "Noticed but did not touch" — real issues you spotted and correctly left alone. This list is
   valued; it is how the planner finds the next task.

Be precise about what you did **not** verify. The planner would much rather read "no device was
attached, so the on-device check never ran" than discover it later from a failed hands-on test.
