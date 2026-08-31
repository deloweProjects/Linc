# System Architecture

Linc is a two-application system. The apps share no code — only a versioned wire protocol. This
file describes how the whole system is organized; the two app-specific files
(`04-Desktop-Application.md`, `05-Android-Application.md`) go deeper into each side, and
`06-Connectivity-and-Protocol.md` covers the wire.

## Overall shape

```
┌─────────────────────────────┐                     ┌──────────────────────────────┐
│         Windows PC          │                     │        Android Phone         │
│                             │                     │                              │
│  ┌───────────────────────┐  │                     │  ┌────────────────────────┐  │
│  │     Linc Desktop      │  │                     │  │      Linc Android      │  │
│  │  (UI + orchestration) │  │                     │  │    (companion app)     │  │
│  └──────────┬────────────┘  │  3 physical paths   │  └───────────┬────────────┘  │
│             │               │  A. ADB forward     │              │               │
│  ┌──────────▼────────────┐  │  B. adb reverse     │  ┌───────────▼────────────┐  │
│  │  Transport supervisor │◄─┼─  C. Direct TLS   ──┼─►│  adbd + LocalServer-   │  │
│  │  (bundled adb/scrcpy) │  │  mDNS + BLE hint    │  │  Socket + TLS listener │  │
│  └───────────────────────┘  │                     │  └────────────────────────┘  │
└─────────────────────────────┘                     └──────────────────────────────┘
```

- **Discovery.** The phone's wireless debugging advertises via mDNS
  (`_adb-tls-connect._tcp`); the desktop also advertises its own `_linc._tcp` service for the
  Direct-TLS path. A USB-attached phone is discovered by polling the ADB device list. A BLE
  beacon from the phone is a *presence hint* that nudges discovery, never a data path.
- **Pairing.** One-time ADB wireless pairing (QR code / 6-digit code), wrapped in guided UI on
  both sides. During that flow the two apps also exchange self-signed TLS certificates
  (trust-on-first-use) for the Direct-TLS transport.
- **Transport.** The desktop's supervisor ranks and races every viable path and fails over live
  (see below and `06`).
- **Feature channels.** File operations, screen streams (both directions), bulk binaries, and
  the control protocol all run over the connected transport as typed channels.

## The layered model (both apps)

Dependencies point downward only:

```
UI  →  Application (view models / coordinators)  →  Domain services  →  Transport/ADB layer  →  bundled binaries
```

Three invariants hold this together:

1. **The UI never talks to ADB directly.** All ADB interaction goes through the ADB/transport
   layer.
2. **Domain services do not know about the UI.** They expose state via observable models and
   events.
3. **Only the transport layer knows adb/scrcpy exist.** Swapping or adding a transport must not
   ripple above that layer — which is exactly what let Direct TLS slot in beside ADB without
   touching feature code.

## The communication backplane

Everything the product does rides on **one backplane** rather than feature-specific plumbing.
Its layers, top to bottom:

```
FEATURES    widgets · notifications · media · clipboard · mirror · files · photos ·
            sync · messages · calls · reverse-mirror
TOPICS      pub/sub on the control channel (subscribe {topic} → event stream)
CHANNELS    typed connections: 0 control (JSON) · 1 mirror (video+input) ·
            2 files (binary) · 3 bulk (icons/art/thumbnails) · 4 audio · 5 pc-video
SESSION     one listener, many connections; each opens with a {sessionToken, channel} header
TRANSPORTS  A. ADB forward (USB or wireless debug) · B. adb reverse · C. Direct TLS (LAN)
```

**The key trick — one socket, many connections, no multiplexing layer.** The phone's
`LocalServerSocket("linc")` already accepts many simultaneous connections through a single `adb
forward`. Each new connection sends a small header declaring its channel type, and the OS's
per-connection buffering *is* the flow control — a stalled bulk transfer can never starve a
notification. The identical scheme runs over Direct TLS. Linc deliberately does **not** build
in-band multiplexing (frames tagged with channel IDs over one connection), which would require
hand-rolled flow control (D-012).

## Transports and the supervisor

There are three physical paths (detailed in `06`):

- **A. ADB forward** — `adb forward tcp:0 localabstract:linc`. Works over USB and wireless
  debugging. Required for the mirror channel (input injection needs shell UID, which only ADB
  grants).
- **B. adb reverse** — maps a phone-side port to a desktop socket, letting the phone *initiate*
  connections over the USB cable, symmetric with Direct TLS on the LAN.
- **C. Direct TLS** — mutual-TLS, ADB-independent, phone dials the desktop on the LAN. Carries
  everything except the phone→PC mirror channel.

The **transport supervisor** (an extension of the connection supervisor) races all viable paths
on any trigger (mDNS sighting, USB attach, network change, screen-on, BLE presence, resume from
sleep), ranks them **USB > wireless ADB > Direct TLS**, and a higher-priority transport
*preempts* a lower one. A per-device connection preference (Auto / USB-only / Wireless-only /
Direct-only) filters eligibility. The Device page shows which path is live and its latency, and
lets a power user pin one.

## Responsibilities, by app

### Android app

- Guide the user through enabling wireless debugging and pairing.
- Run a lightweight **foreground companion service** for the things ADB alone cannot do:
  notification access and relay, focused-app clipboard events, the Material You palette (needs an
  app context), and the SMS/call/media/file providers.
- Expose a local socket server reached via `adb forward`, plus a TLS listener the phone dials
  out to on the LAN.
- Advertise the BLE presence beacon.

### Windows app

- Bundle and manage the ADB server and scrcpy — the user never runs them manually.
- Discover phones (mDNS + ADB polling + BLE hint), maintain a registry of paired devices, and
  run the reconnection state machine (health checks, backoff, sleep/wake recovery).
- Present the UI as a `NavigationView` shell with dedicated pages, a Chrome-style device-tab
  strip, and a system-tray presence.
- Translate every ADB-level error into plain-language guidance.
- Orchestrate desktop-side engines that need no phone code: the folder/photo sync engine, the
  reverse-mirror capture/encode pipeline, PC-media reporting, and share sweeping.

## Data flows (representative)

- **File transfer.** Desktop UI → `FileService`/`FileServiceRouter` → ADB sync (`push`/`pull`)
  *or* the files channel over Direct TLS → phone filesystem.
- **Phone→PC screen mirroring.** `MirrorService` runs scrcpy → H.264 stream back over the ADB
  tunnel → rendered in a desktop window; input flows the reverse way.
- **PC→phone screen mirroring (reverse mirror).** DXGI Desktop Duplication → hardware Media
  Foundation H.264 → pc-video channel (5) → MediaCodec on the phone; the phone's touches return
  as `pc.input` control messages injected with `SendInput`.
- **Clipboard.** Desktop watcher → protocol → phone clipboard bridge (and the reverse where
  Android permits).
- **Notifications.** Phone `NotificationListenerService` → socket server → forwarded tunnel →
  desktop notification service → toast/center.
- **Discovery/connection.** mDNS advert / USB attach / BLE hint → supervisor → transport →
  control handshake → connected state fanned out to all services.

## Component & dependency boundaries

- The Android app and Windows app share **no code**, only the companion protocol spec.
- The companion protocol is a **versioned, documented contract**; neither side depends on the
  other's internals, and version negotiation keeps mismatched builds working.
- Within each app, the downward-only dependency rule (UI → application → domain → transport)
  keeps the transport swappable and the domain services UI-agnostic.
