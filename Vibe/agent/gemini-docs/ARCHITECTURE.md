# Architecture

> Status: pre-implementation. This document describes the target architecture; it must be updated as the code takes shape.

## Overall system architecture

Linc is a two-application system connected over the local network, with ADB (wireless debugging) as the primary transport:

```
┌─────────────────────────────┐          ┌──────────────────────────────┐
│        Windows PC           │          │        Android Phone         │
│                             │          │                              │
│  ┌───────────────────────┐  │          │  ┌────────────────────────┐  │
│  │     Linc Desktop      │  │          │  │      Linc Android      │  │
│  │  (UI + orchestration) │  │          │  │    (companion app)     │  │
│  └──────────┬────────────┘  │          │  └───────────┬────────────┘  │
│             │               │          │              │               │
│  ┌──────────▼────────────┐  │  Wi-Fi   │  ┌───────────▼────────────┐  │
│  │  ADB Service Layer    │◄─┼──────────┼─►│  adbd (wireless debug) │  │
│  │  (bundled adb/scrcpy) │  │  mDNS +  │  │  + Linc services       │  │
│  └───────────────────────┘  │  ADB-TLS │  └────────────────────────┘  │
└─────────────────────────────┘          └──────────────────────────────┘
```

- **Discovery**: the phone's wireless debugging advertises itself via mDNS (`_adb-tls-connect._tcp`); Linc Desktop listens and matches known devices. A USB-attached phone is discovered separately by polling the ADB device list — no `adb connect` needed for that path.
- **Pairing**: one-time ADB wireless pairing (QR code / 6-digit code), wrapped in a guided UI on both sides. Over USB, the phone's own "Allow USB debugging" prompt is the only pairing step; Linc still asks for one explicit confirmation before trusting an unrecognized USB device.
- **Transport**: ADB-over-TLS (Wi-Fi) or a direct USB ADB connection, both managed entirely by Linc Desktop through the same `ConnectionManager`.
- **Feature channels**: file operations, shell commands, port-forwarded sockets, and scrcpy sessions all run over the ADB connection.

## Android app responsibilities

- Guide the user through enabling wireless debugging and pairing (deep links into system settings, clear step-by-step UI).
- Run a lightweight companion service for features ADB alone cannot provide:
  - notification access (NotificationListenerService) and relay,
  - clipboard change events (within Android's background-clipboard limits),
  - device status (battery, storage, network) reporting.
- Expose a local socket server that the desktop reaches via `adb forward`.
- Surface connection status and pairing state to the user.

## Windows app responsibilities

- Bundle and manage the ADB server and scrcpy binaries (start, stop, recover) — the user never runs them manually.
- Discover phones via mDNS (Wi-Fi) and ADB device polling (USB); maintain a registry of paired devices.
- Establish and monitor the ADB connection; reconnect automatically on drop, sleep/wake, network change, and USB plug/unplug.
- Provide the desktop UI as a `NavigationView` shell with dedicated pages — Device (connection health + mirroring), Files, Notifications, Details (device stats), Logs, and Settings (ADB status, a real shell-command box, and sync toggles) — rather than one single window.
- Translate every ADB-level error into plain-language guidance.

## Service diagram (Windows)

```
┌──────────────────────── Linc Desktop ────────────────────────┐
│                                                              │
│  UI Layer (views, windows)                                   │
│        │                                                     │
│  Application Layer (view models, feature coordinators)       │
│        │                                                     │
│  Domain Services                                             │
│   ├── DeviceRegistry      (paired devices, persistence)      │
│   ├── DiscoveryService    (mDNS listener)                    │
│   ├── UsbWatcherService   (USB-attached device polling)      │
│   ├── ConnectionSupervisor(reconnect state machine, backoff) │
│   ├── ConnectionManager   (wireless + USB transport)         │
│   ├── LogService          (activity log, ring buffer + file) │
│   ├── FileService         (list/push/pull via ADB)           │
│   ├── MirrorService       (scrcpy session lifecycle)         │
│   ├── ClipboardService    (bidirectional sync)               │
│   └── NotificationService (relay from companion socket)      │
│        │                                                     │
│  ADB Service Layer                                           │
│   ├── AdbServerHost       (bundled adb server lifecycle)     │
│   ├── AdbClient           (ADB protocol commands)            │
│   └── PortForwardManager  (companion socket forwarding)      │
└──────────────────────────────────────────────────────────────┘
```

## Service diagram (Android)

```
┌──────────────────────── Linc Android ────────────────────────┐
│                                                              │
│  UI Layer (Compose screens: onboarding, pairing, status)     │
│        │                                                     │
│  Companion Foreground Service                                │
│   ├── SocketServer         (localhost, reached via forward)  │
│   ├── NotificationRelay    (NotificationListenerService)     │
│   ├── ClipboardBridge      (clipboard events, best effort)   │
│   └── DeviceStatusReporter (battery, storage, network)       │
└──────────────────────────────────────────────────────────────┘
```

## Data flow

- **File transfer**: Desktop UI → FileService → AdbClient (`sync` push/pull) → phone filesystem.
- **Screen mirroring**: MirrorService spawns scrcpy → scrcpy pushes its server via ADB → H.264/H.265 stream back over the ADB tunnel → rendered in a desktop window; input events flow the reverse way.
- **Clipboard**: Desktop clipboard watcher → ClipboardService → forwarded socket → ClipboardBridge sets phone clipboard (and the reverse where Android permits).
- **Notifications**: NotificationRelay → SocketServer → PortForwardManager tunnel → NotificationService → desktop toast/center.
- **Discovery/connection**: mDNS advert → DiscoveryService → ConnectionManager → AdbClient `connect` → connected state fan-out to all services.

## Component relationships & dependency boundaries

Dependencies point downward only:

```
UI  →  Application  →  Domain Services  →  ADB Service Layer  →  bundled binaries
```

- **UI never talks to ADB directly.** All ADB interaction goes through the ADB Service Layer.
- **Domain services do not know about UI.** They expose state via observable models/events.
- **The ADB Service Layer is the only component that knows adb/scrcpy exist.** Swapping the transport (e.g., a future direct-socket mode) must not ripple above this layer.
- **The companion protocol** (socket messages between desktop and phone) is a versioned, documented contract shared by both apps — see [PROTOCOL.md](PROTOCOL.md); neither side depends on the other's internals.
- The Android app and Windows app share **no code**, only the companion protocol specification.
