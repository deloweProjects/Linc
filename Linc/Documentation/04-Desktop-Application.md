# The Desktop Application (Linc Desktop)

`DESKTOP/Linc.Desktop/` — a Windows app in **C# / .NET 8 (LTS)** with a **WinUI 3** (Windows App
SDK) UI, MVVM via the CommunityToolkit, and dependency injection via
`Microsoft.Extensions.DependencyInjection`. It owns the entire connection lifecycle and every
piece of orchestration; the phone is a comparatively dumb server behind the protocol.

## Shape of the app

The desktop is a `NavigationView` shell (`AppShell`) hosting one `Page` per destination, with a
Chrome-style **device-tab strip** in the custom title bar and a **system-tray** presence
(`H.NotifyIcon.WinUI`, close-to-tray). It talks to the phone through **AdvancedSharpAdbClient**
over ADB and through a custom mutual-TLS listener over Direct TLS.

Three architectural rules run through the whole app:

1. **Eagerly-started services live in `AppShellViewModel`.** Anything that must run regardless of
   which page is open — the connection supervisor, clipboard sync, notifications, battery alerts,
   the sync engine, PC-media, share sweeping — is constructed there, never in a page's view
   model. (A past bug where `supervisor.Start()` sat in the Device page meant the app never
   connected unless you opened that page.)
2. **The UI never touches ADB.** Every ADB call goes through the service layer; raw adb/scrcpy
   output never reaches the UI, except the one deliberate power-user shell box (D-009).
3. **All view state is observable.** MVVM Toolkit source-generated observable properties and
   commands; no code-behind reaches into services except for a few XAML-binding limitations
   (gradient stops, layout math) that are documented in the code.

## The service layer (`Services/`)

The services divide into the ADB/transport layer, the connection lifecycle, and the feature
services.

### ADB & transport

- **`AdbServerHost`** — owns the bundled adb server's lifecycle (start, stop, recover) so the
  user never runs adb.
- **`ToolLocator`** — finds adb and scrcpy (bundled dir, PATH, or winget install location).
- **`ConnectionManager`** — establishes and tears down a link: wireless `adb connect` or direct
  USB, then `getprop`, `adb forward tcp:0 localabstract:linc`, the protocol handshake, and the
  TLS certificate exchange. Exposes the current connection, whether it has ADB (`HasAdb`), and
  the phone-dialled channel-open mechanism.
- **`ConnectionSupervisor`** — the reconnection state machine (NoDevice / Searching / Connecting
  / Connected / Paused): reconnect count, sticky last error, a 15-second health loop, per-address
  exponential backoff, transport ranking and preemption, and recovery on network change and
  resume-from-sleep.
- **`DiscoveryService`** — the mDNS listener (`_adb-tls-connect` / `_adb-tls-pairing`, and the
  desktop's own `_linc._tcp` advertising).
- **`UsbWatcherService`** — polls the ADB device list for USB-attached phones; a paired serial
  auto-connects, an unknown USB device needs one explicit confirmation.
- **`TlsTransportService`** — the Direct-TLS listener on :46001 (mutual TLS, silent drop of
  unknown certs), `_linc._tcp` advertising with real-LAN addresses only, and channel dial-back
  waiters.
- **`BlePresenceService`** — background-scans for the phone's BLE beacon via WinRT
  `BluetoothLEAdvertisementWatcher` (works from an unpackaged process, no capability
  declaration); a match nudges discovery.
- **`PairingService`** — drives the ADB wireless pairing flow (QR payload, 6-digit code).
- **`PhoneSetupService`** — installs the companion APK on a phone that lacks it and grants
  everything ADB can grant (notification listener, runtime permissions, all-files access).

### The protocol client

- **`CompanionClient`** — the stream-agnostic protocol client: a receive loop with a
  pending-request map plus unsolicited-message events, `SendAsync` for fire-and-forget PC→phone
  pushes, and a per-transport channel opener (ADB dials the forward port; TLS sends
  `channel.open` and awaits the phone's dial-back).

### Feature services

- **`FileService`** / **`TlsFileService`** / (router) — list/push/pull over ADB sync, or over the
  files channel on Direct TLS, behind one `IFileService` interface. Rename/delete/mkdir/screenshot
  are ADB-only (`TlsFileService` throws `NeedsAdb()` for those, D-024).
- **`MirrorService`** — owns the **scrcpy child process** for phone→PC mirroring (located via
  `ToolLocator`, launched with no console, ADB env pinned), with quality presets and quiet
  auto-restart. *(This is the current engine; "swallow scrcpy" = bundle its binaries.)*
- **The reverse-mirror pipeline** (PC→phone):
  - **`PcScreenCapture`** — DXGI Desktop Duplication (Vortice D3D11/DXGI); sizes its ring buffer
    from the first real captured frame.
  - **`PcVideoEncoder`** + **`MediaFoundation`** — a hardware Media Foundation H.264 MFT (no
    software fallback, D-039); Media Foundation is hand-rolled interop, not a package. The
    captured BGRA texture is fed as ARGB32 with no color-conversion stage.
  - **`PcMirrorSource`** — two threads (one drains the async encoder's event queue; the other
    captures and submits on absolute deadlines, D-040). **Built and run on MTA thread-pool
    threads** so its COM calls don't marshal onto the WinUI UI thread and hang it.
  - **`PcMirrorService`** — frames encoded packets onto the pc-video channel (5), honoring the
    phone's requested fps/bitrate.
  - **`PcInputService`** — injects the phone's touches with `SendInput`, mapping
    video→physical→virtual-desktop coordinates (the process is PerMonitorV2 so every step is in
    physical pixels).
- **`ClipboardSyncService`** — bidirectional clipboard with echo protection and an in-memory
  history (never persisted or logged).
- **`NotificationSyncService`** — relays phone notifications to toasts + the in-app center;
  cross-device dismiss.
- **`MediaSyncService`** — the phone's media state/control widget.
- **`PcMediaService`** + **`WinampRemote`** — reports the *PC's* own media to the phone: primarily
  Windows' `GlobalSystemMediaTransportControlsSessionManager`, with a classic Winamp-remote-window
  reader as a fallback for players (like AIMP) that publish nothing to Windows.
- **`ThemeSyncService`** — receives the phone's Material You palette in every `status` and
  retints the `MaterialExpressive.xaml` brushes live; also owns wallpaper fetching, dominant-color
  extraction, and the app accent/scrim.
- **`DeviceRegistry`** — the paired-device registry; every per-device setting (address, pinned
  TLS cert, sync lanes, folder pair, connection preference) lives on the `KnownDevice` record, and
  the registry resolves the old singleton property names against the active device.
- **`DeviceCacheService`** — persists the innocuous half of a device's last-known state per serial
  under `%LOCALAPPDATA%\Linc/cache\<serial>\`, so the UI never blanks on disconnect.
- **`SyncEngine`** — the desktop-orchestrated folder/photo sync engine built on the files channel,
  with persistent seen-state as the loop/duplicate guard.
- **`ShareService`** — pulls files the phone shared (sweeps the phone's outbox on every connect).
- **`BatteryAlertService`** — low-battery toast with re-arm.
- **`LogService`** — the activity log (bounded ring buffer, plus a rolling daily NDJSON file
  under `%LOCALAPPDATA%\Linc/logs`).
- **`LincException`** — the app's plain-language error type; user-facing copy never leaks raw
  adb/scrcpy text.

## View models (`ViewModels/`)

One per page, plus the shell and the tab strip:

- **`AppShellViewModel`** — the composition root for eagerly-started services and navigation.
- **`DeviceTabsViewModel`** — the Chrome-style multi-device tab strip.
- **`HomeViewModel`** — the landing page: phone-preview card, quick actions, media widget,
  clipboard history, notification shade, Messages/Calls Home widgets, "shared from phone" widget.
- **`DeviceViewModel`** — connection health, pairing cards, the connection-method picker, embedded
  mirror controls.
- **`FilesViewModel`** — the phone file browser (breadcrumb navigation, transfer, share-to-phone).
- **`SyncViewModel`** — the Sync page: lane toggles, Messages conversations + reply, Calls log +
  dialer + incoming banner, folder/photo sync controls.
- **`NotificationsViewModel`**, **`DetailsViewModel`**, **`LogsViewModel`**,
  **`SettingsViewModel`**, **`MirrorViewModel`** — the remaining pages.

## Pages (`Views/`)

`HomePage`, `DevicePage`, `FilesPage`, `SyncPage`, `NotificationsPage` *(rendered within Home in
the current design)*, `DetailsPage`, `LogsPage`, `SettingsPage` — each a `Page` with matching
`.xaml` and `.xaml.cs`. Code-behind is kept to XAML-binding limitations only (e.g. gradient-stop
colors, custom layout math, pointer handling), documented inline.

## Theming

`Themes/MaterialExpressive.xaml` is a Material 3 Expressive token dictionary that overrides
Fluent's colors/shapes/type. It defines the fallback Linc-purple palette and the named brushes
`ThemeSyncService` retints live from the phone's palette (18 roles, mapped by name). See
`Documentation/09-Design-Decisions.md` (D-008, D-011) and `docs/THEME.md`.

## Persistence

- **`%LOCALAPPDATA%\Linc/settings.json`** — addresses, paired devices, per-device settings,
  toggles.
- **`%LOCALAPPDATA%\Linc/cache\<serial>\`** — offline device memory (M02).
- **`%LOCALAPPDATA%\Linc/sync-state.json`** — the sync engine's per-pair seen-state.
- **`%LOCALAPPDATA%\Linc/logs\`** — rolling daily activity logs.

## Build note

Plain `dotnet build` **cannot** build a WinUI app (it lacks the PRI resource-packaging tasks that
ship only with Visual Studio). Build from the IDE, or with **VS MSBuild**:

```
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -find MSBuild\**\Bin\MSBuild.exe
& $msbuild DESKTOP\Linc.Desktop\Linc.Desktop.csproj -restore -p:Configuration=Debug -p:Platform=x64
```

Kill any running `Linc.Desktop` process first, or the build fails on a locked exe. See
`Documentation/08-Build-Test-and-Deploy.md` for the full build/verify story.
