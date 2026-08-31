# Tech Stack

> Status: provisional — chosen before implementation begins. Any change must be recorded in DECISIONS.md and reflected here.

## Android app (`ANDROID/`)

| Technology | Choice | Rationale |
|---|---|---|
| Language | Kotlin | Modern Android standard; null safety; coroutines for the companion service. |
| UI | Jetpack Compose + Material 3 | Fast iteration for onboarding/status screens; current Android UI standard. Bottom `NavigationBar` (already in the Material3 BOM) handles the app's 3 destinations — no `androidx.navigation` dependency added, since a manual `enum Screen` switch is simpler at this scale. |
| Min / target SDK | minSdk 30 (Android 11) / latest stable target | Wireless debugging (ADB pairing over Wi-Fi) requires Android 11+ — it is core to the product. |
| Concurrency | Kotlin Coroutines + Flow | Socket server and event relays are naturally async/stream-shaped. |
| Background work | Foreground Service | Companion socket server and notification relay must outlive the UI. |
| Notifications access | NotificationListenerService | Only sanctioned API for reading/dismissing notifications. |
| Serialization | kotlinx.serialization (JSON) | Companion protocol messages; multiplatform-friendly spec. |
| Build | Gradle (Kotlin DSL), Android Gradle Plugin | Standard toolchain. |

## Windows app (`DESKTOP/`)

| Technology | Choice | Rationale |
|---|---|---|
| Language / runtime | C# / .NET 8 (LTS) | First-class Windows integration (tray, toasts, Explorer drag-drop), strong async model, single-file publish. |
| UI | WinUI 3 (Windows App SDK) | Native modern Windows look; supports the "feels like part of Windows" goal. `NavigationView`/`Frame`, `BreadcrumbBar`, and `CommandBar` (the multi-page shell, Files page) all ship in the already-referenced SDK — no new package needed for the 2026-07-11 UI overhaul. |
| ADB protocol | AdvancedSharpAdbClient 3.6.16 | Mature .NET ADB client — device tracking, sync (push/pull), shell, forward — without shelling out for every operation. |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | Source-generated observable properties and commands; the de-facto .NET MVVM standard. |
| DI | Microsoft.Extensions.DependencyInjection 10.0.9 | Service registration per CONTRIBUTING.md's interface + DI rule. |
| Design language | Material 3 Expressive token dictionary (`Themes/MaterialExpressive.xaml`) | Consistent Linc branding across phone and PC (see DECISIONS.md D-008); overrides Fluent colors/shapes/type. |
| ADB server | Bundled `adb.exe` (Android platform-tools) | Linc owns the server lifecycle so users never install or run ADB themselves. |
| Screen mirroring | Bundled scrcpy | Best-in-class open-source mirroring (MIT); Linc manages it as a child process rather than reimplementing video pipelines. |
| Discovery | Zeroconf 3.7.16 | Android wireless debugging advertises `_adb-tls-connect._tcp` (and `_adb-tls-pairing._tcp` while pairing); polling these enables zero-config discovery and hands-free pairing. |
| QR codes | QRCoder 1.8.0 | Renders the ADB QR-pairing payload for the in-app pairing flow; pure managed, no native deps. |
| Tray icon | H.NotifyIcon.WinUI 2.2.0 | WinUI 3 has no built-in tray support; pinned to 2.2.0 (last net8.0-compatible release — 2.4+ requires net10). |
| Serialization | System.Text.Json | Companion protocol messages; built-in, fast. |
| Packaging | MSIX or Inno Setup + auto-update (decide at M10) | Deferred until Polish milestone. |

## Shared

| Item | Choice | Rationale |
|---|---|---|
| Companion protocol | Versioned JSON messages over an ADB-forwarded localhost socket | Human-readable, easy to evolve with a version field; documented spec is the only shared artifact between apps. |
| Docs | Markdown in `claude-docs/` | Single source of truth; reviewed alongside code changes. |
| Version control | Git | Standard. Repo to be initialized at M0. |

## External dependencies & licensing notes

- **Android platform-tools (adb)** — Apache 2.0; redistribution permitted with license notice.
- **scrcpy** — MIT / Apache 2.0; redistribution permitted with license notice.
- **AdvancedSharpAdbClient** — MIT.
- Bundled binaries are pinned to specific versions and updated deliberately (record bumps in CHANGELOG.md).
