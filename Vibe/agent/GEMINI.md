# GEMINI.md — Antigravity operating rules for Linc (STRICT)

> **Layout (since 2026-07-26):** your rules (`AGENTS.md`, this file, `GUARDRAILS.md`) and working docs
> (`gemini-docs/`) live in `Vibe/agent/`; the code + `Documentation/` + `tools/` live
> in `Linc/`. Use the absolute paths in your session prompt.

These rules govern Antigravity on this repo and **override** anything softer in `AGENTS.md` or the
docs. Read `AGENTS.md` and `GUARDRAILS.md` too. The guiding principle:

> **Do exactly what the task says — no more, no less. Do not improvise, do not imagine, do not
> expand scope. When unsure, stop and ask. When in doubt, do less.**

Linc already works. Your job is small, precise, reversible edits that a senior engineer would call
"obviously correct," verified by builds and tests. A clever idea that wasn't asked for is a defect.

## Every session, in this exact order

1. **Read first (no edits yet).** Read `gemini-docs/BRAIN.md`, the milestone in
   `gemini-docs/ROADMAP.md` + `gemini-docs/FEATURES.md`, the relevant section of
   `gemini-docs/REQUIREMENTS-ANALYSIS.md`, and **every code file the task names**. If the task
   references the protocol, read `gemini-docs/PROTOCOL.md`. Also read `GUARDRAILS.md`.
2. **State a plan, then wait/stay on rails.** Post a short numbered plan: the exact files you will
   change and the one-line reason for each, plus how you'll verify. **Do not add steps that aren't
   in the task.** If the task's approach seems wrong, say so and ask — do not silently "fix" it.
3. **Implement the plan, one file at a time, minimal diffs.** Match surrounding style. Do not
   rename, reorder, reformat, or refactor anything you weren't told to. Do not delete anything.
4. **Build + test after each change** (see AGENTS.md §4). If it doesn't build or a test/harness
   fails, fix that before doing anything else. Never leave the tree broken.
5. **Verify without taking over the machine.** Use builds, the `tools/` harnesses, and — only if
   needed — UI-Automation *tree reads* / `InvokePattern` (these don't move the pointer). **Never**
   move the real cursor or tap the phone while the user may be working; write those up as a
   **numbered manual test script** for the user instead.
6. **Report and stop.** End with: exact files changed (and why), build + test results, the manual
   test checklist for the user, and a short "noticed but did not touch" list. Do not start the next
   milestone.

## Hard "ask first" triggers — STOP and ask the planner if you are about to:

- change anything not named in the task, or touch a file the task didn't mention;
- change the wire protocol / `PROTOCOL.md` / the protocol version;
- add a NuGet/Gradle dependency, change build config, or change target frameworks;
- delete, move, or rename any file; run any destructive git/adb/filesystem command; commit;
- work around a failing build/test by disabling, skipping, or deleting the test;
- reinterpret the task because you think a different design is better.

## What "done" means here

The task's stated changes are made, nothing else changed, both apps build clean, the relevant
harness passes, and the user has a clear manual checklist for anything only a human can confirm. If
you couldn't finish exactly as specified, stop and report — do not substitute your own approach.

Full hard constraints and the project's recurring failure modes are in `GUARDRAILS.md`. Read it.
