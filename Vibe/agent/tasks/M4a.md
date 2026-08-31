# TASK M4a — the phone's Tools page: remote keyboard + trackpad. **NO PROTOCOL CHANGE.**

> **Agent: Claude Code 2.** Read `Vibe/agent/claude-code-2/GUIDE.md` first.
>
> **ONE PART, ONE SESSION.**
>
> **📄 WRITE YOUR FULL REPORT TO `Vibe/agent/reports/M4a.md`** as well as
> printing it. That file is the **only** `.md` you may write. (New standing rule — three reports in a
> row were truncated in transit and the QA depends on the parts that got cut.)

---

## 0. FIRST TWO STEPS

**0.1 — Inspect what is already on disk and report it.** Specifically: what does
`ANDROID/app/src/main/java/app/linc/android/service/MirrorControl.kt` expose today, what does
`MirrorActivity.kt` do with keyboard input (see its header comment at line 46), and how does
`ui/Screen.kt` define navigation destinations after M10 moved Share out of the bottom nav?

**0.2 — Build immediately, before you write anything.** `ANDROID\gradlew.bat assembleDebug
testDebugUnitTest --no-daemon` with `JAVA_HOME = C:\Program Files\Android\Android Studio\jbr`. Record
the baseline.

---

## 1. Why there is no protocol work in this task

M4's headline is "the phone drives the PC" and D-042 minted a `pc.control` message for it — **but that
is the *quick controls* half (lock/sleep/shutdown/volume), which is M4b, not this task.**

**Keyboard and trackpad need nothing new on the wire. Planner-verified in both implementations, not
assumed:**

- `PROTOCOL.md:323` — `pc.input` already carries
  `{"kind":"move"|"down"|"up"|"scroll"|"key"|"text", "x","y", "dx","dy", "keyCode", "down", "text",
  "mode":"touch"|"trackpad"}`, on the **control channel, fire-and-forget**.
- `DESKTOP\Linc.Desktop\Services\PcInputService.cs:35` documents `trackpad` as **relative** and `:67-71`
  applies `dx`/`dy` as pixel deltas with `absolute: false`. **So trackpad mode needs no video frame of
  reference** — the 0–65535 coordinate space in the protocol table applies to `touch` mode only.
- `keyCode` / `text` are likewise video-independent.
- `ANDROID\…\service\MirrorControl.kt` already sends `pc.input`, and it is a **service-level class, not
  buried in the Activity** — exactly the lift D-042 anticipated.

**So the wire stays frozen at v16 and this task carries zero protocol risk.** That is deliberate: this
project's own rule is to touch the wire last, and M5 was sequenced the same way.

**The one thing you must verify before building on it (planner's hypothesis — check it):**
`pc.input` is documented as riding the control channel, so it should work with **no mirror session and
no channel 5**. **Confirm that `MirrorControl` does not gate sending on an active stream or a
`pc.mirror.start` having been sent.** If it does, say so in the report — that is a finding, and the fix
is to make the send path independent of the stream, not to start a hidden stream.

---

## 2. Standing rules

- Workspace **yellow**; code **Linc** (`Linc`).
- **Code + tests only. Do not edit any project `.md`** — only your report file.
- **NO PROTOCOL WORK. Do not touch `Protocol.kt`'s version constant or add a message type.**
- **Never move the real cursor. Never tap the phone.** Hand interaction checks back as a numbered
  manual script. **This task is *about* moving the PC cursor — that makes the rule more important, not
  less: the owner drives it, you do not.**
- **Do not change the mirror.** `MirrorActivity` keeps working exactly as it does today.
- Android build: `JAVA_HOME = C:\Program Files\Android\Android Studio\jbr`, then
  `ANDROID\gradlew.bat assembleDebug testDebugUnitTest --no-daemon`. JVM error 1455 means "free memory",
  not a code problem.
- Test phone: Pixel 7, serial `<your-device-serial>`.

---

## 3. The work

**3.1 A Tools entry point on the phone's Home.** Follow **M10's precedent** — Share was deliberately
moved *out* of the bottom nav *into* Home, so add a Home entry that opens Tools rather than growing the
bottom nav. Tools itself is a **full screen**: a trackpad needs the whole surface.

**3.2 The trackpad.** A large touch surface that sends, via the existing `MirrorControl` path:

- one-finger drag → `{"kind":"move","mode":"trackpad","dx","dy"}` with pixel deltas;
- tap → `down` then `up` (left button);
- two-finger drag → `{"kind":"scroll","dx","dy"}`.

**Put the gesture→payload mapping in a pure function** (deltas in, payload out) so a unit test can call
it. **Include a sensitivity multiplier as a named constant** — raw finger deltas map 1:1 to pixels
otherwise, which feels wrong on a high-DPI phone driving a 1920x1080 screen. Pick a value, state it,
and make it one line to change.

**3.3 The keyboard.** Reuse **exactly** the mapping `MirrorActivity` already documents at its line 46:
printable characters go as `pc.input text`; backspace/enter/arrows go as **Windows virtual-key codes**.
**Do not invent a second mapping** — if that logic is currently inside `MirrorActivity`, lift it into a
shared, testable place and have both call it. Report that you did.

**3.4 Not connected = disabled, not hidden.** When there is no live link, the Tools surface is visible
but inert, with a plain-language reason (CONTRIBUTING.md — no jargon in the UI). This works over **any**
transport including pure Direct TLS, so do not gate it on ADB.

**3.5 Unit tests.** `testDebugUnitTest` must cover the pure functions from 3.2 and 3.3: a drag produces
the expected deltas, a tap produces `down`+`up`, two-finger produces `scroll`, a printable character
produces `text`, and backspace/enter/arrows produce the right virtual-key codes.

---

## 4. Acceptance

1. `gradlew assembleDebug testDebugUnitTest --no-daemon` → build succeeds, all unit tests pass. Paste
   the counts against the §0.2 baseline.
2. **The desktop is unchanged.** `git status` shows **no modification under `DESKTOP/`**. If you believe
   a desktop change is needed, **stop and report** rather than making it — it would mean the premise in
   §1 is wrong, which is a finding worth more than the feature.
3. **`Protocol.kt`'s version constant is untouched** and no new message type exists. Paste the grep.
4. Desktop regression, all green: `packagesim`, `presencesim`, `startupsim`, `applaunchsim`,
   `mirrorsettingssim`, `appssim`, `homelayoutsim`, `displaysim`, `homecachesim`, `storesim`,
   `synccachesim`, `outboxsim`, `apkinstallsim`, `autostartsim`, `syncsim`. `desktopsim` and `blescan`
   honest either way (**`blescan`'s rotating-id failure is pre-existing and hardware-dependent — do not
   chase it**). **`cleanroomsim` has a known 5 s log-settle flake** — if it fails, rerun once and say so;
   if you touch that file at all, fix the sleep into a poll.
5. **One negative proof against production source:** break the keyboard mapping (e.g. make backspace
   emit `text`), show the unit test fail, restore, green.

---

## 5. Report — to `Vibe/agent/reports/M4a.md` and printed

- §0.1 findings and the §0.2 baseline.
- **The §1 verification: does `MirrorControl` send `pc.input` without an active stream?** Evidence.
- Where the keyboard mapping now lives and who calls it.
- The trackpad sensitivity constant you chose and why.
- Files changed, one line each. Verbatim build and test output.
- The negative proof, naming the file you broke.
- **A numbered manual test for the owner:** open Tools on the phone with the PC connected, move the
  cursor with the trackpad, tap to click, two-finger scroll a long page, type into a PC text field,
  press backspace and enter. Include what "good" looks like for each.
- **What you could NOT verify and why** — anything needing a real finger on the phone belongs here.
- Anything in this file that was ambiguous, contradictory or wrong.

---

## 6. Out of scope

`pc.control` and the v17 bump (that is **M4b**: lock / sleep / shutdown / restart / volume / PC
brightness, with confirmation on the destructive ones); multimedia and slideshow controls; the telephony
notifier; **phone camera → PC webcam** (its own milestone — it needs a Windows virtual-camera device);
any change to `MirrorActivity`'s existing behaviour or to the mirror video path; `pc.textfocus`;
desktop-side changes of any kind; M13; editing project `.md` files; committing anything.
