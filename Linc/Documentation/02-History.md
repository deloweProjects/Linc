# History — How Linc Was Built

Linc was built milestone by milestone, in two numbered "eras." Each milestone was sized for one
focused implementation session, and each ended with the app building cleanly and existing
features still passing a hardware check. This file is the settled, chronological account of how
the system reached its current shape. (For the *next* phase of work, see `docs/ROADMAP.md`.)

The dates below are development dates in July 2026.

---

## Era 1 — Building the product (M0–M19)

Era 1 took Linc from an empty repository to a full-featured, hardware-verified product.

### Foundation and the first vertical slice (M0–M8)

- **M0 — Foundation (Jul 7–8).** Repository, the documentation set, hello-world builds for both
  apps, and the very first protocol spec (v0: envelope, framing, handshake, ping, status) with
  JVM unit tests.
- **M1 — Android Companion MVP (Jul 8).** Guided onboarding (developer options → wireless
  debugging → pairing), a foreground companion service, a v0 socket server, and a connection
  status screen.
- **M2 — Windows MVP (Jul 8).** Managed ADB server lifecycle (no user-visible terminal), manual
  connect with a stored address, a device dashboard fed by the protocol, plain-language error
  mapping, and the Material 3 Expressive theme.
- **M3 — Automatic Discovery (Jul 8).** An mDNS listener for `_adb-tls-connect` /
  `_adb-tls-pairing` adverts, in-app pairing (QR display with hands-free completion, 6-digit
  code fallback), and a paired-device registry keyed on the hardware serial — no manual IP entry.
- **M4 — Automatic Reconnection (Jul 9).** The connection-supervisor state machine (NoDevice /
  Searching / Connecting / Connected / Paused), 15-second health probes with drop detection
  (covering PC sleep/wake and phone reboot), immediate probe on network change, per-address
  exponential backoff, a tray icon, and close-to-tray.
- **M5 — File Manager (Jul 9).** Browse internal storage and SD cards; download/upload with
  progress and cancellation; conflict-safe naming; drag files from Explorer to the phone;
  rename/delete/new-folder with confirmations. Files ride ADB's own sync protocol.
- **M6 — Screen Mirroring (Jul 9).** A managed scrcpy child process with quality presets and
  crash recovery. (Later revisited as M16.)
- **M7 — Clipboard Sync (Jul 9).** Protocol v1: copy on one device, paste on the other, with
  echo protection both ways. (Phone→desktop works only while the Linc app is focused — an Android
  platform limit, not a bug.)
- **M8 — Notifications (Jul 9).** Protocol v2: a notification bridge via
  `NotificationListenerService`, Windows toasts for arrivals, an in-app notification center, and
  cross-device dismiss.

At this point (Jul 10) the **complete core journey was hardware-verified end-to-end on a real
Pixel 7** (Android 17): wireless QR pairing through the desktop UI, auto-connect, and a live
dashboard. Two phone-only bugs found through hardware testing were fixed.

### The UI/polish pass (Jul 11)

Before the pipeline work, both apps got a major visual overhaul: the desktop became a
`NavigationView` shell with dedicated pages instead of one scrolling window; the Android app got
a bottom `NavigationBar`; both got real app icons and an activity log; the desktop added a
Details page (CPU/RAM/Wi-Fi/uptime) and a Settings page (ADB device list, port forwards,
restart-server, the deliberate power-user shell box, forget-device, sync toggles). Protocol v3
(extended device stats) and v4 (dynamic Material You theme synced phone→desktop) landed here.

### The Pipeline era (M12–M19) — one backplane for everything

The pipeline era unified all communication onto a single session/channel model so every feature
becomes a client of the same plumbing. (See `06-Connectivity-and-Protocol.md` and `PIPELINE`
history in `docs/`.)

- **M12 — Session layer & channels (Jul 11, protocol v5).** The phone accepts multiple
  concurrent connections through one `adb forward`; the control connection issues a session
  token; non-control connections open with a `{channel, sessionToken}` header; the **bulk
  channel (3)** fetches small binaries by id (proven with app icons). Older peers degrade
  gracefully.
- **M13 — Pub/sub, rich notifications, media (Jul 12, protocol v6).** Subscribe/unsubscribe
  topics on the control channel; rich notifications (app package, category, conversation,
  actions, inline reply); media state and control via `MediaSessionManager`, with album art and
  avatars over the bulk channel. User-verified live (real notification reply, real playback
  control).
- **M14 — Home page (Jul 12, protocol v7/v8).** A Home page replaced Notifications as the
  landing page: a phone-preview card, quick actions (ring, DND, screenshot), a media widget with
  seek, clipboard history, and Pixel-shade-style notification cards. The polish pass added the
  real wallpaper as a blurred backdrop, sound profiles, and frosted-acrylic cards.
- **M15 — Direct TLS & standing presence (Jul 12, protocol v9).** The **Direct TLS** transport:
  trust-on-first-use certificate exchange over the ADB link, a desktop mutual-TLS listener, mDNS
  advertising (`_linc._tcp`), and the phone dialing out on its own — so everything except
  ADB-bound features keeps working with wireless debugging *and* USB both off. Verified on
  hardware.
- **M16 — Mirror, in-app client attempt (Jul 12).** An in-app scrcpy-server client was built and
  then **reverted after hardware testing** — a drop-oldest queue corrupted P-frames and clock
  pacing accumulated latency. scrcpy's own engine was restored as the mirror (D-023). "Engulf
  scrcpy" was redefined to mean *bundle its binaries at the Polish milestone*, not reimplement
  the client.
- **M17 — Files everywhere & delights (Jul 13, protocol v10).** A **files channel (2)** with a
  small list/pull/push protocol so file browsing and transfer work over Direct TLS too; a Home
  recent-photos strip; `continue.url` (open a link on the other device); and a share-sheet "Send
  to PC" target.
- **M18 — The Sync page (Jul 13, protocol v11/v12).** A Sync page with lane toggles: the
  **Messages (SMS) lane** (list/send/receive), the **Calls lane** (call log, dial, decline,
  incoming-call banner), and a desktop-orchestrated **folder/photo sync engine** built entirely
  on the files channel (no new protocol). SMS/calls are sideload-only (D-016); no PC call audio
  (D-017).
- **M19 — Phone Home + Share (Jul 14, protocol v13).** The phone got a **Home** screen and a
  **Share** tab, and the protocol got its first **PC→phone push** features: the PC's own media
  shown and controlled on the phone, and two-way file sharing.

Era 1 closed with M18c, the M14/M10 Home polish, and M19 all **user-verified on hardware** on
Jul 14.

---

## Era 2 — The phone and PC extend each other (M00–M07)

On Jul 19 the roadmap numbering reset. Era 1 (M0–M19) was archived as complete; current work
restarted at **M00**. The theme of Era 2: the phone and PC become extensions of each other's
hardware, and the user does almost nothing.

- **M00 — Clean slate & verified baseline (Jul 19).** A documentation sweep to reality, and the
  verification harnesses made permanent under `tools/` (they had previously lived in an
  uncommitted scratchpad and kept getting lost). The probe scored **18/18 protocol lanes** on the
  Pixel 7; the sync harness ran all five sync scenarios under both transports. Four real bugs
  were found and fixed in the process — three of them *invisible* failures (a fire-and-forget
  task swallowing exceptions, a persistence step skipped by an escaping exception, a
  version-gated push returning silently), which crystallized the rule: **a silent catch is a bug
  factory.** Also uncovered: PC media was invisible because the owner's player (AIMP) publishes
  nothing to Windows' media-transport controls — fixed with a classic-Winamp-remote reader.
- **M01 — Effortless companion (Jul 20).** The desktop now looks after the phone's companion. It
  **installs the APK** on a phone that lacks it, **grants everything ADB can grant** (notification
  listener, eight runtime permissions, all-files access), and **auto-starts the service** whenever
  the handshake fails on a live link. The unlock was one manifest line: `am
  start-foreground-service` refuses a non-exported component, which is exactly why every install
  used to demand a manual "Start" tap. Verified from a fully uninstalled state: installed,
  granted, started, and connected in 18 seconds with the phone untouched.
- **M02 — Offline device memory (Jul 20).** Last-known per-device state persists per serial, so a
  disconnect never blanks the UI — everything renders dimmed under a "Last seen HH:MM" banner
  until fresh data replaces it. The privacy split is **structural, not disciplinary**: the cached
  record has no field capable of holding clipboard text or notification bodies, so sensitive data
  cannot leak to disk even by accident.
- **M03 — Device tabs / multi-phone (Jul 20).** A Chrome-style tab strip in the title bar, one
  tab per known phone, with presence at a glance and a "+" to pair another; closing a tab is not
  unpairing. Under the hood every per-device setting moved onto the `KnownDevice` record, and the
  registry resolves the old names against the active device — which *is* the anti-bleed mechanism.
  Shipped as **one live service bundle the tabs re-point**, not N concurrent supervisors (D-037).
- **M04 — Bluetooth presence (Jul 20).** The phone advertises a 10-byte non-connectable BLE
  beacon whose id is a rotating hash of its TLS certificate; the desktop background-scans and
  treats a sighting purely as a "physically here" hint that nudges reconnection. **No data ever
  rides BLE.** Verified with Bluetooth as the *only* channel. This work also uncovered that Direct
  TLS had been silently dead (a companion reinstall wipes the phone's KeyStore certificate and the
  desktop only ever pinned once) — now the desktop re-exchanges certificates on every connect.
- **M05 — Reverse mirror (Jul 20–21).** The phone can now **view and control the PC**. The
  desktop captures a display with DXGI Desktop Duplication, encodes it with a hardware Media
  Foundation H.264 encoder (no software fallback, D-039), and streams it over the new
  **pc-video channel (5)**; the phone decodes with MediaCodec straight to a Surface. Touches come
  back as `pc.input` and are injected with `SendInput`. It became a fullscreen, spacedesk-style
  landscape viewer with pinch-zoom and a real keyboard. This milestone produced two instructive
  bug hunts: a UI-thread hang that turned out to be **COM apartment marshaling** (the Media
  Foundation encoder was being built on the WinUI UI thread), and a **transport-priority /
  failover** rework so USB preempts wireless preempts Direct TLS.
- **M06 — Extend mode — DROPPED (Jul 22).** Extend mode would have made the phone a true *second
  monitor* (a bundled signed Indirect Display Driver creating a virtual display you drag *extra*
  windows onto). It was **cut at the owner's decision** — the reverse mirror already covers the
  need — and no code had ever been written for it, so the drop was simply recorded across the
  planning docs. The shipped protocol stays v14; no v15 was minted.
- **M07 — Polish (in progress).** The first slice landed: the Sync page now shows Messages and
  Calls **side by side** (previously Messages hid Calls whenever both lanes were on), plus tidier
  empty/disconnected states. The remaining M07 work — bundling adb + scrcpy so end users install
  nothing, accessibility, an installer/updater, and a Play-ready Android build — is still open.

---

## Lessons the project keeps re-learning

A handful of hard-won lessons recur throughout the history and shape how the code is written:

- **A silent catch is a bug factory.** If a code path can fail and nobody is awaiting it, it must
  log. Several of the nastiest bugs were invisible failures in fire-and-forget tasks.
- **Anything that must run regardless of the open page must not be started from a page's view
  model.** The connection supervisor once lived in the Device page's constructor, so the app
  never connected unless you opened that page.
- **A custom title bar swallows clicks.** Anything handed to `SetTitleBar` becomes a drag region
  that eats mouse input before child controls see it — keep the drag region to an empty spacer.
- **A security mechanism that fails closed and silently needs something noisy pointed at it.**
  Direct TLS was dead for days with no symptom until the BLE beacon (whose id derives from the
  same certificate) made the mismatch visible.
- **Any COM/Media-Foundation/D3D object with a hot cross-thread call path must be created off the
  UI thread.** The companion receive loop resumes on the UI thread, so anything it reaches is STA
  by default.
- **A reconnect state machine driven only by fresh adverts is blind to a transport that is
  already up but quiet** — always give it a way to adopt what the ADB server already holds.
