# Design Decisions & Rationale

The load-bearing decisions that shape Linc, in plain language. The authoritative, dated decision
log with full reasoning lives in `docs/DECISIONS.md` (entries D-001 … D-040); this file is the
readable summary. Where a later decision revised an earlier one, that is noted.

## Foundational posture

- **D-001 — ADB is the transport, never the interface.** A raw `adb` command reaching the user is
  a bug. This single decision explains the guided onboarding, the plain-language error
  translation, the bundled binaries, and the desktop doing the phone's chores.
- **D-009 — one deliberate exception: a power-user ADB shell box.** Added at the owner's explicit
  request, scoped to one labelled, cautioned box on the Settings page. It does not reopen D-001
  generally.
- **D-002 — Android 11 (API 30) minimum.** Wireless debugging with in-band pairing, core to the
  product, requires it.
- **D-003 — the Windows app is C# / .NET 8 / WinUI 3.** Native modern Windows integration (tray,
  toasts, Explorer drag-drop), a strong async model, single-file publish.
- **D-006 — no shared code between the apps, only a shared spec.** The versioned JSON protocol is
  the only shared artifact; each side is implemented independently, so neither depends on the
  other's internals and version negotiation keeps mismatched builds working.

## Transport & connection

- **D-005 — the companion protocol is versioned JSON over an ADB-forwarded localhost socket.**
  Human-readable, easy to evolve with a version field.
- **D-012 — the pipeline uses typed channels as separate connections, not in-band multiplexing.**
  One socket accepts many connections; each declares its channel; the OS's per-connection
  buffering is the flow control. In-band muxing would need hand-rolled flow control (the classic
  mux trap).
- **D-013 — dual transport: keep every ADB path, add Direct TLS.** ADB is never removed; Direct
  TLS is a sibling that works with wireless debugging and USB both off.
- **D-014 — standing presence, background connection hunting, on by default.** The link is up
  before the user presses Connect.
- **D-022 — Direct TLS via trust-on-first-use cert exchange over ADB; the phone dials everything;
  first-wins failover.** Unknown certs are dropped silently (which is why a cert mismatch was
  invisible until the BLE beacon exposed it).
- **D-018 — the session layer keeps the control connection headerless; only channels carry a
  header.** Preserves exact legacy interop (`hello` first).
- **D-037 — device tabs switch one service bundle rather than running N concurrently.** With one
  phone on the bench, N concurrent supervisors would ship unexercised code; instead exactly one
  device is active and its tab is live, every other tab rendering the M02 cache. The upgrade seam
  to true concurrency is recorded.
- **D-034 / D-038 — Bluetooth LE presence is a wake-up hint, never a data path**, identified by a
  rotating hash of the pairing certificate (company id `0xFFFF`, `"LC"` + truncated `SHA-256(cert
  ‖ 5-minute slot)`).
- **D-035 — the desktop does the phone's chores over ADB:** service auto-start
  (`am start-foreground-service`, which needs the service exported) and PC-driven onboarding
  (`adb install` + `cmd notification allow_listener` + `pm grant` + `appops set`).

## Screen mirroring

- **D-004 — embed scrcpy rather than build a mirroring pipeline** (phone→PC). Best-in-class
  open-source mirroring; manage it rather than reimplement video pipelines.
- **D-015 — mirror phase 1: bundle scrcpy-server and replace the desktop client** (partially
  supersedes D-004). Also unlocks per-app windows via `--start-app` + virtual displays. Phase 2
  (an own `linc-agent.dex`) is deliberately unscheduled.
- **D-023 — the in-app scrcpy-server client was reverted after hardware testing.** A drop-oldest
  queue corrupted P-frames and clock pacing accumulated latency; scrcpy's own engine decodes all
  frames and drops only at display. scrcpy.exe is the engine again, and "engulf scrcpy" now means
  *bundle its binaries*, not reimplement the client. *(This is the state the next-phase "vendor
  scrcpy" work builds on.)*
- **D-029 / D-039 / D-040 — the reverse mirror.** Desktop Duplication → hardware Media Foundation
  H.264 (**no software fallback**, so a machine without a hardware encoder is told plainly rather
  than served a bad experience) → pc-video channel (5) → MediaCodec; pacing is deadline-driven and
  capture is decoupled from the encoder's event pump.
- **D-030 — extend mode would have used a bundled *signed* Indirect Display Driver, not a
  hand-written one. DROPPED** at the owner's decision (the reverse mirror covers the need); no code
  was ever written.

## Features & scope

- **D-007 — Google account integration is deferred and scope-gated.** Only worth building where it
  adds clear value (e.g. FCM push-wake).
- **D-016 — SMS ships in sideload builds only.** A Play-ready build compiles the SMS/call lanes
  out.
- **D-017 — call support ships without PC audio.** Dial and decline work; routing call audio to PC
  speakers is a future Bluetooth-HFP idea.
- **D-024 — files over Direct TLS use a small list/pull/push protocol on channel 2 behind one
  desktop interface.** Rename/delete/mkdir/screenshot stay ADB-only (`TlsFileService` throws
  `NeedsAdb()`), so any code composing file ops must tolerate a missing delete on ADB-free links.
- **D-025 / D-026 — the Sync page is staged; Messages first, then Calls.** SMS needs no
  default-SMS-app; calls dial + decline, no PC audio.
- **D-027 — folder/photos sync is a desktop-orchestrated poll over the files channel with
  persistent seen-state as the loop guard; no protocol bump.** New-files-both-ways, newest-wins, no
  delete propagation.
- **D-028 — M19 gave the phone a Home + Share and the first PC→phone pushes** (media, share);
  protocol v13.
- **D-032 — offline device memory: the UI never blanks on disconnect,** and the privacy split is
  structural — the cached record has no field able to hold clipboard text or notification bodies.
- **D-033 — multi-device via Chrome-style tabs in the title bar** (implemented per D-037).

## Design language & process

- **D-008 — Material 3 Expressive is the design language on both platforms.**
- **D-010 → D-011 — the theme evolved from a shared static brand palette to live Material You
  dynamic color, driven by the phone.** The phone sends its wallpaper-derived palette over the
  wire and the desktop retints itself to match.
- **D-019 / D-020 / D-021 — protocol/versioning judgment calls** around pub/sub gating, wallpaper
  transfer (dropped, then partially restored as a thumbnail via the all-files grant), and sound
  profiles.
- **D-031 — self-verification is standing policy.** The user runs only physically-unautomatable
  checks.
- **D-036 — verification harnesses are committed repo assets, not scratch.** They kept getting
  lost from an uncommitted scratchpad; now they live under `tools/`.

## The recurring engineering lessons

These aren't formal decisions but they shape every change (see also `02-History.md`):

- A silent catch is a bug factory — if a path can fail unawaited, it must log.
- Anything that must run regardless of the open page belongs in `AppShellViewModel`, not a page.
- A custom title bar swallows clicks — keep the drag region to an empty spacer.
- Any COM/Media-Foundation/D3D object with a hot cross-thread call path must be created off the
  UI thread.
- A reconnect machine driven only by fresh adverts is blind to a transport that is already up but
  quiet.
- Not every file operation exists on every transport — check `HasAdb` or catch `LincException`.
