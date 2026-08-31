# The Android Application (Linc Android)

`ANDROID/` — the phone-side companion in **Kotlin** with a **Jetpack Compose + Material 3** UI,
**minSdk 30 (Android 11)** (D-002), Kotlin Coroutines + Flow for concurrency, and
kotlinx.serialization for the protocol. Its package root is `app.linc.android`.

## Why the companion exists

Most of what Linc does could, in principle, ride on ADB alone. The companion app exists for the
things **ADB cannot do**, which are precisely the things that need an app context or a system
listener:

- **Notifications** — reading and dismissing them requires a `NotificationListenerService`.
- **The focused-app clipboard** — on Android 10+ only the focused app may read the clipboard, so
  phone→desktop clipboard events can only come from a running app.
- **The Material You palette** — deriving the wallpaper palette needs an app context.
- **SMS, call log, media sessions, file access** — these are content providers and system
  services an app queries, not shell commands.
- **A standing presence** — a foreground service can dial out to the desktop on the LAN over
  Direct TLS, independent of ADB and wireless-debugging state.
- **A BLE presence beacon** — advertised by the app so the desktop knows the phone is near.

## The foreground service

The heart of the app is **`CompanionService`** (`service/CompanionService.kt`), a foreground
service that outlives the UI and hosts everything. It is `android:exported="true"` — a single
line that matters a great deal: a non-exported service refuses `am start-foreground-service`,
which is exactly why every install used to demand a manual "Start" tap. Exporting it let the
desktop start/revive the service itself over ADB (its socket was already reachable by any app on
the device, so exporting grants a caller nothing new).

### The socket server and session layer

**`SocketServer`** hosts `LocalServerSocket("linc")` (reached by the desktop via `adb forward`)
and, since the pipeline era, serves **every connection on its own coroutine**. The first frame on
a connection routes it:

- A headerless connection starting with `hello` is the **control connection** (exactly the legacy
  v≤4 behavior). Only the control connection may touch `CompanionStateHolder`, `CompanionOutbox`,
  and session cleanup — if a bulk-channel close ran that teardown it would knock the UI to
  "Listening" and clear the outbox while control is still up.
- A connection whose first frame is a `{channel, sessionToken}` header is a **typed channel**
  (files, bulk, pc-video, …), token-checked against the `SessionRegistry`.

The same server also serves generic stream pairs over TLS (`serveExternal` for SSL sockets) for
the Direct-TLS transport, using the identical dispatch.

### The outbox

**`CompanionOutbox`** carries unsolicited phone→desktop messages (notifications, media, incoming
calls/SMS, PC-media control), gated on the negotiated protocol version. Registrations are an
**owned stack** (a copy-on-write list): sends go to the newest live control connection and each
connection releases only *its own* handle — a fix for a bug where a short-lived control
connection dropping would null the slot a different, still-healthy connection owned, silently
killing every push.

## Providers and bridges (`service/`)

Each feature the phone offers is a small, focused class the socket server dispatches to:

| Component | Responsibility |
|---|---|
| `CompanionStateHolder` | Holds the live connection/handshake state and protocol version. |
| `SessionRegistry` | Issues and validates session tokens for typed channels. |
| `DeviceStatusReporter` | Battery, storage, CPU load, RAM, Wi-Fi, uptime → the `status` payload. |
| `LincNotificationListener` | The `NotificationListenerService`: relays, actions, inline reply, cross-device dismiss. |
| `ClipboardBridge` | Clipboard events both ways (phone→PC only while the app is focused). |
| `ClipboardHistoryStore` | In-memory clipboard history (capped, never persisted). |
| `MediaBridge` | Wraps `MediaSessionManager` → `media.state` / `media.control`, with sticky primary-session election. |
| `LargeIconCache` / `BulkResources` | Serve small binaries (app icons, album art, avatars, wallpaper, photo thumbnails) over the bulk channel. |
| `PhotosProvider` | `photos.recent` from MediaStore + bulk photo thumbnails. |
| `WallpaperProvider` | The wallpaper id + a small pre-blurred thumbnail (needs all-files access). |
| `FileChannelServer` | The files-channel list/pull/push server (needs all-files access). |
| `SmsProvider` / `SmsReceiver` | SMS list/send + inbound `sms.received` (sideload builds only). |
| `CallProvider` / `CallStateWatcher` | Call log, dial, decline + inbound `call.incoming`. |
| `SyncStore` | Persists which sync lanes are on (`sync.config`). |
| `PcMediaStore` | Holds the PC's media state pushed from the desktop, for the phone's Home widget. |
| `ShareStore` | Staging for two-way file share (phone outbox + incoming). |
| `LocateRinger` | The "ring my phone" full-volume locate action. |
| `OutgoingActions` | Helpers for phone→desktop control messages. |
| `MirrorReceiver` / `MirrorControl` / `MirrorSettings` / `VideoFrame` | The reverse-mirror client: decode the PC's H.264 (pc-video channel) with MediaCodec, send touches/keys back as `pc.input`, and carry the quality preset. |
| `LogStore` | The phone-side activity log (bounded ring buffer). |

## Transport & identity (`service/`)

- **`PresenceClient`** — the standing-presence loop: discovers the desktop's `_linc._tcp` advert
  (NsdManager), dials out over Direct TLS with a widening backoff, and sets `adb reverse` for the
  cable path. Gated on a "Background connection" preference (on by default, D-014).
- **`TlsIdentity`** — the phone's Direct-TLS identity: an AndroidKeyStore EC key (with
  `DIGEST_NONE` included for TLS). **The KeyStore is wiped on uninstall**, which is why the
  desktop re-exchanges certificates on every connect.
- **`TransportStore`** — persists the pinned desktop certificate and transport prefs.
- **`BlePresenceAdvertiser`** — advertises the 10-byte non-connectable beacon (company id
  `0xFFFF`, `"LC"` + `SHA-256(TLS cert ‖ 5-minute slot)` truncated). No data ever rides BLE.

## Activities and UI (`ui/`)

The app is small and screen-based; navigation is a manual `enum Screen` switch (no
`androidx.navigation` dependency) rendered as a bottom `NavigationBar`.

- **`MainActivity`** — hosts the Compose UI (`LincApp`), applies the `Scaffold` inner padding so
  screen content isn't hidden behind the nav bar.
- **`MirrorActivity`** — the fullscreen, landscape reverse-mirror viewer: a `TextureView` with
  view-level pinch-zoom and pan (a `Matrix` decoupled from the stream), one finger as the mouse,
  a hidden `EditText` forwarding typing to the PC, a floating tool bar (Keyboard / Win / Esc /
  Alt+Tab / End), and a self-heal loop that retries `pc.mirror.start` until frames flow.
- **`ShareReceiverActivity`** — the Android share-sheet target (`ACTION_SEND`) that forwards
  shared content to the PC.
- **Screens** (`ui/`): `HomeScreen` (with the connection & health card), `StatusScreen`,
  `OnboardingScreen`, `ShareScreen`, `SettingsScreen` (permission grants + "View logs"),
  `LogsScreen`, plus `Screen.kt` (the nav enum).
- **Theme** (`ui/theme/`): `Theme.kt` uses `dynamicLightColorScheme`/`dynamicDarkColorScheme`
  (Android 12+) and falls back to the fixed Linc-purple `ColorScheme` in `Color.kt` on Android 11.

## The protocol implementation (`protocol/`)

- **`Framing.kt`** — the length-prefixed frame codec.
- **`Protocol.kt`** — the message envelope and typed message models, mirroring `docs/PROTOCOL.md`.

This is implemented **independently** from the desktop's C# `Linc.Desktop.Protocol` — no shared
code, only the shared spec (D-006). Protocol changes always bump the version in `PROTOCOL.md`
first, gate traffic on the negotiated version, and preserve unknown-type/field tolerance.

## Permissions

The companion requests a broad set (notification listener, all-files access, SMS/call/phone
permissions, Bluetooth advertise, internet) — but M01 means **the desktop grants most of them
over ADB automatically** (`cmd notification allow_listener`, `pm grant`, `appops set
MANAGE_EXTERNAL_STORAGE allow`). The Settings screen still surfaces the grants the user can toggle
and a "View logs" button. SMS/call features are compiled into **sideload builds only** (D-016);
a Play-ready build compiles those lanes out.

## Build note

`JAVA_HOME` must point at Android Studio's bundled JBR, then:

```
cd ANDROID
gradlew.bat assembleDebug testDebugUnitTest --no-daemon
```

The JVM heap is pinned to 1 GB in `gradle.properties` for the dev machine's memory limit; a JVM
error 1455 means "free memory," not a code problem.
