# Linc

**Your Android phone, on your PC — files, screen, notifications, clipboard, calls — over a cable or
your own Wi-Fi, with nothing in between.**

[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)
[![Status: beta](https://img.shields.io/badge/status-1.0.0--beta.11-orange.svg)](https://github.com/deloweProjects/Linc/releases)
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
                         │   versioned JSON envelopes (protocol v24) │
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
- **Clipboard** — sync both ways, send text to the other device, and a searchable history (500 items).
- **Share** — files and links to the phone over Linc, or Quick Share when it isn't connected.
- **Remote** — the phone as the PC's trackpad, keyboard, media and presentation remote.
- **Notifications** on your desktop, with optional on-disk history (off by default).
- **Media remote** — transport controls and a slideshow clicker.
- **Messages and calls** — read and send SMS, see and place calls.
- **Offline cache** — the app is still useful with the phone in another room.
- **Hotspot link-up in ~500 ms** when the phone brings up a hotspot.
- **Multiple paired phones**, one active at a time.

## Status

**Beta — `1.0.0-beta.11`.** Used daily and tested on real hardware; still a one-maintainer project.
Honest notes:

- **The phone app is debug-signed.** Android asks you to confirm installing it, and a future
  release-signed build won't upgrade over it (uninstall first, then).
- **The Windows app isn't code-signed yet**, so SmartScreen warns the first time (see Install).
- **There is no CI.** Every build is made and checked on one PC, with the harnesses in `Linc/tools`.

Full detail per version: [`Releases/RELEASE-NOTES.md`](Releases/RELEASE-NOTES.md).

## Install

1. From the latest **[Release](https://github.com/deloweProjects/Linc/releases)**, run
   **`Linc-Setup-<version>.exe`**. It installs for your Windows account only — no admin prompt — to
   `%LOCALAPPDATA%\Programs\Linc`, with a Start menu entry and, if you tick it, a desktop shortcut and
   "Start when I sign in".
   - **"Windows protected your PC"?** The installer isn't code-signed yet. Click **More info**, check it
     says `Linc-Setup-<version>.exe`, then **Run anyway**. You can check the file first against
     [`Releases/CHECKSUMS.txt`](Releases/CHECKSUMS.txt) (`Get-FileHash <file> -Algorithm SHA256`).
   - Prefer no installer? `Linc-Desktop-<version>.zip` is the same app, portable: unzip anywhere and run
     `Linc.Desktop.exe`.
2. **Phone:** the PC can install the Linc app for you — connect the phone (below) and Linc offers to. Or
   sideload `Linc-Companion-<version>.apk` from the release.

Linc keeps itself up to date from `Releases/update.json` in this repo: an installed Linc updates in
place (it closes, updates and restarts by itself). Turn that off in **Settings → Updates**.

**Updating from 1.0.0.10 or earlier:** those builds can't install an update themselves (their
updater was broken). Run `Linc-Setup` once; from then on updates are automatic. Your phones, history
and settings are kept — they live in `%LOCALAPPDATA%\Linc`, not in the program folder.

## First connection

1. On the PC, open Linc and choose **Add a phone** (the **+** next to the phone tabs).
2. Pick how: **USB cable**, **Wi-Fi** (scan a QR code on the phone's *Wireless debugging* screen), or
   **It's already set up** for a phone that has used Linc before. The steps on screen say exactly what to
   tap on the phone (developer options, then USB or wireless debugging).
3. Linc checks the connection and says **Added Pixel 7** — or **Pixel 7 was already added** — and you're
   done. After that the phone reconnects by itself: over the cable when it's plugged in, over Wi-Fi when
   both are on the same network, and it moves between the two without dropping.

Phones you've used before are under **Phones you've used before** in the same screen — no pairing again.

## Requirements

| | |
| --- | --- |
| **PC** | Windows 10 (1809 or later) or Windows 11, x64 |
| **Phone** | Android 11 (API 30) or newer |
| **Link** | A USB cable, or both devices on the same Wi-Fi (or the phone's hotspot) |
| **Mirror PC** | A hardware H.264 encoder on the PC's graphics chip (AMD, NVIDIA or Intel; Intel via NV12) |
| **Phone as the PC's microphone** | VB-CABLE, a free virtual audio driver. Linc downloads VB-Audio's own installer and opens it when you first use the feature — you approve Windows' prompt |

## Limits

- **One phone at a time.** Several can be paired (one tab each); connecting one disconnects the other.
- Windows 10/11 x64 only; Android 11+.
- Clipboard sync is text only. Android lets Linc read what you copy only while Linc is open on the phone
  (an Android rule); copies from other apps reach the PC when you open Linc or share the text to it.

## Troubleshooting

- **Copy the log on both sides and send them together:** on the PC, **Logs → Copy log**; on the phone,
  **Settings → Help & diagnostics → Copy log**. The phone's report includes why Android last stopped Linc.
- **It keeps disconnecting over Wi-Fi:** on the phone, allow **Stay connected in the background** (Home
  shows a card until you do), and on itel/Tecno/Infinix/Xiaomi/Oppo/Vivo also turn Linc on in the maker's
  **Auto-start** list.
- **Turn off the helper text** you no longer need: Settings → Appearance → **Show explanations** (each app).

## Uninstall

**Settings → Apps → Installed apps → Linc → Uninstall** (or Linc in the Start menu → right-click →
Uninstall). The uninstaller closes Linc, puts back what it changed on the PC (default microphone, Voice
Access's microphone, display scale, Explorer's right-click entries, the autostart entry) and asks
whether to delete Linc's data (`%LOCALAPPDATA%\Linc`: paired phones, history, settings, logs).
**VB-CABLE stays installed** — remove it from Linc's Settings → Phone as microphone before
uninstalling, or later from Windows' Apps list. On the phone, uninstall Linc like any app.

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
