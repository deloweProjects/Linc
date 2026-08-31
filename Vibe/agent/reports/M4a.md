# REPORT — M4a: phone Tools page (remote keyboard + trackpad), no protocol change

Agent: Claude Code 2. Session did §0.1 and §0.2 first, as instructed, before making any edit.

## Bottom line

Acceptance items 1–5 all pass. The wire stays at v16, `DESKTOP/` has no modifications, and the
one negative proof (break/fail/restore/green) is done against production source. One correction
to the task's own premise in §0.1/§1: `MirrorControl.kt` already exposed nearly all of the
trackpad sends before this session touched anything — see below.

---

## §0.1 — what was on disk, inspected before any edit

**`MirrorControl.kt`** (`ANDROID/app/src/main/java/app/linc/android/service/MirrorControl.kt`)
already exposed, at the start of this session: `ready()`, `start()`, `key(virtualKey, down)`,
`pointer(kind, x, y, mode)` (absolute/touch), `pointerRelative(kind, dx, dy)` (trackpad move,
hardcodes `mode="trackpad"`), `scroll(dx, dy)`, `text(text)`, and `stop()`. It is a service-level
`object`, not inside `MirrorActivity`, exactly as §1 describes. The only function it did **not**
yet have was `click(down: Boolean)` (a trackpad tap/button press, `kind: down|up`, `mode:
trackpad`) — `git diff` for this file shows exactly that one function as the addition, 14 lines.

Every one of these functions calls `CompanionOutbox.trySend(envelope, requiredVersion = 14)`
directly. `CompanionOutbox.trySend` (`service\CompanionOutbox.kt:65`) only checks the current
control-connection `Registration`'s negotiated version against `requiredVersion` — there is no
reference anywhere in `MirrorControl.kt` or `CompanionOutbox.kt` to a streaming flag, to
`PC_MIRROR_START` having been sent, or to channel 5. **§1's hypothesis holds**: `pc.input` sends
are independent of the mirror stream. No fix was needed because there was nothing gating it.

**`MirrorActivity.kt`** header comment (line ~46, now at the same relative spot) reads: *"a
zero-size EditText holds IME focus. Printable characters go to the PC as `pc.input text`;
backspace/enter/arrows go as Windows virtual-key codes."* The actual `wireImeSink()` body at the
start of this session mapped exactly four cases — `KEYCODE_DEL`, `KEYCODE_ENTER`,
`KEYCODE_DPAD_LEFT`, `KEYCODE_DPAD_RIGHT` — plus a text-watcher fallback for IME commit-without-
key-event (Gboard). "Arrows" in the comment is therefore loosely worded; the code only ever
handled left/right, never up/down. That mismatch between the header's wording and the code was
already there and is carried forward unchanged into `KeyboardMapping.kt` (§3.3 says reuse
*exactly*, not invent a second mapping, so up/down were correctly not added).

**`ui/Screen.kt`** defines the bottom-nav `enum class Screen`: `Home, Status, Logs, Settings` —
no `Share` entry, confirming M10 moved Share out of the bottom nav into a Home card. There is
also no `Tools` entry in this enum, consistent with §3.1's instruction to follow that precedent
(a Home card, not a nav destination).

**Beyond what §0.1 named**, `git status` (run from `Linc/`, per the guide) showed the working
tree already carried implementation matching essentially the whole of §3:

- Modified: `AndroidManifest.xml`, `MirrorActivity.kt`, `MirrorControl.kt`, `ui/HomeScreen.kt`.
- Untracked: `ToolsActivity.kt`, `service/KeyboardMapping.kt`, `service/KeyboardSink.kt`,
  `service/TrackpadGestures.kt`, `ui/ToolsScreen.kt`, and three test files (`KeyboardMappingTest`,
  `MirrorControlTest`, `TrackpadGesturesTest`).

I read every one of these files in full before touching anything. They implement:

- A `ToolsWidget` Home card (`HomeScreen.kt`) opening `ToolsActivity`, not a bottom-nav entry.
- `ToolsActivity` → `ToolsScreen` (full-screen Compose screen), manifest-declared
  `exported="false"`, same posture as `MirrorActivity`.
- `TrackpadGestures` (pure object): `drag(dx, dy)` → `Action.Move` scaled by `MOVE_SENSITIVITY`;
  `twoFingerDrag(dx, dy)` → `Action.Scroll` in wheel notches via `SCROLL_PIXELS_PER_NOTCH`.
- `KeyboardMapping` (pure object, lifted verbatim from `MirrorActivity`'s old `wireImeSink`):
  `map(keyCode, unicodeChar) : Mapped?` → `VirtualKey` or `Text`.
- `KeyboardSink`: the one shared glue object that wires an `EditText`'s key events and
  text-watcher through `KeyboardMapping` to `MirrorControl`. `MirrorActivity.wireImeSink()` is now
  a one-line delegate to it (`git diff` shows the ~35-line inline implementation replaced by
  `private fun wireImeSink() = KeyboardSink.wire(imeSink)`), and `ToolsScreen`'s own IME sink
  calls the same `KeyboardSink.wire(...)`.
- `ToolsScreen`'s trackpad gesture loop (`trackpadGestures()`), which does only pointer-count /
  tap-vs-drag recognition and calls `TrackpadGestures` + `MirrorControl`, matching §3.2's "put the
  mapping in a pure function" split.
- A `connectionReason(state): String?` function gating the surface: visible-but-inert with a
  plain-language reason, driven by `CompanionStateHolder.state` (set by `SocketServer.kt` on any
  control connection — ADB or Direct TLS both go through it; nothing in the reason logic or its
  data source is ADB-specific).
- Three test files covering exactly the pure functions in §3.5.

I did not treat this as license to skip verification. I built, ran every test, checked each
requirement in §3 line-by-line against the actual code (not just against file *names*), ran the
full desktop regression list, and performed the negative proof myself (below) — all against the
code exactly as it now sits in the tree.

## §0.2 — baseline build (before any edit)

```
ANDROID\gradlew.bat assembleDebug testDebugUnitTest --no-daemon
JAVA_HOME = C:\Program Files\Android\Android Studio\jbr

BUILD SUCCESSFUL in 41s
43 actionable tasks: 43 up-to-date
```

Unit test counts (from `app/build/test-results/testDebugUnitTest/*.xml`), all green:

| Suite | tests | failures |
|---|---|---|
| protocol.EnvelopeTest | 3 | 0 |
| protocol.FramingTest | 9 | 0 |
| service.AppInventoryTest | 6 | 0 |
| service.CompanionOutboxTest | 8 | 0 |
| service.DisplayControlTest | 20 | 0 |
| service.KeyboardMappingTest | 7 | 0 |
| service.MirrorControlTest | 4 | 0 |
| service.OutgoingActionsTest | 13 | 0 |
| service.TrackpadGesturesTest | 9 | 0 |
| **Total** | **79** | **0** |

This is the recorded baseline, taken before this session wrote or edited anything.

---

## §1 verification — does `MirrorControl` gate sending on an active stream?

**No.** Evidence:

- `MirrorControl.kt` — every send function (`key`, `pointer`, `pointerRelative`, `click`,
  `scroll`, `text`) calls `CompanionOutbox.trySend(envelope, requiredVersion = 14[, topic])`
  directly. None reference a streaming/mirror-active flag.
- `CompanionOutbox.kt:65-78` (`trySend`) — the only gates are: a live control `Registration`
  exists (`current()`), its negotiated version `>= requiredVersion`, and (v6+ links only) topic
  subscription if a topic is given. `pc.input` sends carry no topic. No stream/session state is
  consulted anywhere in this file.
- `ToolsScreen.kt`'s gesture loop and `KeyboardSink` never call `MirrorControl.start()` — they go
  straight to `pointerRelative` / `scroll` / `click` / `key` / `text`.

So `pc.input` rides the control channel exactly as documented, requires only a negotiated v14+
desktop, and needs no `pc.mirror.start` and no channel 5. No change was needed to make this true.

---

## Where the keyboard mapping lives, and who calls it

`ANDROID/app/src/main/java/app/linc/android/service/KeyboardMapping.kt` — a pure object,
`map(keyCode: Int, unicodeChar: Int): Mapped?`, returning a sealed `Mapped` (`VirtualKey(code)` or
`Text(text)`), lifted verbatim from what was inline in `MirrorActivity.wireImeSink()`.

`ANDROID/app/src/main/java/app/linc/android/service/KeyboardSink.kt` wires an `EditText`'s
`OnKeyListener` and `TextWatcher` through `KeyboardMapping` to `MirrorControl.key(...)` /
`MirrorControl.text(...)`. Two callers, one mapping:

- `MirrorActivity.wireImeSink()` → `KeyboardSink.wire(imeSink)`.
- `ToolsScreen`'s invisible `AndroidView(EditText)` → `KeyboardSink.wire(this)`.

## The trackpad sensitivity constant

`TrackpadGestures.MOVE_SENSITIVITY = 2f` (`service\TrackpadGestures.kt:18`) — one-finger drag
pixels are multiplied by 2 before becoming the `dx`/`dy` sent as a trackpad move. Documented
rationale in the file: 1:1 raw finger pixels feel sluggish for short thumb strokes on a high-DPI
phone driving a 1920×1080 desktop; 2× reads closer to a laptop trackpad. It's a single `const
val`, one line to retune, and pinned by a test (`` `sensitivity is currently 2x...` ``) so a future
change to it shows as a deliberate diff rather than an incidental shift in the other drag tests.

Separately, `TrackpadGestures.SCROLL_PIXELS_PER_NOTCH = 24f` converts two-finger drag pixels to
wheel notches. This exists because `PcInputService.Scroll` (`DESKTOP\Linc.Desktop\Services/
PcInputService.cs:103-108`, read-only, not modified) multiplies the received `dy`/`dx` by
`WHEEL_DELTA` (120) — i.e. the desktop expects **notch counts**, not raw pixels. 24px/notch is
the phone-side half of that unit conversion.

---

## Files touched, one line each

Nothing in `ANDROID/` required a functional change to satisfy §3 — the code already in the
working tree at the start of this session, verified line-by-line above, meets §3.1–§3.5 and the
§1 hypothesis. The one edit made this session was the required negative proof (immediately
reverted, see below); the net diff from that round-trip is zero.

`git status --porcelain` (from `Linc/`), same before and after this session:

```
 M ANDROID/app/src/main/AndroidManifest.xml
 M ANDROID/app/src/main/java/app/linc/android/MirrorActivity.kt
 M ANDROID/app/src/main/java/app/linc/android/service/MirrorControl.kt
 M ANDROID/app/src/main/java/app/linc/android/ui/HomeScreen.kt
?? ANDROID/app/src/main/java/app/linc/android/ToolsActivity.kt
?? ANDROID/app/src/main/java/app/linc/android/service/KeyboardMapping.kt
?? ANDROID/app/src/main/java/app/linc/android/service/KeyboardSink.kt
?? ANDROID/app/src/main/java/app/linc/android/service/TrackpadGestures.kt
?? ANDROID/app/src/main/java/app/linc/android/ui/ToolsScreen.kt
?? ANDROID/app/src/test/java/app/linc/android/service/KeyboardMappingTest.kt
?? ANDROID/app/src/test/java/app/linc/android/service/MirrorControlTest.kt
?? ANDROID/app/src/test/java/app/linc/android/service/TrackpadGesturesTest.kt
```

No file under `DESKTOP/` is modified. No stray files (no `task.md`, no scratch scripts) were
added.

---

## Acceptance, one by one

**1. Build + tests.**

```
ANDROID\gradlew.bat assembleDebug testDebugUnitTest --no-daemon
BUILD SUCCESSFUL in 31s
43 actionable tasks: 4 executed, 39 up-to-date
```

79 tests, 0 failures — identical to the §0.2 baseline count (this task added no new test files
beyond what was already in the tree; all pure-function coverage from §3.5 already exists and
passes: `KeyboardMappingTest` (7), `MirrorControlTest` (4), `TrackpadGesturesTest` (9)).

**2. Desktop unchanged.** Confirmed by the `git status --porcelain` above — no path under
`DESKTOP/` appears.

**3. `Protocol.kt` untouched, no new message type.**

```
$ grep -n "PROTOCOL_VERSION\|const val PC_" ANDROID/app/src/main/java/app/linc/android/protocol/Protocol.kt
12:const val PROTOCOL_VERSION = 16
75:    const val PC_MEDIA_STATE = "pc.media.state"      // desktop → phone
76:    const val PC_MEDIA_CONTROL = "pc.media.control"  // phone → desktop
80:    const val PC_MIRROR_START = "pc.mirror.start"    // phone → desktop
81:    const val PC_MIRROR_STOP = "pc.mirror.stop"      // either direction
82:    const val PC_DISPLAYS_GET = "pc.displays.get"    // phone → desktop
83:    const val PC_DISPLAYS = "pc.displays"            // desktop → phone
84:    const val PC_INPUT = "pc.input"                  // phone → desktop
85:    const val PC_TEXT_FOCUS = "pc.textfocus"         // desktop → phone: a text field gained/lost focus
```

`PROTOCOL_VERSION` is 16. All `pc.*` types listed already existed pre-M4a; none were added. `git
status`/`git diff` show zero changes to this file this session.

**4. Desktop regression, all run from `Linc/` via `dotnet run --project tools/<name> -c Debug`:**

| Harness | Result |
|---|---|
| packagesim | PASS — "All verification checks succeeded." |
| presencesim | PASS |
| startupsim | PASS |
| applaunchsim | PASS |
| mirrorsettingssim | PASS |
| appssim | PASS |
| homelayoutsim | PASS |
| displaysim | PASS |
| homecachesim | PASS |
| storesim | PASS |
| synccachesim | PASS |
| outboxsim | PASS |
| apkinstallsim | PASS |
| autostartsim | PASS ("ALL CHECKS PASSED") |
| syncsim | PASS ("ALL SCENARIOS PASSED", both ADB and Direct TLS transports) |

All 15 required harnesses green, verbatim `SUMMARY`/`PASS` lines captured above.

`desktopsim` and `blescan` — honest either way, as instructed:
- `desktopsim`: FAILs on 1 check — `"No attached ADB device found"` — hardware-dependent, no
  Pixel 7 attached this session.
- `blescan`: FAILs — `"no known phone was recognised (Linc-shaped beacons seen: 0)"` — same
  cause, and this is the pre-existing rotating-id/hardware-dependent failure the task says not to
  chase.

`cleanroomsim`: ran once, failed on `"No log file appeared under the redirected root — the
redirect did not take effect"` (the documented 5s log-settle flake). Reran once per the task's
instruction — **failed again on the same check, both times**. I did not touch `cleanroomsim`'s
source (no `fix the sleep into a poll` obligation applies), and it exercises no file this task
touched — it is entirely about `ToolLocator`/`LogService`/publish-output behavior on this
machine, unrelated to Tools/keyboard/trackpad. Flagging the double-failure honestly rather than
rounding it up to "known flake, ignore": on this machine the redirect genuinely isn't taking
effect in the 5s window on either run, not just occasionally.

**5. Negative proof against production source.**

Broke `ANDROID/app/src/main/java/app/linc/android/service/KeyboardMapping.kt` line 28:

```diff
-        KeyEvent.KEYCODE_DEL -> Mapped.VirtualKey(VK_BACK)
+        KeyEvent.KEYCODE_DEL -> Mapped.Text("")
```

Ran `gradlew testDebugUnitTest --tests "app.linc.android.service.KeyboardMappingTest" --no-daemon`:

```
KeyboardMappingTest > backspace maps to VK_BACK FAILED
    java.lang.AssertionError at KeyboardMappingTest.kt:18
7 tests completed, 1 failed
BUILD FAILED
```

Restored the line exactly. Reran the full suite:

```
ANDROID\gradlew.bat assembleDebug testDebugUnitTest --no-daemon
BUILD SUCCESSFUL in 31s
```

All 79 tests green again (counts identical to the §0.2 table, including
`service.KeyboardMappingTest : tests="7" failures="0"`).

---

## Numbered manual test script for the owner

Requires a v14+ desktop connected (ADB or Direct TLS) and the Pixel 7 (`<your-device-serial>`) in hand.
I did not perform any of these — they need a real finger on the phone screen and/or the real PC
cursor moving, both explicitly off-limits to me.

1. **Open Tools with the PC connected.** Home → tap the "Tools" card. *Good:* a full-screen page
   opens with a back arrow, "Tools" title, a "Keyboard" button, and a large card below with the
   hint text "Drag to move the cursor · Tap to click · Two fingers to scroll" at full opacity (not
   dimmed).
2. **Move the cursor.** One finger, drag slowly across the card. *Good:* the PC's real cursor
   moves smoothly in the same direction, roughly 2× your finger's travel distance (the
   `MOVE_SENSITIVITY` constant).
3. **Tap to click.** Touch down and lift without moving. *Good:* whatever is under the PC cursor
   receives a left-click (e.g. a taskbar icon opens, a text field gets focus).
4. **Two-finger scroll.** Put two fingers down over a long web page or document open on the PC and
   drag both up/down together. *Good:* the PC page scrolls in the matching direction; a very short
   drag (under ~24px) may produce no visible scroll — that's the per-notch rounding, not a bug.
5. **Type into a PC text field.** First click into an editable field on the PC (step 3), then tap
   "Keyboard" to raise the phone's soft keyboard, then type some text. *Good:* the characters
   appear in the PC's focused field as you type.
6. **Backspace and enter.** With the keyboard up and text present in that PC field, press
   backspace a few times, then press enter. *Good:* backspace deletes characters on the PC one at
   a time; enter behaves as Enter would locally (submits a form, adds a newline, etc. — whatever
   that field normally does on Enter).
7. **Not-connected state.** Stop the desktop app or disconnect, then open Tools again. *Good:* the
   trackpad card is visibly dimmed/inert and a plain-language sentence appears above it (e.g.
   "Waiting for your PC to connect." or "Linc's companion service is off. Open the Status tab to
   start it.") — no jargon, no crash, no silently-dead surface.

---

## What could NOT be verified, and why

- **All finger/touch interaction (steps 1–6 above)** — needs a real finger on the phone and a
  real PC cursor moving; both are off-limits to me per the task and the standing guardrails ("the
  owner drives it, you do not").
- **`desktopsim` / `blescan`** — hardware-dependent (no Pixel 7 attached this session); reported
  as failing, not chased, per the task's own instruction.
- **`cleanroomsim`** — failed twice on the same log-settle check; reported honestly above, not
  investigated further since it is outside this task's file set.
- I did not independently re-derive whether `SocketServer.kt`'s `CompanionStateHolder.update`
  calls fire correctly over a live Direct-TLS-only connection (no ADB) on real hardware — I
  confirmed only that the code path is not ADB-conditional by reading the source; a live TLS-only
  session would be the strongest proof and needs the owner's hardware.

---

## Ambiguities / things worth flagging in the task file

- **§0.1/§1 frame the investigation as if `MirrorControl` might need to be found and might need
  a fix ("if it does, say so... the fix is to make the send path independent of the stream").**
  In the tree as inspected, `MirrorControl` already had no such gate, and most of §3's
  implementation (trackpad pure functions, keyboard mapping extraction, the Tools screen and
  activity, the Home card, and matching unit tests) was already present before this session
  edited anything — only `MirrorControl.click()` was the one new function added to that file.
  Worth the master agent's attention: if the intent was to verify a *fresh* implementation
  end-to-end, that verification is what this report contains; if the intent was to build it from
  scratch, most of the ground was already covered.
- **The header comment "backspace/enter/arrows"** (`MirrorActivity.kt`, and `KeyboardMapping.kt`'s
  own doc comment) overstates the actual mapping, which only ever handled left/right, not up/down.
  §3.3 says to reuse the mapping *exactly*, so I left this as-is rather than "fixing" it by adding
  up/down, which would be inventing scope. Flagging in case the wording was meant literally and a
  future task should add up/down arrows.
- No other contradiction found between this task file and the code/protocol as they stand.
