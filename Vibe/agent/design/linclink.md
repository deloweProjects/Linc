# LincLink — design

_2026-10-22, branch `claude/linc-link`. Brief: `Vibe/claude-notes/linc-link/BRIEF.md`. Protocol v31 → **v32**.
Supersedes `connection.md` §2 once build A ships; `connection.md` gets rewritten then._

One module on each side. The rest of Linc asks two things: **is the phone linked?** and **send this**.
Everything about routes, retries, pairing and discovery lives inside.

---

## 1. What changes for the user

| Today | LincLink |
|---|---|
| "Connected" only after the phone app's `hello`; ADB up but app silent = stuck on Searching | **Connected** as soon as any route reaches the paired phone. ADB up + app not answering yet = Connected with "getting Linc ready on the phone…" while the app is installed/started in the background (retried, never a dead end) |
| "Connected (no ADB)" wording | A separate **ADB chip**: grey, green when ADB is also up (Mirror, Tools, fast files light up) |
| Direct Wi-Fi only after a first ADB connect | Pair once by **scanning a QR** on the PC (or automatically when ADB is there) |
| One route at a time on timers | Every known route **raced at once**; the fastest wins; the winner per network is remembered |
| A blip = reconnect, pages reload | A blip under 5 s is invisible: **one session survives route changes**, nothing on the control channel is lost |
| Phone dials only when it happens to wake | Wakes on boot, app open, Wi-Fi join, screen on, cable, BLE sighting; both sides call out |

## 2. Identity and pairing

- **Device id** = the existing ids: `SHA-256(cert DER)[..16 hex]` (PC: `TlsTransportService.PcId`; phone:
  `TransportStore.idOf`). The keys stay where they are (PC `tls-identity.pfx`, phone KeyStore `linc-tls`) and never leave.
- **Link secret** (new): 32 random bytes made by the PC per paired phone, sent once over an authenticated
  channel, stored encrypted (PC: DPAPI per user; phone: AES-GCM key in the Android KeyStore). It keys the
  UDP beacon, the BLE beacon and the BLE side channel. `gen` increments when it is replaced.
- **Pairing routes** (both end with the same state: certificates pinned both ways + link secret):
  1. **ADB present** (today's `tls.exchange`, now also carrying `linkSecret`/`linkGen` at v32). Automatic.
  2. **QR** (no ADB): the PC shows `linc://pair?v=1&k=<PC cert SHA-256, full>&a=<addrs>&p=46001&t=<token>&n=<name>`
     (`t` = 16 random bytes, valid 5 min, one use). The phone scans it, dials TLS, and **checks the PC's
     certificate hash equals `k`** (authenticates the PC). The PC, inside its open pairing window only, accepts
     an unknown client certificate for one message: `link.pair {proof = HMAC-SHA256(t, "linc-pair" ‖ phoneCertHash ‖ pcCertHash), model, name}`.
     A valid proof (constant-time compare) pins the phone; the token is burnt. A MITM needs the PC's private key
     (phone check) or the token (visual, out of band). Phone scanner: Google code scanner (no camera permission);
     exact artifact/version verified before use.
  3. Later, if needed: **compare a 6-digit code** on both screens (for phones without Play services).
- A phone paired without ADB has no ADB serial yet: its PC record is keyed `linc:<phoneId>` and merged into the
  serial record the first time an ADB connect's `tls.exchange` returns the same certificate.

## 3. Routes

| Route | Who dials | Notes |
|---|---|---|
| ADB USB | PC | adb `track-devices` (push, no 3 s poll) + the existing poll as a backstop |
| ADB wireless | PC | mDNS `_adb-tls-connect`, last host:port, `:5555`; one raced candidate |
| TLS PC→phone (**new**) | PC | phone listens on **46002** (mutual TLS, pinned; unknown certs dropped at handshake) |
| TLS phone→PC | phone | existing 46001 + `127.0.0.1:46011` over adb reverse |
| BLE side channel (B) | PC central → phone GATT | light traffic only (notifications, clipboard, media keys); app-layer AES-GCM |
| Wi-Fi Direct legacy (C) | phone joins PC's group | then TLS as above over that network |
| USB tethering (C) | either | plain IP over the cable; just more addresses in the race |

## 4. The session (v32)

- The phone issues `session` (16 random bytes) in its first `hello` reply. Each side numbers its outgoing
  control envelopes `seq` 1, 2, 3… and piggybacks `ack` = highest seq received. An idle side sends
  `link.ack` after 2 s or 32 frames.
- Each side keeps unacked frames (cap 256 frames / 1 MiB). A new transport's `hello` carries
  `link: {session, recv}`; the reply says `resumed: true|false` and its own `recv`. Both replay frames with
  `seq > recv`; receivers drop `seq ≤` what they already have (exactly-once, so `sms.send` never doubles).
- The phone keeps a detached session **60 s**. Expired / overflowed / `resumed:false` = a fresh session, which
  pages see as a reconnect (today's behaviour).
- **PC `CompanionClient` becomes session-scoped**: pending requests and subscriptions live on the session; a
  transport swap replaces only the stream. Typed channels (files, video, audio) are not resumable; their
  owners re-open them as today. Honest limit: a file copy or mirror stream in flight restarts.
- Up to **two** transports per session: the active one and one warm **standby** (e.g. Wi-Fi beside USB), so a
  cable pull switches in one round trip. Extra ones are closed.

## 5. The brain (pure, both sides, unit-tested)

**Events** (each one line in the log, rate-limited): app start, boot, app opened, cable in/out, adb device
arrived/gone, network changed, Wi-Fi joined, screen on, PC resume (clock gap), mDNS/UDP/BLE sighting, phone dialled
in, transport died, user asked.

**On an event**: strong events reset all backoff → **race**. Otherwise wait out a **decorrelated-jitter backoff**
(`next = min(30 s, rand(0.5 s, prev × 3))`).

**Race** (RFC 8305 style): candidates ordered by per-network memory (§6) then rank; start the first, then the
next every **250 ms** while none has finished; the first to complete the Linc handshake wins, the rest are cancelled
(or one kept as standby). A candidate whose failure is permanent (wrong certificate, `busy`) is dropped from the round.

**Visible state**: `NoDevice | Searching | Connected | Paused` (+ `Connecting` only on a first connect taking
over 1.5 s). After a transport dies, the session **lingers 5 s** with nothing shown while the race runs; only
then Searching. A real failure shows one reason and one button.

**Rank** for which transport is active: USB > Wi-Fi (ADB or TLS, equal; never churn between equals) > Wi-Fi Direct > BLE.
The user's hand-picked transport (M15a) still wins while it answers.

**ADB as a capability**: ADB up without the app = Connected + background repair: install/grant if missing,
`am start-foreground-service`, then **probe the socket every 500 ms for 15 s** (not one fixed 2 s wait),
backoff 1, 2, 5, 10, 30 s after that. Each step's time is logged: the cause of "USB fails the first time" is
not proven yet, and these lines will show it.

**Health**: a ping on the session every 5 s when idle (cheap; data frames count); 2 misses = transport dead → race.

## 6. Discovery and memory

- **Network fingerprint**: PC = SHA-256(default-gateway MAC ‖ IPv4 subnet); phone = SHA-256(gateway IP ‖ subnet ‖ SSID when readable).
  Memory per fingerprint: up to 8 `(route, address, port, lastOk, okCount, failCount, rttMs)`, 16 networks LRU,
  forgotten after 5 failures in a row. Stored with the other per-device settings; no secrets in it.
- **UDP beacon**, port **46003**, IPv4 subnet broadcast on every up interface, both directions: while searching
  every 2 s for 10 s after an event then every 15 s; on every strong event at once.
  `"LK" ‖ ver(1) ‖ role(1) ‖ tag(8) ‖ port(2) ‖ flags(1)`, `tag = HMAC-SHA256(linkSecret, "linc-beacon" ‖ role ‖ slot)[..8]`,
  `slot = unixSeconds / 30`, accepted ±1. Strangers see 8 random bytes that change every 30 s. A match =
  "my peer is at this source address" → race it. A replay only costs a TLS attempt that fails. The phone holds
  a `WifiManager.MulticastLock` only while searching.
- mDNS (`_linc._tcp` both sides now) and the existing ARP spotting stay as extra sources.
- `link.where` tells the peer, after connect and on change: current fingerprint, addresses, ports.

## 7. Always on

- Phone: `BOOT_COMPLETED` receiver (+ `RECEIVE_BOOT_COMPLETED`) starts the service (FGS type `connectedDevice`,
  which Android 14/15 still allow from boot — verify); opening the app starts it; Wi-Fi join and screen on poke it.
  After boot on a remembered network it re-arms wireless debugging itself (`AdbArming`, already holds
  `WRITE_SECURE_SETTINGS`).
- PC: the installer is per-user (`PrivilegesRequired=lowest`), so the firewall rule
  (`netsh advfirewall firewall add rule name=Linc-LincLink … program="{app}\Linc.Desktop.exe" profile=private,domain`,
  so TCP 46001 + UDP 46003) is a checked post-install step that asks for admin once (`ShellExec` with `runas` in
  `[Code]`; declining is silent, a silent self-update never asks). Uninstall removes it only if it exists. Its own
  name, so a rule Windows or the owner made for Linc is never touched. (Built: no in-app "add the rule" button yet.)

## 8. Bluetooth (build B)

- Phone advertises `"LK" ‖ tag(8)` with `tag = HMAC(linkSecret, "linc-ble" ‖ slot)`, slot 300 s, when every known PC
  has a link secret; the old v31 format (`SHA-256(cert ‖ slot)`) otherwise, so old PCs keep their hint.
- Phone runs a GATT server (one service, write + notify characteristic). PC (`BluetoothLEAdvertisementWatcher`,
  then `BluetoothLEDevice.FromBluetoothAddressAsync` → GATT) connects on a sighting while searching. Frames are
  AES-GCM with a key from HKDF(linkSecret, both nonces); counter nonces; replays rejected.
- Over it: `where` (addresses, network), `arm` (re-arm wireless debugging), `wifi` (build C), then light traffic.
  Optional: no Bluetooth = no change in anything else.

## 9. PC-made network (build C)

- `WiFiDirectAdvertisementPublisher` with `LegacySettings { IsEnabled, Ssid = "Linc-XXXX", Passphrase }`
  (no internet needed, no admin). Passphrase: 24 random characters, stored DPAPI. `gen` bumps only on change.
- `link.wifi {ssid, pass, gen}` goes to the phone over an authenticated channel (session or BLE), **only when its
  gen differs** from what the phone confirmed. The phone joins with `WifiNetworkSpecifier` + `requestNetwork`
  (one system approval, mobile data stays for internet) and binds Linc's sockets to that network.
- If the adapter can't host it beside its Wi-Fi: fall back to BLE for light features and say why.

## 10. Wire changes (v32) and old peers

| Message / field | Dir | v |
|---|---|---|
| `hello.link {session?, recv}` / reply `link {session, recv, resumed, id}` | both | 32 |
| envelope `seq`, `ack`; `link.ack {ack}` | both | 32 |
| `link.secret {secret, gen}` | PC→phone | 32 |
| `link.pair {proof, model, name}` → `link.pair {ok, linkSecret, linkGen}` | phone→PC (pairing window only) | 32 |
| `link.where {addrs, port}` | phone→PC | 32 |
| `link.wifi {ssid, pass, gen}` → `link.wifi {gen}` | PC→phone | 32 (C) |
| UDP 46003 beacon, BLE v2 beacon, phone TLS listener 46002 | — | outside the envelope |

**A v31 peer** never negotiates 32: no `seq`/`ack`, no resume, no secret, never sees UDP or the 46002 listener
(it isn't paired for it). The PC engine still races ADB routes and accepts the old phone's TLS dial-in, so an
old phone connects exactly as today, just faster. A v32 phone with a v31 PC dials as today.

**Switch**: Settings › Connection › "New connection engine" (both apps). On by default on this branch's test
builds; off = today's `ConnectionSupervisor` / `PresenceClient` untouched. Removed after the owner signs off.

## 11. Code layout (as built, build A)

- PC `Services/LincLink/`, pure: `LinkSession.cs` (seq/ack/replay), `LinkBeacon.cs`, `LinkPairing.cs` (QR text,
  proof, pairing window), `NetMemory.cs`, `RouteRace.cs` (happy eyeballs), `LinkBrain.cs` (backoff, order,
  presence, timings). I/O: `LincLinkService.cs` (UDP beacon, pairing probes, dialling the phone, network
  fingerprint, `link-memory.json`), `LinkSecretStore.cs` (`link-secrets.json`).
- The engine lives **inside** `ConnectionSupervisor` rather than beside it: every new path is behind
  `registry.LincLinkEnabled`, and all of the supervisor's admission rules (known phones, the add-a-phone
  flow, hand-picked transports, closed tabs) stay one copy. `CompanionClient` holds the v32 session;
  `ConnectionManager` resumes it on a new transport, repairs the phone app over ADB and shares the link secret;
  `TlsTransportService` dials the phone and runs the pairing window; `UsbWatcherService` follows adb's device
  list (`DeviceMonitor`) so a cable is seen at once.
- Phone `service/link/`: the same pure pieces in Kotlin (`LinkSession`, `LinkBeacon`, `LinkPairing`,
  `LinkBrain`/`NetMemory`, JVM tests in `LinkCoreTest`), plus `LinkStore` (switch, "stopped by hand", secrets in
  a KeyStore AES box), `LinkListener` (TLS 46002), `LinkBeaconService` (UDP 46003), `LinkPairingClient`,
  `LinkRuntime` (the `SocketServer.LinkHost`). `SocketServer` runs v32 sessions (`serveLinkControl`,
  `LinkControl`) around the unchanged message loop (`controlLoop`). `BootReceiver`, `PairActivity`.
- `Linc/tools/linclinksim`: the pure core's rules and scenarios, plus the PC's **real** `CompanionClient`
  against a fake v32 phone (the phone's side as `SocketServer.kt` does it) over in-memory links cut mid-stream.
  Exits 0 when green (run by `build.yml`).

## 13. What changed from the plan while building A (and why)

- **The link secret** travels in `link.secret` on any authenticated v32 session, not inside `tls.exchange`:
  a phone paired by QR code has no ADB link to exchange it on.
- **The QR code is read by the phone's own camera.** It carries a `linc://pair` link that opens Linc
  (`PairActivity`), which asks before pairing (any app could open such a link). No scanner library (Google's
  pulls in telemetry), no camera permission. Pairing works with the PC's firewall closed: the phone dials the
  PC *and* answers the PC's pairing probes so the PC can dial the phone.
- **The phone's dial order** stays `ReconnectPolicy`'s; the per-network memory that matters is the PC's (it
  is the side that dials now). The phone's `NetMemory` is written and tested, not yet wired.
- **Firewall rule** covers private and domain networks. A home Wi-Fi Windows calls "Public" gets no inbound
  rule; the PC still reaches the phone by dialling out.
- **After an adversarial review of A** (each finding checked by two skeptics, then fixed):
  - a request that timed out is never run later by a resume (`link.noop` takes its number);
  - the session is offered to, and resumed by, only the phone it belongs to (pinned certificate, `id` checked);
    the PC picks a phone's link secret by its certificate, and the phone files a PC's secret under the id of
    the certificate it presented (ADB: only the shell user may send one);
  - the `linc://pair` link shows a check code on both screens, carries only private IPv4 addresses, and no
    longer turns the engine on without saying so;
  - a transport that dies before its connection is installed fails at once instead of holding the gate; the
    app repair can be cancelled (disconnect, phone switch, engine switch) and never runs on the UI thread;
  - beacon sightings wake the dialler once per place per 30 s, and each address the PC dials has its own
    backoff, so a phone busy with another PC can't make it spin;
  - the phone's session does no I/O under its bookkeeping lock, a resume closes the old route first, and the
    listener caps handshakes (8 at a time, 5 s each);
  - the cable-in push speaks adb's `host:track-devices` itself (the library's DeviceMonitor spins on a closed
    server connection and restarts the adb server on its own);
  - the installer's firewall rule has its own name and a declined permission prompt is silent;
  - a phone updated from an older build stays stopped if it was, until Linc is started once.
- Not in A (as planned): Bluetooth (B), wireless-debugging re-arm after a reboot (B), Wi-Fi Direct and USB
  tethering (C).

## 12. Targets (simulator and logs)

USB plug-in → connected < 2 s (app running) / < 10 s (app missing); same Wi-Fi, app running → < 3 s;
blip < 5 s → invisible; phone reboot on known Wi-Fi → connected with no taps.
