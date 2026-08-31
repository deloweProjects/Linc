# AGENTS.md — Standing rules for AI agents working on Linc

> **NAMING.** The project / workspace is **`yellow`**; the **product** you build (the codebase) is
> **`Linc`**, under `yellow/Linc/`. There is also a free worker agent, **`nemo`** (managed by Claude
> Code) — see `Vibe/agent/NEMO.md`; you don't drive it unless told.
>
> **WORKSPACE LAYOUT (changed 2026-07-26).** Everything lives under `<repo-root>/`:
> - `Linc/` — the **code + resources** (git repo): `ANDROID/`, `DESKTOP/`, `SCRCPY/`, `tools/`, `Documentation/`.
> - `Vibe/agent/` — **your rules** (`AGENTS.md` = this file, `GEMINI.md`, `GUARDRAILS.md`) and your
>   working docs (`gemini-docs/`, `claude-docs/`). These are here, NOT at the Linc repo root anymore.
> - `Vibe/master/` — the human planner's orchestrator context (do not edit).
>
> So in the paths below: **`gemini-docs/` / `claude-docs/`** are `Vibe/agent/…`,
> while **`Documentation/` and `tools/`** are under `Linc/…`. Your session prompt gives
> absolute paths — use them.

Linc is an **established, working, hardware-verified** Android↔Windows companion app. It is under
**refinement** — we add features and fix bugs on top of a large existing codebase. It is **not** a
greenfield project. Treat every existing file as load-bearing until proven otherwise.

**One agent works at a time.** Each agent has its own working-doc folder; a human planner keeps the
active one current and writes the per-session task prompts.

## Which docs are yours

- **Antigravity** → your working docs are **`Vibe/agent/gemini-docs/`**. Your enforced rules are this file
  plus **`GEMINI.md`** and **`GUARDRAILS.md`** in **`Vibe/agent/`** — read all three before touching anything.
- **Claude Code** → your working docs are **`Vibe/agent/claude-docs/`** (including `claude-docs/CLAUDE.md`).
- **Both** → the formal project reference is **`Linc/Documentation/`** (start at
  `Documentation/00-Linc-Master-Documentation.md`). The canonical wire spec is `<your-docs>/PROTOCOL.md`;
  the decision log is `<your-docs>/DECISIONS.md`.

Do not edit the other agent's folder.

## The shared engineering baseline (non-negotiable)

1. **Read before you write.** Read your folder's `BRAIN.md`, the milestone in `ROADMAP.md` /
   `FEATURES.md`, and the exact code files named in the task, before making any change.
2. **Smallest possible change.** Touch only what the task requires. No refactors, renames,
   reformatting, or "improvements" to adjacent code. Match the existing style exactly.
3. **Don't invent.** No features, abstractions, files, dependencies, or scope beyond what the task
   states. If something is unspecified or ambiguous, **ask** — do not guess.
4. **Build and test after every change.** Desktop builds with **VS MSBuild x64** (plain
   `dotnet build` cannot build WinUI); kill any running `Linc.Desktop` first. Android builds with
   `gradlew assembleDebug testDebugUnitTest`. Run the relevant `tools/` harness.
5. **Self-verify, but never hijack the user.** Never drive the real mouse cursor
   (`SetCursorPos`/`mouse_event`) or script phone taps while the user may be using the PC/phone —
   hand those checks back as a numbered manual script.
6. **Errors stay plain-language.** Raw adb/scrcpy output never reaches the UI.
7. **Protocol is versioned and sacred.** Never change the wire protocol unless the task explicitly
   says so; bump the version in `PROTOCOL.md` first and gate on it.
8. **One milestone per session.** Finish it; don't start the next. End with a tight report.

`GEMINI.md` and `GUARDRAILS.md` tighten these further for Antigravity.
