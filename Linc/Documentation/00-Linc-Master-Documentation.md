# Linc — Master Documentation

> # ⚠️ STALE AS OF 2026-08-12 — READ THIS FIRST
>
> **Every file in this folder was last written 2026-07-22, before the Convergence phase produced
> anything. Thirteen milestones have shipped since. Treat the chapters below as the settled account of
> the project up to July 2026 and NOTHING MORE.**
>
> **For current reality, read `agent-docs\opencode-docs\` — `BRAIN.md` (state and gotchas),
> `PROTOCOL.md` (the wire spec), `ROADMAP.md` (position), `DECISIONS.md` (D-001…D-064).**
>
> **The specific claims in this folder that are now WRONG:**
>
> | This folder says | Reality on 2026-08-12 |
> |---|---|
> | Protocol **v13/v14** | **v18**, both sides |
> | No Apps section | Apps inventory over `apps.get` (**v16**), per-app windows via scrcpy `--start-app` |
> | No Desktop Mode | Ships, as a **second screen** — real windowing dropped, the platform declares no freeform support (D-053) |
> | No local database | **SQLite `LincStore`**, opt-in notification history (default **off**), offline outbox, cache-first Home + Sync |
> | Phone cannot drive the PC without the mirror | **Tools page** — remote keyboard + trackpad over `pc.input`, no video; plus **v17** `pc.control` for lock / sleep / shutdown / restart / volume / PC brightness |
> | Wireless needs a shared LAN | **v18 hotspot links** — no discovery at all; measured **514 ms** end to end |
> | Not packaged | Self-contained publish, app-local VC++ runtime, crash logging, tray icon, opt-in **start on sign-in** |
> | Purple UI | **Black and white by default**, new Linc mark on both apps |
>
> **Why it is stale, stated plainly:** the master session kept `BRAIN.md`, `STATUS.md`, `PROTOCOL.md`,
> `DECISIONS.md` and the agent ledger current every session, and let this folder drift. Bringing these
> eleven narrative chapters back to reality is a real piece of work — closer to a rewrite than an edit
> for the architecture, protocol and feature chapters — and it has not been done. **This banner exists
> so nobody is misled in the meantime.**

> **This folder (`Documentation/`) is the formal, human-facing documentation for the Linc project.**
> It describes what Linc is, how each app works, how they connect, and how it was built.
> It is written to be read by people — engineers joining the project, reviewers, or the
> project owner — rather than by an automated agent.
>
> The `docs/` folder is a different thing: it is the *working* documentation an AI or human
> contributor reads to pick up in-flight work (roadmap, decisions log, living context/BRAIN,
> protocol spec). When the two disagree about tactical detail, `docs/` is newer; when you
> want the settled, formal picture of the whole system, read here.

---

## 1. What Linc is, in one paragraph

**Linc links an Android phone to a Windows PC and makes the phone feel like part of the PC** —
files, screen, clipboard, notifications, live device stats, messages, calls, media, and
two-way screen control — through a clean desktop app, with the hard rule that **the user never
touches ADB or a terminal**. Under the hood everything rides on ADB (Android Debug Bridge,
including wireless debugging) plus a direct app-to-app TLS channel, but Linc bundles,
configures, launches, and manages all of that invisibly. It is two independent programs that
share no code — only a versioned wire protocol:

- **Linc Android** — a lightweight Kotlin/Jetpack Compose companion app that runs a foreground
  service on the phone.
- **Linc Desktop** — a C# / .NET 8 / WinUI 3 Windows app that discovers, connects to, and
  drives the phone.

## 2. How to read this documentation

The files in this folder are ordered so you can read them top to bottom, but each stands alone:

| File | What it covers |
|---|---|
| `00-Linc-Master-Documentation.md` | **This file** — the map of everything, the elevator pitch, and the system at a glance. |
| `01-Overview-and-Vision.md` | What the product is for, the mission, the target user experience, guiding principles. |
| `02-History.md` | How Linc was built, milestone by milestone — the two development eras and every shipped capability, in order. |
| `03-System-Architecture.md` | The whole two-app system: layers, services, dependency boundaries, data flows. |
| `04-Desktop-Application.md` | The Windows app in depth — every service, view model, and page, and the connection lifecycle. |
| `05-Android-Application.md` | The Android companion in depth — the foreground service, the socket server, every provider and bridge. |
| `06-Connectivity-and-Protocol.md` | **How the two apps actually connect** — transports (ADB forward, ADB reverse, Direct TLS), discovery, pairing, presence, the session/channel model, and the full v14 wire protocol. |
| `07-Feature-Catalog.md` | Every user-facing capability, what it does, and which app/protocol pieces power it. |
| `08-Build-Test-and-Deploy.md` | Building both apps, the committed verification harnesses, the dev-machine facts, and the packaging story. |
| `09-Design-Decisions.md` | The load-bearing architectural decisions and the reasoning behind each. |
| `10-Glossary.md` | Terms, acronyms, and component names in one place. |

## 3. The system at a glance

```
┌─────────────────────────────────┐                       ┌──────────────────────────────────┐
│           Windows PC            │                       │            Android Phone          │
│                                 │                       │                                   │
│  ┌───────────────────────────┐  │                       │  ┌─────────────────────────────┐  │
│  │       Linc Desktop        │  │                       │  │        Linc Android         │  │
│  │  WinUI 3 shell + services │  │                       │  │  Compose UI + foreground svc │  │
│  └─────────────┬─────────────┘  │                       │  └──────────────┬──────────────┘  │
│                │                │   3 physical paths:   │                 │                 │
│  ┌─────────────▼─────────────┐  │  A. ADB forward (USB  │  ┌──────────────▼──────────────┐  │
│  │   Transport supervisor    │◄─┼──  or Wi-Fi debug)    ┼─►│  adbd  +  LocalServerSocket   │  │
│  │  ranks & races transports │  │  B. adb reverse       │  │  ("linc")  +  TLS listener    │  │
│  │                           │  │  C. Direct TLS (LAN)  │  │                               │  │
│  └───────────────────────────┘  │                       │  └───────────────────────────────┘  │
│  bundled adb.exe + scrcpy       │   mDNS + BLE presence │   BLE beacon (presence hint only)  │
└─────────────────────────────────┘                       └──────────────────────────────────┘
```

Over whichever physical path is fastest, a small **session layer** runs: a single listening
socket accepts many concurrent connections, each tagged with a **channel** (control, mirror,
files, bulk, audio, pc-video). The **control channel** speaks a versioned JSON protocol
(currently **v14**); the other channels carry binary streams. Everything the product does —
notifications, media, clipboard, files, photos, messages, calls, screen mirroring in both
directions — is a client of that one backplane.

## 4. Technology, briefly

| | Android app | Desktop app |
|---|---|---|
| Language / runtime | Kotlin, minSdk 30 (Android 11) | C# / .NET 8 (LTS) |
| UI | Jetpack Compose + Material 3 (dynamic color) | WinUI 3 (Windows App SDK), `NavigationView` shell |
| Concurrency | Coroutines + Flow | `async`/`await`, MVVM Toolkit |
| Key libraries | kotlinx.serialization, AndroidKeyStore, MediaCodec | AdvancedSharpAdbClient, Vortice (D3D11/DXGI), hand-rolled Media Foundation interop, Zeroconf/Makaretu (mDNS), QRCoder, H.NotifyIcon |
| Its irreplaceable job | The things ADB can't do: read/dismiss notifications, read the focused-app clipboard, expose the Material You palette, host SMS/call/media providers | Own the whole connection lifecycle, manage adb + scrcpy, translate every ADB error into plain language |

The two apps **share no code** — only the protocol specification (`docs/PROTOCOL.md`), which is
implemented independently in Kotlin (`app.linc.android.protocol`) and C#
(`Linc.Desktop.Protocol`).

## 5. Current status (as of this writing)

The core product is **complete and hardware-verified end-to-end on a real Pixel 7** across two
development eras:

- **Era 1 (M0–M19):** foundation, pairing/discovery/reconnection, file manager, screen
  mirroring, clipboard, notifications, the unified session/channel pipeline, rich
  notifications + media, a Home page, the Direct-TLS transport, files/photos/share, and the
  Sync page (SMS + calls + folder/photo sync).
- **Era 2 (M00–M07):** a clean-slate verification sweep, an "effortless companion" (the desktop
  installs/provisions/auto-starts the phone app), offline device memory, Chrome-style
  multi-device tabs, BLE presence, and the **reverse mirror** (view and control the *PC from the
  phone*). Extend mode (a true second monitor) was deliberately **dropped**. Polish (M07) is
  partway done.

See `02-History.md` for the full milestone-by-milestone account, and `docs/ROADMAP.md` for the
*next* phase of work (the new feature set is planned there, not here).

## 6. Repository layout

```
Linc/
├── ANDROID/                 Android companion app (Kotlin, Jetpack Compose, Gradle)
│   └── app/src/main/java/app/linc/android/
│       ├── protocol/        Wire protocol (framing + message codec)
│       ├── service/         Foreground service, socket server, providers, bridges
│       └── ui/              Compose screens + theme
├── DESKTOP/                 Windows app (C# / .NET 8 / WinUI 3)
│   └── Linc.Desktop/
│       ├── Services/        Domain + ADB service layer
│       ├── ViewModels/      MVVM view models (one per page + shell + tabs)
│       ├── Views/           One Page per navigation destination
│       └── Themes/          Material 3 Expressive token dictionary
├── tools/                   Committed verification harnesses (plain net8.0 consoles)
│   ├── probe/               Desktop-emulating ADB probe over every protocol lane
│   ├── syncsim/             Runs the real SyncEngine under both transports
│   ├── devicesim/           Multi-device registry semantics
│   ├── blescan/             Proves the BLE beacon derivations agree
│   └── mirrorsim/           Reverse-mirror capture/encode/framing/coordinates
├── docs/                    Working docs for contributors (agent-facing, roadmap, protocol)
├── Documentation/                   Formal project documentation (this folder)
└── README.md
```

## 7. The one rule that explains most of the design

**ADB is an implementation detail, never an interface** (decision D-001). Everything else — the
guided onboarding, the desktop installing the companion for you, the plain-language error
translation, the bundled binaries, the "background connection" that reconnects on its own —
follows from the decision that a user should never open a terminal, type an `adb` command, or
know what ADB is. The single deliberate exception is one clearly-labelled power-user "run an ADB
shell command" box on the desktop Settings page (D-009), added at the owner's explicit request.
