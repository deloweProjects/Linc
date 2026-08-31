# Overview & Vision

## What Linc is

Linc is a phone-to-PC companion system that links an Android phone with a Windows PC. It is two
applications working together:

- **Linc Android** — a companion app that runs on the phone.
- **Linc Desktop** — a Windows app that discovers, connects to, and controls the phone.

Under the hood Linc uses ADB (Android Debug Bridge), including wireless debugging, as its
transport and control channel, complemented by a direct app-to-app TLS link on the LAN. On top
of that foundation Linc delivers file management, screen mirroring (both directions), clipboard
sync, notifications, messages, calls, media control, and live device information.

## Mission

**Make an Android phone feel like a natural extension of a Windows PC — with zero terminal
commands, zero cable-fiddling, and zero ADB knowledge required.**

## The target user experience

1. Install Linc on the phone and on the PC.
2. Pair once (QR code or 6-digit pairing code) — guided entirely by the apps.
3. From then on, the phone and PC find each other and reconnect **automatically** whenever they
   share a network (or a cable).
4. Everything — files, screen, clipboard, notifications, messages, calls, media — is available
   through a clean, modern desktop UI that retints itself to match the phone's colors.

The defining principle: **users should rarely, if ever, interact with ADB directly.** ADB is an
implementation detail. Linc bundles, configures, launches, and manages ADB itself. If something
goes wrong at the ADB layer, Linc explains it in plain language and offers a guided fix — it
never asks the user to open a terminal. The desktop even installs and provisions the phone app
for you over ADB, so the phone-side setup is little more than enabling wireless debugging and
scanning a QR code.

## Guiding principles

**Reliability over feature count.** A small set of features that always work beats a large set
that sometimes works. Every milestone is "done" only when its features work reliably, are
covered by automated tests where practical, and the docs reflect reality — and the app must
still pass a hardware check at each milestone boundary.

**ADB is transport, never interface (D-001).** A raw `adb` command surfacing to the user is a
bug. The single sanctioned exception is one labelled power-user shell box on the Settings page
(D-009).

**The two apps share a contract, not code (D-006).** They exchange a versioned JSON protocol and
nothing else. Each side is implemented in its own language and can be built, tested, and shipped
independently; version negotiation keeps mismatched builds working.

**Both apps look alike, driven by the phone (D-011).** The desktop follows the phone's Material
You dynamic color live over the wire, so changing the phone's wallpaper retints the PC app within
one status cycle.

**Self-verification is standing policy (D-031).** Whoever does the work (human or AI) exhausts
everything a machine can check — unit tests, desktop-emulating ADB probes, scripted round-trip
harnesses, screenshots — and hands the user only the genuinely unautomatable checks: physical
movement (Wi-Fi range, cables), real calls/SMS to real contacts, and subjective feel (latency,
animation smoothness).

## What you can do with it

- **Pair once, connect forever.** Scan a QR code (or type a 6-digit code) one time; after that
  the phone and PC find each other and reconnect automatically over Wi-Fi or USB, surviving
  reboots, sleep, and network changes.
- **Browse and transfer files.** A full file manager for the phone's internal storage and SD
  cards, with drag-and-drop from Windows Explorer, progress, and cancellation.
- **Mirror the phone's screen** in its own window (via scrcpy), with quality presets.
- **Mirror and control the PC from the phone** — a fullscreen, spacedesk-style viewer with
  pinch-zoom and a real keyboard.
- **Sync the clipboard** — copy on one device, paste on the other.
- **Bridge notifications** — phone notifications appear as Windows toasts and in an in-app list;
  dismissing on the PC dismisses on the phone; rich actions and inline reply work.
- **Control media** — see and control the phone's playback (and the PC's, on the phone).
- **Messages and calls** — read and send SMS, see the call log, dial and decline from the PC
  (sideload builds only).
- **Keep folders and photos in sync** between the phone and PC.
- **See live device details** — battery, storage, CPU load, RAM, Wi-Fi signal, and uptime.
- **Match colors automatically** — the desktop retints itself live to the phone's wallpaper
  palette.
- **Use several phones** — a Chrome-style tab strip switches between paired devices.
- **Know when the phone is near** — a Bluetooth presence hint nudges reconnection the moment the
  phone is physically close.
- **Diagnose** — an activity log on both apps, plus a desktop Settings page with ADB status,
  device list, port forwards, and safe recovery actions.

## Long-term goals

- **Effortless pairing** — one guided setup flow, never repeated unless the user unpairs.
- **Automatic discovery** — the desktop finds the phone on the LAN without manual IP entry.
- **Automatic reconnection** — links survive reboots, sleep, and network changes without user
  action.
- **Full-featured file manager** — browse, transfer, and organize phone storage from the PC.
- **Low-latency screen mirroring** in both directions.
- **Seamless clipboard sync.**
- **Notification bridge** with rich actions.
- **Reliability over feature count.**

Google-account integration remains a deliberately deferred, scope-gated idea (D-007): it is only
worth building where it adds clear value (for example, an FCM push to wake a Dozing phone on
cellular), and that value case has not yet justified the scope.

## Platform scope

- **Android 11+ (API 30) only (D-002).** Wireless debugging with in-band pairing — core to the
  product — requires Android 11 or newer.
- **Windows** for the desktop app (WinUI 3 / .NET 8). macOS/Linux desktop ports are a future
  idea, not a commitment.
