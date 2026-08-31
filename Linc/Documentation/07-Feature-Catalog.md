# Feature Catalog

Every user-facing capability Linc ships today, what it does, and which app/protocol pieces power
it. (Planned/next-phase features live in `docs/ROADMAP.md`, not here.)

## Connection & setup

- **Guided pairing.** One-time ADB wireless pairing via QR code (or 6-digit code), guided on both
  sides; TLS certificates are exchanged at the same time. *(PairingService, protocol v9
  `tls.exchange`.)*
- **PC-driven onboarding (M01).** The desktop installs the companion APK itself and grants
  everything ADB can grant — the phone-side work is little more than enabling wireless debugging
  and scanning a QR. *(PhoneSetupService.)*
- **Effortless companion (M01).** The desktop auto-starts/revives the phone service whenever the
  handshake fails on a live ADB link — after installs, reboots, or crashes.
- **Automatic discovery & reconnection.** mDNS (Wi-Fi) + ADB polling (USB) + BLE presence hint;
  a reconnection state machine survives reboots, sleep/wake, and network changes with backoff.
- **Multiple transports with failover.** USB, wireless ADB, and Direct TLS, ranked and preempting;
  a per-device connection-method picker (Auto / USB / Wireless / Direct). *(Transport
  supervisor.)*
- **Bluetooth presence.** The desktop knows when the phone is physically near and reconnects
  faster; no data over BLE. *(BlePresenceService / BlePresenceAdvertiser, M04.)*
- **Multi-device tabs (M03).** A Chrome-style tab strip, one tab per paired phone, with presence
  at a glance and "+" to pair another; closing a tab ≠ unpairing.
- **Offline device memory (M02).** The UI never blanks on disconnect — last-known state renders
  dimmed under a "Last seen HH:MM" banner. Sensitive text is never persisted.

## Files

- **Full file manager.** Browse internal storage and SD cards, download/upload with progress and
  cancellation, conflict-safe naming, rename/delete/new-folder. *(FileService, works over ADB
  sync or the files channel on Direct TLS.)*
- **Drag from Explorer to the phone.** Drop files from Windows onto Linc to send them.
- **Two-way share (M19).** "Send to PC" from the Android share sheet, and "Share to phone" from
  the desktop Files page; shared files land in `Pictures\Linc/Shared` / `Downloads\Linc`.
- **Folder & photo sync (M18c).** A desktop-orchestrated engine keeps a chosen folder pair in
  sync (bidirectional, newest-wins, no delete propagation) and pulls camera photos to
  `Pictures\Linc/Photos`, with persistent seen-state as the loop guard.

## Screen

- **Phone → PC mirroring.** View and control the phone in its own window via scrcpy, with quality
  presets and quiet auto-restart. *(MirrorService, mirror channel 1, ADB only.)*
- **PC → phone mirroring, the reverse mirror (M05).** A fullscreen, spacedesk-style landscape
  viewer on the phone shows the PC and drives it: one finger is the mouse, two fingers zoom/pan,
  and a real keyboard forwards typing (the phone raises its keyboard automatically when the PC
  reports a focused text field). *(DXGI capture → hardware Media Foundation H.264 → pc-video
  channel 5 → MediaCodec; touches → `pc.input` → `SendInput`.)*

## Clipboard, notifications, media

- **Clipboard sync.** Copy on one device, paste on the other, with echo protection; a clipboard
  history widget (in-memory only). *(ClipboardSyncService / ClipboardBridge, protocol v1.)*
- **Notification bridge.** Phone notifications appear as Windows toasts and in an in-app center;
  dismissing on the PC dismisses on the phone. **Rich notifications**: app icons, category,
  conversation, actions, and **inline reply**. *(NotificationSyncService / LincNotificationListener,
  protocol v2/v6.)*
- **Media control.** See and control the phone's playback (album art, seek). *(MediaSyncService /
  MediaBridge, protocol v6.)*
- **PC media on the phone (M19).** The PC's own media appears and is controllable from the phone's
  Home screen — including players that publish nothing to Windows (via the classic Winamp-remote
  reader). *(PcMediaService / WinampRemote, protocol v13.)*

## Messages & calls (sideload builds only, D-016)

- **Messages (SMS) lane (M18a).** Read conversations and send SMS from the PC; inbound messages
  arrive live. No default-SMS-app needed. *(SmsProvider / SmsReceiver, protocol v11.)*
- **Calls lane (M18b).** See the call log, dial from the PC, decline, and get an incoming-call
  banner + toast. No PC call audio (D-017). *(CallProvider / CallStateWatcher, protocol v12.)*

## Device info & appearance

- **Live device details.** Battery, storage, CPU load, RAM, Wi-Fi signal/link speed, uptime, plus
  session counters. *(DeviceStatusReporter, protocol v3.)*
- **Home page.** A phone-preview card, quick actions (ring, DND, screenshot), the media widget,
  clipboard history, a Pixel-shade-style notification shade, Messages/Calls Home widgets, a
  "shared from phone" widget, and a connection/health card. *(HomeViewModel, M14/M10.)*
- **Live theme matching (D-011).** The desktop retints itself to the phone's wallpaper-derived
  Material You palette within one status cycle; the real wallpaper appears as a blurred backdrop.
- **Quick actions.** Ring the phone (`device.locate`), toggle Do Not Disturb (`dnd.set`), set the
  sound profile (`sound.set`), take a screenshot to `Pictures\Linc`.

## Diagnostics

- **Activity log on both apps.** Bounded ring buffer (Info/Warn/Error); the desktop also writes
  rolling daily NDJSON files. *(LogService / LogStore.)*
- **Settings power tools.** Live ADB device list + port forwards, restart-server, forget-device,
  sync toggles, and one deliberate **"run an ADB shell command" box** (D-009) — the single place
  ADB is exposed on purpose.
- **Plain-language errors (D-001).** Every ADB/scrcpy failure is translated into guidance; raw
  tool output never reaches the UI (except the D-009 box).

## Future ideas (not committed here)

Call-audio routing over Bluetooth HFP (D-017); a phase-2 in-app mirror agent (`linc-agent.dex`,
D-015); a MediaProjection fallback mirror for ADB-less sessions; phone-as-webcam; FCM push-wake
(ties into the deferred Google scope, D-007); cross-device drag-and-drop of clipboard files;
macOS/Linux desktop ports; Wi-Fi Direct / hotspot fallback. The **next committed phase** of work
is planned separately in `docs/ROADMAP.md`.
