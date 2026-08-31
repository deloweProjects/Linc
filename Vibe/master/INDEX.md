# Vibe/master — start here

This folder is the **master (orchestrator) session's** memory and context for the **Linc** project.
A fresh planning session should read this folder first, then work from the live agent docs.

## Read order
0. **`HANDOVER.md`** — the **paste-ready resume prompt**, kept current at all times. On "handoff" the
   owner just points a fresh session at this file. **The master must refresh its "STATE AS OF THIS
   HANDOVER" block at the end of every session** — a stale handover file is worse than none, because
   the resuming session will trust it.
1. **`ORCHESTRATOR.md`** — who you are (the master), the workflow, how to write agent prompts, and the
   full workspace layout. Its own paste block is the long-form original; `HANDOVER.md` is the live one.
2. **`STATUS.md`** — where the project is right now: roadmap position, what's done, what's in flight,
   loose ends. Update this every session.
3. **`AGENTS-REGISTRY.md`** — the coding agents (Antigravity, Claude Code, + new ones), how to run
   each, and the who-did-what-when ledger. Log every session here.
4. Then the **live agent docs** for canonical detail — start at `../Vibe/agent/gemini-docs/BRAIN.md`.

## Naming
- **`yellow`** = the project / workspace (`<repo-root>/`).
- **`Linc`** = the product being built (the code), under `yellow/Linc/`.

## The workspace, in one picture
```
<repo-root>/
├── Vibe/master\   ← you are here (master's context; you own + update this)
├── Vibe/agent\    ← agents' rules (AGENTS/GEMINI/GUARDRAILS) + NEMO.md + working docs (gemini-docs, claude-docs)
├── MCP\nemo_agent\← the nemo worker MCP server (free NVIDIA-hosted hands, managed by Claude Code)
└── Linc/          ← the product + resources (git repo): ANDROID, DESKTOP, SCRCPY, tools, Files
```

## Who does what
- **Master (this session):** thinking, sequencing, risk, writing prescriptive per-session agent
  prompts, and keeping ALL docs current (gemini-docs + STATUS + the registry ledger).
- **Coding agents:** production code + tests only, one at a time. See `AGENTS-REGISTRY.md`.
- **nemo:** a free NVIDIA-hosted **worker** managed by Claude Code (the hands, not the brain). Full
  guide + protocol: `../Vibe/agent/NEMO.md`. Trust its audit verdicts over its prose;
  never relay a `[REJECTED]`/`blocked` run as success.
- **Owner:** runs each prompt in the coding agent, tests hands-on, pastes results back.

## Source-of-truth note
`STATUS.md` here is the quick snapshot; the **canonical** project state is
`../Vibe/agent/gemini-docs/BRAIN.md`. If they ever disagree, BRAIN wins — then fix STATUS.
