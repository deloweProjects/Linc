# Handoff: where Linc stands (written 2026-10-20, after 1.0.0.17)

Read this first if you are a new session. It is the notebook of the Claude Code session that built
1.0.0.10 to 1.0.0.17 with the owner. Everything here was true when it was written; check git and
GitHub before trusting a version number.

Read next in this folder:
- `CONCERNS.md`: what isn't proven, what could break, what I'd look at first.
- `SOUND-QUALITY.md`: why Play everywhere sounds "a little weird" on the phone, and how to improve it.
- `TIDY-UP.md`: code that was written in a hurry and should be cleaned up, with file:line.
- `DOCS-STATE.md`: which of the project's docs are current and which are stale.
- `AUTHORSHIP.md`: how to show Linc is the owner's work (open fingerprint plus the code's own traits).
- `WORKING-WITH-THE-OWNER.md`: how the owner works and what they expect. **Read it before you reply.**

---

## 0. Linc Tools: built 2026-10-21 (Phase 1 + sections), not yet released

The Tools page exists: Type, Rescue, Apps, Capture, Health, System, **Commands** (the owner's
searchable catalogue of ~170 adb commands with parameters, Run and Copy) and Console. It finds
phones straight from adb (`AdbShell.DevicesAsync`), so it already works without the companion app;
the rest of Linc still needs the app to say "connected" (Phase 2 below, not started). Not built yet:
Inspector, Logs, Macros, the ADB-only connection, debloat, retiring Settings' shell box. Tests:
`tools/toolsim` (pure parsers), uishots `tools-*.png` with `--fake-adb tools/uishots/fake-adb/`.

### The original plan (set by the owner, 2026-10-20)

The owner's next job for the incoming session is a new PC page, **Tools** ("ADB with a face"), and
**ADB-only connection** ("Linc should connect just by ADB pairing, no need to open Linc on the
phone"). It is fully designed:
- **`linc-tools/PLAN.md`**: the architecture (`IAdbShell`, ADB-only mode, the page), design rules,
  build order in 7 phases, testing.
- **`linc-tools/TOOLS.md`**: every tool: surface, hidden extras, exact commands, parsing, danger
  tier, gating, and the never-offer list.
- **Mockup:** <https://claude.ai/artifact/NpTpEcrzv6RYnZAifctNPh> (Type, Apps, Health, Rescue).
  Ask the owner whether it is approved before building the visuals; the plan holds either way.

Start with PLAN.md §4 Phase 1. Ship the ADB-only change (Phase 2) on a `test-build/adb-only` branch
for the owner to try before anything else depends on it.

## 1. State right now

- **Released:** `v1.0.0.17` ("1.0.0-beta.17"), 2026-10-20, protocol **v31**.
  <https://github.com/deloweProjects/Linc/releases/tag/v1.0.0.17>.
  - Assets: APK `Linc-Companion-1.0.0.17-2026-10-20.apk` (30 MB), `Linc-Setup-1.0.0.17.exe`
    (113 MB, self-contained .NET), portable zip (172 MB), `CHECKSUMS.txt`, and
    `Linc-1.0.0.17-phone-and-PC.7z` (120 MB; it holds both the APK and the setup).
  - Posted to Telegram (t.me/LincApp): APK, setup, 7z, checksums.
- **main** = PR #8 merged ("Linc 1.0.0.17 (#8)"), so `Releases/update.json` is live and both apps
  offer 1.0.0.17.
- **Branches:** the session branch `claude/linc-feature-ui-polish-lgf8au` and **main hold the
  same code**. These notes reached main in a docs-only PR straight after the release. A new session
  gets its own branch: start it from **main**.
- **Owner's verdict on 1.0.0.17:** "everything works".
  - The one known complaint: Play everywhere on the phone sounds a little weird / low quality.
  - The owner said **no more fixing for now**; they are going to use the apps daily and report bugs.
  - **Don't start changing code unless they ask.**

## 2. What this run of sessions built (1.0.0.16 → 1.0.0.17)

All of it is listed in `Releases/RELEASE-NOTES.md` › 1.0.0.17. The full feature guide is
`Vibe/agent/opencode-docs/FEATURES.md`. How sound works is `Vibe/agent/opencode-docs/SOUND.md`, and
the wire protocol is `PROTOCOL.md` next to it. In short:

- **Sound**
  - Play everywhere: PC sound plays on the phone and on every PC output, in step. This is channel 6,
    AAC, on the PC's clock.
  - Phone-as-source (v31): scrcpy `--audio-source=playback` without `--audio-dup` plays into the
    virtual cable, and the same pipeline sends it everywhere. Its extras live under Advanced.
  - Phone sound on the PC goes through scrcpy `--audio-dup` (`PhoneSoundOnPc`).
- **Phone**
  - Sync folders (v30), Terminal (wide console, swipe sideways), Share multi-select, Gallery scan.
  - Control "Pages" panel; Clipboard is search-first.
  - Appearance: wallpaper background with frosted, tinted overlays; floating nav bar; fonts.
- **PC**
  - Home scales to fit; Now playing over album art; clock styles; fonts.
  - Wallpaper backdrop, switchable off to the plain look; frosted, tinted flyouts, dialogs and side
    bar.
  - Clipboard redesign plus an overflow fix (`PageLayout` gives a ScrollViewer's column an explicit
    width).
  - Device page and offline status fixes; DND-access hint (optional status field `dndAccess`).
  - About fixed; takeover of an older running copy.
- **Authorship**
  - `Provenance.kt` / `Provenance.cs`, `NOTICE`, manifest meta-data, and the hello `author` field.
  - The fingerprint is SHA-256 of `Linc by deloweProjects — https://github.com/deloweProjects/Linc`
    = `93b660d11f6717cfe67f72a92881210977685b4954a673cadf67e2e11358f281`.
- **Release automation**
  - `release.yml` (with a 7z step) and `release-pack.yml` (new).
  - `telegram-upload.yml` posts only the APK, setup, 7z and checksums.

## 3. How work flows (the machinery a new session must know)

- **No local toolchain in a cloud session.**
  - There is no dotnet and usually no Android SDK. A helper once found an SDK under the scratchpad;
    the scratchpad doesn't survive a session, so don't count on it.
  - Every compile and run happens in GitHub Actions, so write carefully and let CI judge.
- **Screenshot test:** create a branch `ui-test/<name>` from your branch (GitHub MCP
  `create_branch`); `ui-test.yml` runs on push.
  - It builds both apps and runs the PC app against a fake phone (`Linc/tools/uishots`) and the
    phone app in an emulator against a fake PC (`Linc/tools/uishots/fakepc.py`).
  - Artifacts: `ui-windows` (PNGs plus `pc-log.txt`) and `ui-android` (PNGs, `logcat.txt`,
    `summary.md`).
  - Download them with `actions_get download_workflow_run_artifact` and then curl.
  - The Android emulator sometimes shows system dialogs ("Pixel Launcher isn't responding", "access
    all device logs"). Those are not Linc bugs.
- **Test build to Telegram:** branch `test-build/<name>`. Add `-runtime` to the name to also post
  Microsoft's .NET 8 Desktop Runtime installer.
  - It posts one 7z (both apps, framework-dependent PC build, LZMA2 max) plus
    `Releases/TEST-NOTES.md` as the message.
  - **It posts to the owner's PUBLIC channel: only when the owner says "send".**
- **Release:**
  - Write the version's section in `Releases/RELEASE-NOTES.md` first. Then empty
    `Releases/TEST-NOTES.md` of shipped items: it still lists the notes for builds that went out in
    1.0.0.17.
  - **Merge the work to main first** (PR via MCP `create_pull_request` + `merge_pull_request`; the
    owner's ask for a release counts as asking for this PR).
  - Then dispatch `release.yml` **on main** with `version` = next four-part number (check the newest
    tag) and `display_name` = `1.0.0-beta.N`.
  - On main, `release.ps1` skips its own PR step (`release.ps1` ~304) and goes on to the 7z and the
    Telegram post.
  - **Don't dispatch it on a feature branch.** Its PR step can never succeed there: the workflow
    grants only `contents` and `actions` permissions (no `pull-requests: write`), so the repo setting
    alone doesn't fix it. That is exactly how 1.0.0.17 stopped after publishing.
  - If a run does stop after publishing: merge to main by hand, then dispatch `release-pack.yml` on
    main with `tag=v1.0.0.N`. It adds the 7z and posts to Telegram.
- **Signing:**
  - The APK is a debug build signed with the owner's debug key, restored from the
    `ANDROID_DEBUG_KEYSTORE` secret. Its certificate SHA-256 is
    `8182d31662613b85f418e3d8601997ff957d5b7104a1ddd25ad7eed9ecd9d969`.
  - **Never commit a keystore.** A plan exists to move to a Play upload key.
- **Commits:** end each with the Co-Authored-By and Claude-Session lines the session gives you. No
  model IDs anywhere else in the repo.
- **Docs rule (the owner's):**
  - Write down how every protocol and feature works, and update the doc in the same commit as the
    code (SOUND.md, PROTOCOL.md, FEATURES.md).
  - When something that worked breaks, diff against the build that worked before theorising.

## 4. Things a new session gets wrong (learned the hard way)

- **Guessing instead of reading logs.**
  - The Play everywhere cut-off was a MediaSession callback with no Looper (`PcPlaybackSession`),
    not the "two-link race" first blamed.
  - The Clipboard overflow was the ScrollViewer centring content by its *desired* width (847 px)
    rather than its arranged width. Two guesses (AutoSuggestBox, sideways scrolling) were wrong. One
    logging run found it.
  - Add a one-line diagnostic and run the UI test; it's cheaper than another guess.
- **Kotlin gotchas:**
  - A `/*` inside a KDoc (for example `"image/*"`) opens a nested comment and swallows the file.
  - Only one `companion object` is allowed per class.
- **WinUI gotchas:**
  - With no explicit Width, a column in a ScrollViewer is placed by its desired size.
  - In-app AcrylicBrush has nothing to blur inside a popup window (menus already get system
    acrylic).
  - `x:Bind` needs `Mode=OneWay` to update.
- **"Didn't change" can be an old copy still running.** The owner once ran an old instance from the
  tray. `App.TakeOverFromOtherCopy` now handles it.
- **Owner's English is fast and casual** (typos, run-ons). Read for intent. When an ask is visual,
  offer a mockup to approve: they approved the frosted design that way, using a Design artifact
  canvas.

## 5. If the owner comes back with…

- **A bug:** ask for (or read) the logs: phone Settings › Logs › Copy log; PC Settings › Logs. The
  first line of each says `Linc <version> by deloweProjects`, so you know the version.
- **"Sound is weird":** see `SOUND-QUALITY.md`. Start with measurements, not changes.
- **"Send to telegram":** a text message goes through `telegram-message.yml`; a build goes through a
  `test-build/<name>` branch. Then confirm the run is green and read its upload log before saying it
  was sent.
- **A release:** see section 3. The next version is `1.0.0.18` / `1.0.0-beta.18` (check tags).
