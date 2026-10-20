# Which docs to trust (checked 2026-10-20)

The project has many generations of docs, from the Gemini, OpenCode and Claude eras. A new session
trips on the stale ones. **Trust order:**
1. git and GitHub;
2. `CLAUDE.md`;
3. this folder;
4. `Vibe/agent/opencode-docs/{FEATURES,SOUND,PROTOCOL}.md`;
5. `Releases/RELEASE-NOTES.md`.

Treat everything else as history.

## Current (2026-10-20)

- `Vibe/claude-notes/*` (this folder).
- `CLAUDE.md`: Telegram, releases, signing. Fixed on 2026-10-20: cert range to 1.0.0.17, main now
  holds the release, dispatch the release on main.
- `Vibe/START-HERE.md`: overview, protocol v31, links to FEATURES. It still sends readers to the
  stale master docs (below).
- `Vibe/agent/opencode-docs/FEATURES.md`: full feature guide as of 1.0.0.17.
- `Vibe/agent/opencode-docs/SOUND.md`: every sound path, including v31 phone-as-source.
- `Vibe/agent/opencode-docs/PROTOCOL.md`:
  - **It is the only live protocol doc.** The v25–v31 sections match the code (both sides are at
    31).
  - Doc defects: the title still says v17; the version-history paragraph stops at v24; v22/v23 have
    no sections; the v28–v31 sections sit after "Protocol draft".
  - Copies under `claude-docs/` and `gemini-docs/` (v15) are dead traps.
- `Releases/RELEASE-NOTES.md`, `update.json` (1.0.0.17), `CHECKSUMS.txt`.
  - The 7z isn't listed in CHECKSUMS because it is added after the checksums are written.
- Version stamps: `build.gradle.kts` versionCode 18 / versionName 1.0.0.17; csproj 1.0.0.17.

## Stale or contradictory (fix when docs are next touched)

| Doc | Problem |
|---|---|
| `Vibe/master/HANDOVER.md` | August, v18, "M1–M14 done". START-HERE says it "supersedes everything", which is false now |
| `Vibe/master/STATUS.md`, `INDEX.md`, `AGENTS-REGISTRY.md` | August; calls `gemini-docs` canonical (dead); no record of the Claude cloud sessions |
| `Vibe/agent/RESUME.md` | August (M19); Windows-only build; "never commit or edit .md", the opposite of how cloud sessions work |
| `Vibe/agent/SESSION-HANDOVER.md` | 2026-10-18: v27/v28, 1.0.0.16, unmerged branch. Its cloud-build recipe is still useful. Superseded by `claude-notes/HANDOFF.md` |
| `Vibe/agent/opencode-docs/BRAIN.md` | August v18; says the active agent is "opencode/Antigravity" |
| `opencode-docs/CHANGELOG.md` | Stops at 2026-10-18 / v27; nothing for v28–v31 or releases 1.0.0.15–1.0.0.17 |
| `opencode-docs/ROADMAP.md` | July; M5/M6/M9 marked "in progress" |
| `opencode-docs/DECISIONS.md` | Ends at D-097 (2026-10-18); v28–v31 have no decision entries |
| `Vibe/agent/claude-code-2/OWNER-CONCERNS-2026-10-19.md` | Items 1–14 **shipped in 1.0.0.17**, but it still says "don't start new features before these". A status line was added on 2026-10-20 |
| `Vibe/agent/GUARDRAILS.md` | Rules still valid; says "read with GEMINI.md" (dead); no release, keystore or workflow traps (CLAUDE.md has them) |
| `telegram-uploader/README.md` | Missing test-build.yml and release-pack.yml; doesn't say what gets posted (APK, setup, 7z, checksums; not the zip) |
| `Releases/TEST-NOTES.md` | Lists notes for builds that shipped in 1.0.0.17. **Rewrite it before the next test build**, or the post repeats old news |
| `README.md` (root) | Says protocol v24, "updating from 1.0.0.2" |
| `Vibe/agent/reports`, `tasks` | Newest from August; the October work has no reports (it lives in commits and these notes) |

## Workflows (cheat-sheet)

| File | Trigger | Does | Secrets |
|---|---|---|---|
| `build.yml` | every push, PR, dispatch | Ubuntu and Windows builds; most sims. The only XAML validation | none |
| `ui-test.yml` | push to `ui-test/**`, dispatch | Phone in an emulator with a fake PC; PC with a fake phone. Screenshot artifacts | none |
| `test-build.yml` | push to `test-build/**`, tag `test-*` | Signed APK plus framework-dependent PC build in one 7z, posted to Telegram with `TEST-NOTES.md`. `-runtime` in the name adds the .NET 8 installer. No release | ANDROID_DEBUG_KEYSTORE, TELEGRAM_* |
| `release.yml` | dispatch (version, display_name, notes, prerelease, allow_new_apk_key) | `release.ps1 -Publish`, then the 7z, then the Telegram dispatch. **Run it on main** (see CLAUDE.md) | ANDROID_DEBUG_KEYSTORE |
| `release-pack.yml` | dispatch (tag) | Adds the 7z to a release and posts it to Telegram; for a run that stopped after publishing | none |
| `telegram-upload.yml` | release published, dispatch (tag, message) | Posts the APK, setup, 7z and checksums through a local Bot API server | TELEGRAM_* (4) |
| `telegram-message.yml` | dispatch (text) | Posts text, split at 4096 characters | TELEGRAM_* |

Gotchas:
- Telegram dispatches use `--ref main`, so workflow edits only take effect once merged.
- A release created with the workflow token doesn't fire `release: published`; that's why
  release.yml dispatches telegram-upload itself.
- The three Telegram workflows share the `telegram-bot` concurrency group.
- A cloud session can push only its own branch. It makes `ui-test/*` and `test-build/*` branches
  with MCP `create_branch`.
