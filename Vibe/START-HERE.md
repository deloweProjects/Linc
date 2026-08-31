# START HERE

**You are looking at how this project is actually built.** If you are an AI that has just been handed
this repository, or a person trying to work out what all these documents are — this page is enough to
get you working. Read it, then read the four files your role points you at.

---

## What Linc is

Linc connects an Android phone to a Windows PC — files, screen mirroring in both directions,
clipboard, notifications, media, messages and calls — over a USB cable or your own Wi-Fi. It talks
directly between the two devices using a **versioned JSON protocol** (currently **v18**) over ADB,
with a direct TLS channel as a fallback.

**There is no backend and no account.** This repository is the only infrastructure the project has:
even the update channel is a file in it (`Releases/update.json`).

It is an **established, hardware-verified codebase under refinement** — not a greenfield project.
Treat every existing file as load-bearing until you have proven otherwise.

---

## The two roles

Exactly one of these is you. They do not overlap, and the separation is the reason this works.

### The master (planner)

Decides *what* gets built and reviews everything. **Never writes production code.**

Reads, in order:

1. `Vibe/master/HANDOVER.md` — **the paste-ready resume prompt.** Self-contained and current; its
   "STATE AS OF THIS HANDOVER" block supersedes everything else.
2. `Vibe/master/STATUS.md` — the running state of the project.
3. `Vibe/master/ORCHESTRATOR.md` — how the loop is run.
4. `Vibe/master/INDEX.md`, `Vibe/master/AGENTS-REGISTRY.md` — what exists, and which agent slot is active.

Writes: task files into `Vibe/agent/tasks/`, and reconciles `Vibe/agent/opencode-docs/` after each
session. **The master owns every `.md` file in the project.**

### The agent (coding)

Turns one task file into working code, tests, and an honest report. **Never writes documentation**
other than its own report.

Reads, in order:

1. `Vibe/agent/RESUME.md` — **the paste-ready resume prompt.**
2. `Vibe/agent/AGENTS.md` — the shared engineering baseline.
3. `Vibe/agent/GUARDRAILS.md` — hard constraints and traps that have already cost someone a session.
4. `Vibe/agent/claude-code-2/GUIDE.md` — the behavioural guide for the currently active slot.
5. `Vibe/agent/opencode-docs/BRAIN.md` — current state, where things live, accumulated gotchas.

Then: the task file it was given, and the exact code files that file names.

> **`Vibe/agent/opencode-docs/` is the LIVE doc set.** The folder name is historical — read it as
> "the live docs", not "opencode's docs". `gemini-docs/` and `claude-docs/` are **dead**, stale by
> whole milestones, and must never be read or updated. They are kept only as history.

---

## The loop

```
  owner ──pastes a task file──▶  AGENT
                                   │  writes code + tests, builds, runs harnesses
                                   │  proves the negative cases
                                   ▼
                        Vibe/agent/reports/<ID>.md
                                   │
  owner ──pastes the report───▶  MASTER
                                   │  QAs by READING THE REPO, not the report
                                   │  reconciles opencode-docs/ + STATUS + HANDOVER
                                   ▼
                        Vibe/agent/tasks/<next>.md  ──▶ back to the top
```

The owner is a **courier** between the two. They relay text; they do not arbitrate engineering
decisions. **If a task file leaves something undecided, the agent decides it, states what it chose
and why in the report, and keeps going** — asking the owner to choose between engineering options
just stalls the session.

**The master QAs by reading the repository, never by trusting the report.** This project has been
burned repeatedly by green reports over broken features; that habit is the correction.

---

## The five rules an agent must never break

1. **The protocol is versioned and sacred.** Implemented twice (Kotlin + C#); mixed-version peers
   must keep working. Never change the wire format unless the task says so — and when it does, bump
   `PROTOCOL.md` first and gate on the negotiated version.
2. **Smallest possible change.** Only what the task requires. No refactors, renames or reformatting
   of adjacent code. Found a real problem outside scope? *Report* it; do not fix it.
3. **No stray files.** No scratch notes, task lists or one-off scripts left in the repo. Committed
   harnesses live in `Linc/tools/`; hand-off notes go in the report.
4. **Never claim you did not do something, and never call file state "pre-existing."** Report what a
   file contains *now*. Beliefs about how it got that way — including your own — are unreliable.
5. **Hand physical checks back as a numbered manual test script.** Never move the real mouse cursor,
   never script taps on the phone. The owner may be using the machine.

---

## If you have five minutes

Read these, in this order, and stop when the clock runs out:

1. **`README.md`** (repo root) — what Linc is and what state it is in.
2. **`Vibe/agent/opencode-docs/BRAIN.md`** — the single most useful file in the project: current
   state, where everything lives, and every gotcha the hard way.
3. **`Vibe/agent/GUARDRAILS.md`** — the things that will bite you, each of which already has.
4. **`Vibe/agent/opencode-docs/PROTOCOL.md`** — the wire contract.
5. **`Vibe/agent/opencode-docs/DECISIONS.md`** — D-001 onward, *why* things are the way they are.
6. **One recent report** in `Vibe/agent/reports/` — for the standard of evidence expected.

---

## Why the reports and the ledger are kept

`Vibe/agent/reports/` and `Vibe/agent/tasks/` go back to the beginning of the project. They are not
clutter and they are not archived-and-forgotten:

- They are **the project's memory.** When you wonder why a piece of code is shaped the way it is, the
  report that produced it usually says — including what was tried and rejected.
- They are **the QA record.** Every claim of "this works" has a report behind it naming what was run.
- They are **the best training material this project has.** Several of the standing rules above exist
  because a specific session failed in a specific way, and the report says exactly how.

Do not delete them, and do not rewrite them — a report is a record of what was true at the time, not
a document to be kept current.
