# Permissions — why the agent kept asking, and what the replacement does

## The diagnosis

`.claude/settings.local.json` had accreted **31 allow entries, every one an exact string literal.**
Claude Code matches these literally unless you give it a wildcard, so an entry only ever silences the
*identical* command again. Four of them were worse than useless — they contained session-specific temp
paths like:

```
Select-String -Path "C:\Users\<you>\AppData\Local\Temp\claude\…\bpr415ku3.output" …
```

That file no longer exists and that string will never recur. It was pure noise.

The result: the agent re-prompted on essentially every command, because a build with one different
flag, or a `grep` for a different pattern, was a brand-new literal.

## The fix

Wildcards, so a *shape* of command is approved once instead of one exact invocation. Per the docs,
`Bash(npm run *)` matches anything starting with `npm run`; rules are evaluated **deny → ask → allow**
and the first match wins. Claude Code also reloads settings live, so no restart is needed.

`settings.recommended.json` next to this file is the replacement. Copy it over
`<repo-root>/.claude/settings.local.json`.

## What is deliberately still gated

This project has destroyed real data twice and it is worth being precise about why these particular
things stay behind a prompt.

**`git clean`, `git reset --hard`, `git restore` — denied outright.** This is the big one. **The Linc
tree is almost entirely uncommitted.** `git status` shows ~40 untracked paths including `LincStore.cs`,
`OutboxService.cs`, `AppCatalog.cs`, every one of the `tools/*sim` harnesses, `Documentation/`, and
`SCRCPY/Custom/`. A single `git clean -fd` would delete **months of work that has never been
committed**. Nothing an agent needs to do requires these commands.

**`git push` — denied.** Nothing in this workflow pushes.

**`git add` / `git commit` — ask.** Agents do code and tests; deciding what enters history is the
owner's call, and it costs one prompt per session at most.

**`adb shell input`, `adb uninstall`, `pm uninstall` — denied.** These are the phone-tap rule (D-031)
expressed as configuration rather than as a sentence in a prompt the agent might skim past.

**`adb install` — ask.** Installing the APK is a real state change on the owner's phone and the task
file has to authorise it explicitly anyway.

**`rm -rf` and `Remove-Item -Recurse` — denied.** Harnesses clean up their own temp roots with
narrower calls; nothing legitimate needs a recursive force-delete.

## What was deliberately opened up

Builds (MSBuild x64 both path spellings, `dotnet build/run/test`), all read-only inspection (`ls`,
`cat`, `head`, `tail`, `grep`, `rg`, `find`, `sed -n`, `Select-String`, `Get-Content`, `Test-Path`),
read-only git (`status`, `diff`, `log`, `show`), read-only adb (`devices`, `dumpsys`, `pm list`,
`wm size`, `getprop`), and `Stop-Process -Name Linc.Desktop` — which the build recipe requires before
every desktop build, so prompting for it was pure friction.

## The honest caveat

**Bash pattern matching is not a security boundary.** The docs say so directly, and it is easy to see
why: a denied command can be reached through a shell construct the pattern does not anticipate. These
rules stop *accidents* — an agent reflexively reaching for `git clean` to tidy a tree — and that is
genuinely most of the risk here. They are not a sandbox and should not be trusted as one.

**Do not add a blanket `Bash` or `PowerShell` allow rule.** That is the one change that would turn
this from "fewer prompts" into "no brakes," and this project's history argues against it.

## If it still prompts too much

Tell the planner which command it stopped on. The fix is to add that *shape* to the allow list, not to
widen the net. The list is meant to grow by observation, one pattern at a time.
