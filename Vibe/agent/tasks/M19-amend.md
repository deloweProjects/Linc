# M19-amend — the last two things before the repo goes public

> **Agent: Claude Code 2.** Same rules as `M19.md`. **SHORT SESSION.**
> **📄 REPORT TO `Vibe/agent/reports/M19-amend.md` — MAX 40 LINES.**
> **DO NOT PUSH. DO NOT add a remote.** The owner pushes.

**Why this exists:** git history is permanent. Once the first push lands, anything in the tree is
public forever, even if a later commit removes it. Both items below are cheap now and impossible
later.

## 1. Strip the VPN / CGNAT addresses (owner's ruling: remove them)
19 hits across the docs and a `hotspotsim` fixture: `<vpn-address>`, `<vpn-gateway>`, `<cgnat-address>`, and any
other `26.*` or `100.64-127.*` literal you find in the same sweep.
- **Docs and reports:** replace with a placeholder that keeps the meaning — `<vpn-address>`,
  `<gateway>`, `<peer-address>`. **The reports' reasoning must still read correctly afterwards** —
  these are the evidence behind M13's findings, so a bare `x.x.x.x` that destroys the argument is
  worse than the address. Read the sentence before you replace.
- **`hotspotsim`'s fixture:** replace with **RFC 5737 documentation addresses** (`192.0.2.x`,
  `198.51.100.x`) or RFC 1918 (`192.168.x.x`) — whatever keeps the test's intent. **The harness must
  still pass and still test the same thing.** If a substitution changes what a check proves, say so.
- Re-run the residual scan and report **zero** remaining.

## 2. Remove `CLAUDE.md` from the repo root
You flagged it yourself: it documents the `nemo` worker whose folder is gitignored, it auto-loads for
any Claude Code session, and it contradicts `Vibe/START-HERE.md`. **The owner has authorised its
removal — GUARDRAILS' no-delete rule is explicitly overridden for this one file.**
- `git rm CLAUDE.md` (delete it, do not merely untrack).
- **If anything in `Vibe/` still points at it, repoint that reference to `Vibe/START-HERE.md`.**
- If it contains anything the project still needs that is *not* about `nemo`, move that content into
  `Vibe/agent/` first and say what you moved.

## 3. Fold both into the existing commit
`git add -A` then **`git commit --amend --no-edit`** — the owner wants one clean first commit, not a
"fix the leak" commit sitting in public history next to the leak.
**Then print `git log --oneline -3` and `git status --short`.**

## 4. Acceptance
1. Residual scan: **zero** VPN/CGNAT addresses, zero phone serials, zero owner paths.
2. `hotspotsim` still green, and a one-line statement that it still tests the same thing.
3. `CLAUDE.md` gone; nothing in `Vibe/` points at it.
4. Both sides still build; harness list unchanged from M19.
5. `git log --oneline -3` showing **one** commit on top of nothing, and an empty `git status --short`.

## 5. Out of scope
Pushing. Adding a remote. Any code change beyond the fixture substitution. Anything else at all.
