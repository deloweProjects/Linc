# Changelog

Chronological record of significant features, fixes, refactors, and architectural changes. Newest
first.

> **History before 2026-07-22** (all of Era 1 `M0–M19` and Era 2 `M00–M07`) is captured formally
> in `Documentation/02-History.md`, and the raw old changelog is preserved at
> `v2.docs.old/CHANGELOG.md`. This file starts fresh for the **Convergence** phase (`gemini-docs/ROADMAP.md`).

## [Unreleased]

### Added — 2026-10-18 — Play everywhere (D-096)

- **Play everywhere** (Tools → Media): the PC's sound on its own speakers, its Bluetooth devices
  and the phone's speaker or Bluetooth at once, in step, with a volume and mute per device.
- **Align automatically** times each speaker with the phone's microphone; **Play test clicks** and
  a per-speaker nudge let you align by ear; an extra safety margin helps on a busy Wi-Fi.
- Everything it does is in the logs on both sides. Protocol v26.
- File sharing to the PC no longer stops at 100 MB per file (250 MB per selection).
- PC Remote: the Pointer buttons show the mode you picked; the click ring shows in Touch mode too.

### Added — 2026-10-17 — Background clipboard, phone as PC speaker, smarter recognition (D-092–D-094)

- **Clipboard without opening Linc**: copy on the phone and it reaches the PC with Linc in the
  background, after the PC has connected over ADB once (it grants what this needs).
- **Phone as PC speaker** (Tools): the PC's sound moves to the phone instead of playing on both,
  so nothing echoes; the PC's own speakers come back when you stop or disconnect.
- **Send to PC** shows a notification while sending, then "Sent", then disappears.
- **Mirror**: a click ring and a haptic tick the instant you click; the pointer stays in step with
  the PC on slow links and after the PC moves it itself.
- **Home → Your PC**: battery, network, volume and brightness at a glance.
- **Setup** explains ADB pairing and shows live checks that it worked.
- **Recognition**: the PC also knows your phone by its Wi-Fi address and connects as soon as it
  joins the network.
- The PC Remote bubble menu is now called **Tools**. Protocol v25.

### Fixed — 2026-10-18 — Smaller fixes and test harnesses (D-095)

- Closing Linc's app windows on disconnect no longer stops at a window that had just exited and
  leaves the rest open.
- The PC log no longer says "Detection took -1 ms" for a link that closed before any probe failed;
  empty Bluetooth errors show their code; direct connections name their peer.
- Voice Access's and Tablet mode's restore notes, the mic note and the VB-CABLE download live under
  the store root (`--data-root` is honoured). The Beta publish finds the VC++ runtime on any Visual
  Studio edition. `updatesim`, `quicksharesim` and `applaunchsim` no longer fail for reasons that
  aren't bugs.

### Fixed — 2026-10-11 — Mirror PC no longer gets stuck; Start Linc works after Stop (1.0.0.14, D-091)

- After stopping Mirror PC (more likely on 4K screens) it could refuse to start again until Linc was
  restarted. It now always starts again.
- On the phone, Start Linc did nothing after you had stopped Linc. It now starts.

### Fixed — 2026-10-11 — Linc no longer closes when the phone disconnects (1.0.0.13, D-090)

- Since 1.0.0.11 the PC app shut itself down the moment the phone's connection dropped (the
  connected/disconnected tray icon). It now stays open and reconnects.

### Fixed — 2026-10-11 — Mirror PC on Intel graphics, for real this time (1.0.0.12, D-087)

- When the graphics chip's encoder won't start, Linc retries it with basic settings and then uses
  Windows' own software encoder, so Mirror PC always starts. If the encoder stops mid-stream, Linc
  switches without dropping the mirror.
- Settings → Mirror PC → Use software encoding, for PCs where the graphics encoder misbehaves.
- Mirroring keeps going through UAC prompts and the lock screen, and uses the right graphics chip on
  PCs with two.

### Added — 2026-10-11 — The phone remembers each PC; pick which one it connects to (1.0.0.12, D-088)

- Phone: Connection → PCs lists every PC you've paired with. Pick one, or "Any of my PCs".
- Linc is with one PC at a time: another PC is told the phone is busy, and by whom.
- Forget a PC you no longer use.

### Added — 2026-10-11 — Setup can skip installing Linc on the phone (1.0.0.12, D-089)

- Add a phone → "Linc is already on my phone — don't install it".

### Added — 2026-10-11 — A Windows installer (1.0.0.11, D-085, D-086)

- `Linc-Setup-<version>.exe`: installs for your account without an admin prompt, with Start menu and
  optional desktop shortcut and "Start when I sign in". Linc updates itself through it from now on.
- Uninstalling puts back the PC's microphone and display scale, removes Linc's Explorer entries and
  asks whether to delete Linc's data.
- The portable zip is still published.

### Changed — 2026-10-11 — A new look for the phone, Clipboard and Share pages on both apps (1.0.0.11, D-082–D-084)

- The phone app is rebuilt in Material 3 Expressive: Home, Remote, Clipboard and Share tabs, with
  Settings behind the gear. Remote is a full-screen trackpad with keyboard, media, presentation,
  voice and power sheets.
- Clipboard pages on both apps: send text to the other device, Sync now, and a searchable history of
  500 items (or 100 / 2,000) that survives restarts. Passwords and other private copies are skipped.
- Share pages on both apps say whether they're using Linc or Quick Share and pick by themselves;
  transfers show progress and can be retried.
- The PC's tray icon shows when the phone isn't connected, and its menu can send files, clipboard
  text or a link to the phone. Explorer gains "Send to phone (Linc)".
- "Show explanations" (Settings → Appearance) hides the helper lines in each app.
- The phone app now actually checks for updates.

### Changed — 2026-10-11 — Connections that just work (1.0.0.11, D-081)

- Plugging in the USB cable while on Wi-Fi switches to the cable without disconnecting; unplugging it
  moves back to Wi-Fi without showing a drop.
- The phone reconnects as soon as it joins Wi-Fi, its screen comes on or you open Linc, and tries the
  PC address that worked last time first.
- The PC notices waking from sleep (it never did on PCs with Modern Standby) and reconnects.
- No more minutes of redialling an old hotspot address after the phone leaves; retries back off and
  restart the moment something changes, and addresses that keep failing are forgotten.
- The PC log no longer repeats the same line more than three times a minute.

### Fixed — 2026-10-11 — The Remote trackpad works without Mirror PC (1.0.0.11, D-080)

- The phone's trackpad, keyboard and presentation keys work on their own — the PC used to ignore them
  unless Mirror PC was running.
- Taps click even if your finger wobbles; two-finger scroll scrolls smoothly; two-finger tap is a
  right click; tap then drag drags; slow drags move precisely and fast ones go far.

### Changed — 2026-10-11 — Adding, re-adding and closing phones flow properly (1.0.0.10, D-079)

- Closing a phone's tab asks first: disconnect and close, remove completely, or cancel. Closing the
  last one really disconnects; a closed phone isn't reconnected behind your back.
- "Add a phone" starts with a choice: phones you've used before (reconnect, no pairing), or a new one
  by USB, Wi-Fi QR, or "already set up". Then a checking screen, then "Added X" or "X was already
  added", and Continue.

### Fixed — 2026-10-10 — First setup, adding and removing phones (1.0.0.9, D-078)

- Linc restarts its own ADB service when it has died, instead of failing every pairing after it.
- A phone paired over Wi-Fi is no longer mistaken for a USB cable (and then "unplugged").
- Linc no longer restarts wireless debugging under a link that is using it, right after setup.
- One phone is one phone: its saved apps, photos and messages no longer split by how it connected.

### Fixed — 2026-10-10 — Mirror PC on Intel graphics; the phone stays connected (1.0.0.8, D-077)

- Mirror PC feeds encoders that won't take the screen's pixels directly (Intel Quick Sync) a
  GPU-converted NV12 picture, and a failed start no longer blocks every retry.
- Home warns while Linc may be stopped in the background, with Allow and the phone maker's own
  auto-start screen; the log says plainly when Android cut the connection.
- The phone stops redialling the PC every 3 s when the PC is already connected another way.

### Fixed — 2026-10-10 — Mirror PC on hybrid-graphics PCs; icon downloads over Wi-Fi (1.0.0.7, D-076)

- Mirror PC tries every hardware video encoder, the screen's own graphics chip first, and cleans up
  one that fails — it used to try only the first and leave it stuck, so every retry failed.
- Many app icons fetched at once over a direct Wi-Fi link no longer get mistaken for new connections.
- PC Copy log lists the graphics chips and video encoders.

### Fixed/Added — 2026-10-10 — Voice Access really hears the phone; Phone | PC mic; Microphone tool (1.0.0.6, protocol v23, D-075)

- **Voice Access now hears the phone.** It keeps its own microphone setting and ignored the Windows
  default Linc changed; Linc now points that setting at the virtual cable while the phone is chosen.
- **Phone | PC microphone choice** in the PC Remote panel (and on the Voice Access and Microphone
  cards); switches a running Voice Access over at once.
- **Microphone tool** in Tools: the phone as the PC's microphone for any app (calls, Win+H dictation).
- **Automatic in Mirror PC**: with Phone chosen, any PC app that starts listening hears the phone; the
  phone's mic only runs while something is listening.
- The phone keeps recording with the screen off (microphone-type foreground service).

### Fixed — 2026-10-10 — Voice Access microphone setup actually finishes (1.0.0.5, D-071)

- The VB-CABLE download used to time out after 5 minutes on vb-audio.com's slow server and start
  over each time. It now downloads in parallel ranges, resumes where it stopped, waits as long as
  data keeps arriving, and accepts a copy you downloaded yourself into Downloads.
- The phone shows the download's progress, then what to click on the PC.
- Taps on Voice Access while it is setting up no longer start it on the PC's own microphone.
- The app icon's L is smaller again.

### Added — 2026-10-10 — Tablet mode in the bubble; APK icon and Material You fixed (1.0.0.4, protocol v22, D-074)

- **Tablet mode** toggle in the PC Remote panel makes a non-touch PC finger-friendly: the mirrored
  display's scale goes up two steps (e.g. 125 % → 175 %) and File Explorer gets touch spacing; no
  Windows prompt, no reconnect; all put back when it is switched off, the session ends, Linc exits,
  or after a crash. The panel's buttons no longer shift under a finger.
- **The app icon is the right size**: the L was drawn ~4x too big and cropped; it now fits the adaptive
  icon, and follows Android 13+ themed icons.
- **Material You everywhere**: the bubble and panel use the phone's wallpaper colours, and the app's
  window follows dark mode (no white flash, dark system dialogs).

### Fixed — 2026-10-10 — Mirror PC no longer freezes on the bubble; faster start; Voice Access sets up its own microphone (D-071, D-072)

- **Tapping the PC Remote bubble no longer kills Linc.** It was an ANR, not a crash: the bubble's
  shadow followed its concave cookie shape, which Skia tessellates on the render thread for seconds
  per frame. The same work was the lag before the tap. The shadow is now a circle.
- **Mirror PC starts faster**: the phone races the PC's addresses when opening the video and sound
  channels instead of waiting out a 4 s timeout on each unreachable one.
- **Voice Access sets up the phone-as-microphone itself**: after you agree, the PC downloads VB-CABLE
  from VB-Audio (pinned, SHA-256 checked) and opens VB-Audio's own installer (one Windows prompt,
  then "Install Driver"); the phone shows "Setting up the microphone…". Not silent or bundled —
  VB-CABLE's licence forbids integrating it into another installer without the author's agreement. Remove it from desktop Settings → Phone as microphone.
- The PC's own default microphone is now really restored on Exit, and after a crash on next start.
- **Wireless drops** (D-073): Settings → "Stay connected in the background" asks Android to stop
  freezing Linc with the screen off; a Wi-Fi lock is held while connected; the phone logs Doze,
  screen, battery saver and Wi-Fi events and why each link ended.
- **Copy log** on both apps' Logs page — a report to paste to whoever is helping (phone: model,
  Android, background settings, why Android ended Linc recently, the log across restarts).
- Tests: `EndpointRaceTest`, `PcRemoteOverlayTest`, `PcVoiceAccessTest` (setup state); `mirrorsim`
  "D-071 virtual cable".

### Added — 2026-10-10 — Voice Access, Material 3 Expressive bubble, floating keyboard (protocol v21, D-069, D-070)

- **Voice Access** (Tools, and the bubble): start Windows Voice Access from the phone and talk to the PC
  through the phone's microphone (needs the free VB-CABLE on the PC; without it the PC's own mic is used).
- The status bubble is **Material 3 Expressive**: a cookie-shaped button whose Wi-Fi icon shows the
  connection, springing to the nearer edge; the panel has stats, connected Touch|Mouse and
  Floating|Phone keyboard groups, and tonal toggles for sound, Voice Access, display, quality, fit,
  Task view and Desktop.
- **Floating PC keyboard** (drag, resize, sticky modifiers, Fn layer, multi-touch, key repeat) with
  **voice typing** (🎤). "Pop up on text fields" can be switched off.
- Removed before release: the "Remote PC (private)" curtain mode (D-068, withdrawn).
- Fixed: every mirror session left a thread blocked inside the encoder (no `IMFShutdown` on dispose).
- `mirrorsim`: compressible motion pattern; fresh-encoder and phone-mic scenarios. Android: 188 tests.

### Added — 2026-10-09 — PC Remote, Windows-App style (protocol v20, D-067)

- **The PC's mouse pointer now shows on the phone**, drawn by the phone so it moves the moment your
  finger does (the mirror had no pointer at all before — Desktop Duplication never captures it).
- **Touch and Mouse modes.** Mouse mode is a trackpad with acceleration; in both, press-and-hold or a
  two-finger tap is a right click, two fingers scroll smoothly, pinch zooms; zoomed in Mouse mode the
  view follows the pointer.
- **Connection bar:** quality dot, round trip, Mb/s, fps, plus pointer mode, keyboard, extra keys,
  sound, display picker, quality picker, fit and end. Stalls are healed (keyframe at 2 s, restart at
  8 s); a dropped link shows "Reconnecting…" over the last frame instead of going black.
- **Extra-keys row:** sticky Ctrl/Alt/Shift/Win (also apply to the phone keyboard), Esc, Tab, arrows,
  Home/End/PgUp/PgDn/Del, Alt+Tab, Show desktop, Task Manager, Snip, F1–F12. Arrows and friends are
  sent as true extended keys.
- **PC sound on the phone** (pc-audio channel 6, AAC-LC 128 kb/s), on by default, toggle in the bar.
- `mirrorsim`: 5 new scenarios (live pointer, system cursor shapes, monochrome I-beam decode,
  buttons/extended keys, audio capture + AAC encode); `mirrorsim -- <text>` runs a subset. Android:
  `PcRemoteV20Test`, 23 tests.

### Added — 2026-09-26 — Quick Share, returning-phone recognition, NFC tap-to-connect

No Linc protocol change (still v19). Quick Share is Google's Nearby Share wire format, spoken
alongside the Linc link, not inside it. **Loopback- and unit-verified only; Android interop is an
owner hardware check.**

- **Quick Share on the PC** (`DESKTOP/Linc.Desktop/QuickShare/`). The PC is a Quick Share device like
  Google's Windows app: it advertises `_FC9F5ED42C8A._tcp` so any nearby Android (no Linc needed) can
  send to it, and it can send to phones, using the BLE `0xFE2C` wake beacon that makes Android start
  advertising (Windows can publish service data; macOS cannot). UKEY2 P-256/SHA-512, AES-256-CBC +
  HMAC-SHA256, PIN from Chromium's algorithm. Wi-Fi LAN medium only. Every incoming transfer needs
  Accept (in-app dialog with the PIN, or toast buttons); files land in `Downloads\Quick Share`,
  names sanitised and never overwritten. The protos are Google's (Apache-2.0,
  `QuickShare/Proto/NOTICE.txt`), generated by Grpc.Tools, hooked before `MarkupCompilePass1` so the
  WinUI XAML pre-compile sees them.
- **Share page** (new nav item, tray "Quick Share..."): visibility toggle and name, nearby scan,
  send files (picker or drag-drop), send text/links, transfer history with Open / Open link / Copy.
- **Auto-accept from your own phone** (opt-in, default off): only when the sender's address equals
  the live Linc Wi-Fi link's phone address AND the names match (`QsTrust`). A name alone is never
  trusted; a USB link cannot qualify.
- **Explorer "Send with Quick Share"** (opt-in, HKCU verb): a second Linc started by Explorer
  forwards the paths over a per-user named pipe to the running one, which opens Share with them.
- **Quick Share wakes the Linc link:** a Quick Share from a known phone while Linc is searching calls
  `IConnectionSupervisor.NudgeReconnect` (scan now + adopt what ADB holds).
- **Phone:** Share section gets a Quick Share card (send via the system Quick Share; "Receive from PC"
  makes the phone visible) and offers Quick Share when a Linc send is staged while the PC is offline.
  `<queries>` added for Android 11+ package visibility.
- **NFC tap-to-connect (phone):** Settings writes `linc://tap` + an AAR to a blank NTAG sticker;
  tapping it opens `NfcTapActivity`, which starts the companion and pokes `ReconnectSignal`, so
  `PresenceClient` dials at once instead of waiting out its up-to-30 s backoff. Nothing secret on
  the tag; trust is still the pinned TLS certificates.

### Fixed — 2026-09-26 — a returning phone is recognised, not treated as new

- Only the active phone was ever recognised. Any other known phone got the "new device" card on
  USB, had its mDNS adverts ignored, and had its Direct-TLS dial-in rejected (only the active phone's
  cert was pinned). All three now recognise any known phone (`DeviceAdmission.RecogniseKnown`) and
  adopt it **only while idle** (`MayAdoptKnownPhone`) — never over a live link (D-037). The USB card
  and the onboarding wizard now say "already known" vs "new phone".
- **Cert pinned to the wrong phone:** `ConnectionManager` exchanged TLS certs before the supervisor
  switched the active record, so connecting a second phone pinned its cert onto the first — the
  source of 9 spurious "The phone has a new identity (it was reinstalled)" log lines. The record is
  now switched as soon as `ro.serialno` is read.

### Fixed — 2026-09-25 — preemptive failover and a stale standby link

- The hotspot health loop redialled a dead endpoint forever after the phone changed networks (every
  25-40 s for as long as the app ran). It now retires after 6 failed re-resolves
  (`HotspotBackoff.RetireAfter`), reopens the race and sends `adb.arm` so the phone re-announces
  its current address. A loop that dies no longer leaves `LinkedEndpoint` naming a dead link.
- Recovery after a drop now tries the verified hotspot standby first, then `LastHostPort`, so a cable
  pull fails over without waiting for an mDNS advert; the first failed health probe starts a
  discovery scan straight away; a failed upgrade to a better transport falls back to what is still up
  instead of stranding the link in Searching.


### Fixed — 2026-09-25 — the reverse mirror over Wi-Fi, round 2 (D-066)

The 2026-09-20 fix dropped frames to stay live, but a real session showed it looping: 184 drops in
18 bursts, 11 frames every ~0.37 s, because nothing reduced what the encoder sent.

- **The encoder no longer pads.** Its default (and our CBR) sent **7.2 Mb/s for an idle screen**, half
  of it filler NALs, saturating Wi-Fi full-time. Now peak-constrained VBR with a QP floor of 20: idle
  costs **0.55 Mb/s**.
- **Congestion control.** The sender now adapts the encoder's bit rate to the link (AIMD on skip ratio
  and send delay; the AMD MFT honours live changes) and **skips frames before encoding** instead of
  dropping encoded ones, so no reference chain breaks and no IDR is needed. Simulated 2.5 Mb/s link:
  0 drops, 2.09 Mb/s delivered. IDRs rationed to 1/s.
- **No hidden buffering.** pc-video socket send buffer capped at 64 KB so congestion is visible within
  a frame or two; the phone holds a low-latency Wi-Fi lock while PC Remote is on screen.
- Frame PTS is wall-clock. A capture-loop path that could leak an encoder input slot is closed.
- `mirrorsim`: 14 scenarios (idle cost, live bit-rate change, convergence on a throttled link) with a
  noise window for motion.

### Fixed — 2026-09-20 — the reverse mirror over Wi-Fi (protocol v19)

- **The encoder no longer writes to the socket.** Frames used to be written from the MFT pump
  thread, so a slow link blocked the one thread that drains the encoder's event queue: capture
  stalled behind it, the picture froze, and when the link recovered a backlog of stale frames
  arrived at once and the mirror stayed seconds behind. New `PcMirrorSender` owns a writer
  thread and a small queue; `Enqueue` never blocks, and a backlog past 10 frames / 2.5 MB is
  **discarded** with a keyframe requested, because a mirror is a live view and a late frame is
  worth nothing.
- **Recovery after loss, both ends (v19 `pc.mirror.keyframe`).** The phone asks the PC for an
  IDR when its decoder loses sync and discards frames until one arrives, instead of feeding a
  decoder pictures it cannot resolve; the encoder now also runs a **two-second GOP, CBR and one
  reference frame** (`ICodecAPI`), which bounds recovery even against a v18 desktop.
- **The phone's decoder no longer drops packets silently.** `MirrorReceiver.feed` gave up after
  one non-blocking attempt; it now waits, reports a genuine loss so the stream resynchronises,
  and **rebuilds a faulted MediaCodec** rather than leaving the mirror black for the session.
- **No more start storm.** The Mirror screen re-asked every 3 s while a start — which takes
  longer than that over Wi-Fi — was still running, and each retry tore the half-built stream
  down. Retries now back off (5 s → 20 s) and the desktop ignores a start that duplicates what
  it is already streaming or one already in flight.
- **`TCP_NODELAY` on every Linc socket**, both ends. Nagle plus the peer's delayed ACK showed up
  as a mirror hitching in ~40 ms steps on an idle network. Video packets are also written as one
  buffer instead of header-then-payload, and the phone's dialled channels read through a
  `BufferedInputStream`.
- **A failed channel dial-back no longer leaks a hardware encoder session.** It is the common
  Wi-Fi failure, and every attempt left the capture and encoder built, so each retry was slower
  than the last.
- `mirrorsim` gains a scenario driving `PcMirrorSender` against a stalled link: 11 scenarios green.

### Added — 2026-08-12 reconciliation (M9a → M14, written in one pass by the master)

> _Planner note: this file drifted from 2026-07-31 to 2026-08-12 while `BRAIN.md`, `STATUS.md`,
> `PROTOCOL.md` and the ledger were kept current every session. Recovered below from those sources and
> from `Vibe/agent/reports/`. Entries are grouped by milestone, newest first._

- 2026-08-12 — **M14: black-and-white default + the new Linc mark, both apps.** One geometry, two icon
  variants: an **opaque square** for the app/store icon and a **transparent-foreground mono** variant for
  the Windows tray and the Android adaptive foreground — the source PNG has no alpha, and a pre-baked
  black square loses adaptive behaviour. `SCRCPY/Linc.scrcpy/bin/linc.ico` replaced so the mirror window
  matches; `ToolLocator`'s lookup order and six-level depth untouched. Neutral monochrome palette with a
  stated tonal ramp and contrast ratios. **`ContentOpacity` stays `1.0`** — D-032: offline is full
  brightness plus one banner, never dimming. Phone section now owns the single connection statement.
- 2026-08-11 — **M13f: the defects the live hotspot test exposed.** `TransportRank.Choose` makes rank
  yield to liveness, so a **stale USB entry can no longer pre-empt a live link** (diagnosed as the adb
  server's `offline` entries being treated as truth, compounded by rank ignoring liveness).
  `HotspotConnect` now reads **`ro.serialno` via `adb shell`** instead of `get-serialno`, which over TCP
  returns `ip:port` and made the identity check hollow. `TlsTransportService` re-enumerates on
  `NetworkAddressChanged` so it stops advertising a stale LAN address and the VPN instead of the live
  hotspot. `HotspotEndpoint.Build` kills the `ip:port:port` malformation. Realized-tile counter tracks a
  live set. Home: one connection statement; the ghost behind the notification buttons was the M02 offline
  banner sharing the tab strip's grid row; Sections/Swap sides moved behind a 3-dots flyout.
- 2026-08-11 — **M13a–M13e: hotspot ADB link-up, protocol v18.** **Discovery is deleted on a hotspot
  link** (D-062): the desktop reads its own control socket's peer address, and the phone announces only
  the port. Role rule (`gateway == my address or absent → AP/listen, else client/dial`), same-subnet
  filter, link-local-IPv6 → IPv4 preference. `adb.announce`/`adb.down`/`adb.arm`/`adb.ack` with a
  monotonic `gen` counter (**`>=`**, so a same-epoch re-announce after promotion is not dropped) and a
  mandatory phone-side loopback liveness gate. `serial` is optional — an unprivileged app cannot read it —
  so identity is verified against `PairedSerial`. `prefer` is advisory: a non-root app cannot arm legacy
  tcpip, so the desktop promotes with `adb tcpip 5555` itself. `tools/hotspotprobe` + `tools/hotspotsim`.
  **Owner-measured: 514 ms end to end, phone-AP direction.**
- 2026-08-10 — **M12i: publish staleness guard + `canSleep`.** `packagesim` check 10 fails when the
  publish output is older than the newest desktop source — it had been passing green against an
  August-7 publish missing `System.Management.dll`. `canSleep` now uses `GetPwrCapabilities` (S3 **or**
  Modern Standby); `IsPwrSuspendAllowed()` knows only legacy S3 and reported `false` on a machine where
  sleep works.
- 2026-08-09 — **M4b: protocol v17, phone→PC quick controls.** `pc.control` (request/reply, action enum)
  + `pc.state.get`/`pc.state`. **Destructive actions require `confirm: true` on the wire** as well as a
  phone-side dialog. PC brightness is WMI-only; `canBrightness`/`canSleep`/`canShutdown` let the phone
  **disable, never hide**. Both version constants moved in one session (M6c's pattern).
- 2026-08-09 — **M4a: phone Tools page — remote keyboard + trackpad, NO protocol change.** `pc.input`
  (v14) already carried `key`/`text`/`move`/`scroll` with relative `mode:"trackpad"`, so the wire stayed
  at v16. `KeyboardMapping`/`KeyboardSink`/`TrackpadGestures` lifted into shared testable objects — the
  mirror and Tools now provably share one mapping.
- 2026-08-07 — **M12d–M12h: packaging, tray, startup, D-057 completed.** The `SCRCPY/Custom` →
  `SCRCPY/Linc.scrcpy` rename followed through (the stale glob had been shipping a package with no
  scrcpy and no adb); tray icon moved off an `ms-appx` URI in an unpackaged app; opt-in **start on
  sign-in** via the HKCU Run key; `tools/cleanroomsim`; scrcpy payload moved `Content` → `None` to stop
  `makepri.exe` choking on the dot in `Linc.scrcpy`; and **D-057 finished** — `DeviceCacheService`,
  `LogService`, `SyncEngine` and `TlsTransportService` all take `IDeviceRegistry.RootPath`, with a
  `--data-root` switch, because **`Environment.GetFolderPath` ignores the `LOCALAPPDATA` env var**.
- 2026-08-06 — **M9e/M9f/M10/M11/M12–M12c.** Startup race on `LincStore.IsAvailable` fixed (three
  features had been silently reading an unready store); `--no-vd-system-decorations` on per-app windows;
  install-APK from the PC + share sheet takes files; audio as an explicit per-device setting; and
  packaging — `.pri` copied into `publish/`, `ToolLocator.FindAdb()` finally checking Linc's own bundle,
  and a dev-tree depth wrong since M3.
- 2026-08-02 → 08-06 — **M9a–M9d: SQLite store, notification history (opt-in, default off), the offline
  outbox, and cache-first Home + Sync.**

### Added
- 2026-07-31 — **M5c-2 + M5c-3: desktop side of v15 display control — M5c is code-complete (opencode).**
  `ProtocolConstants.Version` 14 → **15** (the switch that activates the feature end-to-end), the two
  new message types, `CompanionClient.SetDisplayRotationAsync` / `SetDisplayBrightnessAsync` built on
  pure-static `DisplayPayload` helpers, three nullable v15 fields on `DeviceStatus` (**absent means
  unknown — the UI says so rather than guessing**), `DisplayControlViewModel` with a 250 ms brightness
  debounce and **private setters** so a `status` seed cannot originate a send, and a third "Display"
  section in the Device page's Tools card that **disables rather than hides** when the peer negotiates
  below 15. Harness `tools/displaysim`.
  **M5c-2 shipped the UI dead and M5c-3 fixed it:** the rotation `ComboBox` and brightness
  `ToggleSwitch` were bound `Mode=OneWay` with no handler, so their commands were invoked nowhere;
  and the `Slider` paired a `OneWay` `Value` with `ValueChanged`, echoing every status seed back to
  the phone. All three now use one stateless guard — **send only when the control's new value differs
  from the view model's current value** — chosen over a suppression flag because it has no ordering
  fragility between a seed and a user action. `displaysim` gained 7 crude text checks that read the
  view files and fail if a control is unwired or a guard removed, proven by deliberately breaking one.
- 2026-07-30 — **M5c-1: phone side of protocol v15 — display control from the PC (opencode).**
  `PROTOCOL_VERSION` 14 → 15 with `display.rotation.set` / `display.brightness.set` in `Protocol.kt`;
  new `service/DisplayControl.kt` as the single place that touches `Settings.System` for orientation
  and brightness, with the 0–100 ↔ platform scaling kept in pure companion functions so tests reach
  them without a `Context`; two `SocketServer` branches gated on `negotiated >= 15` following the
  `dnd.set` shape (`internal` for a malformed payload, `not-granted` when `Settings.System.canWrite`
  is false, `SecurityException` swallowed); `rotationMode` / `brightnessAuto` / `brightnessLevel`
  added to `status` at ≥ 15 only; `WRITE_SETTINGS` declared in the manifest; 18 new unit tests
  (38 total, all green). **Nothing sends these yet — the desktop is M5c-2, so behaviour is unchanged.**
  **The version bump is safe against the still-v14 desktop:** the phone negotiates
  `minOf(maxV, PROTOCOL_VERSION)`, so a v14 desktop still lands on 14 and never sees the new fields.
  Brightness maximum taken as **255** (the documented `SCREEN_BRIGHTNESS` clamp) because
  `PowerManager`'s per-panel constants are `@SystemApi @hide` and off the public SDK surface;
  scaling rounds to nearest so 0 and 100 round-trip exactly.
  **Two defects found by the planner, fixed at the head of M5c-2:** `USER_ROTATION` values **2** and
  **3** (reversed portrait / reversed landscape) fall into an "anything else → portrait" branch, so a
  phone left in reversed landscape reports `portrait`; and `setRotation("auto")` also writes
  `USER_ROTATION = 0`, which **destroys** the user's last forced orientation instead of preserving it.
- 2026-07-29 — **M5b: editable, per-device screen-mirroring settings (opencode).** New dep-free
  `Services/MirrorSettings.cs` record (`MaxSize`, `VideoBitRate`, `MaxFps`, `Crop`, `StayAwake`,
  `TurnScreenOff`, `ShowTouches`) with a pure static `ValidateSettingsFields`; persisted as
  `KnownDevice.Mirror` (appended last, so old `settings.json` still deserialize) with
  `IDeviceRegistry.Mirror` / `MirrorChanged` / `SaveMirror`. `MirrorService.StartAsync` now takes a
  `MirrorSettings` instead of a `MirrorPreset` and builds its args through a new pure static
  `BuildScrcpyArgs`, which the new `tools/mirrorsettingssim` harness (13 checks, green) uses to prove
  a never-touched device still emits the historical Balanced args byte-for-byte. New
  `MirrorSettingsViewModel` + a collapsed "Mirror settings" expander in the Tools card. **Per D-054
  the preset combo is now only a shortcut that writes into `MirrorSettings`** — one source of truth —
  and `HomeViewModel`'s quick-action reads the same record. M5a's duplicate inline `TitleText` labels
  ("Screen", "Desktop Mode") were deleted and the surrounding `Grid.Column` indices tightened
  (4 columns → 3, and 3 → 2) so nothing shifted.
  **Two defects found by the planner afterwards, fixed at the head of M5c:** (1) `tools/devicesim`
  and `tools/blescan` compile `DeviceRegistry.cs` verbatim but were never given the new
  `MirrorSettings.cs` (nor, since M8a, `DesktopModeSettings.cs`) — **both harnesses had stopped
  compiling**; (2) `Save()` fires `MirrorChanged`, whose handler re-runs `Load()`, which clears the
  "Settings saved" confirmation the user is meant to see.
- 2026-07-29 — **M5a: Device page gets a scroll host + a single "Tools" card (opencode/GLM).**
  `Views/DevicePage.xaml` root `<Grid Padding="32">` is now wrapped in a `<ScrollViewer>` with
  `VerticalScrollMode/BarVisibility="Auto"` and horizontal scrolling disabled; the `Padding="32"`
  moved onto the inner `Grid` so the scrollbar sits at the true window edge. **This fixes the
  pre-existing bug the M8f check exposed:** the page had no scroll host at all, so the expanded
  "Desktop Mode settings" expander ran past the window bottom and could not be reached. The
  Screen-mirroring and Desktop-Mode blocks were then **moved** (markup unaltered) out of their two
  separate `ExpressiveCard` borders into one `ExpressiveCard Padding="20,16"` "Tools" card holding a
  `StackPanel Spacing="16"`, with a "Tools" heading, two section sub-headings and a 1px
  `OutlineVariantBrush` divider between them. No protocol, no view-model, no behaviour change; build
  0 errors; `tools/desktopsim` 7/7 with the Pixel 7 attached. **Known cosmetic follow-up (folded into
  M5b):** the moved blocks kept their own inline `TitleText` labels ("Screen", "Desktop Mode"), so the
  card now shows the card heading, the section heading and the inline label all at `TitleText` size.
- 2026-07-28 — **M8 Desktop Mode: DONE, shipping as a SECOND SCREEN (Antigravity → opencode/GLM).**
  Backfilled here as one entry (a/b/c by Antigravity, d/e/f by the opencode agent). Desktop-side only,
  no protocol bump. New `DesktopModeSettings` (per-device on `KnownDevice`), `DesktopModeService`
  (idempotent first-run ADB setup + reboot handling), `DesktopLaunchService` (owns the scrcpy process:
  `--new-display=WxH/DPI --mouse=uhid --keyboard=uhid --stay-awake --max-fps=30 --video-bit-rate=8M
  --no-audio`), `DesktopModeViewModel` + `DesktopModeSettingsViewModel`, a collapsible settings
  expander on `DevicePage`, and `tools/desktopsim` (7 checks). Geometry caps on a **pixel budget** of
  1,440,000 preserving aspect end-to-end, 640 floor, even dimensions, `DPI = round(160 * height / 900)`
  clamped 120–320 → Pixel 7: MatchMonitor 1600x900/160, MatchPhone 804x1788/318. **The windowing half
  of the spec was dropped (D-053)** — Android 17 on the Pixel 7 doesn't declare
  `android.software.freeform_window_management`, so the virtual display is `mWindowingMode=fullscreen`
  and apps run fullscreen; M8f then deleted the hollow `ApplyWindowingModeAsync`, removed the dead UI
  controls and added an honest "second screen" caption. Real per-app windowing moves to **M7**.
- 2026-07-23 — **M3.5b-2c-2: fixed the invisible control-bar buttons (planner-applied, awaiting rebuild).**
  The agent re-ran the original b-2c prompt (identical report, no net change), so the planner edited the
  source directly: `control_bar.c` draws via `SDL_GetRendererOutputSize` + neutralizes logical size;
  `sc_control_bar_handle_mouse` now takes pre-scaled pixel coords; `screen.c` scales SDL mouse points→
  drawable pixels using `screen->display.renderer`. All coordinate spaces (window.c `GetClientRect`,
  overlay draw, overlay hit-test) now agree in physical pixels, so buttons render/interact at the true
  top-right on HiDPI. Files: `control_bar.{c,h}`, `screen.c`. **Needs an owner rebuild + hands-on test.**
- 2026-07-23 — **M3.5b-2c: hover control bar (min/max/close) — implemented, has a coordinate bug (Antigravity).**
  New portable `control_bar.{c,h}` component (SDL-drawn buttons: minimize/maximize-restore/close), drawn
  in `sc_display_render` before `SDL_RenderPresent`, mouse intercepted in `sc_screen_handle_event` before
  `sc_input_manager_handle_event` (protocol/input path untouched), and `window.c` `WM_NCHITTEST` returns
  `HTCLIENT` over the top-right cluster so SDL receives the clicks. Built clean. **BUG (owner: no buttons
  on hover):** the overlay uses `SDL_GetWindowSize` (logical *points*) while the SDL renderer draws in
  *drawable pixels*, and the window has `SDL_WINDOW_ALLOW_HIGHDPI` — so on a scaled display the buttons
  render mid-top-left at the wrong size instead of the top-right corner, effectively invisible. Fix =
  **b-2c-2**: draw + hit-test in drawable pixels (`SDL_GetRendererOutputSize`, scale mouse points→pixels)
  to match window.c's physical-pixel hit-test. `SCRCPY/Default` untouched.
- 2026-07-23 — **M3.5b-2b: restored resize + maximize on the custom window + rounder corners (Antigravity).**
  Fixed the b-2a regression (frameless window couldn't resize/maximize). In `sys/win/window.{c,h}`:
  re-added `WS_THICKFRAME | WS_MAXIMIZEBOX` (via `SetWindowLongPtr` + `SWP_FRAMECHANGED`) while still
  returning 0 from `WM_NCCALCSIZE`; `hit_resize()` returns the 8 `HT*` edge/corner codes for a 6px
  inset border (`SC_WIN_RESIZE_BORDER`) evaluated before the caption check; `WM_GETMINMAXINFO` clamps
  maximize to the monitor work area (`rcWork`); `apply_rounded_region` drops the region when `IsZoomed`
  (square corners maximized). Corner radius 24→32. Built portable via MSYS2; `--list-encoders` clean;
  **owner-verified: edge/corner resize + maximize now work** (double-click strip / Win+↑). See D-051.
  Remaining: **b-2c** hover control bar (min/max/close buttons). `SCRCPY/Default` untouched.
- 2026-07-23 — **M3.5b-2 (first cut): frameless + rounded + draggable custom scrcpy window (Antigravity).**
  New Windows-only module `SCRCPY/Custom/src/app/src/sys/win/window.{c,h}`, invoked from `screen.c`
  after `SDL_CreateWindow` (`#ifdef _WIN32`); `meson.build` adds `window.c` + links `dwmapi`.
  Subclasses SDL's `WndProc` (`SetWindowLongPtr(GWLP_WNDPROC)`, old proc chained): `WM_NCCALCSIZE`→0
  (no title bar), `WM_NCHITTEST` makes the top `SC_WIN_CAPTION_H`(28)px a drag zone, `SetWindowRgn`
  rounded corners (`SC_WIN_CORNER_RADIUS`, re-applied on `WM_SIZE`), DWM drop shadow via
  `DwmExtendFrameIntoClientArea`. Built MSBuild x64; `--list-encoders` clean; owner confirmed corners
  + drag. **Follow-ups → b-2b:** (1) **regression** — window can't resize/maximize (frameless dropped
  `WS_THICKFRAME`); (2) owner wants a slightly larger corner radius. Hover-reveal control buttons split
  to **b-2c**. See `SCRCPY-WINDOW-SPEC.md` and D-051.
- 2026-07-23 — **M3.5b-1: Linc ships its OWN from-source scrcpy, standalone (Antigravity).** Made
  `SCRCPY/Custom/src` an unmodified editable source copy (git-untracked; only `src/x/` build output
  gitignored), built it via the proven MSYS2/MINGW64 recipe, and replaced M3's official prebuilt
  binaries in `SCRCPY/Custom/bin/` with our build + its **98 mingw64 dependency DLLs** (resolved by
  `ldd`, copied from `C:\msys64\mingw64\bin`). csproj Content glob narrowed `../../SCRCPY/Custom/**/*`
  → `../../SCRCPY/Custom/bin/**/*` so the source tree isn't copied into build output; `ToolLocator`
  now also checks `SCRCPY/Custom/bin/linc.ico`. Purged the stray `adb.exe.bak` (M3 loose end) plus
  scrcpy's `open_a_terminal_here.bat` / `scrcpy-console.bat` / `scrcpy-noconsole.vbs` / `icon.png`.
  **Fix (D-050):** the initial build lacked `-Dportable=true`, so scrcpy resolved `scrcpy-server` at
  the MSYS2 prefix (`C:/msys64/mingw64/share/scrcpy/scrcpy-server`) and failed on a clean machine with
  *"does not exist … Server connection failed"*. Rebuilt **portable**; verified non-intrusively with
  `scrcpy.exe --list-encoders` (starts the server, no mirror window) and owner-confirmed live mirror.
- 2026-07-23 — **M3.5a: the from-source scrcpy Windows build is proven (gate PASSED; Antigravity).**
  Built the *unmodified* scrcpy client from the `SCRCPY/Default` submodule via MSYS2/MINGW64
  (client-only, using the bundled prebuilt server, so no Java/Android SDK); `./run x --version`
  reported scrcpy 3.3.4. `SCRCPY/Default` kept pristine (git clean, no commits). Two snags resolved
  in-bounds: the `pkg-config`/`pkgconf` conflict, and `-Dprebuilt_server` needing a **relative** path.
  The full working recipe + the DLL-version nuance (the build links avcodec-62/SDL 2.32, newer than
  M3's avcodec-61 release DLLs) are recorded in `SCRCPY-WINDOW-SPEC.md`. This unblocks M3.5b (the
  actual window work).
- 2026-07-23 — **M3: scrcpy vendored + bundled + the mirror window branded (D-041 revised; Antigravity).**
  `Genymobile/scrcpy` added as a git submodule at `SCRCPY/Default` (pinned **v3.3.4**) for upstream
  tracking; the official prebuilt Windows binaries (scrcpy.exe, scrcpy-server, SDL2/FFmpeg/libusb DLLs,
  adb) bundled under `SCRCPY/Custom/bin/` and shipped with the desktop via `.csproj` Content links;
  `SCRCPY/NOTICE.md` + a Linc `.ico` added. `ToolLocator`/`MirrorService` now prefer the bundled
  scrcpy and launch it with `--window-borderless --window-title "Linc"` + `SCRCPY_ICON_PATH` — a
  **frameless, Linc-branded** mirror with **no scrcpy source edited or built** (interim look; M3.5
  supersedes it). Both apps build clean; bundled `scrcpy.exe --version` = 3.3.4; files confirmed in the
  build output. *(No protocol change.)* Loose ends: a stray `adb.exe.bak` sits in the bundle;
  `mirrorsim` couldn't run in Antigravity's environment (display-dependent — not an M3 regression); the
  frameless-look **manual UI check is owed to the owner**.

### Changed
- 2026-07-23 — **M2c: the onboarding wizard is now the app's own full-window UI** (owner feedback;
  first Antigravity session). The first-run wizard was a centered `ContentDialog`; it's now a
  **full-window in-app overlay** — new `Views/OnboardingView.xaml` (UserControl) hosts the step UI
  styled with `MaterialExpressive` tokens to match `SettingsPage`, layered over the whole
  `MainWindow` root grid and shown/hidden via an `IsOnboarding` flag on `AppShellViewModel`;
  `MainWindow` / `HomePage` open the overlay instead of the dialog; **`OnboardingDialog` removed**.
  `OnboardingViewModel` step logic unchanged. Desktop + Android build clean; `apkprobe` /
  `onboardsim` / `devicesim` green; **owner-checked, looks good.** *(No protocol change.)* Cleanup
  owed: it left `task.md` / `walkthrough.md` / `verify-ui.ps1` scratch files in the repo root.

### Added
- 2026-07-23 — **M2b: guided first-run onboarding + the desktop carries the companion APK.** A
  stepped onboarding wizard (`OnboardingDialog` / `OnboardingViewModel`) — Intro → enable USB +
  wireless debugging → detect over ADB → install the bundled companion → "You're all set" — opened
  from the tab-strip `+` and reachable from the zero-device first-run state. The desktop now
  **bundles the companion APK and injects it** during onboarding (no manual APK handling).
  `apkprobe` / `onboardsim` harnesses added and green. *(No protocol change.)* **Owner feedback: the
  wizard is a centered `ContentDialog`; it should use the app's own UI, full-screen — queued as M2c.**

### Fixed
- 2026-07-23 — **M2b: onboarding crash + false-success, both fixed.** The wizard crashed the app on
  open — `OnboardingDialog.xaml` bound a **string** (`UsbStatusText`) straight to `Visibility`, and
  WinUI `x:Bind` implicitly converts `bool`→`Visibility` but **not** `string`, so realizing the
  dialog threw `E_INVALIDARG` (`0xc000027b` STOWED_EXCEPTION in `Microsoft.UI.Xaml.dll`). Fixed with a
  bool `HasUsbStatus`. Also removed a false-success shortcut where `OnUsbDeviceSeen` treated an
  already-connected phone as a completed pairing and jumped straight to "You're all set"; success now
  comes only from a genuine `StateChanged → Connected` transition. Verified via UI-Automation
  InvokePattern (no cursor moved, phone untouched); builds + harnesses green.
- 2026-07-22 — **M2a: device management actually works, and the Files page reaches the whole
  phone.** M1 made the tab strip visible but its actions were never clicked; hands-on testing found
  `+`, close-tab, and switching all did nothing. Root cause of the dead `+`: pairing was gated by
  `IsDisconnected`, so the QR/code card was hidden whenever a phone was connected. Fixes:
  `DeviceViewModel.ShowPairing => PairingActive` (+ `ShowDisconnectedArea`/`ShowConnectedArea`), so
  pairing shows over a live link; `MainWindow.xaml.cs` `+` navigates to the Device page and runs
  `StartPairingCommand`; `DeviceTabsViewModel.CloseTab` dropped the `Count<=1` guard (closing the
  last tab hides it; the phone stays paired per D-033; closing the active tab switches to a
  neighbour first). Files page: `UpAsync` climbs to `/`, a denied directory degrades gracefully with
  a plain-language note (a shell `ls` probe surfaces `Permission denied`, which the sync protocol
  otherwise returns as an empty list), and downloads now land in **`Downloads/Linc`**. Verified with
  real DPI-aware clicks on the Pixel 7 + `devicesim`/`filesim` green.
- 2026-07-22 — **M1: the desktop Files page lists the phone's directory again.** Root cause: the
  Files load was edge-triggered on the supervisor's `StateChanged → Connected` transition, but
  `FilesViewModel` is created lazily on first navigation to the page — after `AppShellViewModel`
  has already connected at launch — so that transition had already fired and been missed, leaving
  the listing blank. Fix (`FilesViewModel.cs`): also load once in the constructor when the link is
  already `Connected`. Verified via UI Automation against the Pixel 7: opening Files while already
  connected now populates the listing. Added **`tools/filesim`**, a regression compiling the real
  `TlsFileService`/`FileServiceRouter` + `Framing` — covers the Direct-TLS files channel and the
  router's `HasAdb` path selection.

### Changed
- 2026-07-23 — **M2a: Settings › Devices management + a Files "Go to" menu.** A new Settings
  "Devices" card lists every paired phone with **Set active** (which also un-hides a closed tab) and
  **Forget** (clears that device's cache + unpairs). The Files page's single-select storage dropdown
  became a **"Go to"** menu (Phone root `/`, Internal storage, SD cards) rebuilt live on open so
  every pick navigates. `tools/devicesim` gained a close-last-tab scenario. *(No protocol change.)*
- 2026-07-22 — **M1: the device tab strip is always visible once a phone is paired.**
  `DeviceTabsViewModel.IsStripVisible` went from `Tabs.Count > 1` to `>= 1`, so a single-device
  owner finally sees their tab and the "+" add button (previously the strip hid itself for anyone
  with one phone). Verified via UI Automation: the "Pixel 7" tab renders with one paired device.
- 2026-07-22 — **Documentation reorganized; a fresh roadmap for the next phase.** Added
  **`Documentation/`**, the formal human-facing documentation of the whole project (master overview +
  vision, history, architecture, the Desktop app, the Android app, connectivity & protocol, the
  feature catalog, build/test/deploy, decisions, glossary). Archived the Era 1/2 planning docs
  (BRAIN, CHANGELOG, ROADMAP, FEATURES, PIPELINE, EXTEND-MODE, DESCRIPTION, VISION, task.txt) into
  **`v2.docs.old/`** and rewrote `gemini-docs/ROADMAP.md`, `FEATURES.md`, `BRAIN.md`, `CHANGELOG.md`, and
  `task.txt` clean for the new **Convergence** phase. Kept the living technical references
  (`PROTOCOL.md`, `DECISIONS.md`, `TECH_STACK.md`, `THEME.md`, `ARCHITECTURE.md`, `AGENT-GUIDE.md`,
  `CONTRIBUTING.md`) in place. Added `gemini-docs/REQUIREMENTS-ANALYSIS.md` (each new requirement mapped to
  Implemented/Polish/New/Bug and to the code to reuse) and **decisions D-041…D-048** for the new
  work. No source code changed in this pass; the previous session's uncommitted M07 Sync-page split
  remains in the working tree.

### Planned next
- **M2c — redesign the onboarding as the app's own full-screen UI** (owner feedback): replace the
  centered `ContentDialog` with a full-window, in-app-styled first-run experience (Material
  Expressive brushes, same control styles as the other pages); keep the working `OnboardingViewModel`
  step logic. **First Antigravity session.**
- **M3 — vendor scrcpy** (`SCRCPY/Default` + `SCRCPY/Custom`) as the foundation for the
  screen-feature milestones. See `gemini-docs/ROADMAP.md`.
- **Known limitation (M2b):** opening the wizard to add a *second* phone while the first is actively
  connected won't complete the new connect — the supervisor holds a single active link by design
  (D-037). The real first-run path (zero devices → wizard) is unaffected.
- **Known caveat from M2a:** a `devicesim` run (killed mid-way by a piped command before its restore
  step) overwrote the real Pixel 7 pairing with test devices; it was cleaned up and the phone
  re-paired over USB, but the device's **sync-lane / folder-sync config may need re-setting**.
- **Known caveat from M2a:** a `devicesim` run (killed mid-way by a piped command before its restore
  step) overwrote the real Pixel 7 pairing with test devices; it was cleaned up and the phone
  re-paired over USB, but the device's **sync-lane / folder-sync config may need re-setting**.
