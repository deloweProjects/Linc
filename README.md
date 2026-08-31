# Linc

**Your Android phone, on your PC — files, screen, notifications, clipboard, calls — over a cable or
your own Wi-Fi, with nothing in between.**

[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)
[![Status: beta](https://img.shields.io/badge/status-1.0.0--beta.1-orange.svg)](https://github.com/deloweProjects/Linc/releases)
[![Platform: Windows 10 1809+ · Android 11+](https://img.shields.io/badge/platform-Windows%2010%201809%2B%20%C2%B7%20Android%2011%2B-lightgrey.svg)](#requirements)

---

## Why this exists

Phone-to-PC apps mostly ask you to trust a company with your screen, your messages and your files.
They route through someone's servers, they want an account, and they stop working the day the
company loses interest.

Linc has no servers, no account, and no telemetry. Your phone talks to your PC directly — over the
USB cable in front of you, or over your own network — and nobody else is in the conversation. There
is no backend to shut down, because **there is no backend**. The repository you are reading is the
only infrastructure the project has.

It is also, unapologetically, a tool built for one person's daily use first and shared second. That
is why it is fast and a little opinionated, and why some of its edges are still sharp.

## How it works

Two links, ranked, with automatic failover between them:

```
          ┌─────────────────────────────┐            ┌──────────────────────────────┐
          │      Android companion      │            │      Linc.Desktop (WinUI)    │
          │  Kotlin · Compose · M3      │            │   C# · .NET 8 · MVVM         │
          └──────────────┬──────────────┘            └───────────────┬──────────────┘
                         │                                           │
                         │   versioned JSON envelopes (protocol v18) │
                         ├───────────────────────────────────────────┤
                         │                                           │
         ┌───────────────┴───────────────┐         ┌─────────────────┴──────────────┐
         │  1. ADB  (USB, or Wi-Fi ADB)  │         │  2. Direct TLS over your LAN   │
         │     forwarded local socket    │         │     used when ADB is absent    │
         └───────────────────────────────┘         └────────────────────────────────┘
                         │                                           │
                         └──────────────┬────────────────────────────┘
                                        │
                              ┌─────────┴──────────┐
                              │  scrcpy (vendored) │  screen + input, both directions
                              └────────────────────┘
```

Every message on either link is a **versioned JSON envelope**. The two ends negotiate a protocol
version on connect and refuse to guess, so a newer phone and an older PC either agree or say so
plainly. The wire format is documented in `Vibe/agent/opencode-docs/PROTOCOL.md` and is treated as
a contract — see [CONTRIBUTING.md](CONTRIBUTING.md).

## Features

- **File manager** — browse the phone, transfer both ways, delete, rename, make folders, screenshot.
- **Two-way mirroring** — the phone's screen on your PC, and your PC's screen on the phone, with
  touch, pinch-zoom and keyboard.
- **Per-app windows** — pull a single Android app out into its own desktop window.
- **Desktop Mode** — for apps that support Android's desktop windowing.
- **Clipboard sync** in both directions.
- **Notifications** on your desktop, with optional on-disk history (off by default).
- **Media remote** — transport controls and a slideshow clicker.
- **Messages and calls** — read and send SMS, see and place calls.
- **Offline cache** — the app is still useful with the phone in another room.
- **Hotspot link-up in ~500 ms** when the phone brings up a hotspot.
- **Multiple paired phones**, one active at a time.

## Status

**First public beta — `1.0.0-beta.1`.** It works, it has been used daily, and it has been verified
against real hardware. It is not polished. Being straight with you about the rough parts:

- **The APK is debug-signed.** Android will warn when you install it, and a future properly-signed
  build will *not* upgrade over it — you will have to uninstall and reinstall.
- **The desktop build ships Debug-configuration WinUI.** It is larger and slower to start than it
  should be.
- **Reverse-mirror input is hand-verified, not automated.** `adb input tap` cannot drive the mirror
  surface, so there is no regression test behind that path.
- **`Linc/Documentation/` is stale** by more than a dozen milestones and says so in a banner at the
  top. `Vibe/agent/opencode-docs/` is the current picture.
- **There is no CI.** Every build here was made and checked on one machine.

Full detail: [`Releases/RELEASE-NOTES.md`](Releases/RELEASE-NOTES.md).

## Install

1. Grab the latest **[Release](https://github.com/deloweProjects/Linc/releases)**.
2. **Desktop:** extract `Linc-Desktop-<version>.zip` anywhere and run `Linc.Desktop.exe`. It is
   self-contained; there is no installer and nothing to register.
3. **Phone:** sideload `Linc-Companion-<version>.apk`. It is **debug-signed** (see Status), so
   Android will ask you to confirm an install from an unknown source.
4. Plug the phone in over USB, or put both on the same Wi-Fi, and follow the pairing prompt.

Check your download against [`Releases/CHECKSUMS.txt`](Releases/CHECKSUMS.txt) first.

Linc keeps itself current from `Releases/update.json` in this repo. You can turn that off in
**Settings → Updates**; it will then never check for anything and you update by hand from the
Releases page.

## Requirements

| | |
| --- | --- |
| **PC** | Windows 10 1809 (build 17763) or newer, x64 |
| **Phone** | Android 11 (API 30) or newer |
| **Link** | A USB cable, or both devices on the same Wi-Fi |

## Contributing

Issues and pull requests are welcome — including "this is broken and here is what I saw", which is
genuinely the most useful thing right now. Start with **[CONTRIBUTING.md](CONTRIBUTING.md)**: it has
the real build commands (including the two traps that will otherwise cost you an afternoon), how to
run the verification harnesses, and the one rule that is not negotiable — **the wire protocol is
versioned and sacred**.

Reach the maintainer as **@delow3**.

## Licence

[MIT](LICENSE) © 2026 delow3.

Linc vendors [scrcpy](https://github.com/Genymobile/scrcpy) (Apache-2.0); see
`Linc/SCRCPY/NOTICE.md`.
