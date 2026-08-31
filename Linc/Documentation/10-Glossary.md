# Glossary

Terms, acronyms, and component names used across Linc's documentation and code.

## Concepts & external tech

- **ADB (Android Debug Bridge)** — Google's device bridge. Linc's primary transport; treated
  strictly as plumbing (D-001).
- **adbd** — the ADB daemon on the phone.
- **Wireless debugging / ADB over Wi-Fi** — ADB over the LAN with in-band pairing; needs Android
  11+ (D-002).
- **`adb forward`** — maps a desktop TCP port to a phone-side socket (desktop initiates).
- **`adb reverse`** — maps a phone-side port to a desktop socket (phone initiates, over the cable).
- **mDNS / Zeroconf** — multicast DNS service discovery. The phone advertises
  `_adb-tls-connect._tcp`; the desktop advertises `_linc._tcp`.
- **Direct TLS** — Linc's ADB-independent, mutual-TLS, LAN transport; the phone dials the desktop.
- **TOFU (trust-on-first-use)** — the pairing model for Direct TLS: certificates are exchanged
  once over the authenticated ADB link and pinned.
- **BLE (Bluetooth Low Energy) presence beacon** — a non-connectable advert the phone emits so the
  desktop knows it's near; a hint only, never a data path (D-034).
- **scrcpy** — the open-source Android screen-mirroring tool Linc uses for phone→PC mirroring
  (D-004); its server runs with shell UID via `app_process`.
- **shell UID** — the privilege level ADB grants; the only way to get screen capture without a
  prompt *and* input injection, which is why the phone→PC mirror is ADB-only.
- **DXGI Desktop Duplication** — the Windows API that captures the PC screen for the reverse
  mirror.
- **Media Foundation (MF)** — the Windows media framework; Linc hand-rolls interop to drive its
  hardware H.264 encoder (no software fallback, D-039).
- **GSMTC / SMTC** — Global System Media Transport Controls; how Windows exposes media sessions.
  Some players (e.g. AIMP) publish nothing to it, hence the Winamp-remote fallback.
- **Material You / dynamic color** — Android's wallpaper-derived palette; Linc syncs it phone→PC
  live (D-011).
- **PerMonitorV2** — the DPI-awareness mode both the desktop and `mirrorsim` declare so every
  display API reports physical pixels.

## Protocol & session

- **Control channel (0)** — the JSON protocol connection; issues the session token.
- **Channels 1–5** — mirror (video+input, ADB only), files (binary), bulk (small binaries), audio,
  pc-video (reverse mirror, desktop→phone).
- **Session token** — issued on the control connection; every typed channel presents it.
- **Topic (pub/sub)** — `subscribe`/`unsubscribe` on the control channel turns unsolicited
  messages into a subscribed stream (gated only at v≥6).
- **Envelope / framing** — the length-prefixed JSON message format; unknown types/fields are
  tolerated.
- **`hello`** — the control handshake message that negotiates the protocol version.
- **Protocol version (currently v14)** — the negotiated wire version; bumped in `PROTOCOL.md`
  first, then gated on.

## Desktop components (C#)

- **AppShell / AppShellViewModel** — the WinUI shell; composition root for eagerly-started
  services.
- **ConnectionManager / ConnectionSupervisor** — establishing a link vs. the reconnection state
  machine (also the transport supervisor).
- **CompanionClient** — the stream-agnostic protocol client.
- **DiscoveryService / UsbWatcherService / TlsTransportService / BlePresenceService** — the
  discovery + transport services.
- **PhoneSetupService** — installs and provisions the companion over ADB.
- **DeviceRegistry / KnownDevice** — paired-device registry; per-device settings live on the
  record.
- **DeviceCacheService** — offline device memory.
- **FileService / TlsFileService / FileServiceRouter** — files over ADB sync vs. the files channel.
- **MirrorService** — the scrcpy child process (phone→PC).
- **PcScreenCapture / PcVideoEncoder / PcMirrorSource / PcMirrorService / PcInputService** — the
  reverse-mirror pipeline (PC→phone).
- **ClipboardSyncService / NotificationSyncService / MediaSyncService / PcMediaService /
  WinampRemote** — clipboard, notifications, phone media, PC media.
- **ThemeSyncService** — retints the app to the phone's palette; owns wallpaper/accent.
- **SyncEngine** — folder/photo sync.
- **ShareService / BatteryAlertService / LogService** — share sweeping, low-battery alerts, the
  activity log.
- **LincException** — the plain-language error type.

## Android components (Kotlin)

- **CompanionService** — the exported foreground service hosting everything.
- **SocketServer** — the multi-connection socket + TLS server.
- **CompanionOutbox / CompanionStateHolder / SessionRegistry** — unsolicited-send routing, live
  state, token issuing.
- **Providers/bridges** — DeviceStatusReporter, LincNotificationListener, ClipboardBridge,
  MediaBridge, PhotosProvider, WallpaperProvider, FileChannelServer, SmsProvider/SmsReceiver,
  CallProvider/CallStateWatcher, PcMediaStore, ShareStore, LocateRinger, BulkResources.
- **PresenceClient / TlsIdentity / TransportStore** — Direct-TLS dial-out, identity, prefs.
- **BlePresenceAdvertiser** — the BLE beacon.
- **MirrorReceiver / MirrorControl / MirrorSettings** — the reverse-mirror client.
- **MainActivity / MirrorActivity / ShareReceiverActivity** — the activities.

## Milestones & eras

- **Era 1 (M0–M19)** — building the product (foundation → pipeline → Sync page → phone Home/Share).
- **Era 2 (M00–M07)** — the phone and PC extend each other (clean slate → effortless companion →
  offline memory → device tabs → BLE presence → reverse mirror; Extend mode dropped; Polish
  ongoing).
- **M06 (Extend mode)** — a true second-monitor mode; **dropped** at the owner's decision.
- **Sideload build** — the full-feature Android build including SMS/call lanes (D-016); a
  Play-ready build compiles those out.

## Storage locations

- `%LOCALAPPDATA%\Linc/settings.json` — settings + paired devices.
- `%LOCALAPPDATA%\Linc/cache\<serial>\` — offline device memory.
- `%LOCALAPPDATA%\Linc/sync-state.json` — sync engine seen-state.
- `%LOCALAPPDATA%\Linc/logs\` — rolling daily activity logs.
- `Pictures\Linc/Photos` / `Pictures\Linc/Shared` / `Downloads\Linc` — synced photos, shared-from-
  phone files, and received shares.
