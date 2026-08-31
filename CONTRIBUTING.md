# Contributing to Linc

Thanks for looking. Issues and pull requests are both welcome, and a good bug report — what you did,
what happened, what you expected — is worth more right now than a patch.

## A note on how this project is built

**Linc is largely AI-built, under a human planner, and that is deliberate.** A person decides what
gets built and reviews everything; coding agents do the implementation against written task files
and report back with evidence. The whole workflow — the roles, the rules, the task files, the
milestone reports going back to the first commit — is in **[`Vibe/`](Vibe/START-HERE.md)**.

That folder is not leftover scaffolding. It is how the project is resumed: a fresh agent (or a fresh
human) can clone this repo anywhere, read `Vibe/START-HERE.md`, and pick up where the last session
stopped. If you want to understand *why* a piece of code looks the way it does, the report that
produced it is almost certainly in `Vibe/agent/reports/`.

## Building

### Desktop (`Linc/DESKTOP/`) — C#, .NET 8, WinUI 3

**Two traps, both of which will waste your time if you skip them:**

1. **`dotnet build` cannot build WinUI.** You must use Visual Studio's MSBuild, x64:

   ```
   & "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" Linc\DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m
   ```

2. **Kill any running `Linc.Desktop.exe` first**, or the build fails on a locked executable
   (MSB3027). Note that Linc is single-instance and **hides to the tray rather than exiting** when
   you close its window, so "I closed it" is not the same as "it is gone":

   ```
   Get-Process Linc.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force
   ```

### Android (`Linc/ANDROID/`) — Kotlin, Compose, Material 3

`JAVA_HOME` must point at Android Studio's bundled JBR:

```
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
Linc\ANDROID\gradlew.bat -p Linc\ANDROID assembleDebug testDebugUnitTest --no-daemon
```

The Gradle heap is 1 GB. **JVM error 1455 means "free some memory", not "your code is broken".**

## Verification harnesses

`Linc/tools/` holds ~30 plain `net8.0` console harnesses — `probe`, `syncsim`, `filesim`,
`devicesim`, `updatesim`, `palettesim`, `mirrorsim`, `blescan` and the rest. Each one exits `0` when
green and `1` when not, and prints numbered sections saying what it checked.

**Run them with the working directory set to `Linc/`**, not the repo root — several locate the repo
by walking up for a directory with a `DESKTOP` child, and give a confusing error from elsewhere:

```
cd Linc
dotnet run --project tools\updatesim
```

Run the harness relevant to whatever you changed, and say in the PR which one you ran and what it
printed.

**One footgun worth knowing:** `devicesim` backs up, briefly deletes, and then **restores** your real
`settings.json`. It must be allowed to run to completion. Never pipe it into anything that can exit
early (`| Select -First`, `| head`) and never kill it mid-run, or your real device pairing is left
overwritten with the test fixtures. Do not run it while `Linc.Desktop` is running.

## The rule that is not negotiable

**The wire protocol is versioned and sacred.**

It is implemented twice — Kotlin on the phone, C# on the PC — and mixed-version peers must keep
working. The version is currently **18**.

If your change touches the wire format at all — a new message type, a new field, a changed shape —
then, in this order:

1. Bump the version in `Vibe/agent/opencode-docs/PROTOCOL.md` and document the change **first**.
2. Update **both** implementations.
3. **Gate the new behaviour on the negotiated version**, so an older peer still works.

A PR that changes the wire without doing all three will be asked to redo it, however good the code
is. If you think a feature needs a protocol change and you are not sure, open an issue and ask
before writing it.

## Coding conventions

These already exist throughout the codebase; match what is around you rather than importing your own
style.

- **Smallest possible change.** No drive-by refactors, renames or reformatting of code you happened
  to open. If you spot a real problem outside your change, say so in the PR instead of fixing it.
- **A silent catch is a bug factory.** If a path can fail and nobody awaits it, it must log. Several
  of the worst bugs in this project's history were invisible fire-and-forget failures. A silent
  catch with a comment explaining *why* it is silent is fine; an unexplained one is not.
- **Errors reaching the user are plain language.** Raw `adb` or `scrcpy` output must never appear in
  the UI. The one sanctioned exception is the Settings ADB shell box.
- **WinUI specifics that have each cost someone a day:** never bind a non-`bool` to `Visibility`
  (`x:Bind` only converts `bool`, and a string there throws `E_INVALIDARG` and crashes the app); MVVM
  Toolkit partial properties need `<LangVersion>preview</LangVersion>` and cannot have initialisers;
  anything that must run regardless of the open page belongs in `AppShellViewModel`, because page
  view models are created lazily on first navigation.
- **Any COM / Media Foundation / D3D object with a hot cross-thread call path must be created off the
  UI thread** — the companion receive loop resumes on the UI thread (STA), and getting this wrong
  hangs the desktop app outright.
- **No stray files.** Scratch notes, one-off scripts and task lists do not belong in the repo.
  Committed harnesses live under `Linc/tools/`.

## Proposing a change

1. **Open an issue first** for anything beyond a small fix, so we can agree on the shape before you
   write it. For a protocol change, this is required.
2. Branch, and keep the change focused on one thing.
3. Build **both** sides and run the relevant harness. Say what you ran and paste what it printed.
4. Open the PR describing what changed, what you verified, and what you could **not** verify —
   "I have no second phone to test multi-device" is a useful sentence, not an admission.

If you cannot test something because you lack the hardware, say so and open the PR anyway.

## Reporting a bug

Include: what you did, what happened, what you expected, your Windows and Android versions, whether
you were on USB or Wi-Fi, and the Linc version from **Settings → About**. If the desktop app is
involved, its log helps a great deal.

Please do not paste anything you would not want public — logs can contain device serials and network
addresses.

## Licence

By contributing you agree that your contribution is licensed under the [MIT Licence](LICENSE).
