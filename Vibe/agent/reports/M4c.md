# REPORT — M4c: the phone as a media remote and a slideshow clicker

**Agent:** Claude Code 2 · **Session:** 2026-08-25/26 · one session, self-continued.
**Task file:** `Vibe/agent/tasks/M4c.md`. Read first: `AGENTS.md`, `claude-code-2/GUIDE.md`,
`GUARDRAILS.md`, `opencode-docs/BRAIN.md` (POSITION block).

Per-part result lines, written as each part finished:

- **Part 0 — DONE.** `syncsim`'s fixed `Task.Delay(120)` replaced with a bounded poll (30 s ceiling,
  100 ms interval, loud failure on timeout). 5/5 consecutive runs exit 0.
- **Part A — DONE, and mostly not built.** A1: the phone already had the whole `pc.media.control` /
  `pc.media.state` path (`PcMediaControl`, `PcMediaStore`, wired in `CompanionService`/`SocketServer`
  and driven by Home's `PcMediaWidget`). Tools reaches it with **no new plumbing** and none was
  invented. Only a `MediaCard` composable was added to `ToolsScreen.kt`. Volume was **already on this
  page** from M4b, so it moved into the Media block rather than being duplicated.
- **Part B — DONE.** New pure `PresentationKeys` + `PresentationControl` (`PresentationKeys.kt`) and a
  `PresentationCard` on Tools, riding the existing v14 `pc.input`. **11 new Android tests** for the
  mapping and send path.
- **Part C — DONE.** Both doc comments now say `"Showing what was last synced at {time}."`, which is
  what `HomeOfflineBanner.FormatText` actually returns (`HomeCacheFormat.cs:66`).
- **Protocol untouched.** Both constants still **18**; no new message type. No bump was needed.

---

## §0.1 — Inspection

```
$ git rev-parse --abbrev-ref HEAD
m13-hotspot-links

$ git log --oneline -3
d64a1b7 M14: black-and-white default theme, new Linc mark on both apps, Phone section
01d7504 Convergence M13a-M13e: hotspot ADB link-up, protocol v18
536ac28 M13a-M13c: hotspot links - protocol v18, announce path, self-arming

$ git rev-parse --short main
e23fbc3
$ git rev-list --count main..HEAD
3
```

Full `git status --short`, with each path's last-write time (UTC):

```
 M  2026-08-24 22:28:20  ANDROID/app/src/main/java/app/linc/android/service/WallpaperProvider.kt
 M  2026-08-24 21:07:52  DESKTOP/Linc.Desktop/App.xaml.cs
 M  2026-08-24 21:07:25  DESKTOP/Linc.Desktop/Services/AdbServerHost.cs
 M  2026-08-24 21:31:22  DESKTOP/Linc.Desktop/Services/ConnectionSupervisor.cs
 M  2026-08-24 22:14:56  DESKTOP/Linc.Desktop/Services/HomeCacheFormat.cs
 M  2026-08-24 22:29:43  DESKTOP/Linc.Desktop/Services/ThemeSyncService.cs
 M  2026-08-24 21:06:24  DESKTOP/Linc.Desktop/Services/TransportRank.cs
 M  2026-08-24 21:09:56  DESKTOP/Linc.Desktop/Services/UsbWatcherService.cs
 M  2026-08-24 21:19:01  DESKTOP/Linc.Desktop/ViewModels/DeviceViewModel.cs
 M  2026-08-24 22:17:52  DESKTOP/Linc.Desktop/ViewModels/HomeViewModel.cs
 M  2026-08-24 21:20:18  DESKTOP/Linc.Desktop/Views/DevicePage.xaml
 M  2026-08-24 22:42:29  DESKTOP/Linc.Desktop/Views/HomePage.xaml
 M  2026-08-24 22:43:34  DESKTOP/Linc.Desktop/Views/HomePage.xaml.cs
 M  2026-08-12 11:39:59  Files/00-Linc-Master-Documentation.md
 M  2026-08-12 11:40:25  Files/06-Connectivity-and-Protocol.md
 M  2026-08-24 22:49:24  tools/applaunchsim/Program.cs
 M  2026-08-24 22:39:39  tools/homecachesim/Program.cs
 M  2026-08-24 22:38:44  tools/homelayoutsim/Program.cs
 M  2026-08-24 21:27:52  tools/hotspotsim/Program.cs
 M  2026-08-24 21:28:28  tools/hotspotsim/hotspotsim.csproj
 M  2026-08-24 22:49:39  tools/synccachesim/Program.cs
??  2026-08-24 21:40:08  DESKTOP/Linc.Desktop/Services/DeviceAdmission.cs
```

**Difference from what the task expected:** branch, HEAD, `main` three behind at `e23fbc3` and the
untracked `DeviceAdmission.cs` all match. The count does not: the task says "~14 modified files";
the tree has **21 modified + 1 untracked = 22 entries**. Two of the 21 are `Files/*.md` dated
2026-08-12 (older than the rest, which are 2026-08-24 21:06–22:49). Nothing was committed, branched
or merged in this session.

## §0.2 — Baseline builds, before any edit

Desktop (`Linc.Desktop.exe` killed first; incremental build):

```
$ MSBuild.exe DESKTOP\Linc.Desktop.sln -p:Configuration=Debug -p:Platform=x64 /v:m
MSBuild version 18.7.8+1ac568fee for .NET Framework
D:\...\ViewModels\HomeViewModel.cs(166,72): warning CS9113: Parameter 'connection' is unread.
  Linc.Desktop -> D:\...\bin\x64\Debug\net8.0-windows10.0.19041.0\Linc.Desktop.dll
```

The incremental build prints no warning summary, so it was re-run as `-t:Rebuild /clp:Summary` to
count them exactly:

```
D:\...\ViewModels\HomeViewModel.cs(166,72): warning CS9113: Parameter 'connection' is unread.
D:\...\ViewModels\HomeViewModel.cs(166,72): warning CS9113: Parameter 'connection' is unread.
Build succeeded.
    5 Warning(s)
    0 Error(s)
```

**Matches §0.2's expected baseline: 0 errors and the duplicated `CS9113` at `HomeViewModel.cs:166`.**
The other 3 of those 5 are `MSB3061` "unable to delete … `SCRCPY/Linc.scrcpy/bin/adb.exe` … locked by
adb.exe (21108)" — an artefact of `-t:Rebuild` while an adb server is running, not present in the
incremental build. That adb server was left alone (it may be the owner's).

Android:

```
$ JAVA_HOME=C:\Program Files\Android\Android Studio\jbr
$ gradlew.bat assembleDebug testDebugUnitTest --no-daemon -q
EXIT=0
tests=117 failures=0 errors=0  (12 test-result XML files)
```

**Matches §0.2's expected 117 Android tests.**

## §0.3 — traps noted

Both taken as given and honoured: `packagesim` re-published before it is run (below), and no
by-hand adb query was made against the wrong server.

---

## PART 0 — the `syncsim` flake

**File changed:** `tools/syncsim/Program.cs` (only this file).

`Harness.Reconcile()` ran the engine six times with a fixed `await Task.Delay(120)` between passes.
It now polls for the condition instead:

- **Ceiling: 30 s** (`SettleCeiling`), the number `cleanroomsim` uses, so the two harnesses agree.
- **Poll interval: 100 ms** (`PollMs`).
- **Quiet = 2 consecutive passes that leave the observable world identical.** The world
  (`WorldSnapshot()`) is both folders' file names, lengths and contents plus the persisted
  `sync-state.json` — everything the scenarios assert on — rendered as one comparable string.
- **Liveness guard:** `SyncEngine.ReconcileNowAsync` takes its gate with `WaitAsync(0)` and returns
  immediately when another pass holds it (`SyncEngine.cs:133-136`). A call whose
  `FakePhoneFiles.ListCount` did not move therefore ran no pass and observed nothing, so it is not
  counted as quiet — the loop polls again.
- **On timeout it fails loudly**, adding a `Failures` entry naming what was true (passes run,
  gate-held calls, total listings, engine log lines) and what was not (quiet passes achieved of 2),
  plus the last world it gave up on.

### A real finding the loud failure produced

The first version of the quiet test also required *"and the engine added no log line"*. That made
all five runs fail, identically and reproducibly, on **"one bad file does not lose everyone's
bookkeeping"** in both transports:

```
2 FAILURE(S):
  - [ADB sync] one bad file does not lose everyone's bookkeeping: the engine went quiet within 30s
    — it did NOT. True: 10286 pass(es) ran and listed the folders; 0 call(s) found the gate already
    held; 10288 listing(s) total; 10290 engine log line(s). Not true: 0 of 2 consecutive no-change
    passes. Last observed world: pc:good1.txt|1|a | pc:good2.txt|1|c | phone:bad.txt|1|b | ...
```

That is not a flake and not an engine bug: that scenario deliberately leaves `bad.txt` unsyncable
and unrecorded **so it retries**, so the engine logs a fresh `Warn: Couldn't sync bad.txt` on
**every** pass, forever (~10,300 passes in the 30 s window). A quiet-log condition can never be
satisfied there. The quiet test is now the **world only**; the log-line count is kept in the failure
message as diagnostics. This is recorded in the method's doc comment so nobody re-adds it.

### Five consecutive runs

```
$ cd Linc
$ 1..5 | % { dotnet run --project tools/syncsim; "EXIT=$LASTEXITCODE" }
RUN 1 EXIT=0  ALL SCENARIOS PASSED
RUN 2 EXIT=0  ALL SCENARIOS PASSED
RUN 3 EXIT=0  ALL SCENARIOS PASSED
RUN 4 EXIT=0  ALL SCENARIOS PASSED
RUN 5 EXIT=0  ALL SCENARIOS PASSED
```

**Exit codes: 0, 0, 0, 0, 0.** No flake observed in five runs. No numbers were tuned to reach this —
the only change after the first result was removing the log-line term, for the reason above.


---

## PART A — media transport on the Tools page

### A1 — what the phone side already had (the finding you asked for)

**Everything. The phone half of `pc.media.control` was already complete and in use.** Read:
`service/OutgoingActions.kt`, `service/PcMediaStore.kt`, `service/MediaBridge.kt`,
`service/CompanionService.kt`, `service/SocketServer.kt`, `ui/HomeScreen.kt`.

| Piece | Where it is NOW | State |
|---|---|---|
| Send `pc.media.control` | `OutgoingActions.kt:14-24` — `object PcMediaControl { fun send(action: String) }`, `requiredVersion = 13`, fire-and-forget | Exists |
| Message-type constants | `Protocol.kt:75-76` — `PC_MEDIA_STATE`, `PC_MEDIA_CONTROL` | Exists |
| Receive `pc.media.state` | `SocketServer.kt:496` (`negotiated >= 13`) then `CompanionService.kt:98` `onPcMediaState` then `parsePcMedia` (`:144-160`) | Exists |
| Hold the state | `PcMediaStore.kt` — `StateFlow<PcMedia?>`; `PcMedia` carries title, artist, playing, positionMs, durationMs, app, present | Exists |
| A UI already driving it | `HomeScreen.kt:343/351/357` — `PcMediaControl.send("prev"/"pause"/"play"/"next")` | Exists |

**Therefore the Tools page reaches the PC's media with no new plumbing, and I added none.** No new
object, no new message type, no new parser, no new subscription, no fetch. `ToolsScreen.kt` now
does exactly what `HomeScreen.kt` already did: `collectAsState()` on `PcMediaStore.state` and call
`PcMediaControl.send(...)`.

**"Show what is playing" was free and is shown.** `pc.media.state` is pushed unsolicited by the
desktop on connect and on every change (`PcMediaService.PublishAsync`, plus a 6 s republish while
something plays), and `PcMedia` already carries `title`/`artist`/`playing`/`present`. No fetch was
added, per A2.

**One thing the planner's finding did not mention, worth knowing.** `MediaBridge.kt` is the
*opposite* direction — the phone's own sessions mirrored to the desktop as `media.state`, with the
desktop sending `media.control` back. It is unrelated to this milestone and was not touched. The
two directions genuinely are separate paths with separate message types.

### A2 — what was built

**`ANDROID/app/src/main/java/app/linc/android/ui/ToolsScreen.kt`** gains a `MediaCard`:

- **Transport:** Previous / Play-Pause / Next calling `PcMediaControl.send("prev" | "play" | "pause" | "next")`.
  Same v13 message. The Play/Pause label follows `pcMedia?.playing`, the same way Home's widget does.
- **Now playing:** title — artist, from the state already in hand.
- **Volume:** minus, level %, plus, Mute/Unmute calling `PcControl.volumeDown/volumeUp/volumeMute`
  (v17 `pc.control`).
- **Disable, never hide**, following M4b's pattern exactly:
  - Transport gated on a **v13** link (`mediaReason()`, a new third gate) **and** on
    `media?.present == true`. The second half is the real "the PC cannot honour this" case: when
    `pc.media.state` carries `none: true`, `PcMediaService.OnCompanionMessage` reaches neither its
    SMTC branch (`_session is { } session && IsPlaying(session)`) nor the `WinampRemote.Control`
    fallback, so the press is silently dropped. The buttons now say **"Nothing is playing on the PC
    right now."** instead.
  - Volume gated on the existing **v17** `controlsReason()` gate.
  - Both blocked reasons render as text under the card; nothing is hidden.

### A DECISION I MADE, because the task file left a collision — please note this one

**A2 asks for "volume up/down/mute" in the new Media block. Volume was already on this exact page**,
in M4b's `QuickControlsCard` (`ToolsScreen.kt`, the `Text("Volume")` row). Adding it to the Media
block as written would have put **two identical volume controls one card apart on one screen.**

I put volume in the Media block as the task asks, and **removed the now-duplicate row from
`QuickControlsCard`.** `QuickControlsCard` keeps Lock / Sleep / Shut down / Restart / Brightness;
its `onVolumeDown`/`onVolumeUp`/`onMuteToggle` parameters were removed with the row. This is the
same `PcControl` volume, sending the same `pc.control` payloads, just one card lower.

Flagging it explicitly per GUIDE §3: **this is an edit to M4b's shipped card that the task file did
not ask for**, made because the task could not be carried out correctly without resolving the
collision. It is a move, not a rewrite — no volume behaviour changed. No harness asserted anything
about `ToolsScreen.kt` before this session (`grep -r ToolsScreen tools\` returned no matches), so
nothing depended on the row's old location. If the planner wanted volume left in both places, that
is a one-line revert.

### A3 — a real desktop-side behaviour I did NOT fix (out of scope, reporting instead)

`PcMediaService.OnCompanionMessage` routes to the SMTC session only when `IsPlaying(session)` is
**true**. A registered-but-**paused** SMTC player (a video paused in PotPlayer, say) is therefore not
reachable — the handler falls through to `WinampRemote.Control`, which will not know that player.
`BuildPayloadAsync` has the mirror-image rule (`:193`), so such a PC publishes `none: true` and the
phone correctly shows "Nothing is playing". The net effect: **pressing Play cannot resume a paused
SMTC player from the phone.** This is pre-existing Era-1 behaviour, identical on Home's widget, and
fixing it is a desktop `PcMediaService` change — which §7 puts out of scope and which the "the wire
does not move" framing of M4c argues against. Not touched. Worth a line in a future milestone.

---

## PART B — the slideshow clicker

**New file: `ANDROID/app/src/main/java/app/linc/android/service/PresentationKeys.kt`** — two pure
objects, no `Context`:

- `object PresentationKeys` — `enum Action { Next, Previous, Start, End, Black }` and
  `keyFor(action): Int`. **Reuses `KeyboardMapping.VK_RIGHT` / `KeyboardMapping.VK_LEFT`** for
  next/previous rather than declaring a second copy; adds `VK_ESCAPE`, `VK_SPACE`, `VK_UP`,
  `VK_DOWN`, `VK_B`, `VK_F5`.
- `object PresentationControl` — `send(action)` calls `MirrorControl.key(code, down = true)` then
  `key(code, down = false)`. That is the existing v14 `pc.input` message, `requiredVersion = 14`,
  the same one this page's keyboard and trackpad already use. **No mirror session is required** —
  verified by reading `MirrorControl.key` (`MirrorControl.kt:39-51`): it references no streaming
  flag and no channel 5, only the negotiated version.

**On `KeyboardMapping`, which the task told me to read first:** it carries `VK_BACK`, `VK_RETURN`,
`VK_LEFT`, `VK_RIGHT` — so it had **two** of the keys I needed and not the other four. `map()` is a
`KeyEvent`-to-`Mapped` function for IME keystrokes, a different shape from action-to-VK, so I did
not extend it (that would have changed what the M4a keyboard sends for DPAD_UP/DOWN). I reused its
two constants and left the file untouched.

**A decision the task file left open.** §B says *"Right/Down/Space for next, Left/Up for previous."*
Sending all three would advance three slides per tap, so `keyFor` returns exactly one key —
**Right** for next, **Left** for previous. The alternates exist as named constants (`VK_DOWN`,
`VK_SPACE`, `VK_UP`) so the choice is visible, and a test asserts the mapping does **not** send them.

**`PresentationCard` on `ToolsScreen.kt`:** Previous / Next, then Start (F5) / End (Esc) / Black (B),
gated on the page's existing v14 `ready` flag with its existing reason text. It carries the required
one-liner verbatim: *"These send key presses to whatever is in focus on the PC — there's no
slideshow app detection."* No app detection and no PowerPoint integration was written.

---

## PART C — the two stale doc comments

Both now describe what the code produces. `HomeOfflineBanner.FormatText`
(`Services\HomeCacheFormat.cs:66`) returns `$"Showing what was last synced at {latest.ToLocalTime():t}."`
— the `"Phone disconnected — "` prefix was removed by M15b (its own comment at `:61-65` explains why).

- `ViewModels\HomeViewModel.cs:319` — the summary's quoted text changed from
  `"Phone disconnected — showing what was last synced at {time}."` to
  `"Showing what was last synced at {time}."`, plus a clause noting the banner states cache age only
  and the link's state is the Phone card's `ConnectionCaption`.
- `ViewModels\SyncViewModel.cs:173` — same correction, same shape.

**Comments only. No rendered string changed** — confirmed: the only producer of that text is
`HomeOfflineBanner.FormatText`, and it was not edited.

---

## Files changed

| File | Part | New? |
|---|---|---|
| `tools/syncsim/Program.cs` | 0 | modified |
| `ANDROID/.../service/PresentationKeys.kt` | B | **new** |
| `ANDROID/.../ui/ToolsScreen.kt` | A, B | modified |
| `ANDROID/.../test/.../service/PresentationKeysTest.kt` | B | **new** (11 tests) |
| `ANDROID/.../test/.../ui/ToolsScreenSourceTest.kt` | A | **new** (10 tests) |
| `DESKTOP/Linc.Desktop/ViewModels/HomeViewModel.cs` | C | modified (comment only) |
| `DESKTOP/Linc.Desktop/ViewModels/SyncViewModel.cs` | C | modified (comment only) |

Nothing else was touched. No files deleted, moved or renamed; no dependency, build-config or target
framework change; no stray scratch files left anywhere in the repo. Nothing committed, branched or
merged.

---

## Acceptance

### 1. Builds and tests

Desktop, after all edits (`Linc.Desktop.exe` killed first):

```
D:\...\ViewModels\HomeViewModel.cs(166,72): warning CS9113: Parameter 'connection' is unread.
D:\...\ViewModels\HomeViewModel.cs(166,72): warning CS9113: Parameter 'connection' is unread.
  Linc.Desktop -> D:\...\bin\x64\Debug\net8.0-windows10.0.19041.0\Linc.Desktop.dll
Build succeeded.
    2 Warning(s)
    0 Error(s)
```

**Identical to the §0.2 baseline: 0 errors, the same 2 duplicated `CS9113`.** No new warning.

Android, `clean assembleDebug testDebugUnitTest` (a full clean, so no Kotlin warning could hide
behind an up-to-date task):

```
BUILD SUCCESSFUL in 1m 19s
EXIT=0
CLEAN BUILD: tests=138 failures=0 errors=0 files=14
```

No `warning:` or `error:` line in the clean build's output.

**Test count: 117 to 138, i.e. 21 new Android tests.**

| New test class | Tests | Covers |
|---|---|---|
| `service/PresentationKeysTest` | **11** | the key mapping and `PresentationControl`'s real send path |
| `ui/ToolsScreenSourceTest` | **10** | the "disable, never hide" rule on `ToolsScreen.kt` |

The task asked how many new tests cover the key mapping: **11**. The other 10 exist because
acceptance item 6(b) needs a check that can fail when a control renders enabled — see below.

### 2. Protocol constants — both still 18, no new message types

```
$ Get-Content DESKTOP\Linc.Desktop\Protocol\Envelope.cs | Select -Skip 11 -First 1
    public const int Version = 18;

$ Get-Content ANDROID/...\protocol\Protocol.kt | Select -Skip 11 -First 1
const val PROTOCOL_VERSION = 18
```

**No bump was needed and none was made.** Every message this milestone sends already existed:
`pc.media.control` (v13), `pc.control` (v17), `pc.input` (v14). No constant was added to
`MessageType` on either side. Nothing about the wire moved.

### 3. A1's finding

Above, in full. Short version: **the phone side already had all of it**, so what I did *not* build
is the entire transport path — no send object, no message type, no `pc.media.state` parser, no
store, no subscription, and no fetch for "what's playing".

### 4. `syncsim` — five consecutive exit codes and the ceiling

**Ceiling: 30 s** (`cleanroomsim`'s number), polling every **100 ms**, quiet = **2 consecutive
passes** that leave the observable world identical.

```
RUN 1 EXIT=0  ALL SCENARIOS PASSED
RUN 2 EXIT=0  ALL SCENARIOS PASSED
RUN 3 EXIT=0  ALL SCENARIOS PASSED
RUN 4 EXIT=0  ALL SCENARIOS PASSED
RUN 5 EXIT=0  ALL SCENARIOS PASSED
```

### 5. Regression sweep — all green

Run with CWD = `Linc`, `packagesim` after a fresh
`-t:Publish -p:PublishProfile=Beta -p:Configuration=Release -p:Platform=x64` (§0.3's trap; the
publish itself was 0 errors / 2 warnings):

```
packagesim           EXIT=0
cleanroomsim         EXIT=0
pccontrolsim         EXIT=0
hotspotsim           EXIT=0
presencesim          EXIT=0
startupsim           EXIT=0
applaunchsim         EXIT=0
mirrorsettingssim    EXIT=0
appssim              EXIT=0
homelayoutsim        EXIT=0
displaysim           EXIT=0
homecachesim         EXIT=0
storesim             EXIT=0
synccachesim         EXIT=0
outboxsim            EXIT=0
apkinstallsim        EXIT=0
autostartsim         EXIT=0
syncsim              EXIT=0
```

`desktopsim` / `blescan`, honest either way:

```
desktopsim     EXIT=1
    === SUMMARY ===
    FAIL: 1 check(s) failed:
      - No attached ADB device found
    (all 7 pure checks above it PASSED, incl. ParseWmOutput and the legacy-JSON default case)

blescan        EXIT=0
    PASS  Z2453 (LZ0A35AEDC8000199) sighted 4x — the beacon matched a pinned pairing.
```

`desktopsim`'s only failure is its hardware check: no phone was attached to this session.
`blescan` sighted a pinned device — note it is **`Z2453` / `LZ0A35AEDC8000199`, not the Pixel 7**;
I did not investigate further, it was not in scope, and no phone was touched.

### 6. Two negative proofs, both against PRODUCTION source

#### (a) Break one key in the presentation mapping

**File broken:** `ANDROID/app/src/main/java/app/linc/android/service/PresentationKeys.kt` — a
production source file, not a test's copy of the mapping. Change: in `keyFor`,
`Action.Black -> VK_B` became `Action.Black -> VK_ESCAPE`.

```
$ gradlew.bat testDebugUnitTest --no-daemon --tests "*PresentationKeysTest*"
PresentationKeysTest > every action maps to a distinct key FAILED
    java.lang.AssertionError at PresentationKeysTest.kt:69
PresentationKeysTest > black screen sends VK_B FAILED
    java.lang.AssertionError at PresentationKeysTest.kt:56
11 tests completed, 2 failed
> Task :app:testDebugUnitTest FAILED
BUILD FAILED in 46s
EXIT=1
```

Restored `Action.Black -> VK_B`; the full suite is green again (138/138, above).

#### (b) Make a PC-unsupported control render enabled instead of disabled

**File broken:** `ANDROID/app/src/main/java/app/linc/android/ui/ToolsScreen.kt` — production source.
Change: `OutlinedButton(onClick = onSleep, enabled = ready && canSleep)` became
`OutlinedButton(onClick = onSleep, enabled = ready)`, i.e. Sleep now renders **enabled** on a PC
whose `pc.state` reply says `canSleep: false`.

```
$ gradlew.bat testDebugUnitTest --no-daemon --tests "*ToolsScreenSourceTest*"
ToolsScreenSourceTest > sleep is disabled when the PC reports it cannot sleep FAILED
    java.lang.AssertionError at ToolsScreenSourceTest.kt:29
10 tests completed, 1 failed
> Task :app:testDebugUnitTest FAILED
BUILD FAILED in 45s
EXIT=1

java.lang.AssertionError: ToolsScreen.kt no longer contains the Sleep button gated on canSleep.
  expected to find: OutlinedButton(onClick = onSleep, enabled = ready && canSleep)
  A control the PC cannot honour must be DISABLED with a reason, never rendered enabled-but-inert
  and never hidden.
	at app.linc.android.ui.ToolsScreenSourceTest.requireExact(ToolsScreenSourceTest.kt:29)
	at app.linc.android.ui.ToolsScreenSourceTest.sleep is disabled when the PC reports it cannot
	   sleep(ToolsScreenSourceTest.kt:51)
```

Restored `enabled = ready && canSleep`; the full suite is green again (138/138, above).

**Where this check lives and why, stated plainly (GUIDE §4.1/§4.4).** No harness in `tools/` asserts
anything about `ToolsScreen.kt` — `grep -r ToolsScreen tools\` returned nothing before this session.
The thing under test is a `@Composable`'s `enabled =` argument, which this module's plain-JUnit
setup cannot call (no Robolectric, no Compose test rule). So `ToolsScreenSourceTest` reads the
**production file's text** and requires each control's `enabled` expression *paired with that
control's own handler in one string* — `OutlinedButton(onClick = onSleep, enabled = ready && canSleep)`,
not a bare grep for `canSleep`. It also fails hard, never skips, if it cannot find the file.

**Its weakness, stated rather than hidden:** it is a text match. It proves the gate is written; it
cannot prove Compose honours it at runtime. A consistent rename of both handler and flag would need
this file updated too. That is the intended cost, and it is written into the class's doc comment.

### 7. The real `%LOCALAPPDATA%\Linc/settings.json` — hash before and after

```
BEFORE   SHA256=BABE43DEC9D512847AD9DADB1BBB7B2EB86B9E2E99342BD392D3834039307FFD
         Size=3289  MTimeUtc=2026-08-25 20:32:27

AFTER    SHA256=BABE43DEC9D512847AD9DADB1BBB7B2EB86B9E2E99342BD392D3834039307FFD
         Size=3289  MTimeUtc=2026-08-25 20:32:27
```

**Byte-for-byte identical.** The file was not touched.

*Honest scoping of "before":* the hash was first taken after Part 0's `syncsim` runs and before the
full harness sweep, the publish, and everything in Parts A/B/C, then again at the very end. Part 0's
`syncsim` runs are covered by A0/D-057 — that harness points `SyncEngine` at its own temp root and
never reaches `%LOCALAPPDATA%\Linc`. `devicesim` was **not** run in this session at all.

### 8. No negative proof ran through a real-data path

Both are structural: one edits a pure Kotlin mapping and runs a JVM unit test, the other reads a
source file as text. Neither opens a socket, touches `%LOCALAPPDATA%`, talks to adb, or writes
outside `ANDROID/app/build/`.

---

## What I could NOT verify

1. **Nothing was hardware-verified. No phone was attached to this session** — `desktopsim`'s single
   failure is exactly that. The companion APK was **not** installed (the task did not say to), so
   none of Part A or Part B has run on a real Pixel 7 against a real PC.
2. **The Tools page's rendering was not seen.** No emulator, no screenshot, no Compose test rule.
   That `MediaCard` and `PresentationCard` compile and are wired is proven; that they *look* right,
   fit on one screen, and that the disabled states appear grey to a human, is not. **Layout risk
   worth naming:** the Tools `Column` is not scrollable and the trackpad `Card` takes `weight(1f)`;
   two new cards squeeze it. It will shrink, not clip, but on a short phone the trackpad area may
   now be noticeably smaller. That is item 17 of the manual script.
3. **That the PC actually plays/pauses/skips from the Tools buttons** — the desktop half was read,
   not exercised. `pccontrolsim` covers the `pc.control` validator; there is no harness that
   exercises `PcMediaService.OnCompanionMessage`, and I did not add one (out of scope).
4. **That the slide keys land in a real slide deck.** `pc.input` keystrokes reaching a focused
   Windows app cannot be verified from here without moving the real cursor / driving the desktop,
   which GUARDRAILS forbids. The mapping is proven; delivery is not.
5. **That `pc.input` works with no mirror open** — verified by *reading* `MirrorControl.key` and
   asserting in a unit test that `send()` emits only `pc.input` and never a mirror-start, which is
   as far as a source-level proof goes. The task states M4a already proved this on hardware; **I did
   not re-verify that on hardware and am not citing M4a as evidence I gathered.**
6. **Whether `blescan`'s `Z2453` sighting matters.** Out of scope, not investigated.

---

## Anything in the task file that was wrong, ambiguous, or contradictory

1. **§0.1's "~14 modified files" is low.** The tree has **21 modified + 1 untracked**. Everything
   else in §0.1's expectation matched exactly.
2. **§A2 asks for volume in the Media block, but volume was already on that same page** from M4b's
   `QuickControlsCard`. Following A2 literally would have shipped two identical volume controls one
   card apart. Resolved by moving rather than duplicating — see Part A's decision note. **This is the
   one place I edited something the task did not name.**
3. **§B's "Right/Down/Space for next, Left/Up for previous" cannot all be sent.** Three keys per tap
   would advance three slides. Read as "these are the keys that mean next"; one is sent.
4. **§B's "Reuse `KeyboardMapping` if it already carries these keys"** — it carries two of six
   (`VK_LEFT`, `VK_RIGHT`). Those two are reused from it; the other four are new constants in
   `PresentationKeys`. `KeyboardMapping.kt` itself was not modified, because its `map()` is a
   `KeyEvent`-to-`Mapped` IME function and adding DPAD_UP/DOWN there would change what M4a's
   keyboard sends.
5. **§5.6(b) presumes a check already exists** ("its check fails"). None did — no harness in `tools/`
   referenced `ToolsScreen.kt`. I wrote `ToolsScreenSourceTest` so the item could be honoured;
   the 10 tests it adds are why the new-test count is 21 and not 11.
6. **§0.2's warning count needs `-t:Rebuild` to observe.** An incremental MSBuild run prints the
   `CS9113` once and no summary at all; the duplicate only appears on a rebuild. Not a problem —
   just noting that "2 warnings" is not what an incremental build shows.

---

## Numbered manual test script (for the owner)

Everything below needs the real phone and PC; none of it was done for you.

**Setup**

1. Build and install the companion app on the Pixel 7 yourself — I did not install the APK.
   `cd Linc/ANDROID`, then `gradlew.bat installDebug` (or install
   `app/build/outputs/apk/debug/app-debug.apk` your usual way).
2. Start `Linc.Desktop` on the PC and confirm the Pixel 7 shows as connected. **Note: I killed
   `Linc.Desktop.exe` several times during this session for the builds — it is not running now.**
3. On the phone, open **Home, then the Tools card, then Tools**.

**Media, with the mirror window CLOSED (this is the point of the test)**

4. Make sure no Linc mirror / PC-Remote window is open anywhere.
5. On the PC, play something with a real media session — Spotify, a YouTube tab, Groove, VLC.
6. On the phone's Tools page, look at the **Media** card. It should name the track (title — artist)
   within a few seconds. If it says *"Nothing is playing on the PC right now."* while audio is
   audible, that is a finding — tell the planner which player it was.
7. Tap **Pause**. The PC should pause and the button should become **Play**.
8. Tap **Play**, then **Next**, then **Previous**. Confirm each acts on the PC.
9. Tap volume **minus** three times, then **plus** three times, then **Mute**, then **Unmute**. The
   percentage on the card should track the PC's real volume each time.
10. Stop all playback on the PC. Within ~6 s the card should say *"Nothing is playing on the PC right
    now."* and **Previous / Play / Next should go grey (disabled), not vanish.** That is the
    "disable, never hide" rule; report it if they stay tappable or disappear.

**Presentation, still with the mirror closed**

11. On the PC, open any slide deck (PowerPoint, Google Slides in a browser, LibreOffice Impress) and
    **click once on the PC to give it focus** — these are key presses to whatever is focused.
12. On the phone, tap **Start**. The deck should enter presentation mode (that is F5).
13. Tap **Next** four or five times, then **Previous** twice. Each tap should move exactly **one**
    slide. If a tap jumps two or three slides, tell the planner — that would mean more than one key
    is arriving.
14. Tap **Black**. The screen should go black (that is B). Tap **Black** again to come back.
15. Tap **End**. The deck should leave presentation mode (that is Esc).
16. Now click on **Notepad** on the PC and tap **Next** on the phone. The caret should move right one
    character. That is the honest behaviour and the card says so — confirm the wording reads
    acceptably to you.

**Layout / regression**

17. On the Tools page, confirm all four blocks fit and the **trackpad area at the bottom is still
    usable** — it now shares the screen with two more cards. Drag on it and confirm the PC cursor
    still moves, and that a tap still clicks.
18. Tap **Keyboard**, type a few characters into a focused PC text field, and confirm the M4a
    keyboard still works.
19. Confirm **Lock / Sleep / Shut down / Restart / Brightness** are all still present in the
    quick-controls card — only the volume row moved out of it.
20. On the PC's Home page, disconnect the phone and confirm the offline banner still reads
    **"Showing what was last synced at HH:MM."** with no "Phone disconnected —" prefix. (Part C
    changed only comments, so this should be unchanged; it is here as a regression check.)

**If something is wrong**, the phone's own **Logs** screen is the fastest read, and on the desktop
side `PcMediaService` logs `"PC media for the phone: <title> (<app>)"` whenever what the phone would
show changes.
