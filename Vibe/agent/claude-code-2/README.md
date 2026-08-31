# `claude-code-2/` — the Claude Code 2 slot's own folder

## What lives here

- **`GUIDE.md`** — the behavioural guide. This is the file the task files point the agent at.
- Anything else that is **about the agent**: its permission notes, its known quirks, scratch notes
  from a session that are worth keeping but are not project state.

## What must NEVER live here — read this before adding a file

**No project-reality documentation. Not one file.** No `BRAIN.md`, no `ROADMAP.md`, no `PROTOCOL.md`,
no `DECISIONS.md`, no `CHANGELOG.md`, no copy of any of them.

There is **ONE live doc set for every agent: `Vibe/agent/opencode-docs/`** (D-056). The folder name is
historical — read it as "the live docs", not "opencode's docs". Every agent, including this one, reads
that folder and only that folder for project state.

### Why this rule exists

The project used to keep a full parallel doc set per agent. Look at the sibling folder
`Vibe/agent/claude-docs/` and check the timestamps: **every project file in it is frozen at Jul 23.**
It predates M5, M6, M7, M8 and all of M9. Its `BRAIN.md` is 164 lines against the live 301. Handing it
to an agent today would actively mislead — it describes a version of Linc that no longer exists.

That is not because anyone was careless. It is because **keeping 13 files in sync across two folders
is work nobody will do**, so it silently stops happening and the copy rots. D-056 retired the scheme
for exactly that reason, and the payoff was immediate: an agent switch went from a 13-file merge to
**one ledger line**. Two switches since then have each cost one line.

**So: a folder for the agent, yes. A folder for the project, never again.** If you find yourself
copying a project doc in here to make it "convenient," you are re-creating the thing that broke.

## Where the rest of it lives

| What | Where |
|---|---|
| Project state, protocol, decisions, roadmap | `Vibe/agent/opencode-docs/` (the live docs) |
| Enforced rules for every agent | `Vibe/agent/AGENTS.md`, `GUARDRAILS.md` |
| This agent's behaviour | `claude-code-2/GUIDE.md` (here) |
| Permissions / what it may run unprompted | `.claude/settings.local.json` at the workspace root |
| Task files (the actual work) | `Vibe/agent/tasks/<ID>.md` |
| Who did what, when | `Vibe/master/AGENTS-REGISTRY.md` |
