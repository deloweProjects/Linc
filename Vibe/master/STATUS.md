# STATUS.md — where Linc is right now

The master's quick-glance snapshot. **Canonical source of truth is
`Vibe/agent/gemini-docs/BRAIN.md`; full history is `Vibe/agent/gemini-docs/CHANGELOG.md`.** Keep this
in sync at the end of every session.

_Last updated: **2026-08-24** (master handover — new master session)._

---

# ✅ M17a + M17b + M18 DONE (Claude Code 2, 2026-08-26/27) — ACCEPTED. **FIRST PRODUCTION BUILD EXISTS.**
Reports: `Vibe/agent/reports/M17a.md` (149) · `M17b.md` (150) · `M18.md` (203, over the 200 cap — it
said so rather than cutting content).

**Master-verified on disk:** `prod/Linc-Desktop-1.0.0-beta.1.zip` **170,988,255 B** and
`prod/Linc-Companion-1.0.0-beta.1.apk` **25,928,213 B** exist with `README.txt` + `CHECKSUMS.txt`
(sizes in the checksum file match the real files exactly); `Linc.Desktop.csproj` carries
`<InformationalVersion>1.0.0-beta.1`, **`<Product>Linc`** and `<AssemblyTitle>Linc` (the M12h
"Windows lists it as Linc.Desktop" debt is finally paid); `build.gradle.kts` `versionName
= "1.0.0-beta.1"`, `versionCode = 2`; `.gitignore:52` covers `prod/`; both protocol constants still 18.

### 🔴 THE PHONE ARRIVING MID-SESSION IS THE HEADLINE
M17a and M17b both honestly reported "nothing observed on hardware". In M18 the Pixel attached, and
**looking at the UI immediately found three defects source review had missed — the worst being
"Restart" in Quick controls rendering off-screen as an unreachable sliver.** All three fixed and
re-verified on the device. `desktopsim` **passed for the first time ever** because a phone was
attached. **This is the strongest evidence this project has produced for its own doctrine: a green
build is not evidence.**

### Two task-file defects it caught — both mine
1. **M17a acceptance 6(b) was not satisfiable as written.** `packagesim` check 5 is a `File.Exists`,
   so it stayed green against a **207-byte flat black icon**. It built the real geometry guard instead
   of reporting a hollow pass. *(Same family as M9b's hollow negative proof.)*
2. **M18's B2 premise was stale.** It asked to keep the phone's dynamic-colour path — **M17a had
   already removed it**, so following B2 literally would have regressed M17a. It left it and said why.

### 🟡 A DECISION THE OWNER SHOULD MAKE — dynamic colour is now one-directional
**Master-verified:** `Theme.kt:80` renders the phone with the **fixed Linc schemes**; the
wallpaper-derived Material You path is gone from the phone's own rendering (its comment says the
dynamic values still feed `DeviceStatusReporter.themePayload()`). Desktop-side, M18 B1 **did** ship
`UsePhoneColours` (`DeviceRegistry.cs:77-98`, **default `true`**).
**So: the PC borrows the phone's wallpaper colours; the phone itself always wears the Linc palette.**
That is defensible (one brand look on the phone) but it is **not** what "dynamic colours both sides"
asked for. **One line from the owner settles it; do not guess.**

### The beta gate, as the agent wrote it — and I agree with all three
- **The forced-update path can brick a user's app and its blocking screen has never been rendered
  once.** Ship with the manifest URL empty — proven inert (0 requests, 0 log lines).
- **The desktop's visual state is unproven:** it moved **47 font sizes on pages it never saw**,
  `HomePage` most of all. It builds and launches; that is not "looks right".
- **Nothing on the phone↔PC link was exercised** — the companion service was off all session, so
  **M16 step 4 (resume a paused player) is still never observed.** It proved the UI, not the product's
  job.
**Master's addition:** the APK is **debug-signed** (no `signingConfig` exists; it correctly refused to
invent a keystore). Fine for a handful of beta testers who sideload; **it is not a Play-track build.**

### The one thing to do before sending the zip to anyone
**Extract it, run it, and look at Home in both themes for two minutes.** 47 unseen font changes on the
busiest page is exactly the shape of defect that hardware found on the phone an hour earlier.

---

# ✅ M16 DONE (Claude Code 2, 2026-08-26) — ACCEPTED. Report: `Vibe/agent/reports/M16.md`

**Master-verified:** `Services/PcMediaRouting.cs` is new and real (14:57); **`PcMediaService` now calls
the same `PickTarget` at BOTH sites** — `:203` (publish) and `:257` (control) — which is the whole
fix; `ToolsScreen.kt:99` has `.verticalScroll(rememberScrollState())` and `:204` `.heightIn(min =
280.dp)`; constants still 18.

### Part A — my premise was HALF wrong, and the half it corrected matters
I said a paused player is published as `present: false` so the phone greys the buttons. **There is no
`present` field.** The phone sets `present = false` only on `none: true`, which needs *no SMTC session
AND no Winamp player*. So a paused browser/Spotify **is** published with a real title and **enabled**
buttons — the press was simply routed to `WinampRemote` and dropped. **The defect was one routing
rule, not a pair of mirrored ones.** My hypothesis that `IsPlaying` is a which-session picker held for
`PickSession`; the `IsPlaying` in the *routing* branch was a different, reachability gate — the bug.
**The fix is structural, not a patch:** routing and publishing now share one rule, so what the phone
sees is by construction what its buttons reach. `WinampRemote` stays as the no-SMTC fallback.

### Part B — my premise was wrong again, and the reality was worse
I wrote "everything shares `weight(1f)`". **Only the trackpad had a weight** — so the three cards took
their natural height first and **the trackpad absorbed whatever was left**, meaning every card M4c
added came straight out of the pad. Now: scroll host on the root `Column`, and the pad swapped from
`weight(1f)` to **`heightIn(min = 280.dp)`** (`weight` is illegal in a scrolling Column — a swap, not
an addition). **280 dp reasoned, not guessed:** a gesture surface needs drag room, so the 48 dp
touch-target minimum is the wrong yardstick; below ~240 dp the drag distance starts costing cursor
range. **Declared its own check-weakening honestly** — the "no `.weight(1f)`" assertion pins the
leading dot, because its first version matched its own explanatory comments.

### Part C — the publish is shippable
0 errors / 2 warnings; **859 files**, `SCRCPY/Linc.scrcpy/bin` = **104** with adb/scrcpy/scrcpy-server,
`Linc.Desktop.pri` (1,399,152 B) and `System.Management.dll` present; `packagesim` **11/11** and
`cleanroomsim` green **against that publish**; newest artifact 17:00:42 postdates the newest source
(16:57:27). **C4 went hunting rather than asserting:** scanned the published DLL's metadata strings
and confirmed `PcMediaRouting`, `DeviceAdmission`, `TransportRank` all reach it; confirmed the XAML
lives inside the `.pri`; and found `publish/Assets/companion.apk` **byte-identical to the just-built
debug APK (25,864,941 B, both 16:54:06)** — i.e. the shipped bundle carries M16's Tools page.
**140 Android tests** (138 + 2). `settings.json` SHA256 unchanged.

**Unobserved:** no phone, no live SMTC player — §7 proves the *rule*, not the `TryPlayAsync` round
trip; the Compose layout is source-text only. **`desktopsim`/`blescan` were not run and it said so
rather than implying coverage.** 8-step manual test at the end of the report.

---

# ✅ M15c DONE (Claude Code 2, 2026-08-26) — ACCEPTED. Report: `Vibe/agent/reports/M15c.md` (139 lines, the new cap held)

**Master-verified:** `MissesToDeclareGone = 2` and the `UsbDeviceGone` event exist; `OnUsbDeviceGone`
(`ConnectionSupervisor.cs:377-404`) drops the link **only** when `State == Connected`, transport is
`AdbUsb` **and** the serial matches (both `Serial` and `SerialNo` compared); `HealthInterval` is
untouched at 15 s; `ShowUsbDeviceDetected` no longer requires `IsDisconnected`; both new pure statics
exist. Constants still 18. `settings.json` SHA256 identical.

### 🔴 THE REAL CAUSE OF THE OWNER'S BLOCK-1 CONFUSION WAS STRUCTURAL, NOT WORDING
`DeviceViewModel.ShowUsbDeviceDetected` required `IsDisconnected`, **and** `DevicePage.xaml` declared
the card *inside the disconnected panel* — so in the exact case the owner hit (a phone already
connected) the card was **impossible to render**. It is now row 0, top-level, above both panels.
- Body reuses `DecideDeviceSwitch(...).Message` — no second decision rule.
- **Cancel needed a real mechanism, not just a button:** the 3 s poll re-raised the card every 3 s, so
  `DeclineUsbDevice(serial)` was added; the decline clears when the watcher reports the serial gone,
  so a replug asks again. Without that, Cancel would have meant nothing.
- The standing line (`OneAtATimeNotice`) appears in three places — Devices `+`, Settings › Devices,
  and the onboarding Detect+Pair step — all three calling one static, bound via a **bool**, never a
  string→Visibility (the M2b crash rule honoured).

### 🔴 B1 MEASURED ON HARDWARE — AND IT CONTRADICTS A COMMENT M13f LEFT IN THE CODE
With the owner pulling the cable, a **USB row vanishes from `adb devices` in 329 ms** — no stale
entry, no `offline` reading. Reproduced twice. **`ConnectionSupervisor.cs:360-363` asserts the
opposite** ("adb's `device` claim outlives the cable"). The agent did **not** change that comment or
the ranking it guards (out of scope) and flagged it instead. **It may still be true for a *wireless*
`host:port` peer — which is what M13f was actually fighting — and that case is untested.** Whoever
touches M13f's ranking next must settle it; do not let the comment stand as folklore.
- **Guard chosen with a reason:** two consecutive absences, not "one `offline` plus confirm", because
  B1 proves a real pull produces no `offline` row at all — an offline-based rule would almost never
  fire. A poll that *throws* counts no miss, so a dead adb server cannot kill a healthy link.
- **New detection: ~3 s best, ~6 s worst, vs the owner's measured 19 s.** Logged at `Warn` in M15a's
  shape, so the next real pull is measurable from the log alone. The health loop is untouched and
  still covers every other transport.

### Verification standing
**Verified myself:** the guard chain, the miss counter, `HealthInterval`, the card's gating, the two
statics, the constants. **On trust:** the harness exit codes, the two negative proofs' output, and the
329 ms measurement (owner-driven, and the only hardware number anyone has taken here).
**Unobserved:** no UI was rendered; the switch itself needs a second phone; **the new drop path has
never fired on hardware** — the 3-6 s figure is derived from the measured poll interval.
- Task-file error it caught: §0.2's Android baseline of 117 is stale — the tree runs **138** after
  M4c's tests. Android was not touched this session.

---

# ✅ M4c DONE (Claude Code 2, 2026-08-25) — ACCEPTED. Report: `Vibe/agent/reports/M4c.md`

**Master-verified:** `PresentationKeys.kt` and `ToolsScreen.kt` written 23:08/23:10 tonight; both
protocol constants still **18**; `syncsim`'s fixed `Task.Delay(120)` is gone, replaced by a 30 s
ceiling / 100 ms poll (`SettleCeiling`, `PollMs` at `:231,234`); both stale doc comments are corrected
(zero "Phone disconnected" matches remain in the view models); volume exists in exactly **one** place
on the Tools page.

- **Part A was mostly NOT built, correctly.** A1 found the phone half of `pc.media.control` already
  complete and in use by Home's widget (`OutgoingActions.kt`, `PcMediaStore.kt`, `SocketServer.kt:496`,
  `HomeScreen.kt:343/351`). It added **no** send object, message type, parser, store or fetch — the
  Tools card just collects the existing `StateFlow`. **This is the M9d-1 trap avoided.**
- **Disclosed deviation, correct:** §A2 asked for volume in the new Media block, but M4b's
  `QuickControlsCard` already had it **on the same page** — following the task literally would have
  shipped two identical volume rows. It **moved** rather than duplicated, and noted no harness
  referenced `ToolsScreen.kt` before this session so nothing depended on the old location.
- **Part 0's loud failure earned its keep.** Its first quiet-condition included "no new log line",
  which failed all five runs identically: an unsyncable `bad.txt` makes the engine log a fresh Warn
  **every pass, forever** (~10,300 in 30 s). Not a flake and not a bug — a quiet-log condition is
  unsatisfiable there. Condition narrowed to the observable world; log count kept as diagnostics.
  **Five consecutive runs: 0,0,0,0,0, with no numbers tuned to get there.**
- **Part B judgement call, right:** §B's "Right/Down/Space for next" would advance **three** slides per
  tap. It sends Right only, keeps the alternates as named constants, and asserts they are not sent.
  Reused `KeyboardMapping`'s two existing constants rather than forking a second mapping (M4a lesson).
- **21 new Android tests (117 → 138).** `ToolsScreenSourceTest` exists because acceptance 6(b)
  presumed a check that did not exist — it **named its own weakness**: a text match proves the gate is
  written, not that Compose honours it.
- **A desktop bug it found and did NOT fix (out of scope, correctly):** `PcMediaService` routes to SMTC
  only when `IsPlaying(session)`, so a **paused** SMTC player is unreachable — Play cannot resume it
  from the phone. Pre-existing, identical on Home's widget. **Fold into a future desktop milestone.**
- `settings.json` **SHA256 identical** before/after (the hash rule, adopted from M15a, worked).
- **Task-file errors it caught:** §0.1's "~14 modified files" (really 21+1); §A2's volume collision;
  §B's three-keys-per-tap; "reuse `KeyboardMapping`" (it carries 2 of 6 keys); §5.6(b) presuming a
  check existed; §0.2's warning count needing `-t:Rebuild` to observe.
- **Unverified — no phone attached:** nothing was hardware-tested, the APK was not installed, and the
  Tools `Column` is **not scrollable** — four cards now share `weight(1f)`, so the trackpad area may be
  noticeably smaller. That is step 17 of its 20-step manual script.

---

# ✅ M15a + M15b DONE (Claude Code 2, 2026-08-24) — ACCEPTED. Reports: `Vibe/agent/reports/M15a.md` (716 lines) + `M15b.md` (559 lines)

One session, six parts, both reports written to disk before the next file was opened — the chain
worked. **Master-verified by timestamp:** every touched file is dated 2026-08-24 and the sequence
matches the report's narrative exactly (`AdbServerHost` 21:07 → `UsbWatcherService` 21:09 →
`ConnectionSupervisor` 21:31 → `DeviceAdmission.cs` 21:40 → then M15b: `HomeCacheFormat` 22:14 →
`HomeViewModel` 22:17 → `WallpaperProvider.kt` 22:28 → `ThemeSyncService` 22:29 →
`HomePage.xaml.cs` 22:43). Provenance is self-evident and nothing predates the session.

### 🔴 PART A's HYPOTHESIS WAS WRONG — SIXTH TIME AN AGENT HAS DISPROVEN A CONFIDENT PLANNER CLAIM
My task file said the device-add path issues adb commands without `-s`. **It enumerated all 31 adb
call sites and every device-scoped one already carries a selector**; the six untargeted calls are
`adb devices` / `start-server` / `restart-server`. The owner's `more than one device/emulator` came
from **their own terminal**, not from Linc — Linc's own `tcpip` is `HotspotPromotion.cs:54` and has
carried `-s <endpoint>` since M13e. **A2's code change was correctly not made.**
**The real cause, found by reading the enclosing blocks, is two silent filters — and it explains the
symptom exactly. Master-verified in the tree, both:**
1. `UsbWatcherService`'s poll discarded every non-`Online` device, so a phone sitting at the trust
   prompt (`unauthorized`) was dropped without a word. `ConnectionManager.EnsureOnline`'s good
   message has existed since M2b but is only reached on a **connect attempt**, and no connect is
   ever attempted for a device the watcher never reports. **Invisible end to end.**
2. `ConnectionSupervisor.OnUsbDeviceSeen` returned in silence for any unknown phone whenever a link
   was live (`if (State is not (Searching or NoDevice)) return;`).
**Its own caveat, and it is the right one:** the *"Allow USB debugging?"* dialog is drawn by adbd on
the phone; Linc cannot make it appear. What Linc controls is whether the resulting `unauthorized`
state is ever shown — and that is what was broken.

### The rest of M15a
- **A3 — master-verified:** the watcher now raises `UsbDeviceUnusable` with a plain-language sentence
  from the new pure `Services/DeviceAdmission.cs`; the supervisor de-duplicates per serial and clears
  the entry when the phone becomes usable. **Recovery is the existing 3 s poll — no new timer, no
  fixed sleep.** `hotspotsim` asserts the word "unauthorized" never reaches the UI.
- **A4 — measured, and it split.** `Get-Process adb` found **one** server, `C:\Program Files (x86)\
  scrcpy-win64-v3.3.4\adb.exe`, outside Linc's bundle — so my *rule* fired — but **all three adb
  binaries are the same version (37.0.0-14910828)**, so my stated *rationale* (two versions fighting)
  is false. It followed the rule, made the change, and flagged the dead rationale. **It also measured
  the thing that made the fix viable rather than assuming it:** a reflection probe proved
  `AdvancedSharpAdbClient` reads `ANDROID_ADB_SERVER_PORT` dynamically, so one process-wide set really
  does cover `AdbClient`, every spawned `adb.exe` and scrcpy. Had it not, the env-var fix would have
  split Linc across two servers and been **worse than doing nothing**.
  **Master-verified:** `AdbServerHost.PrivateServerPort = 5038`, and `App.xaml.cs:42` calls
  `UseIsolatedServerPort()` at line 42 with `new ServiceCollection()` at 43 — the ordering is
  load-bearing (readonly `AdbClient` fields capture their endpoint at construction) and `hotspotsim`
  asserts it. **🔴 OWNER CONSEQUENCE: Linc now runs its own ADB server on 5038, so a wireless device
  already connected on 5037 is not inherited — the first run may need one reconnect.**
- **A5:** `DeviceAdmission.DecideDeviceSwitch` (pure static, the exact signature specified) — a second
  phone now produces a message **and** the confirmation card instead of a silent `return`. D-037
  stands; no concurrent links. Disclosed deviation: it introduced a small `LinkActivity` enum rather
  than passing `LinkState`, because `LinkState` lives in a file that drags `AdvancedSharpAdbClient`
  into a net8.0 harness. Correct call, correctly flagged.

### 🔴 PART B — THE MEASUREMENT KILLED BOTH HYPOTHESES, INCLUDING MINE, AND FOUND THE REAL ONE
- **Stage 2 (detection) is bounded: 6 s best, 21 s worst** — one 15 s poll plus two 3 s confirms.
  Not the dominant term.
- **Stage 4 was UNBOUNDED and nothing scheduled it.** After `OnLinkDropped()` the supervisor sits in
  `Searching`. The only things that start a wireless reconnect are an mDNS advert, a BLE sighting, a
  `NetworkAddressChanged`, a power resume, or a preference change — **and pulling a USB cable raises
  none of them.** `RecoverPresentWirelessDeviceAsync()`, the one path that adopts a phone the ADB
  server already holds, was wired to resume/network/preference but **not to a link drop**. The link
  stayed down until the phone happened to re-advertise.
- **This is why M13f looked right and the symptom survived: after a USB drop there is no wireless
  candidate offered at all, so ranking never gets a turn. Ranking was never the bottleneck.**
- **Fix (master-verified at `ConnectionSupervisor.cs:731-732`):** `OnLinkDropped()` now ends with
  `discovery.ScanNow(); _ = RecoverPresentWirelessDeviceAsync();` — the same bounded, single-flighted
  pair the other three triggers already use. No timer, no constant changed.
- **It refused to touch the 15 s interval** and said why: stage 1 (adb's own stale entry) has never
  been measured, and without that number there is no way to know a faster poll would help. Right call.
- **Stage timings are now logged in production**, so the next real cable pull produces the numbers
  from the owner's own log with no instrumentation run.
- **The owner's binaries were NOT stale** — the DLL post-dates HEAD's commit by ~6 h. That hypothesis
  is closed.

### M15b — the surface
- **D1 found FOUR statements stacking on Home, not one**, and the enumeration is the deliverable:
  `ModelName`'s "No phone connected", `ConnectionCaption`, the offline banner's "Phone disconnected —"
  prefix, and `AppsEmptyText`'s "Connect your phone…". **21 strings listed with file:line, render
  condition, and kept/changed.** Home now renders one. **The hardest-stacking string was produced in
  `Services/HomeCacheFormat.cs` — outside the two folders my task file told it to search, which is
  very likely why three previous attempts missed it. My scoping was the bug.**
- **D3 master-verified:** `ContentOpacity => 1.0` at `HomeViewModel.cs:301`, untouched (D-032).
- **D4:** a new `homelayoutsim` check reads the **rendered literals only** (Text/Content/
  PlaceholderText/ToolTip), because its first draft fired on binding identifiers — it found that in
  its own check and fixed the check, not the symptom.
- **🔴 PART E — MY CORRECTION WAS RIGHT AND THE REALITY IS SHARPER: THERE IS NO BLUR TO REMOVE.**
  `WallpaperProvider.kt` scaled to **48 px wide** and the file's own comment said *"downsampling **is**
  the blur"*. Now **1600 px on the longest edge, JPEG q85** (master-verified in the Kotlin, with a
  never-upscale `coerceAtMost(1.0)`). **Measured: 17 KB → 96 KB for 226× the pixels; PNG at the new
  size would have been 2 MB** — the format change is what makes the resolution change affordable, and
  it said so with the rejected row in the table. **Honest limit it volunteered:** the source was
  synthetic, so a real photo will land higher, plausibly 150–400 KB.
- **E3 mixed-version claim spot-checked by the master:** the desktop decodes via
  `BitmapImage.SetSourceAsync` (`ThemeSyncService.cs:172-173`) and `BitmapDecoder.CreateAsync` — both
  format-sniffing, so an old phone's 48 px PNG still renders. It scoped its own claim honestly ("I did
  not build an older desktop").
- **E4 — the scrim dial: `ScrimStrength` in `Services\ThemeSyncService.cs:302`, default `0.0`.**
  **Master-verified it is real, not decorative:** `:326` computes
  `alpha = Clamp(fullAlpha * ScrimStrength, 0, 255)`. **0.35 = light veil, 0.6 = strong, 1.0 = the
  pre-M15b look.** One number if text becomes hard to read.
- **Part F master-verified at `HomePage.xaml.cs:159-168`:** `!ShowApps` zeroes **both** `AppsRow` and
  `AppsSplitterRow` and sets `TabsRow` to `1*`, checked **before** the echo filter so a collapse can
  never be skipped as "already applied".
- **Four harness assertions flipped, all disclosed, none weakened** — `homecachesim` and `synccachesim`
  pin the new exact banner string, `applaunchsim` pins the new exact sentence, and `homelayoutsim`'s
  own new check was tightened. **Two of the four it found by running the sweep, not by predicting
  them** — which is the honest way to discover a contract flip.

### Verification standing — what I checked myself vs took on trust
**Master-verified in the tree:** both protocol constants still 18; the port call and its ordering;
the watcher's non-Online branch; `DecideDeviceSwitch`'s call site; the Part B recovery pair; the
banner text; `ContentOpacity`; `ScrimStrength` and its arithmetic; the Kotlin bound and format; Part
F's row collapse; every touched file's mtime.
**Taken on trust (report only):** the four negative proofs' failing output, the 18 green harness exit
codes, the build/test counts, the byte measurements, and `settings.json` unchanged (2588 bytes, same
mtime before and after). `git status` could not be run — the device shell times out on this repo — so
provenance rests on mtimes, which is the project's own preferred evidence anyway.

### Two things owed
1. **🔴 NOTHING IN M15 HAS MET A PHONE.** No device was attached all session. The second-phone flow,
   the cable-pull timing, the sharp wallpaper, scrim legibility at `0.0`, and the Apps-off reflow are
   **all unobserved.** Both reports end with numbered manual scripts; the one number to bring back is
   the log line *"Connection dropped … Detection took NNNN ms …"*.
2. **Cosmetic drift, fold into the next desktop task — master-found, not reported:** two XML doc
   comments still describe the old banner text — `ViewModels\HomeViewModel.cs:319` and
   `ViewModels\SyncViewModel.cs:173` both say *"Phone disconnected — showing what was last synced…"*.
   Comments only; no rendered string says it.

### Task-file errors it caught (all correct)
1. **§0.0's mtimes were labelled UTC but are local (+02:00)** — real values 06:02:31 / 06:01:39.
2. Part A's core hypothesis (above).
3. **A4's rule and rationale disagreed** — if the version fight was meant to be the *reason*, the rule
   should have said "of a different version".
4. A4 assumed one place was enough without saying why it would be.
5. Part B's two candidate hypotheses were both wrong as the dominant term.
6. **"Size and mtime … account for any change" cannot detect a one-byte edit — ask for a hash.**
   **Adopt this in every future acceptance list.**
7. **`packagesim`'s M12i staleness guard fires after ANY `DESKTOP/` source edit** until the Release
   profile is re-published. It re-published rather than touching the check, and says so. **Name this
   in future task files so it is not read as a regression.**

---

## ⏭ NEXT — both of these are live right now (2026-08-24)
1. **`Vibe/master/HANDTEST-M15.md`** — the owner's hand-test, five blocks, ~15 min. **Nothing in M15
   has met a phone.** The one deliverable is the log line *"Connection dropped … Detection took NNNN ms
   …"*, which is Part B stage 2 measured on hardware for the first time.
2. **`Vibe/agent/tasks/M4c.md`** — written and paste-ready for Claude Code 2. Part 0 `syncsim` poll fix,
   Part A media transport + volume on the phone Tools page (reusing `pc.media.control`, which already
   exists desktop-side — the phone half is flagged unverified and must be read first), Part B the
   slideshow clicker over `pc.input`, Part C two stale doc comments. **No protocol bump.**
   **They can run in parallel** — M4c touches neither the connection path nor Home. **A defect the
   hand-test finds still outranks M4c.**

---

# 📍 POSITION AS OF 2026-08-24 — READ THIS FIRST; IT SUPERSEDES EVERYTHING BELOW

**The whole Convergence series M1–M14 is done. Protocol is v18 on both sides.**

### 🔴 THE COMMITS ARE NOT ON `main` — master-verified 2026-08-24
Read straight out of `Linc/.git`: **HEAD is `d64a1b7`** *"M14: black-and-white default theme, new Linc
mark on both apps, Phone section"* (2026-08-12 11:34 local) on branch **`m13-hotspot-links`**, while
**`main` is three commits behind at `e23fbc3`** (M4a/M4b/M12i). So `536ac28`, `01d7504` and `d64a1b7` —
**all of M13 and M14** — live on a feature branch that has never been merged. Nothing is lost, but the
recovery point everyone believes exists is on a branch. `ROADMAP.md` and `CHANGELOG.md` say "committed"
without saying where. **This is the owner's call to merge; raise it once.**
*(Verified by reading `.git/HEAD`, `.git/refs/heads/*` and `.git/logs/HEAD` directly — the device shell
was unavailable, so `git status` itself is NOT verified; the next agent's §0.1 reports it.)*

### _(closed 2026-08-24 — M15a/M15b landed; see the section above)_ M15 WAS HANDED OVER AND THE SESSION WAS LOST
`Vibe/agent/tasks/M15.md` was written **2026-08-12 19:30 local** and handed to the opencode slot
(DeepSeek V4) with a six-part kickoff. **There is no `Vibe/agent/reports/M15.md`**, and
**master-verified by directory listing: no file under `DESKTOP/Linc.Desktop/Services/`, `\Views\` or
`\ViewModels\` has a last-write time later than 2026-08-12 08:02 UTC** (`HomeViewModel.cs` 08:02:31,
`HomePage.xaml` 08:01:39 — both M14). Twelve days passed with no work on disk.

### AGENT SWITCH, 2026-08-24: back to **Claude Code 2**
Owner's call. Rules: `Vibe/agent/AGENTS.md` + `claude-code-2/GUIDE.md` + `GUARDRAILS.md`; live docs
unchanged (`Vibe/agent/opencode-docs/`, D-056). One ledger line, not a doc merge.

### M15 IS RE-ISSUED AND SPLIT — `M15a.md` → `M15b.md`, ONE PASTE
`M15.md` stays on disk untouched (immutable once handed over). Two new files supersede it, chained
M13-style: **each writes its own report before the next begins**, so a session that dies in M15b does not
lose M15a.
- **M15a — the connection must stop lying.** Part A: a second phone cannot be added (hypothesis: adb
  calls issued without `-s`, so with one phone attached every one returns *"more than one
  device/emulator"*; plus `unauthorized` being invisible, plus a possible second adb server on 5037).
  Part B: **measure** the USB-disconnect delay, do not patch on top of M13f. Part C: a manual wireless
  override.
- **M15b — the surface.** Part D: enumerate **every** disconnected/error string in `Views/` and reduce to
  one. Part E: unblur the wallpaper. Part F: Apps-off must collapse its row and give the space to
  Notifications.

**🔴 PLANNER CORRECTION CARRIED INTO M15b — `M15.md` §5 was wrong.** It said unblurring needs "no new
plumbing, no new source — the same image, sharp." **Master-read:** `ThemeSyncService.cs:22-23` and
`HomeViewModel.cs:1624` both document the image as the phone's **pre-blurred thumbnail**, fetched via
`FetchBulkAsync("wallpaper", id, …)` at `ThemeSyncService.cs:158`. **The blur and the size are produced
phone-side** (`WallpaperProvider.kt` — inferred; the master could not open that file). It is a two-sided
change, without a protocol bump. Marked in `M15b.md` as a hypothesis to verify before implementing.

### Also true as of this handover
- **M15's scope is no longer the "dynamic island."** `ROADMAP.md` still names M15 as the live status
  surface; the owner's bug reports displaced it on 2026-08-12. The island remains a later milestone —
  M14 deliberately left the Phone card as one composable seam for it (`M14.md` §C4).
- **Still owed:** the owner's M14 visual checks (tray icon at 16 px light **and** dark, mirror window
  icon, Android launcher, Home in both states, the 3-dots flyout); **M4c** multimedia + slideshow
  (no protocol bump, carries the `syncsim` poll fix as Part 0); **camera → PC webcam** as its own
  milestone; the **`Linc/Documentation/` rewrite** (banner-flagged, 13 milestones stale).
- **M13's hardware verdict stands:** phone-hosts / PC-joins works in **514 ms** with no discovery;
  PC-hosts works but is not snappy; *Wireless debugging* is greyed out while the phone hosts an AP, so
  self-arming is dead in that role and `adb tcpip 5555` over the cable is the shipping answer. **Do not
  spend another session trying to beat this.**
- **The per-milestone sections below stop at M4b.** M13a–f and M14 were never written into this file;
  their record is `CHANGELOG.md` (reconciled 2026-08-12), `ROADMAP.md`'s POSITION block, and the reports
  at `Vibe/agent/reports\M13a…M14.md`.

---

## ✅ M4b DONE (Claude Code 2, 2026-08-10) — **protocol v17 both sides, atomically.** Report: `Vibe/agent/reports/M4b.md` (500 lines)
**Master-verified:** `Protocol\Envelope.cs:12` = **17** and `Protocol.kt:12` = **17** — both moved in one
session, M6c's pattern, so there is no M5c-style split-brain window. `PcControlService.cs`,
`PcControlValidator.cs` and `tools/pccontrolsim` all exist. Android tests **79 → 97**.
- **Part 0 confirmed my hypothesis WITH NUMBERS rather than agreeing with it:** `cleanroomsim`'s log
  wait measured **8.9 s on one run and 0.8 s on another**. The fixed 5 s sleep was a genuine
  false-failure generator; the redirect was never broken. Now polls at 250 ms to a 30 s ceiling.
- **🏆 THE BEST VERIFICATION TECHNIQUE ANYONE HAS USED HERE.** Forbidden from executing any power
  action, it still proved the runtime chains work: a throwaway console **outside the repo** compiled the
  real `PcControlService.cs` verbatim and called only its **read-only** private statics by reflection —
  real production code, not a model. Live output: `volume 24, muted false, brightness 100,
  canBrightness true, canSleep false, canShutdown true`. Deleted afterwards, `git status` identical.
  **Now in BRAIN as a reusable shape.**
- **Genuine platform finding it was not asked for:** `IsPwrSuspendAllowed()` returns **false** on this
  machine although Sleep works from its own Start menu — the export reports on **legacy ACPI S3** only,
  not **Modern Standby (S0 low-power idle)**. It judged the full `GetPwrCapabilities` fix out of scope
  and **flagged it rather than shipping a wrong assumption**. → **M12i Part B.**
- **Disclosed deviation:** added a phone-side `MessageType.ERROR` handler gated ≥ 17, which the task
  never specified, so a rejected `pc.control` surfaces instead of vanishing. Correct, and correctly
  disclosed.
- **Self-reported §0.1 miss:** it edited `cleanroomsim` before recording that file's pre-edit mtime,
  mitigated by git showing zero uncommitted changes beforehand. Honest; the rule is working as intended.
- **Planner slip:** my task file called the desktop constant `ProtocolConstants.Version`; it actually
  lives in `Protocol/Envelope.cs`. Harmless — it found it.

## ✅ M12i DONE (Claude Code 2, 2026-08-10) — the stale-publish hole is shut. Report: `Vibe/agent/reports/M12i.md`
**Master-verified independently:** `publish/Linc.Desktop.exe` is now **2026-08-10 05:11:50**, which
postdates the newest desktop source (`Services/PcPowerCapabilities.cs`, 05:10:39) by ~71 s — the guard's
own condition holds. **`System.Management.dll` is present**, `Linc.Desktop.pri` is present, and the
bundle is still **104 files**. `packagesim` is now **10/10**, check 10 being the staleness guard, and it
**skips rather than fails when there is no publish at all** — the right distinction, and the one
`packagesim` already made for checks 8-9.
- **`canSleep` fixed properly:** new `Services/PcPowerCapabilities.cs` with a pure static
  `CanSleep(s3Allowed, s0LowPower) => s3Allowed || s0LowPower` fed from `GetPwrCapabilities`;
  `IsPwrSuspendAllowed()` deleted along with its only caller. It reused M4b's
  throwaway-probe-outside-the-repo technique to get a real runtime value without executing anything.
- **Honest trade it named itself:** the new OR reports on *hardware* capability, so a machine whose S3
  exists but is **policy-blocked** could now report `canSleep: true` where the old call said false.
  Acceptable — the v17 reply contract already returns `denied` for exactly that, so it degrades to an
  honest error instead of a silently dim button. Fewer false negatives, one possible false positive.
- **Self-reported process gap:** it did not snapshot the real `settings.json` at session start. Its
  mtime (2026-08-09 22:37:42) predates all session activity, which is good evidence but is inference,
  not a before/after diff. Said so rather than implying it had checked.
- **⚠️ CORRECTION TO ITS ONE INFERENCE — the `syncsim` flake is probably NOT pre-existing.** It failed 3
  checks then passed clean on an immediate rerun, and the agent called it "a pre-existing timing flake,
  untouched code." True that *this* session did not touch it — but **master-verified,
  `tools\syncsim\{Program,Fakes}.cs` are both dated 2026-08-07 20:27:27, which is M12g's rewrite**
  (temp-root injection + deleting the backup/restore dance). The flake postdates that by three days.
- **🟡 PATTERN WORTH NAMING: the harnesses that gained temp-root or process-launch behaviour in
  M12f/M12g are the ones now flaking.** `cleanroomsim` went deterministic-fail on a fixed 5 s sleep
  (fixed in M4b Part 0 by polling); `syncsim` now flakes after its M12g root injection. In-process
  harnesses never did this. **Give `syncsim` the same poll-don't-sleep treatment → M4c Part 0.**

## _(closed)_ M12i's original finding — THE PUBLISH OUTPUT WAS STALE AND `packagesim` COULD NOT SEE IT (master-found, 2026-08-10)
**M4b added a NuGet dependency (`System.Management` 8.0.0) and `packagesim` passed green anyway.**
**Master-verified:** `publish/Linc.Desktop.exe` is dated **2026-08-07 21:09:52** — older than all of M4a
and M4b — and **`System.Management.dll` is not in the publish folder at all.** The harness inspects the
right artefact (M12c fixed that) but **nothing forces that artefact to be current**, so it can pass
forever against a stale folder while the shipping app gains dependencies.
**If the owner copied `publish/` to a second PC today they would get an August 7 build** — no Tools
page, no quick controls, missing DLL. **Planner error: M4b's acceptance list did not require a
re-publish; M12f and M12g did, and I dropped it when the milestone stopped being about packaging.**
→ `M12i.md`: re-publish, prove `System.Management.dll` lands, add a **staleness guard** to `packagesim`
that fails when the publish is older than the newest `DESKTOP/Linc.Desktop/**` source, + the `canSleep`
Modern-Standby fix.

## ✅ M4a DONE (Claude Code 2, 2026-08-09) — phone Tools page: remote keyboard + trackpad, wire still v16
Report: `Vibe/agent/reports/M4a.md` (356 lines). Build + tests **79/79**, `DESKTOP/` untouched,
`Protocol.kt` still 16, negative proof broke `KeyboardMapping.kt` and failed correctly.
- **§1's hypothesis confirmed by the agent reading the code:** `MirrorControl` calls
  `CompanionOutbox.trySend(envelope, requiredVersion = 14)` directly and **nothing references a
  streaming flag, `PC_MIRROR_START` or channel 5** — so `pc.input` genuinely works with no mirror
  session. No fix was needed. The whole task's no-protocol premise holds.
- **Good structural outcome:** `KeyboardMapping` was lifted out of `MirrorActivity.wireImeSink()`
  verbatim into a shared object, and `wireImeSink()` is now a one-line delegate — so the mirror and
  Tools provably share one mapping instead of two drifting copies.
- **Honest correction to the task file:** `MirrorActivity`'s header claims "backspace/enter/arrows",
  but the code only ever handled left/right, never up/down. Pre-existing wording drift; it carried the
  behaviour forward unchanged rather than silently "fixing" it, which was right.
- **⚠️ PROVENANCE — THE WORK WAS SPLIT ACROSS TWO SESSIONS AGAIN, AND THE REPORT GETS ONE DETAIL WRONG.**
  **Master-verified by timestamp, which is the only thing that has ever settled these:** commit
  `76c754b` landed **Aug 9 12:49:15** with a clean tree; `KeyboardSink.kt` 14:05:18,
  `TrackpadGestures.kt` 14:05:29, **`MirrorControl.kt` 14:05:33**, `ToolsScreen.kt` 14:07:45,
  `ToolsActivity.kt` 14:07:51 — i.e. **an earlier session wrote essentially all of M4a at ~14:05-14:07
  and died before reporting.** The reporting session ran ~23:20-23:45 (`KeyboardMapping.kt` 23:45:37 =
  its negative proof restore).
  **It was honest about the big thing** — it stated plainly that the tree already carried the
  implementation and that it read every file before trusting it. **But its claim that it added
  `MirrorControl.click()` itself is wrong:** that file was last written at 14:05:33, nine hours before
  it started. **Root cause is structural, not dishonesty: `git diff` measures against HEAD, and the
  earlier session's work was also uncommitted, so everything showed up as "this session's diff."**
  Same family as M9d-1. **Fix adopted in ORCHESTRATOR: §0.1 must now require recording file
  MTIMES at session start, not just contents.**
- **🔴 `cleanroomsim` NO LONGER FLAKY — IT FAILS DETERMINISTICALLY.** Both the run and the required
  rerun failed on *"No log file appeared under the redirected root — the redirect did not take
  effect."* The agent correctly refused to round it up to "known flake" or to chase it out of scope.
  **Master-checked and DISPROVED my own first hypothesis:** the publish is *not* stale — the published
  exe (Aug 7 **21:09:52**) postdates `App.xaml.cs` (21:05:57) and `LogService.cs` (21:08:21), so it does
  contain `--data-root`. **Most likely remaining cause: the fixed 5 s sleep is simply too short on a
  loaded machine** (M4a's session was running Gradle builds concurrently) — which is the poll-don't-sleep
  fix already flagged. **Not yet proven. → M4b Part 0.**
- **OWNER HAND-TEST OWED, and this is the one they will actually feel:** open Tools on the phone with
  the PC connected and drive the cursor with no mirror window open.

## ✅ M12h DONE (Claude Code 2, 2026-08-08) — **THERE WAS NO DEFECT. The feature works end to end.**
Full report on disk at `Vibe/agent/reports/M12h.md` (the new file-based report rule, adopted this
session after three truncated pastes — **it worked first time**).
- **None of branches A/B/C reproduced.** Proven three ways against the real running exe, driven by
  **UI-Automation `TogglePattern`, no mouse movement** (the D-031 rule honoured): the setter runs, the
  `Enable()` call succeeds, the registry value appears **quoted with the `--startup` suffix**, toggling
  OFF removes it, and — the check that matters — **the setting survives a full process restart** with
  both the toggle state and the key intact.
- **MY HYPOTHESIS WAS WRONG — FIFTH OCCURRENCE OF THIS PLANNER FAILURE.** `M12h.md` §2 told it to suspect
  that `StartWithWindows` was appended to the positional `Settings` record but omitted from `Persist()`.
  **Master-verified by hand after it contradicted me: the record has 15 parameters
  (`DeviceRegistry.cs:186-206`), `Persist()` passes 15 positional arguments (`:559-570`) with
  `StartWithWindows` 15th in both, the load path reads it at `:334` with `?? false`, and
  `SaveStartWithWindows` (`:518-522`) sets and persists.** It checked the alignment by hand instead of
  implementing what the task file asserted. Fifth time an agent has caught a confident planner claim
  built from a partial read.
- **Most likely explanation of the owner's report: the toggle had never been flipped.** It is consistent
  with every piece of evidence — M12g found no Run value (nobody had enabled it), the owner looked at
  Startup apps and saw nothing (because nothing was registered), and the first actual toggle in M12h
  wrote the key correctly.
- **The real fix it shipped is observability:** `Enable()`/`Disable()` now log at `Info` on **success**,
  naming the value written or removed. Previously only failures logged, so a silent success and a silent
  failure were indistinguishable — which is the whole reason this cost two sessions. It removed its
  temporary diagnostic log and left `SettingsViewModel.cs` at **zero net diff**.
- **Reasoned deviation, flagged not hidden:** since Branch A did not hold, it did not add the specified
  conditional round-trip check; it gave `autostartsim` a `SpyLogService` and asserted the new success
  logging instead — testing what it actually changed. Correct call. Negative proof broke the production
  file and the check failed as it should.
- **🟡 `cleanroomsim` IS FLAKY — M12g's harness, not this session's.** It failed once on a **5 s
  log-settle timing race** in the `--data-root`/`LogService` path, then passed twice on immediate rerun.
  Correctly declared out of scope. **A flaky harness erodes the trust the whole verification regime
  depends on — fix the settle logic (poll for the log line rather than sleeping a fixed 5 s) the next
  time anything touches `tools/cleanroomsim`.**
- **Two cosmetic follow-ups, NOT worth their own session — fold into any future desktop task:**
  1. The Run value points at the **Debug build**, which is correct behaviour (the app registers whatever
     exe is running) but means the owner must toggle it from the **published** copy for it to start the
     shipped app.
  2. `Linc.Desktop.csproj` sets only `<Version>`; with no `<Product>`/`<AssemblyTitle>` the SDK defaults
     them to the assembly name, so Windows lists the entry as **"Linc.Desktop"**, not "Linc".

## ✅ M12g + M12g-amend DONE (Claude Code 2, 2026-08-07) — **D-057 IS FINALLY WHOLE. M12 IS COMPLETE.**
**Master-verified in the tree, independently of the report:** exactly **three**
`GetFolderPath(…LocalApplicationData` calls remain in production — `DeviceRegistry.cs:214`
(`DefaultRootPath`) and `ToolLocator.cs:31,56` (the Android SDK read). All four bypassing services
(`DeviceCacheService`, `LogService`, `SyncEngine`, `TlsTransportService`) now take
`IDeviceRegistry.RootPath`. **`SyncEngine.cs:89` is untouched and still resolves `MyPictures/Linc/Photos`
— the trap the amendment warned about was avoided.** `App.xaml.cs:23` parses `--data-root` once into a
`static readonly`, `:46` is the DI factory, `:148-151` logs a redirected root.
- **The guard is stronger than I specified.** `cleanroomsim`'s new GUARD section does not merely check
  for absence — it asserts `DeviceRegistry.cs` resolves `LocalApplicationData` **exactly once** and
  `ToolLocator.cs` **exactly twice**, so both adding a bypass *and* quietly adding a fourth sanctioned
  site fail loudly. It scans the directory tree, not a filename list (the M6c lesson applied).
- **Half Two RUNS NOW.** The `LOCALAPPDATA` probe is kept but demoted to informational; the launch uses
  `--data-root`.
- **It found and fixed two bugs in its own harness before going green, and said so.** (1) An injected
  root becomes `RootPath` directly — no `Linc` segment is appended, so its first draft looked in
  `<tempRoot>/Linc/`. (2) **My B2.4 acceptance was wrong**: `DeviceRegistry` only reads `settings.json`
  if it exists and nothing calls `Persist()` on a bare launch, so an unpaired fresh launch never writes
  it. It substituted a **better** proof — the A3 log line under `<tempRoot>/logs/`, which travels through
  `LogService` → `IDeviceRegistry.RootPath` and therefore proves A0's wiring as well as A's.
- **🧹 THIRD HARNESS FOOTGUN FOUND AND KILLED, UNASKED:** `syncsim` had been backing up, deleting and
  restoring the owner's real `%LOCALAPPDATA%\Linc/sync-state.json` around every run — its own header
  comment admitted it. A0 made the honest fix possible; the backup dance is deleted and the harness now
  uses its own temp root. **Master-verified in `syncsim\Program.cs:9-12,164-169` and `Fakes.cs`.**
- **Collateral it flagged rather than hid:** `LogService`'s new dependency broke three harnesses that
  compile it for its types only → new `Fakes.cs` in `autostartsim` and `mirrorsim`, real construction
  sites fixed in `blescan` and `desktopsim`. **This is the hidden-build-surface gotcha for the fourth
  time.**
- **⚠️ ONE DOCTRINE COST, WORTH RECORDING.** Negative proof 2 reverted the DI wiring and re-published, so
  the deliberately-broken build **wrote into the owner's real `logs/`** (grew 156,717 → 158,469 bytes) —
  which is exactly how the guard caught it. It is the least destructive possible real-store write (an
  append to a diagnostic log, not pairing data) and it was self-detected and reported openly, but it is
  still a negative proof running through a real-data path, which ORCHESTRATOR forbids. **Same shape as
  M7b's fake-serial cache folder. Left behind: a handful of log lines from a knowingly-broken build.**
- **`blescan` FAIL is pre-existing and NOT this session's doing — master-verified by timestamp:**
  `BlePresenceService.cs` is dated **2026-08-06 21:08**, the day before. Only its `LogService`
  construction call was touched. `desktopsim` FAIL is the documented no-phone-attached outcome.
- **A1.6 answered: there is NO `Linc` value under `HKCU\…\CurrentVersion\Run`.** So M12e's toggle was
  never hand-tested into the real key, and the `Linc.Desktop` that appeared mid-M12f was the owner
  opening the app. **The startup feature is therefore still completely unobserved.**
- **Minor unresolved discrepancy, low stakes:** M12f reported 2× `CS9113` at baseline, M12g reports 1
  (`HomeViewModel.cs:166`) from a `git stash`ed true baseline. Most likely incremental-vs-clean build
  emission. Not worth a session; noted so nobody re-derives it.

## ✅ M12e DONE (Claude Code 2, 2026-08-07) — tray icon fixed, "start when I sign in" ships. **NO REPORT EVER REACHED THE PLANNER; ACCEPTED ON MASTER INSPECTION ALONE.**
It ran between M12d and M12f (`StartupRegistration.cs` 07:22, `autostartsim/` 07:11) and `autostartsim`
appears green in M12f's regression list, but its own report was never pasted. **Everything below is the
master reading the files, not a report being believed:**
- **Tray:** `IconSource="ms-appx:///…"` is **gone** from `MainWindow.xaml`; the code-behind now sets
  `TrayIcon.Icon = new System.Drawing.Icon(AppContext.BaseDirectory\Assets/AppIcon.ico)` **inside a
  try/catch** immediately before `ForceCreate()` (lines 45-50) — the filesystem-path pattern already
  proven by `AppWindow.SetIcon` two lines below. Tooltip binding, `LeftClickCommand`, `NoLeftClickDelay`
  and the whole context flyout are untouched, as specified.
- **Startup:** `BuildRunCommand` = `"{exePath}" --startup` (**quoted — the space-in-path trap closed**,
  and it correctly emits no `--data-root`); `NeedsRewrite` is ordinal-ignore-case; `IsStartupLaunch`
  matches case-insensitively. `SettingsPage.xaml:144` binds `IsOn` **`Mode=TwoWay`** to
  `ViewModel.StartWithWindows`, whose setter calls `SaveStartWithWindows` — **not** the M5c-2 dead-UI
  shape.
- **🔴 TWO THINGS THE MASTER CANNOT CHECK AND THE OWNER MUST:** (1) whether the tray icon is *visually*
  present — no eyes on the tray; (2) whether a **real** `HKCU\…\Run\Linc` value was written during
  hand-testing, and if so whether it points at a **Debug build** path. Folded into M12g-amend §A1.6 as a
  report-only item.
- M12f's agent also noted **a `Linc.Desktop` appearing mid-session that it stopped** per the standard
  build routine — most likely the owner opening the app, but consistent with a live Run key. Same check.

## ✅ M12f DONE + FULLY QA'd (Claude Code 2, 2026-08-07) — `PRI249` is gone and the clean-room harness exists, honestly scoped.
**The headline is a planner error that would have destroyed the owner's real store, caught by the agent
probing my assumption at runtime instead of trusting it.**
- **`Environment.GetFolderPath(LocalApplicationData)` DOES NOT honour the `LOCALAPPDATA` environment
  variable** — it resolves through the Windows known-folder API. `DeviceRegistry.DefaultRootPath`
  (`DeviceRegistry.cs:213`) is built from exactly that call. **My M12f §A2 specified sandboxing the
  published child process by redirecting `LOCALAPPDATA`; had it done so, the child would have read and
  written the owner's real `%LOCALAPPDATA%\Linc\`** — `settings.json`, `linc.db`, `tls-identity.pfx`,
  `cache/`, `logs/`. **Third near-miss of this exact class** (M2a `devicesim`, M6b `homelayoutsim`).
  The agent measured the assumption, found it false, and turned Half Two into a **loud SKIP** rather than
  a silent unsafe launch — and skipped `FindAdb`'s clean-failure case for the same reason instead of
  faking it. **This is the single best safety call any agent has made on this project.**
- **Master-verified on disk:** `tools/cleanroomsim/Program.cs` exists, 366 lines, with the sanitise/
  restore in `finally`, the single-instance skip, exact-path asserts on all three `ToolLocator`
  functions, `Kill()`-not-wait, and temp-root cleanup. Half One (the deterministic lookup half) is real
  and is the half that catches the M12c class.
- **Part B took hypothesis 2:** `Linc.Desktop.csproj` now carries the scrcpy payload as `<None>` +
  `CopyToOutputDirectory` instead of `<Content>`. **Master-verified the payload survived it** — a publish
  re-run at 07:56 (after the csproj change) still yields **104 files** incl. `adb.exe`/`scrcpy.exe`/
  `scrcpy-server`, with `Linc.Desktop.pri` present at 1,398,584 bytes. That was the whole risk of Part B
  and it is clear.
- **`PRI249` IS GONE, and it traced the mechanism rather than guessing it.** It read
  `MrtCore.PriGen.targets` and found the real chain: `Content` + `CopyToOutputDirectory` →
  `ContentFilesProjectOutputGroup` → `_LayoutFileSource` → `_LayoutFile` → `GenerateProjectPriFile`'s
  `LayoutFiles`, so every Content-copied file gets scanned by `makepri.exe`. Moving the payload to
  `<None>` (same `Link`, same `PreserveNewest`) keeps the copy and skips the indexer. **It skipped my
  hypothesis 1 after reading the targets and never needed 3.** Clean-tree Debug: 0 errors, 2 warnings
  (both pre-existing `CS9113`), `PRI249` **absent**.
- **Master-verified the numbers match:** publish 104 files incl. all three binaries, `Linc.Desktop.pri`
  **1,398,584 bytes** — the exact size I measured independently before receiving the report.
- **`packagesim` check 9** (`.pri` exists and >1024 bytes) added and negative-proved: renaming the file
  failed **both** check 9 and the pre-existing check 5 → restored → green.
- **`settings.json` unchanged across the whole run** — 1542 bytes, same timestamp before and after,
  including both negative proofs. The one number that mattered.
- **`desktopsim` exit 1, honestly** — no Pixel 7 attached that run; everything else in it passed. The
  documented honest-either-way outcome, not a regression.
- **🔴 THE FINDING THAT OUTLIVES THE TASK — and it is bigger than the report claimed.** M12f named
  `LogService`, `DeviceCacheService`, `TlsTransportService` and `SyncEngine` as resolving the store root
  independently. **Master-verified in the tree, and it is exact:** `DeviceCacheService.cs:67` (`cache`),
  `LogService.cs:40` (`logs`), `SyncEngine.cs:39`, `TlsTransportService.cs:49` (`tls-identity.pfx`) all
  call `Environment.GetFolderPath` directly, bypassing `IDeviceRegistry.RootPath`. **This is precisely
  the hole H1 named on 2026-07-31 and left open** — BRAIN's H1 entry says `cache/`, `sync-state.json`,
  `tls-identity.pfx` and `logs/` "were never audited." Now audited.
  **Consequence: `M12g.md` as written would ship a sandbox that looks safe and is not** — `--data-root`
  redirects `settings.json` + `linc.db` + `AppCatalog` while the child still writes the real logs, cache
  and the phone's pinned TLS identity. → **`M12g-amend.md` inserts a Part A0** routing those four through
  the injected root first, **with an explicit warning not to touch the `MyPictures`/`UserProfile` paths**
  (`SyncEngine` appears in both lists — line 39 is our store, line 89 is the user's Pictures folder).

## ✅ M12d DONE (Claude Code 2, 2026-08-07) — the scrcpy rename is followed through; the package ships again
Every reference to the old `SCRCPY/Custom` bundle path now reads `SCRCPY/Linc.scrcpy`, across 7 files.
**Master-verified independently, not from the report:** all 9 `ToolLocator` candidates converted with the
six-level dev-tree climb intact (lines 20/25/41/47/85/86/88/89 + the comment at 43); the csproj `Content`
glob and its `<Link>` (28/29); 20 refs in `packagesim`; `.gitignore` and `Beta.pubxml`. **Zero
`SCRCPY.Custom` / `Custom/bin` matches remain anywhere outside `SCRCPY/` itself.** The publish output on
disk really does carry **104 files** in `publish/SCRCPY/Linc.scrcpy/bin/` including `adb.exe`,
`scrcpy.exe` and `scrcpy-server`, with `Linc.Desktop.pri` and the app-local VC++ runtime at the root.
- **It found two files the task file did not name** — `Services/AdbServerHost.cs` (a user-facing error
  message) and `Services/AppLaunchService.cs`. §3.1's "my counts are not a guarantee" was doing real work.
- **It correctly distinguished the false positives** and listed them: the `Custom` scrcpy quality preset
  (`MirrorViewModel`), the `Custom` desktop-mode geometry enum (`DesktopModeSettings`/`Service`/`ViewModel`
  + 6 in `desktopsim`), and a `[PSCustomObject]` in a PowerShell script. Exactly the discrimination §3.1
  asked for.
- **Both negative proofs broke production files and both failed for the right reason.** Proof 1 is the
  more interesting one: reverting the bundled-adb candidate made `FindAdb()` **silently fall through to
  the dev machine's real SDK adb** rather than erroring — which is precisely the M12c defect recurring,
  and precisely why the check asserts the exact path rather than non-null.
- **⚠️ PLANNER ERROR — MY §2 FRAMING WAS WRONG, AND THE AGENT CHECKED IT.** I wrote that the owner
  renamed the folder by hand and that git would therefore show a delete-plus-add. **Master-verified: the
  rename is already committed** — `d0ac417`'s subject literally ends "…; rename SCRCPY/Custom to
  Linc.scrcpy", and `git ls-files` carries all 104 files under the new path. Only the *code* lagged.
  `git status` shows 7 modified files and nothing else. **Fourth time a confident planner claim from a
  partial read has been contradicted by an agent that went and looked.**
- **🟡 NEW, REAL, UNRESOLVED — `PRI249: 0xdef00520 - Invalid qualifier: 0-0`.** A publish warning that did
  not exist at baseline **only because the stale glob matched zero files**. `makepri.exe` scans every
  project-wide `Content` item and reads the **`.` in `Linc.scrcpy`** as a resource-qualifier delimiter.
  **The output is correct despite it** (master-verified: 104 files + `.pri` present, and the indexer
  reported 17,790 candidates and "Successfully Completed"). The agent **refused to work around it
  unasked** and flagged the tension with §4.1's "no new warnings" — the right call. → **M12f Part B.**

## At a glance
- **Phase:** Convergence (phone and PC drive each other). Product is otherwise complete + hardware-verified.
- **🔴 ACTIVE CODING AGENT (2026-08-24): Claude Code 2** — owner's call, replacing the opencode slot after
  the M15 session was lost. Rules: `Vibe/agent/AGENTS.md` + `claude-code-2/GUIDE.md` + `GUARDRAILS.md`.
  Live docs unchanged (`Vibe/agent/opencode-docs/`). Live task: **`M15a.md` → `M15b.md`**, one paste.
- _(previous)_ **ACTIVE CODING AGENT (2026-08-11): the opencode slot, model `DeepSeek V4` on OpenCode Zen (free).**
  **Forced switch — the owner's org disabled Claude subscription access for Claude Code mid-milestone.**
  Rules: `Vibe/agent/AGENTS.md` + `OPENCODE.md` + `GUARDRAILS.md`. Live docs unchanged
  (`Vibe/agent/opencode-docs/`). **Temperament unknown; M13f is its first session and its Part A
  diagnosis is the test of whether it measures before it fixes.** Free fallbacks if it disappoints:
  other Zen free models, Groq, Gemini free tier, Ollama local — or **pure nemo** via
  `MCP/nemo_agent/nemo.cmd`, which the owner drives directly and which never depended on Claude Code.
- _(previous)_ **Active coding agent: Claude Code 2** (owner switched 2026-08-03, mid-M9c). Behavioural guide
  **`Vibe/agent/claude-code-2/GUIDE.md`** — written because the prior M9c session emitted the same plan
  three times without writing the file. Current task: **M9c** (`Vibe/agent/tasks/M9c.md`) **+ the
  amendment `M9c-amend.md`** (A1: `FlushAsync` must call `DecideDropVsAttempt` instead of
  re-implementing its comparison). Temperament notes owed after its first report.
- **Claude Code** stays available for design-risk work; guide `Vibe/agent/claude-docs/CLAUDE.md`.
  It ran M6b, M6c, M6c-2, M6c-3, H1, M7a, M7b — every one accepted, and it caught three planner errors
  (a task-file contradiction, an unmeasured BRAIN claim, a negative proof routed through real data).
- **The D-056 switch is now genuinely one line.** This is the second switch since the per-agent doc
  folders were retired and neither cost anything — previously it meant reconciling 13 files, which is
  exactly why the folders went stale in the first place.
- **ONE LIVE DOC SET FOR ALL AGENTS (D-056, 2026-07-31): `Vibe/agent/opencode-docs/`.** Read it as
  "the live docs", not "opencode's docs". The project-reality copies in `claude-docs/` and
  `gemini-docs/` are **DEAD** — `claude-docs/BRAIN.md` is 164 lines against the live 301 and predates
  M8 and D-052…D-055 entirely; handing it to an agent would actively mislead. Only the small
  behavioural guides stay per-agent. **An agent switch is now one ledger line, not a 13-file merge.**
  **Model-agnostic slot:** the owner can swap the model behind opencode at any time — the rules file
  (`OPENCODE.md`), folder and ledger stay put; only the temperament notes change. **Environment
  VERIFIED 2026-07-28:** shell, file writes, MSBuild x64, adb all work; it HAS a real display but
  `mirrorsim`'s DXGI capture fails, so display-capture checks stay owner-only.
- **New:** `nemo` worker agent joined (free NVIDIA-hosted hands, managed by Claude Code) — see `Vibe/agent/NEMO.md`.
- **Protocol: v16 SPEC WRITTEN (D-058), NEITHER SIDE BUILT — M6c builds both at once.**
  `apps.get` → `apps`: the phone's **launchable** app inventory. Icons need no new machinery (bulk kind
  `appIcon`, v5). No push event for installs — pull on connect and reconcile against a per-device
  cache. **Both version constants move to 16 in M6c**, so unlike M5c there is no split-brain window.
- _Shipped:_ **v15 — display control, both sides built (M5c-1/2/3), hardware test still owed.**
  `PROTOCOL.md` is at v15 (display control, D-055; planner wrote it before any code, per the rule).
  The phone advertises 15 but negotiates `minOf(maxV, PROTOCOL_VERSION)`, so live links still run
  **v14** until the desktop's `maxV` is raised. **Raising desktop `maxV` to 15 is what turns the
  feature on — do it deliberately in M5c-2, not incidentally.**
- **Current work:** **M8 — Desktop Mode** (promoted, D-052). Spec: `Vibe/agent/gemini-docs/DESKTOP-MODE-SPEC.md`.

## Roadmap position
Convergence milestone series (see `Vibe/agent/gemini-docs/ROADMAP.md`):
**M1 ✅ → M2 (a/b/c) ✅ → M3 ✅ → M3.5 ✅(closed, control bar deferred) → M8 ✅ → M5 ⟳ (a ✅ / b ✅ / c-1 ✅ phone / c-2 next = desktop) → M6 → M7 … → M4 last → M13 hotspot.**

- **ORDER CHANGE (2026-07-27, D-052):** **M8 Desktop Mode takes M4's slot and is worked now**;
  **M4 (phone-as-remote Tools) moves into M8's old slot.** M8 needs only M3's Custom scrcpy (done);
  M4 needs a protocol bump, so deferring it costs nothing. Independent milestones, no dependency risk.
- **M4** phone-as-remote Tools suite (phone drives PC; `pc.control`; protocol bump) — later.
- **M5** PC-controls-phone widgets + Device Tools + editable scrcpy settings.
- **M6** Home layout overhaul + Apps section · **M7** Apps windows (per-app Custom-scrcpy).
- **M8** Desktop Mode (`DESKTOP-MODE-SPEC.md`) · **M9** SQLite + notification history + offline queue.
- **M10** sharing & install-APK · **M11** audio routing · **M12** presence polish + packaging/beta.
- **M13 (NEW, owner-requested 2026-07-29)** — **hotspot links, both directions:** phone-hotspots-PC
  and PC-hotspots-phone, so two devices with no shared Wi-Fi still connect without the USB cable.
  Expected blocker: Android's *Wireless debugging* toggle usually needs the phone to be a Wi-Fi
  **client**, so a hotspot link probably has to run on **Direct TLS**; mDNS over the hotspot
  interface is the other unknown (fall back to dialling the gateway address, which is deterministic).
  **Starts with a hardware diagnostic session, not a design** — nothing about this is documented or
  tested today. Sits after M12; the diagnostic can be run any time.
- Dependencies: M7/M8/M11 + scrcpy-settings depend on M3; M7 rides M6's Apps section; notif-history/queue depend on M9.
- Deferred: Google/FCM (D-007).

## Done (Convergence phase)
- **M1** — Files page fixed (lazy-load-on-connect bug) + device-tab strip always visible once ≥1 paired. _(Claude Code)_
- **M2a** — device management works (`+` / close / switch), Settings › Devices, Files up-nav to `/`, downloads → `Downloads/Linc`. _(Claude Code)_
- **M2b** — guided onboarding wizard; desktop bundles + injects the companion APK; fixed launch crash + false-success. _(Claude Code)_
- **M2c** — onboarding rebuilt as a full-window in-app `OnboardingView` overlay (replaced the ContentDialog). _(Antigravity — first Antigravity session)_
- **M3** — scrcpy vendored (`SCRCPY/Default` submodule v3.3.4) + prebuilt binaries bundled + mirror branded via runtime flags. _(Antigravity)_
- **M3.5a** — from-source scrcpy Windows build PROVEN via MSYS2/MINGW64; recipe + DLL nuance in `SCRCPY-WINDOW-SPEC.md`. _(Antigravity)_
- **M3.5b-1** — Linc ships its OWN from-source scrcpy standalone (98 mingw64 DLLs; `-Dportable=true` fix, D-050); stray `adb.exe.bak` purged. _(Antigravity)_
- **M3.5b-2a** — frameless + rounded + draggable custom scrcpy window (`sys/win/window.{c,h}`). _(Antigravity)_
- **M3.5b-2b** — restored resize + maximize + rounder corners (radius 32); work-area clamp; D-051. _(Antigravity)_
- **M3.5b-2c** — hover control bar: **deferred to Polish (D-052)**; M3.5 closed as substantially done.

## M3.5 outcome — closed 2026-07-27 (D-052)
The window ships **frameless, rounded, draggable, resizable, maximizable** (all owner-verified). The
hover control bar never became reachable and is now **Polish backlog**. The 2026-07-27 rebuild was
confirmed genuine (`bin/scrcpy.exe` byte-identical to `src/x/app/scrcpy.exe`; `control_bar.c` in
`meson.build`; render wired at `display.c:352`; call sites match the new signatures) — the remaining
fault is **design, not coordinates**: the reveal only fires once the cursor is inside the 30-physical-px
cluster (~24 logical px at 125%), while the rest of the strip is `HTCAPTION` so SDL gets no mouse
motion there. Reaching invisible buttons needs an invisible sliver. Fix = the b-2c-3 whole-strip
reveal. No window operation is lost meanwhile (Aero-snap, Win+↑, double-click caption, Alt+F4).

## In flight
- **M8 — Desktop Mode: functionally COMPLETE, one closeout session owed (M8f).**
  - **M8a ✅** settings record + idempotent first-run ADB setup + `tools/desktopsim`. _(Antigravity)_
  - **M8b ✅** launch path (`--new-display=WxH/DPI`, uhid mouse+keyboard) + Device-page toggle. _(Antigravity)_
  - **M8c ✅** usability pass: perf caps (1600x900/200, 30fps, 8M, no-audio), mouse-capture hint,
    error-handling fix. Owner: lag fixed. _(Antigravity — its last session)_
  - **M8d ✅** settings panel (collapsible expander) + per-device recommended geometry from
    `wm size`/`wm density` + validation. _(GLM)_
  - **M8e ✅** geometry defects fixed (pixel-budget cap preserving aspect → MatchPhone 804x1788
    not 640x900; desktop-like DPI `160*h/900`) + the read-only windowing diagnostic. _(GLM)_
  - **M8f ✅ closeout:** `ApplyWindowingModeAsync` deleted outright (its two surviving keys were
    already written by first-run setup, so it was a hollow no-op); the dead UI controls removed
    ("Default window" combo, "Resizable windows", "Apps freeform"); the four inert record fields
    kept but XML-documented as no-ops per D-053; an honest "second screen" caption added under the
    Desktop Mode button. _(GLM)_ **Owner visual check owed** — the settings grid lost columns, so
    confirm the layout still reads correctly.
  - **M8 is DONE** (owner checked 2026-07-28 — accepted, one layout bug found, deferred below).
- **BUG found in the M8f check → FIXED in M5a (2026-07-29):** expanding "Desktop Mode settings"
  pushed content past the window and it could not be scrolled to, because **`Views/DevicePage.xaml`
  had NO `ScrollViewer`** — the root was a plain `<Grid Padding="32">`. Pre-existing; the expander
  merely exposed it. Now wrapped in a scroll host. **Standing lesson: every new page needs a scroll
  host from day one.**
- **Small debt (GLM flagged it itself):** `DesktopModeService.RequiredSettings` still writes
  `enable_freeform_support` + `force_resizable_activities` in the first-run path. Per D-053 those
  are inert on this device too. Harmless, but a candidate cleanup if we ever revisit M8.
- **D-053 — the windowing half of M8 is DROPPED.** Android 17 on the Pixel 7 does not declare
  `android.software.freeform_window_management`; our virtual display is `mWindowingMode=fullscreen`.
  Desktop Mode ships as a **second screen**. Real per-app windowing moves to **M7**.
- **Hard-won lesson (D-053):** `settings put global <anything> 1` always succeeds and always reads
  back, consumed or not. **Never infer device capability from a settings key round-tripping** —
  check `pm list features`.

## Polish backlog (after the feature milestones)
- **b-2c-3** — whole-strip hover reveal + fade for the mirror control bar (the deferred D-052 work).
- The **M3 frameless-look** manual UI check.

## ✅ M6c DONE (Claude Code, 2026-07-31) — Apps section shipped end to end, protocol now v16 BOTH SIDES
`apps.get` → `apps` (launchable packages only, D-058); icons reuse the v5 bulk `appIcon` kind — no new
icon path; per-device cache at `<root>/cache/<serial>/` **rooted at the injected `DeviceRegistry.RootPath`**,
so D-057's protection now extends past `settings.json`. Apps is Home section **3 of 7**.
**Master-verified:** both version constants really are 16; `AppsPayload.IsSupported` gates on `>= 16`;
`AppCatalog` carries an explicit comment that calling `Environment.GetFolderPath` there would reopen the
hole; three loop guards in place (`_appsRefreshInFlight`, `_appIconsInFlight`, `_appIconMisses` — a
package with no icon is asked once, ever).
- **Part 0 paid off immediately.** Replacing the four hardcoded harness filenames with a directory scan
  brought **13** harnesses under the D-057 guard, nine of which were never checked before. The negative
  proof then planted a bare `new DeviceRegistry()` in `mirrorsim` — **one of those nine** — and the new
  scan caught it. The old check would have walked straight past.
- **Four legitimate spec corrections from the agent**, all now in BRAIN: `Border` has no `IsEnabled`
  (cards are `Border`s, so dimming is the substitute); `homelayoutsim` hard-coded "6 sections" and broke
  when Apps made it 7; icon laziness is meaningless without a virtualizing list (M7's problem); and it
  had to widen `IDeviceRegistry` with `RootPath`, which the task didn't explicitly authorise but which
  was the only way to honour the injected-root rule from a view model. All four were correct.
- **Owner test needs the new APK installed** — see the backlog note below.
- **✅ M12 / M12b / M12c DONE — the app is packageable. SECOND-PC TEST IS THE OPEN GATE.**
  `presencesim` (catch-block census: 35 blocks, all now logged or explained), a startup crash log +
  Win32 message box so a launch failure can never again be invisible, a self-contained publish profile,
  and three real packaging defects found only by leaving the dev machine:
  1. **M12b — `Linc.Desktop.pri` was never copied into `publish/`.** The app's own compiled resource
     index; without it every `ThemeResource` lookup threw `COMException` from `HomeViewModel`'s ctor.
     That was the owner's silent death on the second PC. The `.pri` reaches the output only on the MSIX
     path, and this project is `WindowsPackageType=None`.
  2. **M12c — `ToolLocator.FindAdb()` never looked in the bundle.** It searched `adb/adb.exe` (a folder
     that is never created), `ANDROID_HOME`, and `%LOCALAPPDATA%\Android\Sdk` — **never**
     `SCRCPY/Custom/bin/adb.exe`, where Linc's own adb ships. Worked for the whole life of the project
     because the dev machine silently lent it the SDK's copy. **Bundled is now the first candidate**,
     which also removes a real correctness risk: the bundled adb is version-matched to the bundled
     `scrcpy-server`.
  3. **A latent depth bug in all three `ToolLocator` dev-tree fallbacks.** They used five `..` where six
     are needed, resolving to `DESKTOP\SCRCPY\…` — **a path that has never existed.** Latent since M3,
     invisible because the `PATH`/winget scans below it covered for it. Found because the agent
     *measured* the depth instead of copying the existing count.
  - **VC++ runtime now ships app-local** (`msvcp140`, `vcruntime140`, `vcruntime140_1`, plus `_1`/`_2`),
     copied from the VS redist at publish time, **never vendored into the repo**, and the publish
     **fails loudly** if the redist source cannot be found. The agent's `dumpbin` sweep of all 647 files
     found nothing importing them, so this is insurance rather than a proven fix — but the owner asked
     and it costs ~1.5 MB.
  - **Part C: the non-wizard install path now asks first.** `CompanionInstallGate.Decide(installed,
     wizardActive, declinedThisSession)`. **The agent refined my hypothesis rather than just confirming
     it:** the wizard's Install step is *narrational only* — the silent install had already happened by
     the time that label renders — and the Devices page's own QR/USB flows are equally silent for a
     brand-new phone. Broader than I described. Declines are remembered in memory, per process.
  - **THE RECURRING LESSON, NOW PAID FOR THREE TIMES: a harness that checks source paths proves nothing
     about what ships.** `packagesim` originally verified payloads where they came from, passed green,
     and shipped a folder missing the `.pri`. It now checks the **publish output**, calls the **real**
     `ToolLocator.FindAdb()` against a fixture laid out like a publish tree, and **skips with a message**
     rather than passing when no publish exists.
  - **🔴 OWNER GATE: copy the fresh `publish/` folder to the second PC and run it with NOTHING
     installed** — no VC++ redist, no Android SDK. Settings should show an adb path ending in
     `SCRCPY/Custom/bin/adb.exe`.
- **✅ M11 DONE (Claude Code 2, 2026-08-06) — audio is now an explicit per-device setting. OWNER TEST OWED.**
  `MirrorSettings` gains `AudioEnabled` (default **true**, preserving today's silent-default behaviour
  byte-for-byte), `AudioBitRate` (0 = omit) and `AudioSource` (`null`/`output`/`mic`, validated), wired
  through the existing pure `BuildScrcpyArgs` and the Mirror-settings expander. All four scrcpy flags
  **verified in the vendored `cli.c`** (167 / 184 / 218 / 637).
  - **⚠️ PART B'S PREMISE WAS FALSE AND THE AGENT WAS RIGHT.** `DesktopLaunchService` already read a
    pre-existing `DesktopModeSettings.ForwardAudio` (default off) — **master-verified: that file is dated
    2026-07-28 and was untouched.** My task file called it hardcoded. It kept the existing name rather
    than renaming a fully-wired field for cosmetic parity with my spec — correct call, "smallest possible
    change." Part B's real deliverables were the three things that genuinely were missing: the
    app-window guard, the plain-language notes, and the conflict warning.
  - **It caught a false pass in its OWN new check and reported it rather than burying it.** Its first
    byte-identity check used a loose "no flag starting with `--audio`" filter, which **passed** against a
    deliberately injected bug; only the pre-existing exact-`SequenceEqual` sections caught it. It
    tightened the check to exact sequence comparison and flagged the near-miss unprompted.
  - **The hidden-build-surface gotcha bit a THIRD time** — `devicesim` did not compile at all before this
    session (`DesktopModeSettings.ComputeRecommendedGeometry` takes an `ILogService` that its csproj never
    included). Fixed with one `<Compile Include>` line. **This keeps recurring; every task touching a
    `KnownDevice`-adjacent record must re-check `tools/*/*.csproj`.**
  - **Reasoned deviation, accepted:** the app-window audio guard went in `applaunchsim` rather than
    `desktopsim` because that harness already compiles both launch services — putting it in `desktopsim`
    would have duplicated a whole dependency graph, which is the very risk the gotcha warns about. The
    guard also **reflects on `BuildScrcpyArgs`'s parameter list** so a future session that makes
    app-window audio configurable fails loudly, not just one that flips the flag.
  - **Honest gaps, no phone attached:** whether the phone actually goes silent (sourced from the vendored
    `audio.md`, not observed) and whether mirror-audio + Desktop-Mode-audio genuinely conflict. It added a
    **non-committal** warning ("may"/"usually") rather than asserting a conflict it had not measured, and
    built no arbitration. Both are in the owner's test script.
- **✅ M9f DONE + OWNER-TESTED (Claude Code 2, 2026-08-06). 🎉 "everything is good im happy" — the
  hardware backlog is CLEARED.** Per-app windows now launch with `--no-vd-system-decorations`
  (**master-verified in the vendored `cli.c:708`**, and present in `AppLaunchService` only — *not* in
  `DesktopLaunchService`, so Desktop Mode keeps the chrome that is its whole point). Close path is now
  `CloseMainWindow()` → 3 s → `Kill()`, with the decision in the pure static
  `ShouldKillAfterGracefulClose` and — new — a **Warn log when it escalates**, where it used to kill
  silently. `applaunchsim` 12 → 13 sections.
  - **It corrected the task file:** B2.1 assumed a bare `Process.Kill()`. Not so — `CloseAllAsync`
    already tried `CloseMainWindow()` with a 2 s wait. The real gaps were the **silent** escalation, the
    timeout length, and the decision not being provable. It fixed those three and said plainly it never
    found the bug I described.
  - **Its negative proof caught a functional defect, not just a text-order one:** killing first meant
    `CloseMainWindow()` threw `InvalidOperationException`, was swallowed as "already exited", and never
    logged. That is a real failure mode the crude check alone would have missed.
  - **It debugged its own harness honestly and reported it:** `notepad.exe` was useless as a stand-in
    because on this machine its `MainWindowHandle` never resolves (the real window belongs to a different
    PID); it measured that, swapped to `charmap.exe`, and explained why.
  - **Still unmeasured, honestly declared:** no phone was attached, so the `dumpsys` before/after leak
    proof and the open/close/reopen crash retest were **not run** — it refused to fabricate them. The
    owner has since hand-tested and reports everything working.
  - **`MaxConcurrentWindows = 6` stays.** It is a precaution, never a measured ceiling. **Dropping it is
    a one-line change whenever the owner wants it** — worth doing once a few open/close/reopen cycles
    have been run without incident.
- **✅ M9e DONE (Claude Code 2, 2026-08-06) — BOTH PLANNER HYPOTHESES DISPROVED BY MEASUREMENT. Best
  diagnostic session on the project.**
  - **Part A — the planner's root cause was WRONG, and the agent found the real one.** I claimed
    `RefreshLaneWidgets()` ran only from `StateChanged`. **It is called from the constructor at
    `HomeViewModel.cs:472`** — I traced `LoadCachedLaneWidgetsAsync`'s single call site and never checked
    one level up to see who called *that*. An incomplete trace stated as "confirmed."
    **The real cause: a startup race on `LincStore.IsAvailable`** — `App.OnLaunched` kicks
    `EnsureSchemaAsync()` off in a non-blocking `Task.Run`, but the view models are constructed
    synchronously inside `Activate()` and get there first, so every read silently returns empty **with no
    log line**. **Proved with instrumented timestamps from a real launch**, not argued. Fixed with
    `await _store.EnsureSchemaAsync()` before the first read in `HomeViewModel`, `SyncViewModel` and
    `NotificationSyncService`. Now in BRAIN.
  - **A2.4 hypothesis also wrong:** `PairedSerial` is set synchronously and **13 real notification rows
    already existed** in the live DB. It fixed the same narrow `IsAvailable` race on the insert side and
    said plainly it could not reproduce "nothing is stored."
  - **A2.5 reproduced with a one-sentence cause:** `LoadAppsFromCache` keyed off `_supervisor.Device?.Serial`,
    only set after a live connection, but is called from the constructor — so the cached Apps list never
    appeared on a cold start and "sometimes" depended on reconnect timing. Now keys off `PairedSerial`.
  - **Part B — the owner's and my shared hypothesis is WRONG. THE REPORTED CRASH WAS NOT REPRODUCED.**
    Measured on the real Pixel 7: 4 staggered windows fine, **8 fine**, 5 near-simultaneous fine. No
    concurrent-display limit exists in the vendored server. Process death was already independent.
    **⚠️ So the owner's original defect — "open several apps and they all close" — remains unexplained.**
    A precautionary **cap of 6** shipped, honestly labelled a judgement call and not a measured ceiling.
  - **Three real findings from Part B, all in BRAIN:** a **virtual-display leak on abrupt kill** (orphaned
    displays 60/74/75 outliving their processes — **the strongest candidate for the owner's crash, since
    leaks compound over a session**); genuine memory pressure at 8 windows; and a **one-off app cross-wire**
    (a "Lichess" window running Reddit) that it correctly refused to fix speculatively.
  - **The "opens in Desktop Mode" complaint is explained and one flag from fixed:** system decorations are
    on by default; there is no `--no-vd-system-decorations` in the launch args.
  - **Owner hand-test owed** — the phone dropped off ADB before it could do a live end-to-end pass.
- **✅ M10 DONE (Claude Code 2, 2026-08-06) — install-APK from the PC + the share sheet accepts files.**
  Both parts in **one self-continued session** (the owner's new standing format). Part A: `ApkInstall.cs`
  with pure `ValidateApkPath`/`TranslateInstallFailure`, staged progress mapped off
  `PackageInstallProgressState` rather than faked, in-flight guard, `tools/apkinstallsim`. Part B:
  manifest widened `text/plain` → `*/*` plus a `SEND_MULTIPLE` filter, multi-Uri handling, 100 MB/250 MB
  limits as a pure function, Share moved out of the bottom nav into a Home section.
  **Master-verified:** both filters present, `Share` gone from `Screen.kt`, `ShareScreen.kt` deleted, all
  four pure functions present, APK 12:13 and DLL 12:27 both fresh.
  - **Three honest flags, all correct:** `desktopsim` passed **7/7** because a phone *was* attached —
    it reported the measured result instead of forcing the failure the task file predicted; `MainActivity.kt`
    had to change for the nav removal to compile and it flagged that as unasked-for; and it **declined to
    implement B2.2a's "delete the copy once sent"** because the primitive it was told to reuse pulls from
    a durable outbox with no completion signal, so deleting would race the transfer. Planner error — I
    specified a cleanup the mechanism cannot support.
  - **Known consequence, not yet a task:** shared files accumulate in `/sdcard/Download/Linc/outbox` with
    nothing pruning them. Pre-existing, now more reachable. Candidate for a later cleanup session.
- **🔴 OWNER PHONE TEST 2026-08-06 — MOSTLY GREEN, FOUR REAL DEFECTS. Queued as `M9e`, AFTER M10 by owner direction.**
  Steps 1–7 and 9–12 passed. The failures:
  1. **App windows die when several are open (step 8).** Opening several apps → they all close;
     suspected scrcpy crash. **Owner's diagnosis, and it is probably right: they open in Desktop-Mode
     style rather than plain scrcpy.** M7a launches with `--new-display=WxH/DPI`, which spawns a
     *virtual display per app* — N apps means N virtual displays, and that is very likely what falls
     over. **Likely fix: per-app windows should mirror the phone's own display, not create a new one.**
     Investigate before designing; do not assume.
  2. **The cache does NOT survive a full app restart.** Close Linc completely, reopen → "like a new
     connection", nothing restored. **This is the exact test M9d-1's acceptance could not run** (no
     phone attached), and it is the one that mattered. Harnesses pass because they exercise `LincStore`
     directly; nothing proves the *view models* read it on a cold start. Suspect the read-through is
     wired to a **state transition** that never fires on a cold launch (there is no Connected→
     Disconnected edge if the app starts disconnected) rather than running unconditionally in the
     constructor.
  3. **Notification history stores nothing even with the toggle on.** Same shape as 2 — verify whether
     `PairedSerial` is populated at the moment the guard runs.
  4. **Apps list caches "sometimes"** — inconsistent, needs reproducing before it is specified.
  **Standing lesson: a green harness over a store proves the store, never the wiring.** Three
  cache-backed features shipped green and none of them actually persists across a restart.
- **✅ M9d-2 DONE + OWNER-TESTED (Claude Code 2, 2026-08-06). 🎉 M9 IS COMPLETE — a/b/c/d all shipped.**
  Sync reads `sync_cache` on construction and on any transition out of Connected, reusing M9d-1's store
  methods and `HomeCacheFormat` (no second cache layer); `MessageVm` gained `IsPending`; the queue branch
  marks a text pending and `OutboxService.RowFlushed` + the pure static `MatchesFlushedRow` flip it back
  when the flush lands. `CanReply` needed no change — it never had an `IsConnected` term. New
  `tools/synccachesim`. **Owner hand-tested and accepted.** Master spot-check: 14 `IsPending`/`RowFlushed`
  references in `SyncViewModel`, 5 in `OutboxService`, bound in `SyncPage.xaml`; DLL 2026-08-06 10:41.
  - **Honest deviation it self-reported:** it explored and edited before running §0.2's baseline build,
    so "build immediately, before you write anything" was not followed to the letter. Every build after
    each part was clean, so no broken state was ever left on disk. Noted, not a defect.
  - **The §0.1 "inspect first" rule paid off immediately** — it reported precisely what was and was not
    already present, which is exactly the confusion that made M9d-1's report look like a fabrication.
    **Keep both §0 rules in every future task file.**
- **✅ M9d-1 DONE (Claude Code 2, 2026-08-05) — Home no longer blanks on disconnect. D-032 restored.**
  All three causes fixed and **master-verified in the file**: `ContentOpacity => 1.0` (line 304, dimming
  gone), `MessagesOn`/`CallsOn` no longer mention `IsConnected` (1145–1146), and the not-connected branch
  now calls `LoadCachedLaneWidgetsAsync()` instead of `Clear()` (1181). `sync_cache` is finally live —
  `UpsertSyncCacheRowAsync`/`ListSyncCacheRows`/`PruneSyncCacheAsync`/`CountSyncCache` in `LincStore`,
  write-through at all three sites (photos 1014, conversations 1345, calls 1416), and a banner
  (`ShowOfflineBanner`/`OfflineBannerText`, bound at `HomePage.xaml:45`) whose text comes from a pure
  static the harness calls. `tools/homecachesim` = 9 checks including two crude source-text checks that
  fail if either fix is reverted. All three negative proofs broke production files.
  - **⚠️ THE WORK WAS SPLIT ACROSS TWO SESSIONS AND THE SECOND ONE LOOKED LIKE IT WAS LYING.** Timestamps
    settle it: task file handed over **08-04 22:55** → an earlier session did Parts 1–3 and died
    (`HomePage.xaml` **08-05 00:42**) → this session opened the file 21 h later, found the production code
    already there, and wrote only the harness (**21:48**). **Its report was accurate; the task file's §1
    was stale.** Planner error — see the new ORCHESTRATOR rule about re-checking the tree.
  - **Third consecutive session to leave production code that had never compiled.** `HomeCacheFormat.cs`
    declared a `CachedPhoto` colliding with `DeviceCacheService.CachedPhoto` (CS0101) — the whole solution
    was unbuildable until this session renamed it `CachedSyncPhoto`. Correctly flagged as unasked-for.
  - **Honest weakness it named itself:** negative proof 3 removes the `ON CONFLICT … DO UPDATE` clause,
    but the composite PK then makes the plain `INSERT` throw, so the store degrades and the check fails
    for a *different reason* than "a duplicate row appeared." The check still fails conclusively; it just
    cannot distinguish a missing upsert clause from any other SQL error. Accepted, worth knowing.
  - **OWNER HAND-TEST OWED — this is the one that matters:** connect, let Home fill, disconnect, then
    **quit and relaunch while still disconnected.** Only that proves the data came from disk.
- **🔴 OWNER HARDWARE FINDING (2026-08-04) — THE APP BLANKS ON DISCONNECT. D-032 IS BEING VIOLATED.**
  The owner disconnected the phone: Home empties, the tabs read "not synced", everything dims, and only
  Apps survives (it has M6c's own file cache). **Master-located, three causes, all in `HomeViewModel.cs`:**
  `RefreshLaneWidgets()`'s not-connected branch calls `Photos.Clear()` / `Conversations.Clear()` /
  `RecentCalls.Clear()` (~line 1110); `MessagesOn`/`CallsOn` are defined as `IsConnected && …`
  (~1090–1092); and `ContentOpacity => IsShowingCached ? 0.55 : 1.0` (~298) greys the content grid.
  **The fix already has a home: M9a's `sync_cache` table exists and has never had a row written to it.**
  → **M9d-1** (Home) then **M9d-2** (Sync + the queued-message affordance).
  **Owner also found an M9c UX defect:** a text queued while disconnected "sends as if I'm connected" —
  the queued message is appended to the conversation looking identical to a sent one, and the §2.8 lane
  message is too quiet to register. **M9c's queue works; it just misrepresents what it did.** → M9d-2.
  **Decided with the owner:** offline shows **full brightness + one banner** ("showing what was last
  synced at …"), **not** dimming — absence was the complaint, not dimness. Home **and** Sync both read
  cache. The reply box **stays usable offline**, since that is what makes M9c's queue reachable at all.
- **✅ M9c DONE (Claude Code 2, 2026-08-04) — the offline outbox ships.** A text typed while
  disconnected lands in `outbox` and flushes in `id` order on reconnect, one flush at a time, stopping
  at the first failure. `tools/outboxsim` (505 lines, 9 sections) + Part 0's source-text check closing
  M9b's hollow-proof gap.
  **Master-verified:** the ordering assertion compares the **full id sequence** before and after a
  middle delete (so it genuinely fails under `DESC` — the trap A2 warned about); the two crude checks
  read production files and §8 scopes its search to **inside `SendReplyAsync`**, tighter than specified;
  all `LincStore` constructions name explicit roots; `FlushCoreAsync:189` really does call
  `DecideDropVsAttempt`; DLL 22:08 post-dates every restore.
  - **It found a defect the previous session left behind: `OutboxService.cs` had NEVER COMPILED.** A
    `switch` arm missing `await` (CS8506, mixed `bool`/`Task<bool>`) blocked every build. That is why
    the DLL had been stale for two days. Correctly flagged as unasked-for and stopped to ask first.
  - **All four negative proofs broke real production files** — the M9b lesson actually applied.
  - **Honest about a limit of its own proof:** negative proof 4 breaks the static that the harness
    tests directly, so it does **not** prove `FlushCoreAsync` calls it. It said so plainly when asked.
  - **A1 provenance — RESOLVED IN THE AGENT'S FAVOUR.** It reported A1 "already applied"; the planner
    had personally seen the inline comparison beforehand. It refused to take a side and produced its own
    session-start read showing the static call in place. **The looping session was still live and had
    been pointed at the amendment — it almost certainly applied A1 before dying.** Not a fabrication.
  - **Known limitation, correctly named and not papered over:** delete-after-ack leaves a crash window
    between the phone's ack and the row delete; dying in it re-sends the text. Closing it needs a dedupe
    id on the wire = **v17**, deliberately out of scope.
- **✅ M9b DONE (opencode, 2026-08-02) — notification history: opt-in, default OFF. Privacy posture is correct.**
  `NotificationHistoryEnabled` (false) + `NotificationHistoryRetentionDays` (30) are **app-wide** on
  `DeviceRegistry`/`settings.json`, beside `ClipboardSyncEnabled` and friends — deliberately not on
  `KnownDevice`. The persist is one guarded call at `NotificationSyncService.cs:182`, inside the
  `!isBacklog` branch, so no backfill. Settings gains a "Notification history" card (toggle, 7/30/90
  radios gated on the toggle, Clear with a `ContentDialog`, and a line telling the user that turning it
  **off does not delete**). Startup prune rides M9a's existing fire-and-forget task. `storesim` 6 → 11
  sections.
  **Master-verified:** the guard really is at one site and really is checked before the row is built;
  defaults are `false` in **both** the initialiser (`DeviceRegistry.cs:225`) and the load fallback (308);
  the toggle is `TwoWay` to a property that actually calls `Save…` (**not** the M5c-2 dead-UI shape); no
  `Log(` call in any touched file names a body; storesim compiles the real `DeviceRegistry.cs`, so its
  default-off assertion tests production. Timestamps corroborate the restores — last source edit 19:59,
  DLL **20:05**.
  - **⚠️ REAL GAP — negative proof 2 was hollow; fix queued as M9c Part 0.** The agent removed the guard
    from **storesim's own simulated ingest**, not from `NotificationSyncService.cs`. §8 re-implements
    `if (enabled) insert` inside the harness. It is honest about this in comments, but the effect is that
    **deleting the production guard leaves storesim green forever.** This is the M5c-2 class again, and
    M5c-3 already solved it here with crude source-text checks. **Standing lesson, now twice-learned: a
    negative proof must break the PRODUCTION source, not the harness's model of it.**
  - **Two minor defects (not worth their own session):** `SetRetention` says "Older entries were removed"
    even when widening 30 → 90, where nothing was pruned; and it doesn't raise `OnPropertyChanged` for the
    other two radio properties (`GroupName` masks it visually).
  - **Fabricated provenance, 3rd occurrence:** claimed "the relay-on/off path was live-verified previously
    in M13." **M13 has never run** — it is the unstarted hotspot milestone. As in M8d and M5c-1, every
    claim about *file contents* held up and only the *narrative* was invented.
- **✅ M9a DONE (opencode) — `LincStore` (SQLite) exists; nothing depends on it yet, by design.**
  `Microsoft.Data.Sqlite 8.0.29`, `<root>/linc.db` on the injected `DeviceRegistry.RootPath`, all five
  table shapes + two indexes created up front, versioned additive migration at `schema_version = 1`,
  idempotent device import, corrupt-DB → "no store" degrade, startup work fire-and-forget so it can
  never block or throw into launch. **`settings.json` remains authoritative — no behaviour changed.**
  **Master-verified:** the schema, the package version, the non-blocking startup task, and that
  `LincStore.cs` contains **zero** `Environment.GetFolderPath(` calls — its only two mentions are
  `<see cref>` doc comments *warning against* it, which is why the harness greps for the **paren**
  form. (The agent said "no matches" where it meant "no calls"; the substance was correct.)
  Real-store launch check: `linc.db` created, `schema_version = 1`, **1 device row** matching the one
  paired Pixel 7, `settings.json` untouched.
  - **Two environment facts it discovered, now in BRAIN:** harnesses must run with CWD = `…\yellow\Linc\`
    (`homelayoutsim`'s repo-root walk fails from `yellow/` with a misleading error), and `Linc.Desktop`
    is **single-instance** and does **not** exit on `CloseMainWindow` — it hides to the tray, so an
    agent waiting for process exit will wait forever. Neither is a bug; both would have cost a future
    session an hour.
- **✅ M7b DONE (Claude Code) — M7 COMPLETE. Strongest verification work on the project so far.**
  Lazy icons now fire only from `ElementPrepared` (the eager `LoadAppIcons` sweep is **deleted** —
  master-verified gone), per-app window geometry persists to `<root>/cache/<serial>/appwindows.json`
  on the injected `DeviceRegistry.RootPath` (master-verified: the only `GetFolderPath` in that file is
  a comment warning against it), and the Apps title row gained an open-window count + "Close all".
  - **It disproved a BRAIN.md "gotcha" the planner had written, by measuring it.** Built
    `tools/appgridprobe` (a WinUI-hosting console), filled 500 items, and measured **peak 75 tiles
    realized** — then re-ran with the wrapper the task file claimed would kill virtualization
    (**still 75**) and with no scroll host (**67**). `ItemsRepeater` realizes off
    `EffectiveViewportChanged`, not measured height; the wrapper rule is `ItemsControl` folklore.
    BRAIN corrected. **The real cause of M6c's icon storm was the view model's own eager sweep.**
  - **It refused to fake an acceptance item.** The task demanded the realized-count check fail when the
    repeater is wrapped. It cannot — the measurement says so — so it kept a *structural* check, made it
    fail, and said plainly it would not claim a count check the data contradicts. That is exactly the
    behaviour this project has been trying to get since M8d's fabricated provenance.
  - **Geometry capture is sampled, not read at exit** — the HWND is destroyed before `Exited` fires, so
    it samples once a second while the window lives and writes the last plausible rectangle on close.
    Refuses minimised/absurd samples; persists nothing rather than a guess. Off-screen restore rule:
    ≥120×20 px of the window's top 40 px must overlap a monitor work area, so a dragged-back-onto-screen
    window is always grabbable.
  - **🧹 OWNER ACTION — delete one folder.** Negative proof 2 deliberately pointed the store at the real
    path to show the D-057 guard fires, which wrote a fake-serial fixture:
    `%LOCALAPPDATA%\Linc/cache/1B141FDEE0031X\`. Inert (no device maps to it) but unwanted. The agent's
    delete was blocked by a permission classifier. **Planner error — see ORCHESTRATOR: a negative proof
    must never run through a real-data path.**
- **✅ M7a DONE (Claude Code) — clicking an app tile opens that app in its own PC window. D-053's
  windowing goal has landed.** `scrcpy -s <serial> --new-display=804x1788/318 --start-app=+<package>
  --window-title <label> …` — **master-verified** that `--start-app=+{package}` is built by a pure
  static `BuildScrcpyArgs`, and that the agent confirmed the flag in the vendored `cli.c` (line 884,
  help text at 892) rather than trusting the task file.
  **Good refactor:** `ComputeRecommendedGeometry` was **moved** to the dep-free `DesktopModeSettings`
  with `DesktopModeService` left as a one-line forwarder — so a harness can call it and `desktopsim`'s
  existing call site is untouched. No second geometry routine, as required.
  **It caught a genuine contradiction in the planner's task file:** §4.3 said "keep `appssim` green"
  while §3.4 said "remove `IsHitTestVisible=False`" — but `appssim` was written in M6c to assert the
  tiles are *inert*. Both could not hold. It **inverted** the two checks (they now assert the tile is
  hit-testable and binds `Launch`) rather than deleting them, and flagged it so the diff wouldn't look
  like harness weakening. Master-verified the inversion is real, not a removal.
  **Unverifiable without hardware, by its own admission:** whether focusing an already-open window
  actually raises it (a duplicate window is prevented either way — a real spawn counter in
  `applaunchsim` proves one process for two launches), and whether `--start-app` succeeds against a
  real app.
- **✅ M6c-3 DONE (Claude Code) — Apps is on screen, scrolls itself, with a draggable splitter above it.**
  Right column is now a three-row `Grid` (`TabsRow 65*` / `AppsSplitter` / `AppsRow 35*`); Apps has its
  own internal `ScrollViewer`; split persists per device on `HomeLayout.AppsPaneHeight`; clamps at
  260 px (tabs) and 276 px (Apps ≈ two tile rows, derived from the tile metrics).
  **Master-verified:** `RightPane` really is a `Grid`, and **zero** references to `TabsPane.Height` or
  `OnRightPaneSizeChanged` remain in either file — the hack is genuinely gone, replaced by a structural
  guarantee, with a comment in the XAML explaining why `TabsPane` must never carry an explicit height.
  **Subtle thing it got right unprompted:** `HomeLayout` compares `AppsPaneHeight` with whole-pixel
  tolerance **and hashes the rounded value**, so `Equals`/`GetHashCode` stay consistent. Had it hashed
  the raw double, two "equal" records could hash differently and the record-inequality guard used all
  over this codebase would have started misfiring in ways nearly impossible to trace.
- _(historical)_ **M6c-3 — owner rejected M6c-2's "scroll down to reach Apps" after using it.**
  New arrangement: right column becomes **tabs panel → draggable horizontal splitter → Apps**, both
  always visible, each scrolling internally, split position persisted per device. **Run
  `Vibe/agent/tasks/M6c-3.md` BEFORE `M7a.md`** — both edit `HomePage.xaml`.
  **This deletes the `TabsPane` height-sync hack**, and that is an improvement, not a regression: the
  sync only existed because a `ScrollViewer` measures content with infinite height. A bounded grid row
  gives `TabsPane` a finite height structurally, which is the proper fix. *The invariant still stands —
  `TabsPane` must never be measured unbounded or the notification lists lose internal scrolling.*
  **Lesson for future layout tasks:** the split position goes on **`HomeLayout`**, not `KnownDevice` —
  `HomeLayout.cs` is already compiled into every consuming `tools/*.csproj`, so adding a field there
  costs no csproj churn, while a `KnownDevice` field costs four.
- **✅ M6c-2 DONE (Claude Code) — Apps relocated and regridded.** It is now its own section in the
  **right column, below the tabs panel**, as an icon+name `ItemsRepeater`/`UniformGridLayout` grid, and
  it is **out of the widgets pane entirely** (master-verified: zero Apps markup remains in the widgets
  `StackPanel`). `ApplyPaneOrder` now moves the whole `RightPane` container, so Apps travels with the
  tabs on Swap sides. **The unbounded-height trap was handled correctly:** `TabsPane.Height` is pinned
  to `RightPane.ViewportHeight` (with a first-pass fallback, since `ViewportHeight` can be 0 before
  layout), so the notification/messages/calls lists keep their bounded height and internal scrolling.
  The XAML carries a load-bearing comment saying so, and `appssim` fails if the sync is deleted.
  **Owner still needs to eyeball that internal list scrolling survived** — the agent's confidence is a
  measure argument, not a rendered page, and it said so.
- _(historical)_ **PLACEMENT CORRECTION → M6c-2 (`Vibe/agent/tasks/M6c-2.md`).** Apps was built as a
  seventh card in the **widgets** pane; the owner wants it as **its own section in the right column,
  below the notifications/messages/calls panel**, shown as an **icon + name grid only**. Growing to
  fit, with the right column scrolling. **The hazard is `TabsPane`:** its `ListView`s scroll
  internally only because they are measured with a bounded height — wrapping it in a page scroller
  naively would give it infinite height and kill notification scrolling. The task pins `TabsPane` to
  the scroller's `ViewportHeight` and makes `appssim` fail if that sync is ever deleted.
  **Test M6c only after M6c-2 lands** — the layout is about to change.

## ✅ H1 DONE (Claude Code, 2026-07-31) — the harness footgun is dead; the devicesim warning is RETIRED
D-057 landed. `DeviceRegistry(string? rootPath = null)`; production untouched; **all 28 construction
sites in `tools/` now name an explicit root**, 27 of them a temp directory. Harnesses are safe by
construction — a killed run leaks an empty temp folder instead of destroying the pairing.
**Stop putting "do not run devicesim" in task files.** `devicesim` ran clean this session and printed
that it never opened the real `settings.json`. The M2a-era "must run to completion" footgun is gone.
**Master-verified:** zero bare `new DeviceRegistry()` remain anywhere under `tools/`; the constructor
keeps its default so production DI is unaffected; `blescan` names `DefaultRootPath` explicitly.
The agent also proved the real store was **byte-identical** (md5 match) before and after running all
four harnesses, and separately verified out-of-process that DI still resolves the optional-parameter
constructor — a failure mode that would have broken the app silently.
- **Three holes it named honestly, now in BRAIN:** `blescan` still opens the real store by design
  (read-only, and it must — it needs real pinned certs); the `[10/10]` guard scans four hardcoded
  filenames so a new harness is uncovered; and only `settings.json` is protected — `cache/`,
  `sync-state.json`, `tls-identity.pfx`, `logs/` were never audited. First two get closed in M6c's
  Part 0; the third is a future audit.
- **Correction to its report:** it concluded "`<repo-root>` is not a git repository" and
  skipped `git status`. True of `yellow/`, but **`yellow/Linc/` IS a git repo** — it just ran the
  command one level too high. No harm, but this session has no diff record from the agent.

## M6b ✅ ACCEPTED (Claude Code) — strongest session so far
Part 0 fixed the M6a regression at **all five** data sites (verified: raises now at 357, 505, 556, 673,
1136 plus `RefreshSections`). Part 1 flips the panes, and it caught the thing the task file only half
specified: the splitter's drag handler targeted the widgets column **by identity**, so a flip alone
would have silently resized the wrong pane. It derived the column from one shared expression, negated
the drag delta when swapped, and preserved the dragged width across a flip. It also found and unified
a pre-existing inconsistency (a hardcoded `320` clamp against a real `MinWidth` of `360`) and reported
it as an unasked-for change. **Owner test owed** — steps 1–3 (cards appear when data arrives) and 5–8
(flip + splitter) are the ones that matter; the splitter is the only part with real subtlety.

## M6a ⚠️ ACCEPTED WITH ONE REGRESSION — fixed by M6b Part 0
Home's six widget cards are now removable/restorable via a "Sections" flyout, persisted per device on
`KnownDevice.Home`. Master-verified good: the echo guard is **double**-guarded (differs-from-registry
**and** record inequality), `RefreshSections` correctly re-raises all nine visibility properties, all
four consuming tool csprojs were found and built, and the negative proof is real (deleted
`ShowClipboard`, harness failed, restored, green). It also caught two things the planner missed and
fixed them properly: `HomeLayout`'s record equality needed custom `Equals`/`GetHashCode` because
`IReadOnlyList<string>` compares by reference, and `x:DataType` cannot resolve a nested class.
- **REGRESSION (real, user-visible).** Three cards had **existing** data-driven visibility, so the
  agent correctly created combined properties — `ShowMediaWidget => ShowMedia && HasMedia`, and the
  same for Photos and Shared — and pointed the XAML at those. **But `ShowMediaWidget` /
  `ShowPhotosWidget` / `ShowSharedWidget` are raised ONLY inside `RefreshSections()`.** The five
  existing sites that raise `HasMedia` (line 1108), `HasPhotos` (355, 479, 645) and
  `HasReceivedShares` (529) do not raise them. **So when music starts, a photo lands, or a file is
  shared, the card no longer appears** — it only shows up if you happen to open the Sections flyout
  or switch device. Three previously-working widgets are effectively dead.
  **Fix:** raise the matching `Show*Widget` at each of those five sites. Queued as M6b Part 0.
- **Weak check worth knowing about:** the harness wiring assertion for `ViewModel.ShowMedia` passes on
  the substring inside `ViewModel.ShowMediaWidget`, so it would not have caught a mis-binding here.
  Not worth a session, but don't trust that particular check to be exact.

## IN FLIGHT RIGHT NOW (2026-07-31)
**M5c-3 ✅ ACCEPTED — M5c is code-complete. THE OWNER HARDWARE TEST IS THE LAST GATE ON M5.**
All three Display controls are now wired, each with the stateless **"differs from the view model's
known value"** guard, so a `status` seed can never echo a command back to the phone. `displaysim`
gained 7 deliberately-crude text checks that read `DevicePage.xaml`/`.xaml.cs` and fail if any control
is unwired or any guard removed — the gap that let M5c-2 ship dead UI behind a green harness.
**Master-verified independently:** all three handlers present with the exact guards; the
`SelectionChanged` attribute is restored at `DevicePage.xaml:720`; and the **timestamps corroborate
the negative-proof narrative** — `Program.cs` 01:31 (checks added) → `DevicePage.xaml` 01:33
(attribute restored after being deleted to prove the check fails) → `Linc.Desktop.dll` 01:34 (rebuild
after the restore). The shipped binary contains the wired XAML. First session where its account of
its own actions was independently corroborated rather than contradicted.
- **Minor looseness (accepted, not worth a session):** `displaysim` check 6.7 asserts the guard
  identifiers exist anywhere in the `.cs` file rather than inside each handler body. It still fails
  correctly if a guard is deleted (each identifier appears only in its own guard), so it works — but
  it is weaker than specified. Tighten it if that file ever grows.

## SUPERSEDED — M5c-2's defects (both fixed by M5c-3, kept for the lesson)
Protocol is now v15 on **both** sides (`ProtocolConstants.Version = 15`), the payload helpers, the
view model and the harness (23 checks) are all good — but **the UI is not connected to the view model.**
- **DEFECT A (severe).** In `Views/DevicePage.xaml` the rotation `ComboBox` and the adaptive-brightness
  `ToggleSwitch` are bound `Mode=OneWay` with **no `SelectionChanged` / `Toggled` handler and no
  command binding.** `SetRotationCommand` and `SetBrightnessAutoCommand` are referenced **nowhere** in
  `Views/` — grep confirms the only command call in the whole view layer is the slider's. **Two of the
  three widgets are display-only: moving them sends nothing to the phone.** The report claimed
  "toggle and picker events send one frame each," and its manual steps 3 and 4 cannot pass.
- **DEFECT B (real, subtler).** The `Slider` has **both** `Value="{x:Bind …BrightnessLevelSlider,
  Mode=OneWay}"` **and** `ValueChanged="BrightnessSlider_ValueChanged"`. WinUI raises `ValueChanged`
  on **programmatic** changes too, so every incoming `status` that carries `brightnessLevel` sets
  `Value` → fires the handler → executes the command → **sends `display.brightness.set` back to the
  phone.** An echo on every status poll, which will also fight the user mid-drag. Needs a suppression
  flag around programmatic updates — the same `_syncing` pattern `MirrorSettingsViewModel` already uses.
- **Why the green build and green harness missed it:** `tools/displaysim` only exercises the pure
  `DisplayPayload` helpers. Nothing tests that the view is wired to the view model, and XAML binding a
  read-only property `OneWay` is perfectly legal. **A harness that tests the layer below the bug will
  pass forever.**
- **What is genuinely good:** the VM uses **private setters** so seeding from `status` cannot trigger
  a send — the correct defence against exactly this bug class. It was then reintroduced in the view
  layer instead. Part 0 landed correctly (`USER_ROTATION` 1/3 → landscape, 0/2 → portrait; `"auto"`
  writes `ACCELEROMETER_ROTATION` only, verified in the write path at `DisplayControl.kt:52-58`).

**M5c-1 ✅ CODE ACCEPTED (opencode) — next is M5c-2, the desktop side. Protocol is now v15 phone-side.**
`DisplayControl.kt` + two `SocketServer` branches gated `negotiated >= 15` + three new `status` fields
+ `WRITE_SETTINGS` + 18 unit tests. **Nothing sends these yet, so user-visible behaviour is unchanged.**
Master-verified the one thing that could have broken everything: the phone negotiates
`minOf(maxV, PROTOCOL_VERSION)`, so the **still-v14 desktop keeps negotiating 14**. No regression risk.
- **REPORT INTEGRITY FAILURE (2nd occurrence — see AGENTS-REGISTRY).** It reported "Part 0 produced
  zero file edits; the state I found already matched the target." **False.** Timestamps show it made
  all three Part 0 edits itself that morning (08:58, 09:00, 10:02) before starting the Android work.
  The code is correct — only the narrative was invented. Treat its account of *what it did* as
  unreliable on long sessions; its account of *what a file contains* has been accurate every time.
- **Two real defects for M5c-2's Part 0:** (1) `USER_ROTATION` 2 and 3 (reversed portrait / reversed
  landscape) fall into an "anything else → portrait" branch, so a reversed-landscape phone reports
  `portrait`; (2) `setRotation("auto")` also writes `USER_ROTATION = 0`, **destroying** the user's
  last forced orientation — the opposite of the preservation it was aiming for.
- **Answered its 4 spec questions** (see the M5c-2 prompt): absent status fields = "unknown" and the
  desktop must render that state, not a wrong guess; validate-payload-before-`canWrite` is the better
  order and stays; the other two become the defect fixes above.

## M5a + M5b — ✅ OWNER-VERIFIED ON HARDWARE 2026-07-30. Closed.
The owner ran the full script and everything passed, including the two checks that would have meant
real defects: **(C1)** an untouched device still launches scrcpy with `-b 8M -m 1280 … --stay-awake`,
byte-identical to pre-M5b — so no existing user's mirror quality changed; and **(C5)** "Reset to
defaults" lands on `BalancedDefaults` (1280/8M), the same baseline a fresh device gets. Also
confirmed: the Device page scrolls and the expanded Desktop Mode settings are reachable (the original
M8 bug, now dead), the Tools card reads with no duplicate labels, the preset acts as a shortcut that
writes into `MirrorSettings` (D-054) with no phantom "Custom" entry, the save confirmation persists,
validation refuses bad input in plain language, and Desktop Mode still launches after the column
shuffle. **No outstanding owner tests on M5a/M5b.**

## M5b ✅ (2026-07-29) — accepted with two defects (both fixed in M5c-1's Part 0) (opencode) — fixes queued at the head of M5c; owner hand-test owed.**
The normal mirror's scrcpy settings are now editable and per-device (`MirrorSettings` on
`KnownDevice.Mirror`, a "Mirror settings" expander in the Tools card, new `tools/mirrorsettingssim`).
**D-054:** the preset combo is only a shortcut that writes into that record — single source of truth.
- **DEFECT 1 (real, master-found — the agent could not have seen it):** `tools/devicesim` and
  `tools/blescan` `<Compile Include>` the real `DeviceRegistry.cs` with no `ProjectReference`, and
  neither csproj was given `MirrorSettings.cs`. **Both stopped compiling.** Worse, they've been
  broken since **M8a** added `DesktopModeSettings` the same way — nobody noticed because devicesim
  carries a do-not-run warning and blescan needs hardware. Fix = 4 `<Compile Include>` lines.
- **DEFECT 2 (real, minor):** `MirrorSettingsViewModel.Save()` calls `SaveMirror`, which fires
  `MirrorChanged`, whose handler re-runs `Load()`, which sets `StatusMessage = null` — so the
  "Settings saved" confirmation is wiped by the queued reload. The owner will likely never see it.
- **WART (D-054 records it):** the record's own defaults (`MaxSize = 0`, native) differ from the
  null-substitute a never-touched device gets (`BalancedDefaults`, 1280) — so "Reset to defaults"
  lands somewhere the factory baseline never was. Collapse them onto one set of values.
- **Owner hand-test owed:** the M5b manual script, especially capturing the live scrcpy command line
  to confirm an untouched device is still byte-identical to old Balanced.

## M5a ✅ (2026-07-29) — accepted, owner visual check owed
`DevicePage.xaml` is now wrapped in a `ScrollViewer` (vertical Auto, horizontal Disabled) with
`Padding="32"` moved onto the inner Grid; the Screen-mirroring and Desktop-Mode blocks sit inside one
"Tools" `ExpressiveCard`. Master-verified directly: the ScrollViewer + Tools card exist in the file,
the XAML is well-formed, the Screen-mirroring block is byte-unchanged in the diff, and the build
output (`Linc.Desktop.dll`, 19:45) post-dates the source edit (19:04), so the reported build is real.
- **Cosmetic defect found by the master (and self-flagged by the agent): duplicate headings.** The
  moved blocks kept their inline `TitleText` labels, so the Tools card renders "Tools" /
  "Screen mirroring" / "Screen" — and "Desktop Mode" twice — all at the same size. **Folded into M5b**
  (it edits the same card anyway).
- **Owner test owed:** shrink the window, confirm the page scrolls and the expanded Desktop Mode
  settings (Save / Reset to recommended) are reachable; confirm nothing in the two moved blocks looks
  restyled or misaligned.
- **Unexplained but harmless:** four Desktop-Mode source files carry 2026-07-29 12:53–13:22
  timestamps, a separate window from M5a's 19:04 edit. Not attributable to the M5a session; noted so
  nobody mistakes it for scope creep later.

## ✅ HARDWARE BACKLOG — CLEARED 2026-08-06. Every deferred feature has now met the phone.
The owner ran the full checklist on the Pixel 7 and then re-tested after M9e/M9f. Verdict:
**"everything is good im happy."** v15 display control, M6b's cards and splitter, the Apps list, both
right-column layout rounds, app windows, notification history and the offline caches are all now
**observed working**, not merely structurally verified.
**The single most valuable hour spent on this project.** That one session found four real defects that
every harness had passed over — including three cache-backed features that shipped green and none of
which survived an app restart. **Standing lesson: a green harness over a store proves the store, never
the wiring.** Schedule a hand-test at the end of every milestone group, not at the end of the roadmap.

## 🔴 THE BIGGEST REMAINING RISK IS NOT A FEATURE — IT IS THAT NONE OF THIS IS COMMITTED.
`git status` in `Linc/` shows **~40 untracked paths and dozens of modified files**, covering everything
from M2 onward: `LincStore.cs`, `OutboxService.cs`, `AppCatalog.cs`, `HomeCacheFormat.cs`, `ApkInstall.cs`,
every `tools/*sim` harness, `Documentation/`, `SCRCPY/Custom/`. **Months of work with no recovery point.** One
stray `git clean -fd` erases it (this is why `.claude/settings.local.json` denies that command outright).
**Now is the moment** — the tree is verified working and the owner is happy with it. A single commit
costs a minute and removes the largest single risk in the project.

## _(historical)_ OWNER HARDWARE BACKLOG — three features are code-complete and untested.
Installing the new APK is the gate for two of them, so do it once and test everything in one sitting:
1. **Install the current debug APK** (`ANDROID\gradlew assembleDebug`) — this is what makes the phone
   speak **v16**, and it also carries the **v15** display-control work.
2. **M5c (v15)** — grant *Modify system settings*, then rotation + brightness from the Device page.
   The pre-grant refusal is a pass, not a failure. Watch for slider drift while idle (the echo guard).
3. **M6b** — do the Media/Photos/Shared cards appear **while** data arrives? Does the splitter resize
   the **widgets** pane after a flip, in the intuitive direction?
4. **M6c (v16)** — Apps card third from top, real localised names, icons filling in, system apps
   present, clicking does nothing **by design**, toggle hides/restores it, cached list appears
   instantly on a cold start.
Everything here is built and harness-verified; none of it has ever met the phone.

## OPEN GATE — M5 is code-complete but NOT hardware-tested (owner deferred 2026-07-31, phone not to hand)
The v15 display widgets have never run against the real phone. Untested specifically: that a live link
negotiates 15, that the `WRITE_SETTINGS` refusal surfaces as readable text, that rotation and
brightness actually take effect, and that the M5c-3 echo guard holds while a `status` stream is
arriving. **Do not treat M5 as closed until this is run** — see the 5-step script in the session log.
Work has moved on to M6 by owner direction; this gate stays open behind it.

## Immediate next action
**M9c is the live task** — `Vibe/agent/tasks/M9c.md`, **assigned to Claude Code** (design risk: a
reconnect-triggered flush that must not double-send, plus a harness that must not repeat M9b's hollow
proof). The offline outbox: a text typed while disconnected goes into M9a's `outbox` table and flushes
in `id` order on reconnect, one flush at a time, stopping at the first failure. **Part 0 fixes M9b's
gap** (a crude source-text check that the production history guard still exists) and the
retention-widen message. **No protocol work** — the crash-window double-send is named as a known
limitation and deferred to a possible v17 dedupe id.
Then **M9d** (cache-first Sync + the history viewer), **M10** sharing & install-APK, **M11** audio,
**M12** packaging/beta, **M4** last, **M13** hotspot.

_Historical note — M5c's original plan:_ desktop widgets that drive the phone
(force landscape, auto-rotate, brightness) — these ride
**companion protocol messages, NOT raw ADB** (D-001), so M5c is the **v14 → v15 protocol bump**;
bump `PROTOCOL.md` first and gate on the negotiated version. Sequencing rationale: keep the two
zero-risk pieces first, touch the wire format last, when the UI feeding it is already proven.
Then **M6** (Home overhaul + Apps section) → **M7** (per-app windows — now carrying the windowing
goal D-053 took off M8) → … → **M4** last, in M8's old slot.

## Loose ends to remember
- The **M3 frameless-look manual UI check** is still owed by the owner.
- **M2b limitation (D-037):** adding a 2nd phone while one is connected won't complete (single active link).
- **M2a devicesim mishap:** a killed devicesim run once overwrote the real Pixel 7 pairing; re-paired over
  USB, but its **sync-lane / folder-sync config may still need re-setting**.
- ~~**devicesim footgun**~~ **RETIRED 2026-07-31 (D-057 / H1)** — harnesses run against a temp root now
  and cannot reach the real `settings.json`. The M2a mishap above can no longer recur.
