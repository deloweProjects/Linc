# Contributing

Guidelines for all contributors — human or AI. Follow these so the codebase stays consistent.

## Project structure

```
Linc/
├── ANDROID/     # Android companion app (Kotlin, Compose)
├── DESKTOP/     # Windows app (C#, WinUI 3)
└── claude-docs/        # Project documentation — single source of truth
```

## Documentation-first rule

- `claude-docs/` is a first-class part of the project.
- When architecture, features, or major decisions change, update the relevant doc **before or alongside** the code change.
- If code and docs conflict, flag the conflict and propose a doc (or code) fix — never let them silently drift.
- New significant decisions go in `DECISIONS.md`; user-visible changes go in `CHANGELOG.md`; feature status moves in `FEATURES.md`.

## Architectural principles

1. **ADB is an implementation detail.** No UI code touches ADB; all ADB interaction lives in the ADB Service Layer (see ARCHITECTURE.md).
2. **Dependencies point downward only**: UI → Application → Domain → ADB layer. No upward or sideways imports.
3. **The companion protocol is a contract.** Change it only by bumping the protocol version and updating the spec; both apps must tolerate one version of skew.
4. **Errors speak human.** Every failure surfaced to the user must have a plain-language message and, where possible, a guided fix. Raw adb/scrcpy output belongs in logs only.
5. **Reliability over features.** Prefer hardening an existing feature over starting a new one when both compete.

## Coding standards

### Kotlin (ANDROID/)
- Official Kotlin style (enforced via `ktlint` or IDE formatter).
- Compose: stateless composables + hoisted state; one screen per file.
- Coroutines: structured concurrency only — no `GlobalScope`.
- Naming: `PascalCase` types/composables, `camelCase` members, `UPPER_SNAKE_CASE` constants.

### C# (DESKTOP/)
- .NET conventions with `dotnet format` / `.editorconfig`.
- `async`/`await` throughout; no `.Result`/`.Wait()` blocking.
- Naming: `PascalCase` public members and types, `camelCase` locals/parameters, `_camelCase` private fields.
- Services are interfaces (`IFileService`) with a single production implementation; register via DI.

### Both
- Small, focused files; one responsibility per class/service.
- Comments explain constraints and "why", not "what".
- No dead code, no commented-out code in commits.

## Git workflow

- Default branch: `main` — always buildable.
- Branch names: `feature/<short-name>`, `fix/<short-name>`, `claude-docs/<short-name>`.
- Commits: imperative mood, concise subject (≤72 chars), body explains why when non-obvious.
- One logical change per commit; docs updates for a change belong in the same commit/PR as the change.
- PRs (once collaboration starts): description states what + why, links the relevant roadmap milestone.

## Development guidelines

- Test on a real device for anything touching pairing, discovery, or reconnection — emulators do not exercise wireless debugging realistically.
- Never require a contributor-facing workflow that the end user is spared from: if setup needs a manual `adb` command during development, that's acceptable in dev docs, but the shipped product must automate it.
- Pin bundled binary versions (adb, scrcpy); bumps get a CHANGELOG entry.
- Keep secrets and signing keys out of the repository.
