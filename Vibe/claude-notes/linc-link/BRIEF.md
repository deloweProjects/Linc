# LincLink: rebuild the connection (brief for the dedicated build session)

You are the session the owner opened **only** to rebuild how Linc's phone and PC find and keep
each other. Read this whole file, then `../WORKING-WITH-THE-OWNER.md` and `../HANDOFF.md` §3
(how work flows: no local toolchain, CI judges, ui-test branches, commit rules). Everything else
you read on demand.

## 1. The owner's ask, in their words

- "connection sucks right now… I'm 10 minutes tryna connect" when showing Linc to someone.
- "connection fails first time even by USB, where PC recognises the device but nope."
- "Phone and PC are on the same wifi and Linc doesn't even know, doesn't even respond."
- "Create an ID system for the two Linc apps' handshake… phone Linc advertises on USB apart from
  ADB… be two-faced: Linc is very dependent on ADB but the connection part and some features
  don't really [need it]."
- "Don't say 'Linc connected, no ADB'. Put an ADB button/chip that goes green when ADB is also
  connected."
- "Make someone not even notice the devices are already connected." Smooth, seamless, snappy.
- "Crazy smart algorithms", "clever but clean and safe", "a module that fits into Linc".
- Bluetooth idea: the phone sends a unique Linc beacon, the PC sees it, checks Wi-Fi/Bluetooth for
  the phone. When no Wi-Fi/USB: the PC brings up its own network and tells the phone over Bluetooth
  "is that you? yes → join this SSID/password".
- "Windows won't allow everyday Mobile Hotspot with no internet access, so deal with that too."
- "Send SSID and password once, only again when it changes."
- Don't break anything. UI work is done; this is logic.

## 2. What exists today (verified 2026-10-21; file:line are approximate, re-check)

`D/` = `Linc/DESKTOP/Linc.Desktop/`, `A/` = `Linc/ANDROID/app/src/main/java/app/linc/android/`.
Best existing design doc: `Vibe/agent/design/connection.md` (§1 inventory partly stale, §2 = D-081).

- **Transports:** ADB over USB (PC polls its private adb server on port **5038** every 3 s,
  `D/Services/UsbWatcherService.cs`, `AdbServerHost.cs`); ADB wireless (mDNS `_adb-tls-connect`,
  `DiscoveryService.cs`); **Direct TLS** (PC listens on 46001, phone dials; `TlsTransportService.cs`,
  `A/service/PresenceClient.kt`); hotspot quick link (`HotspotLinkService.cs`, never hand-tested);
  BLE beacon = only a "scan now" hint (`ConnectionSupervisor.cs` ~528); NFC tag.
- **Owner of the link:** `ConnectionSupervisor.cs` (states NoDevice/Searching/Connecting/Connected/
  Paused), pure rules in `LinkPolicy.cs`, `TransportRank.cs`. Make-before-break exists for upgrades.
- **The flaws that cause the owner's pain:**
  1. "Connected" requires the companion's `hello`. `ConnectionManager.FinishConnectAsync`
     (~280-420) installs/grants/starts the app (`am start-foreground-service` once, ~697-720),
     and on any hiccup throws; the supervisor sits at **Searching** ("can't reach the Linc app",
     a button name that no longer exists, `CompanionClient.cs` ~558). = the USB-first-time bug.
  2. Direct TLS can't be the first link: trust (cert exchange `tls.exchange`) only happens over ADB.
  3. Phone: **no BOOT_COMPLETED receiver**; opening the app doesn't start the service.
  4. Wireless debugging turns off on reboot/network change and changes port; re-arm (`AdbArming.kt`,
     needs `WRITE_SECURE_SETTINGS`, which the PC grants) only runs from an already-live link.
  5. Discovery is mDNS only (+ ARP spotting, last address). No broadcast; the phone's NSD failure
     callbacks are empty. Windows Firewall: no rule is created; 46001 can be blocked silently.
  6. Retries are timer-paced on a fixed ladder, one route at a time; 15 s health probe, 3 misses.
- **Security today:** mutual TLS with exact cert pinning; PC cert `%LOCALAPPDATA%\Linc\tls-identity.pfx`;
  phone key in Android KeyStore `linc-tls`; pins in `settings.json` / `TransportStore`. Keep this
  strength or better.
- Protocol is **v31**; every feature is gated on the peer's version (`PROTOCOL.md`). Old peers must
  keep working.

## 3. The design (agreed with the owner)

One module, **LincLink**, on both sides; the rest of Linc only asks "connected?" and "send".

1. **Identity & pairing without ADB.** Reuse the existing TLS keys as the device identity (ID =
   hash of the public key). Pair by: (a) the phone app scanning a QR on the PC (carries the PC's
   key hash, addresses, a one-time secret), or (b) automatically whenever ADB is present (today's
   exchange). After pairing, either side recognises the other on any route.
2. **Two-faced status.** The **Linc link** is "Connected". **ADB is a capability chip** that turns
   green when ADB is also up (Mirror, Tools, fast files light up). Never show "connected, no ADB" as
   an error. ADB-only (no app running) still counts as connected and repairs the app in the
   background (install/start, retried with backoff, never a dead end).
3. **One session over many routes.** A logical session with a resume token and sequence-numbered
   messages that survives transport switches (cable out → Wi-Fi in, mid-stream, nothing lost),
   QUIC-migration style. Features never see a reconnect for short blips.
4. **The brain.** Event-driven (cable, Wi-Fi join, screen on, BLE sighting, PC wake, app open) with
   jittered backoff between events. **Race all candidate routes in parallel** (happy-eyeballs) and
   keep the first that completes the handshake. **Remember per network** (fingerprint: SSID/gateway
   MAC/subnet) which route won and its address, and try that first next time. Discovery on LAN:
   mDNS **plus** a small authenticated UDP broadcast/beacon both ways, last-known addresses, both
   sides dial. No visible state change for blips under a few seconds; a real failure shows one
   reason and one fix button.
5. **Bluetooth LE.** Phone advertises a private rotating beacon (HMAC of a shared pairing secret +
   time slot; strangers see noise). PC scans (`BluetoothLEAdvertisementWatcher`). On sighting: wake
   discovery, and over a small encrypted GATT channel swap current network + addresses, and ask the
   phone to re-arm wireless debugging. BLE also carries light traffic (notifications, clipboard,
   media keys) when no IP route exists. Bluetooth is optional: everything works without it.
6. **PC-made network when nothing else works.** Not Mobile Hotspot (needs internet). Use **Wi-Fi
   Direct legacy mode** (`WiFiDirectAdvertisementPublisher` + `LegacySettings` with fixed SSID like
   `Linc-XXXX` and a long random passphrase stored on the PC): no internet, no admin. Send SSID +
   password to the phone **once** (BLE or cable), re-send only when changed. The phone joins as a
   **local-only** network (`WifiNetworkSpecifier`, Android 10+: one system approval, keeps mobile
   data for internet). If the PC's adapter can't run it alongside its Wi-Fi, fall back to BLE for
   light features and say why. If the PC has internet, Mobile Hotspot is an alternative route.
7. **USB without ADB:** when ADB is present, ride `adb forward/reverse` as today; otherwise USB
   tethering (check whether it can be enabled for the user; if not, a one-toggle hint). Skip Android
   Open Accessory (needs a Windows driver).
8. **Always on:** BOOT_COMPLETED + app open + Wi-Fi join + BLE trigger start the service; after a
   reboot the phone re-arms wireless debugging itself on known networks (it already holds
   `WRITE_SECURE_SETTINGS`). Installer adds a Windows Firewall rule for Linc on Private networks
   (Inno Setup, removed on uninstall).
9. **ADB logic:** the slowness is around ADB, not ADB: 3 s polling, waiting for `hello`, one try,
   give-up. Make ADB one raced route; react to device arrival instead of polling where possible.

## 4. How to build it (order, and what "done" means)

Branch: `claude/linc-link` from `claude/linc-feature-ui-polish-lgf8au` (it holds unreleased work:
Tools page, Mute PC). One commit per coherent step; docs in the same commit (PROTOCOL.md,
FEATURES.md, SOUND.md if touched, `Vibe/agent/design/connection.md` rewritten for LincLink).

1. **Read first, then design on paper:** `ConnectionSupervisor`, `ConnectionManager`, `LinkPolicy`,
   `TlsTransportService`, `PresenceClient`, `CompanionService`, `TransportStore`, `PROTOCOL.md`
   connection sections. Write `Vibe/agent/design/linclink.md` (states, messages, version bump,
   fallback to v31 peers) before code. Keep it short.
2. **Pure core + simulator first.** The brain, session/resume, backoff, per-network memory,
   handshake verification as dependency-free code with a harness in `Linc/tools/` (extend
   `linksim` or add `linclinksim`): simulate cable pulls, multicast blocked, IP change, phone
   reboot, BLE-only, slow/lossy links, two PCs. It must exit 0 green in `build.yml`. Mirror the
   logic in Kotlin with JVM unit tests if the phone side has real logic.
3. **Wire it in behind a switch** (Settings › Connection › "New connection engine", default **on in
   test builds**, old path kept until the owner signs off). Ship in this order, each its own
   test build (only when the owner says "send"):
   - A: identity/QR pairing, ADB chip, ADB-only connected + background app repair, session resume,
     racing, UDP beacon, per-network memory, firewall rule, start at boot/app open. **This alone must
     fix "USB fails first time" and "same Wi-Fi, nothing happens".**
   - B: BLE beacon + GATT side channel + wireless-debugging re-arm.
   - C: Wi-Fi Direct legacy network + local-only join; USB tethering.
4. **Targets** (measure in the simulator and in logs): USB plug-in → connected < 2 s (app running)
   / < 10 s (app missing, installed in background); same Wi-Fi, app running → < 3 s; blip < 5 s →
   invisible; after phone reboot on known Wi-Fi → connected with no taps.

## 5. Rules (non-negotiable)

- **Don't invent APIs.** Before using any Windows/Android API, check it exists with that exact
  signature (compile it: install .NET with `dotnet-install.sh --channel 8.0` into your scratchpad
  and compile pure code there; for WinRT/WinUI APIs check Microsoft docs; for Android check the
  SDK docs for the min API level). Linc's Android minSdk is in `Linc/ANDROID/app/build.gradle*`.
- **No local Windows/Android build:** CI judges. Run `build.yml` (push) and the screenshot test
  (`ui-test/<name>` branch via GitHub MCP `create_branch`) and read logs before claiming anything.
  Never say "works" for something only the simulator saw; say what's hardware-unverified.
- **Never break Full mode:** an old phone/PC (v31) must still connect exactly as today.
- **Security:** keys never leave the device; everything over BLE/UDP/Wi-Fi Direct is authenticated
  and encrypted with the paired keys; beacons don't leak identity; Wi-Fi credentials only to a
  peer that proved its key; no account, no server. Never weaken the phone (see
  `Vibe/claude-notes/linc-tools/TOOLS.md` §13). **Never commit a keystore.**
- Logs: every route decision one line, rate-limited; never log secrets/passwords.
- Commit lines end with the Co-Authored-By/Claude-Session lines your session gives you; no model IDs
  in the repo. No PRs, releases or Telegram posts unless the owner asks ("send" = test build).
- Don't touch the Tools page, the UI design, or the constants groups `TIDY-UP.md` says to leave alone.
- Be brief with the owner: plain words, what changed, what to test by hand.

## 6. When you're done

Write `Vibe/claude-notes/linc-link/DONE.md` (what was built, what's verified where, what's not,
how to switch engines, the owner's test checklist) and push. Then give the owner this line to
paste back to the other session, filled in:

> LincLink done on `claude/linc-link` @ <commit>. Build <green/red>. Read Vibe/claude-notes/linc-link/DONE.md.
