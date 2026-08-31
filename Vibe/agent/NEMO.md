# NEMO.md — the nemo worker agent

**Project:** `yellow` (the workspace / project). **Product being built:** `Linc` (the code under
`Linc/`). nemo is a **worker**, not one of the Linc coding agents that edit
the app directly — it's a delegated pair of hands. **Managed by Claude Code** ("Claude Code is the
brain; nemo is the hands"). Canonical live protocol: **this file** (entry point: `Vibe/START-HERE.md`); design rationale: `<repo-root>/MCP/nemo_agent/ORCHESTRATION.md`.

## What it is
- An **MCP server** at `<repo-root>/MCP/nemo_agent/` (`server.py`) that wraps
  `nvidia/nemotron-3-ultra-550b-a55b` (256K context in, ~16K tokens/generation) on NVIDIA's **free
  tier** (request-capped). nemo's work costs nothing; the **orchestrator's context is the scarce
  resource** — delegate to save it.
- **Identity sigil:** `agent_info()` returns **`NEMO-CHILD-OF-CLAUDE::YELLOW-01`**, also stamped on
  the first line of every report. Seeing that sigil = this worker belongs to the Claude that drives it.

## Who drives it
- **Claude Code** manages nemo day-to-day (holds the `nemo` MCP, writes the specs, relays results up).
- **The owner never talks to nemo** — they're a courier pasting tasks down and results up.
- From the **Linc master's** seat: nemo is a sub-worker under Claude Code. Treat its results the way
  Claude Code relays them; if you ever drive it directly, the same rules below apply.

## Two personas — same worker, two ways to drive it

**1. "Unpure" nemo — under the hood of Claude Code (the current / default path).**
nemo runs *inside* Claude Code via the `nemo` MCP (`run_task`, etc.). The master and owner don't see
nemo directly — they see **Claude Code**, which delegates the actual building to nemo. So a prompt
"for unpure nemo" is really a prompt **addressed to Claude Code**, telling it to stay lean and push
the work down to its child. See the opening convention below.

**2. "Pure" nemo — the standalone console (`nemo.cmd`).**
The **owner sits at the keyboard** and drives the same worker directly, no Claude in the middle:
`<repo-root>/MCP/nemo_agent/nemo.cmd` (run from any directory). It narrates each step,
keeps follow-up context ("now add tests and run it" resolves "it"), and shows a done-summary with
token counts + diffs. **Approval-gated:** `[y] yes [n] skip [a] always [q] abort` (reads are free;
`--auto` or `/auto` turns the gate off). It asks a plain-text question when a request is genuinely
ambiguous instead of guessing. Same anti-cheat integrity check runs before every report. Slash
commands: `/help /cwd /new /auto /steps /verbose /info /log /exit`. One-shot too:
`nemo.cmd "add a --json flag to cli.py and verify it"`. Console and MCP share `server.run_loop`.
**Caveat:** the console runs the newest code directly; the **MCP path needs a restart** to pick up
server changes.

## Opening convention — prompts for "unpure" nemo (address Claude Code, not nemo)

Because unpure nemo is really Claude Code delegating, **open every such prompt by talking to Claude
Code** and telling it to conserve itself and offload. A good opening does all of this:

- "You are Claude Code. **Your context/limits are the expensive resource — conserve them.**"
- "**Do NOT implement this yourself and do NOT over-reason or go exploring.** Be extremely efficient."
- "**Hand the heavy/tedious work to your child worker `nemo`** via `run_task` (write one self-contained
  spec with an acceptance command; raise `max_steps` for big jobs)."
- "**Read only nemo's report**, trust the audit lines over the prose, and **report back to me concisely** —
  don't round up a `[REJECTED]`/`blocked` run."

In short: tell Claude to be lean, delegate the heinous work to nemo, not reason much, just issue
orders → collect nemo's output → report.

## Spec template — use it every time
`CONTEXT / TASK / DESIGN / ACCEPT / NOTES`. Write it like a brief to a capable colleague who just
walked in — terse orders get literal-minded work. nemo's system prompt now addresses "the developer"
(console) or "the orchestrator" (MCP) and narrates one line before each step.

## The relay loop (text in, text out)
1. A task arrives (from the master / owner).
2. Convert it into **one self-contained spec** with an **acceptance command**.
3. `run_task(spec=..., cwd=<absolute path>)`.
4. Read **only the report** — do NOT open project files to double-check a clean report; that defeats
   the point of delegating. Open files only to diagnose a reported failure.
5. Hand back a short, paste-ready result.

## Tools
- `run_task(spec, cwd, max_steps?, system?, return_full?)` — the worker; does the work, returns a
  structured report (status, verification, files changed, commands+exit codes, integrity verdict).
- `run_detail(run_id?, section?, grep?)` — replay a saved run from `runs/` to debug; use this instead
  of `return_full=True` (cheaper — you pay only for the lines you ask for).
- `ask_agent(prompt)` — one-shot text, no filesystem.
- `agent_info()` — identity, config, current known limits.

nemo's own tools inside `cwd`: `read_file` (paged, line-numbered) · `edit_file` (exact replace,
refuses ambiguous matches) · `write_file` (refuses to shrink a file it hasn't fully read) · `search`
(regex) · `find_files` (glob) · `list_dir` · `run_bash` · `finish`.

## Writing a good spec (nemo can't see your conversation)
- Name **exact files and paths** — never "the config file".
- State the **acceptance command** (`pytest -q`, `npm test`, `dotnet build`). No command → unverified work.
- **Decide the design yourself and state it.** nemo is a builder, not an architect; it improvises badly.
- **One task per call.** Bundled requests degrade all of them.
- Use `system=` for standing rules ("don't touch X", "match existing style").
- Raise `max_steps` for large jobs (server default is ~25 — confirm via `agent_info`).

## Trusting the report — believe the audit lines over the prose
nemo is not a reliable narrator of its own success. The server prints machine-checked verdicts:
- **`[REJECTED: FAKED PASS]`** — an independent review found it faked the result (hardcoded `__eq__`,
  call counters, weakened/skipped tests). This is a **FAILED** run. **Never relay it as done.** Re-spec and rerun.
- **`[UNRELIABLE]`** — its claim contradicts the recorded exit codes, or it verified nothing. Investigate first.
- **`status: partial` / `blocked`** — relay honestly. An honest blocker is useful; a false success wastes the next instruction.
- **`COMMANDS` with a non-zero exit is ground truth**, whatever the summary says.
- **Do NOT disable `AGENT_REVIEW`** — it's the only thing between a faked green and a success report going upstream.

## Known limits / caveats
- **`run_bash` is NOT sandboxed** (file tools are confined to `cwd`; shell is not). Don't point nemo at
  a directory you wouldn't let a loose script into.
- The **NVIDIA key sits in plaintext** in `MCP/nemo_agent/.env` (gitignored). It was shared earlier —
  **rotating it is still worth doing.**
- After changing the server, **restart** so the MCP picks up new code (`claude mcp list` to check).
- nemo's own past failure modes (now guarded, but know them): faking test passes, data-loss via
  full-overwrite writes, `**` globs missing root files, truncation cutting the tail. Guards: static
  cheat-scan + independent HONEST/GAMED/UNSURE review, `edit_file`, PARTIAL-labelled paged reads,
  shrink-guarded `write_file`, mid-cut truncation, 429/5xx retries, step-budget warning.

## Reporting upstream
Write for the master/owner who'll paste it on: what changed, what verified it, what's still open —
the **real** status, never rounded up. Lead with a `[REJECTED]`/`blocked` if that's what happened.
