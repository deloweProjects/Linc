# Connection logic — what it is, what's wrong, what it becomes

> **2026-10-22: LincLink.** The new connection engine (protocol v32, Settings › Connection › New connection
> engine, on in the LincLink test builds) is designed in [`linclink.md`](linclink.md) and lives inside the
> same `ConnectionSupervisor` described below. What changes with it on: a v32 session survives its transport
> (12 s linger, resume, nothing replayed twice); the PC dials the phone (TCP 46002) as well as the phone
> dialling the PC; a private UDP beacon (46003) finds the phone; every route is raced and remembered per
> network; ADB reaching the phone with its app not answering is *connected* while the app is started; the
> cable is seen through adb's device list, not only the 3 s poll. With it off, everything below is exactly
> how Linc connects. This file describes that old path (D-081) and stays the reference for it.

_Written 2026-10-11 for 1.0.0.11 (D-081). Protocol v23; nothing here changes the wire._

The owner's bar: "it just works". The phone and the PC find each other on their own, the best
link wins, and a better or worse link coming and going never shows as a drop.

---

## 1. Inventory (as of 1.0.0.10)

### 1.1 Transports

| Transport | How it comes up | Who dials | Carries |
|---|---|---|---|
| **USB ADB** | `UsbWatcherService` polls `adb devices` every 3 s; a USB serial in `device` state → `ConnectionSupervisor.OnUsbDeviceSeen` → `ConnectionManager.ConnectToUsbDeviceAsync` (adb forward to the phone's `localabstract` socket) | PC | control + bulk/files channels (forward), `adb reverse` 46011→46001 so the phone can dial TLS channels over the cable |
| **Wireless ADB — mDNS** | `DiscoveryService` resolves `_adb-tls-connect._tcp` every 3 s (2 s scan) → `OnConnectServiceSeen` → `ConnectAsync(ip:port)` | PC | same as USB, over adbd's TLS port |
| **Wireless ADB — last host:port** | `RecoverPresentWirelessDeviceAsync`: if the adb server still lists `registry.LastHostPort` (or the hotspot standby) online, connect to it. 6 tries, 1 s apart, single-flighted | PC | same |
| **Wireless ADB — hotspot announce (v18)** | phone sends `adb.announce {gen, port, addrs, verified}` on every control handshake; `HotspotLinkService` races an "announced" and a "speculative :5555" branch, verifies `ro.serialno`, `adb tcpip 5555` promotion, then a standby **health loop** (poll 4 s; re-resolve with backoff 250 ms ×2 … 15 s cap; retire after 6) | PC | a *standby*: an adb endpoint kept alive beside whatever carries control. Feeds `RecoveryCandidates()` |
| **Wireless ADB — fixed port** | `HotspotPromotion` (`adb tcpip 5555`), skipped while control rides wireless ADB (D-078) | PC | makes the standby's port predictable |
| **Direct TLS (v9)** | PC: `TlsTransportService` listens on 46001, advertises `_linc._tcp` with an `addrs` TXT (re-advertised on address change, 2 s cooldown). Phone: `PresenceClient` dials (reverse port 127.0.0.1:46011 first, then advertised addresses), mutual TLS with pinned certs; PC `OnTlsControlArrived` adopts only when idle | **phone** | control; channels by **dial-back** (`channel.open` → phone races TCP probes to every candidate (`EndpointRace`, D-072), TLS to the first) |
| **BLE hint (M04)** | phone advertises a rotating id (300 s); PC `BlePresenceService` → `OnBleSighting` → `discovery.ScanNow()` while Searching | — | nothing; a "it's nearby" hint |
| **NFC tap** | tag `linc://tap` → `NfcTapActivity` starts the service and `ReconnectSignal.poke("NFC tap")` → `PresenceClient` skips its backoff | phone | a hint to the phone's dialler |

### 1.2 Triggers, and who listens to them

| Trigger | PC | Phone |
|---|---|---|
| Cable in | 3 s USB poll → connect (or switch from wireless, **dropping** it first) | — |
| Cable out | 2 missed polls → `OnUsbDeviceGone` → `OnLinkDropped` → Searching, `ScanNow` + recover wireless | control socket dies → `Listening`; presence loop waits out its delay (≤ 3 s, more after misses) |
| Wi-Fi change | `NetworkAddressChanged` → probe if connected / recover if searching; TLS re-advert | `ConnectionWatch` **logs only** ("Wi-Fi connected") — no dial |
| Screen on | — | **logs only** |
| NFC tap | — | poke |
| BLE sighting | `ScanNow` while searching | — |
| App start | `Start()` | service start; **no poke** |
| PC sleep/wake | `SystemEvents.PowerModeChanged(Resume)` → probe + recover. **Never fired on this PC** (0 log lines in all logs): it is Modern Standby (S0 low-power idle), which doesn't deliver it to desktop apps | — |
| Health probe fails | first failure: `ScanNow`; third (≈ 6 s later): drop | — |
| Quick Share from the phone | `NudgeReconnect` | — |
| Preference change | drop if forbidden, rescan | — |

### 1.3 The current state machine (PC, `ConnectionSupervisor`)

```
                 pair / ExpectNextDevice
   ┌──────────┐ ───────────────────────► ┌───────────┐  advert / USB poll / TLS dial-in /
   │ NoDevice │                          │ Searching │  recover-wireless  (ShouldEngage)
   └──────────┘ ◄─── active phone cleared└───────────┘ ─────────────────────────┐
                                           ▲    ▲   ▲                           ▼
                           connect failed  │    │   │                    ┌────────────┐
                    (backoff 2,5,15,30,60) └────┼───┼────────────────────│ Connecting │
                                                │   │                    └────────────┘
                 health 3×fail / USB gone /     │   │ success                 │
                 probe after resume or network  │   └──────────┐              ▼
                                                │              │       ┌───────────┐
                                                └──────────────┼────── │ Connected │──┐ better transport
                                                               │       └───────────┘  │ (USB > ADB-Wi-Fi > TLS):
                                                RequestDisconnect        ▲            │ StopHealthLoop,
                                                ┌────────┐               └────────────┘ Connecting, Disconnect(),
                                                │ Paused │ ◄──                          connect; on failure
                                                └────────┘                              FallBackIfSwitchFailed
```

Every connect path starts with `ConnectionManager.Disconnect()`: **a switch is break-before-make**.
Upgrading Wi-Fi → USB is a visible drop (`Connecting`), and if the USB connect fails the user is left
with nothing until `FallBackIfSwitchFailed` re-finds Wi-Fi.

The phone's side is simpler: `SocketServer` serves any control connection (ADB-forwarded or
TLS-dialled) and publishes `Connected`/`Listening`; `PresenceClient` dials TLS only while
`Listening`.

### 1.4 Every timer and backoff

| Where | What | Value |
|---|---|---|
| `ConnectionSupervisor` | health probe interval | 15 s |
| | failures to declare a drop / re-probe gap | 3 / 3 s (detection 6–21 s) |
| | per-address connect backoff | 2, 5, 15, 30, 60 s (never forgets an address) |
| | wireless recovery | 6 rounds × 1 s |
| | resume probe timeout | 5 s |
| `CompanionClient` | reply timeout | 10 s |
| `UsbWatcherService` | poll / misses to declare gone | 3 s / 2 |
| `DiscoveryService` | mDNS scan / idle | 2 s / 3 s (woken by `ScanNow`) |
| `TlsTransportService` | TLS handshake / dial-back header wait / re-advert cooldown | 5 s / 3 s / 2 s |
| `ConnectionManager` | dial-back wait / companion start settle | 8 s / 2 s |
| `HotspotLinkService` | standby poll | 4 s |
| | re-resolve backoff | 250 ms doubling to 15 s cap; retire after 6 |
| | arm fallback window / redial min interval | 5 s / 3 s |
| `AdbProcess` | any adb CLI call | 20 s |
| `BlePresenceService` / advertiser | sighting log throttle / id rotation | 60 s / 300 s |
| Phone `PresenceClient` | idle loop | 3 s, doubling to 30 s after misses; a poke resets to 3 s |
| | TLS dial timeout | 4 s (channel race 4.5 s) |
| | "a link under 3 s is a miss" | 3 s |
| Phone `AdbAnnouncer` | loopback liveness / NSD budget | 200 ms / 400 ms |

### 1.5 Where it is duplicated, contradictory, or loops

Measured from the owner's PC logs (2026-09-25 … 2026-10-11, 7 days with entries):

1. **The hotspot standby redials a dead address for minutes.** When the phone leaves, the
   standby's health loop keeps going — independently of the supervisor, which already knows the
   phone is gone. Each redial is `adb disconnect` + `adb connect` that hangs to the 20 s adb CLI
   limit ("A task was canceled"), then backoff 250 ms … 8 s, six times: **~2.5 minutes and 12 log
   lines per drop** (10 Oct 02:55:19 → 02:57:53), ending with an `adb.arm` to a phone that isn't
   connected ("Not sending 'adb.arm' to the phone: no phone connected"). 50 such failures and 52
   "isn't answering" lines on 10 Oct alone.
2. **Switching is break-before-make.** Plugging a cable while Wi-Fi is live drops the session
   first (Connecting → Connected), restarting every page's loads and any Mirror PC session.
3. **Pulling a cable with Wi-Fi available is always a drop**: the phone only dials TLS while
   `Listening`, and its loop may be sleeping up to 30 s; the PC sits in Searching until an mDNS
   advert or the recovery loop finds wireless ADB. Measured "USB cable gone → Connected again"
   **median 10 s, p90 115 s** (n = 12).
4. **Signals the phone ignores.** Wi-Fi joined and screen on are logged, never acted on; opening
   the app doesn't poke the dialler; a control connection ending doesn't either.
5. **PC wake is never seen.** `PowerModeChanged` never fired (Modern Standby). Recovery after
   standby depended on the health probe noticing and the phone's own redial.
6. **Backoff never resets on a real signal and never forgets.** An address that failed five times
   waits 60 s even right after the cable goes in or Wi-Fi comes back; a stale `LastHostPort`
   (an old DHCP lease, an old ephemeral port) is retried forever.
7. **Four "nudge" paths that each do part of the job**: `NudgeReconnect`, `OnNetworkAddressChanged`,
   `RecoverAfterResumeAsync`, `FallBackIfSwitchFailed`, plus `ScanNow` sprinkled in five places.
8. **Wi-Fi vs Wi-Fi churn.** Ranking ADB-Wi-Fi above Direct TLS means a phone connected over TLS is
   dropped and reconnected as soon as an `_adb-tls-connect` advert appears — two links on the same
   Wi-Fi, same latency, and a visible blip for no gain.
9. **Log floods** (worst minute, 10 Oct): "Phone connected directly (no ADB)" **229×**, "Direct
   connection from the phone failed" **80×** (the D-076 dial-back storm, since fixed at the cause,
   but nothing caps a flood), "LincStore: schema ready" 170× (Part 5).
10. **The phone's state flips to "Listening" when an old control connection ends even if a newer
    one is live** (two transports briefly overlapping), which made a make-before-break impossible.

Before numbers, from the same logs: drop → connected again **median 4 s, p90 184 s** (n = 422, drops
followed by a reconnect within 10 min); cable out → connected **median 10 s, p90 115 s**; PC wake —
no data (never logged).

---

## 2. Target design

### 2.1 One owner, one policy

- `ConnectionSupervisor` stays the **only** owner of "which transport is live" on the PC. The
  hotspot standby, discovery, the USB poll, the TLS listener and BLE only *report*; they never
  connect or drop on their own (the standby keeps an adb endpoint warm and must defer to the
  supervisor about whether anyone is there).
- Every decision moves into a pure, unit-tested `LinkPolicy` (no I/O, injected clock):
  ranking, should-we-engage, backoff, forgetting, signal strength, log limiting.
- Phone: `PresenceClient` stays the only dialler; its decisions move into a pure `ReconnectPolicy`.

### 2.2 Visible states

`NoDevice → Searching ⇄ Connecting → Connected`, plus `Paused`. **`Connecting` is only ever shown
when nothing is connected.** A handoff is internal: State stays `Connected`, `StateChanged` fires
once with the new `Device` (so the transport icon updates), and pages see no disconnect.

### 2.3 Ranking

```
USB (2)  >  Wi-Fi (1: wireless ADB or Direct TLS — whichever answered first)  >  nothing (0)
```

A strictly higher rank takes over by **make-before-break**; equal ranks never switch. The user's
hand-picked transport (M15a) still wins while it answers.

### 2.4 Handoff

- **Better appears** (cable in while on Wi-Fi): build the new link beside the old one
  (`ConnectionManager` no longer calls `Disconnect()` first; it swaps `_companion` only after the new
  handshake), then dispose the old. If the new link fails, nothing visible happens and the old one
  keeps working. The phone keeps every live control connection and sends to the newest
  (`CompanionOutbox`); it stays `Connected` while any is live.
- **Better dies, worse available** (cable out, Wi-Fi up): a **grace window of 12 s** in which State
  stays `Connected`. The supervisor nudges (`CableOut`): adopt wireless ADB if the adb server holds
  it, and the phone — whose control socket just died — pokes its own dialler and dials TLS at once.
  Whichever lands first is adopted as a handoff. Only if nothing lands within the window is it a
  drop ("Lost the connection…").
- **Worse dies while better is up** (standby endpoint stops answering while on USB): one log line,
  nothing visible.

### 2.5 Retries and backoff

- Per endpoint: **1, 2, 5, 10, 20, 30 s** (cap 30 s).
- **A real signal resets every wait**: cable in, Wi-Fi joined / network change, PC resume, BLE
  sighting after absence, the phone dialling in, the user asking (Reconnect / tab switch / app
  start). Weak signals (a periodic advert, a probe hiccup) don't.
- **Forget after 5 failures in a row** (a success resets the count; signals don't): a stale
  `LastHostPort` is cleared, a hotspot standby is retired. The phone forgets a remembered direct
  endpoint the same way.
- The hotspot standby, when no phone is connected, retires after **2** failed re-resolves (not 6),
  and adb calls on that path time out after **6 s** (not 20).

### 2.6 One nudge path

`ConnectionSupervisor.Nudge(LinkSignal signal, string detail)` is the only entry point:

```
Nudge(signal)
  policy.OnSignal(signal)                  // strong → reset waits
  log (rate-limited)
  if Connected and signal ∈ {NetworkChanged, Resume}:   probe now (5 s)
  if Connected and HandingOff:             try Wi-Fi recovery
  if Searching/NoDevice:                   ScanNow + recover wireless (single-flight)
```

Triggers routed to it: cable in/out, network change, resume (PowerModeChanged **and** a clock-gap
detector — a 5 s tick that sees > 20 s pass means the PC was asleep), BLE sighting, Quick Share
sighting, link drop, first failed probe, preference change, retarget, app start.

Phone: `ReconnectSignal.poke` from NFC tap, **Wi-Fi joined, screen on, app opened, control
connection ended**. After a poke the dialler tries at once, then 1 s, 2 s, then its normal 3 → 30 s.
Dial order: USB reverse port → **the last endpoint that worked** → mDNS addresses.

### 2.7 Logs

`LogLimiter`: a message key (digits normalised) is written at most **3 times a minute**; then one
"(repeated N more times in the last minute)" line. Applied to every connection-path line that can
repeat (probe failures, hotspot redials, direct dial-ins, connect failures).

### 2.8 Targets, and how they are measured

`tools/linksim log [file]` reads a PC log and prints each metric; run it on the owner's log after
the test script (§3). `tools/linksim` with no arguments runs the policy's unit checks and scenarios. Targets: same-Wi-Fi return ≤ 5 s; cable in ≤ 3 s with no drop; cable out with Wi-Fi,
no visible drop; PC wake ≤ 10 s; NFC tap → connected ≤ 5 s; no line > 3×/min.

---

## 3. Owner test script (connection)

Have the PC and phone on the same Wi-Fi, Linc open on the PC, phone unlocked.

1. **Cable in**: plug the USB cable in. Within ~3 s the transport icon shows USB; the Home page does
   not flash "Connecting".
2. **Cable out**: unplug it. The icon switches to Wi-Fi within ~12 s and never shows
   "Lost the connection".
3. **Wi-Fi off/on (phone)**: turn the phone's Wi-Fi off for 20 s, then on. Linc shows "Searching",
   then "Connected" within ~5 s of the phone joining Wi-Fi.
4. **Screen off 5 min**: lock the phone, wait 5 minutes, unlock. Still connected, or reconnected
   within ~5 s of the screen coming on.
5. **PC sleep/wake**: sleep the PC (Start → Power → Sleep) for 2 minutes, wake it. Connected within
   ~10 s of the desktop appearing.
6. **NFC tap** (if you have the tag): disconnect from the PC tab (or turn PC Wi-Fi off/on), tap the
   tag. Connected within ~5 s.
7. **Walk away and back**: take the phone out of Wi-Fi range for 2 minutes, come back. Connected
   within ~5 s of the phone rejoining Wi-Fi; the PC log has no line repeated more than 3 times a
   minute while you were away.
8. Then on the PC: Settings → Help → Copy log, and run
   `dotnet run -c Release --project Linc\tools\linksim -- log` (it reads today's log) — or send me the log.
