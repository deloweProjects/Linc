# GUARDRAILS.md — hard constraints & known traps (Linc)

Read with `GEMINI.md`. These are non-negotiable unless the task **explicitly** overrides them.
Most exist because they already bit someone.

## Never do these without explicit instruction in the task

- **Don't change the wire protocol** (`PROTOCOL.md`, message shapes, the negotiated version). It is
  implemented twice (Kotlin + C#) and mixed-version peers must keep working.
- **Don't touch code outside the task's scope.** In particular, leave alone unless named:
  the reverse-mirror pipeline (`PcScreenCapture`, `PcVideoEncoder`, `PcMirrorSource`,
  `PcMirrorService`, `PcInputService`, `MediaFoundation.cs`), the transport/session layer
  (`ConnectionManager`, `ConnectionSupervisor`, `TlsTransportService`, `CompanionClient`,
  `SocketServer`), and the sync engine.
- **Don't touch unrelated working-tree changes.** There is an uncommitted M07 Sync-page change in
  the tree; do not revert, "clean up," or build on top of it unless told.
- **Don't delete, move, or rename files. Don't add dependencies. Don't change build config or target
  frameworks. Don't commit. Don't run destructive git/adb/filesystem commands.**
- **Don't disable, skip, or delete a test/harness to make a build pass.** Fix the cause or stop and
  ask.
- **Don't litter the repo with stray files.** No scratch notes, task lists, walkthroughs, or one-off
  `.ps1`/`.md` scripts left in the repo root or source folders. Committed test harnesses live under
  `tools/`; anything throwaway must be deleted before you finish. Your hand-off notes go in your
  end-of-session report, not loose files. (Antigravity left `task.md`, `walkthrough.md`,
  `verify-ui.ps1` in M2c — that must not recur.)
- **Don't uninstall/reinstall the companion app or script taps on the Pixel 7 autonomously** — it
  disrupts the user's phone. Put on-phone steps in the manual test script.

## Build & run (do it this way, every time)

- **Desktop:** VS MSBuild x64 — **not** `dotnet build` (it can't build WinUI). **Kill any running
  `Linc.Desktop.exe` first** or the build fails on a locked exe (MSB3027).
- **Android:** `JAVA_HOME` = Android Studio's JBR, then `gradlew assembleDebug testDebugUnitTest`.
  Heap is 1 GB; JVM error 1455 means "free memory," not a code bug.
- **Harnesses** (`tools/`, plain net8.0 consoles): `probe`, `syncsim`, `filesim`, `devicesim`,
  `blescan`, `mirrorsim`. Run the one relevant to your change.
- **`devicesim` FOOTGUN:** it backs up, briefly deletes, then **restores** the real `settings.json`,
  so it **must run to completion.** Never pipe it into something that can exit early (`| Select
  -First`, `| head`) and never kill it mid-run — the restore won't happen and the real Pixel 7
  pairing gets left overwritten with fake test devices (this already happened once). Don't run it
  while `Linc.Desktop` is running.

## Verification etiquette

- Never move the real mouse cursor (`SetCursorPos` + `mouse_event`) or drive a non-idle interactive
  session to verify UI — the user is working at the PC. Hand those to the user as a **numbered
  manual test script**. Same for phone taps. Tree enumeration / `InvokePattern` (no pointer
  movement) are fine.
- Every user-facing error must be plain language; raw adb/scrcpy text never reaches the UI (the one
  sanctioned exception is the Settings ADB shell box, D-009).

## Recurring failure modes in this codebase (avoid repeating them)

- **A silent catch is a bug factory.** If a path can fail and nobody awaits it, it must log. Several
  of the worst bugs were invisible fire-and-forget failures.
- **Anything that must run regardless of the open page belongs in `AppShellViewModel`,** never a
  page's view model. Page view models are created lazily on first navigation — an initial-load that
  only fires on a `Connected` transition will be missed if the app already connected (the M1 Files
  bug). If you add load-on-connect, also load in the constructor when already connected.
- **A custom title bar swallows clicks:** whatever is passed to `SetTitleBar` becomes a drag region
  that eats mouse input; keep the drag region to an empty spacer.
- **Any COM / Media Foundation / D3D object with a hot cross-thread call path must be created off the
  UI thread** — the companion receive loop resumes on the UI thread (STA).
- **A reconnect state machine driven only by fresh adverts is blind to a transport already up but
  quiet** — let it adopt what the adb server already holds.
- **Not every file op exists on every transport:** `TlsFileService` throws `NeedsAdb()` for
  delete/rename/mkdir/screenshot (D-024). Check `connection.HasAdb` or catch `LincException`.
- **WinUI/MVVM specifics:** MVVM Toolkit partial properties need `<LangVersion>preview</LangVersion>`
  and can't have initializers (set defaults in constructors); `x:Bind` has no function-call tricks
  (add inverse-bool VM properties); `H.NotifyIcon.WinUI` is pinned to 2.2.0.
- **`x:Bind` implicitly converts only `bool`→`Visibility`, never `string`→`Visibility`.** Binding a
  string (or anything non-bool) straight to `Visibility` throws `E_INVALIDARG` when the element
  realizes and **crashes the app** (this took the app down in M2b). Bind `Visibility` to a bool VM
  property (e.g. `HasX`), never to text.

## Environment facts

- adb: `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`. scrcpy v3.3.4 on PATH (a vendored,
  branded build is planned — see the roadmap). Test phone: **Pixel 7**, serial `<your-device-serial>`, on
  USB or wireless ADB.
- The desktop process is **PerMonitorV2** (physical pixels); the dev machine runs at 125% scaling,
  which changes what display APIs report — any new console touching display geometry needs that
  manifest.
