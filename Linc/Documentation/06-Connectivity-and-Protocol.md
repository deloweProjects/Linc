# Connectivity & Protocol — How the Two Apps Connect

This is the file to read to understand **how Linc Desktop and Linc Android actually talk to each
other**: the physical transports, how they find and trust each other, the session/channel model,
and the versioned wire protocol (~~currently **v14**~~ — **see the warning immediately below**).

> # ⚠️ THE PROTOCOL SECTIONS OF THIS FILE ARE OUT OF DATE (2026-08-12)
>
> **This chapter describes v14. The shipped protocol is v18 on both sides.** The transport, discovery,
> trust and channel model below are still broadly accurate; **the message tables are not.**
>
> **The canonical wire spec is `agent-docs\opencode-docs\PROTOCOL.md`.** What has been added since v14:
>
> - **v15** — display control from the PC: `display.rotation.set`, `display.brightness.set`. `level` is
>   a 0–100 percentage on the wire, never a raw platform value.
> - **v16** — the installed-app inventory: `apps.get` → `apps`, launchable packages only. Icons reuse
>   the v5 bulk `appIcon` kind. **No push event** — the desktop re-requests on connect.
> - **v17** — phone→PC quick controls: `pc.control` (request/reply, action enum) and
>   `pc.state.get` → `pc.state`. **`shutdown` and `restart` require `confirm: true` on the wire**, not
>   only a phone-side dialog.
> - **v18** — hotspot ADB link-up: `adb.announce` / `adb.down` / `adb.arm` / `adb.ack`. **On a hotspot
>   link discovery is deleted entirely** — the desktop reads its own control socket's peer address and
>   the phone announces only the port. Carries a monotonic `gen` counter (filter is `>=`) and a
>   mandatory phone-side loopback liveness gate.
>
> **Also changed since this chapter was written:** `pc.input` (v14) turned out to already cover a
> video-less remote keyboard and trackpad — `mode:"trackpad"` is **relative**, so it needs no video
> frame of reference. That is why the Tools page shipped with no protocol bump at all.


---

## 1. The three transports

Linc can carry its traffic over three physical paths. ADB is never removed — it is one arrow in
the quiver (D-013).

### A. ADB forward (the original, always kept)

`adb forward tcp:0 localabstract:linc` maps a desktop TCP port to the phone's
`LocalServerSocket("linc")`. Works over **USB** and over **wireless debugging (ADB over Wi-Fi)**.
This path is **required for the phone→PC mirror channel**, because scrcpy's phone side runs via
`app_process` with **shell UID** — the only way to get screen capture without a consent prompt
*and* input injection, privileges no normal app can hold.

### B. adb reverse (cheap, phone-initiated over the cable)

`adb reverse` maps a phone-side port to a desktop socket, letting the **phone initiate**
connections over USB — symmetric with Direct TLS on the LAN. Used for phone-dialled channels
(like pc-video) over the cable.

### C. Direct TLS (the ADB-independent path)

An app-to-app **mutual-TLS** connection on the LAN, needing no ADB and no wireless-debugging state
to stay alive:

- **Pairing addition.** During the one-time pairing flow, alongside ADB pairing, the two apps
  exchange self-signed certificates in both directions (`tls.exchange`) — **trust-on-first-use**,
  pinned with the paired-device entry. The desktop pins the phone's AndroidKeyStore cert; the
  phone pins the desktop's PFX-backed cert. Validation is byte-equality, no chains.
- **Discovery.** The desktop advertises `_linc._tcp` via mDNS; the phone watches for the advert
  and dials out.
- **Security.** Mutual TLS with the pinned certs; connections presenting unknown certs are
  **dropped silently by design** (which is why a mismatch is invisible — see the lesson below).
  A session token issued on the control channel gates every stream connection.
- **What it can't carry.** The phone→PC mirror channel (needs shell UID — ADB only). Everything
  else runs fine ADB-free.

> **Hard-won lesson.** A security mechanism that fails **closed and silently** needs something
> noisy pointed at it. Direct TLS was silently dead for days after companion reinstalls (the
> phone's KeyStore cert is wiped on uninstall, and the desktop pinned only once) with no error
> anywhere — until the BLE beacon, whose id derives from the same cert, made the mismatch
> visible. The desktop now **re-exchanges certificates on every ADB connect**.

### The transport supervisor

The supervisor **races all viable paths** on any trigger (mDNS sighting, USB attach, network
change, screen-on, BLE presence, resume-from-sleep), **ranks them USB > wireless ADB > Direct
TLS**, and a higher-priority transport **preempts** a lower one (plug in USB while on Wi-Fi ⇒ it
switches to USB). A per-device **connection preference** (Auto / USB-only / Wireless-only /
Direct-only) filters eligibility. "Simultaneous ADB + TLS" means **failover**, not literal dual
control (ADB is a feature superset of TLS, so two live control links only buy resilience). The
Device page shows the live path and its RTT and lets a power user pin one.

---

## 2. Discovery, pairing, and presence

- **Discovery (Wi-Fi).** The phone's wireless debugging advertises `_adb-tls-connect._tcp` via
  mDNS; the desktop listens and matches known devices. The desktop also advertises its own
  `_linc._tcp` for the Direct-TLS path.
- **Discovery (USB).** `UsbWatcherService` polls the ADB device list; a paired serial
  auto-connects, an unknown USB device needs one explicit confirmation before Linc trusts it.
- **Pairing.** One-time ADB wireless pairing (QR code rendered from the ADB pairing payload, or a
  6-digit code), guided on both sides, plus the TLS certificate exchange described above.
- **Standing presence (D-014).** Once paired, both sides hunt for each other continuously
  (opt-out "Background connection" toggle, on by default). The desktop keeps mDNS listen +
  advertise and the USB watcher alive in the tray process; the phone's foreground service watches
  for the desktop advert and dials Direct TLS on sight with a widening backoff. On a pure LAN this
  yields "connected within ~1–3 s of both being awake on the same network." Connecting to a phone
  that is Dozing on cellular would need a push wake (FCM) — the deferred Google scope.
- **BLE presence hint (D-034).** The phone advertises a non-connectable beacon; the desktop
  background-scans and treats a sighting as "physically here," firing immediate discovery and
  showing "Nearby, connecting…". **No data ever rides BLE.**
- **Resume from sleep.** The supervisor subscribes to power-mode changes and, on resume,
  verifies a possibly-dead link, nudges mDNS, and reconnects straight to the last host:port if the
  ADB server still lists it online — because a reconnect machine driven only by *fresh adverts* is
  blind to a transport that is already up but quiet.

---

## 3. The session / channel model

**One listener, many connections, no multiplexing layer.** The phone's single
`LocalServerSocket("linc")` (and the identical TLS listener) accepts many simultaneous
connections. Each connection declares its **channel** in its first frame, and the OS's
per-connection buffering *is* the flow control — a stalled bulk transfer can never starve a
notification. Linc deliberately avoids in-band multiplexing (D-012).

| # | Channel | Payload | Transport | Notes |
|---|---|---|---|---|
| 0 | **control** | length-prefixed JSON | any | Today's protocol socket + pub/sub. Always the first connection; issues the session token. |
| 1 | **mirror** | H.264 video + input | **ADB only** | scrcpy-server's own protocol (phone→PC). |
| 2 | **files** | binary transfer frames | any | list/pull/push; replaces ADB-sync dependency on Direct TLS. |
| 3 | **bulk** | small binaries | any | App icons, album art, wallpaper, photo thumbnails, keyed by ids handed out on control. |
| 4 | **audio** | Opus/AAC phone audio | ADB only (phase 1) | From scrcpy-server's audio capture. |
| 5 | **pc-video** | H.264 PC screen, **desktop→phone** | any | Reverse mirror (M05). First desktop-served channel; input returns as `pc.input` control messages (`SendInput`, no shell-UID constraint, so it works over pure Direct TLS). |

**Control is headerless** (`hello` first, exactly v≤4); channel connections send a
`{channel, sessionToken}` header, disambiguated from `hello` by shape (D-018). Only the control
connection touches shared companion state; multiple TCP connects to the one forwarded port each
become a fresh phone-side `accept()` (the same trick scrcpy uses for its 3 connections).

**Topics (pub/sub).** On the control channel, `subscribe {topic}` / `unsubscribe {topic}` turn
existing unsolicited messages into a subscribed event stream. Gating applies **only at negotiated
version ≥ 6**, so older peers keep receiving unsolicited events unchanged. The desktop subscribes
to `status`, `notifications`, `clipboard`, and `media` on connect.

---

## 4. The wire protocol — version history

The protocol is versioned JSON over the control channel. Every addition is optional and
version-negotiated: unknown message types and unknown fields inside known payloads are ignored,
so mismatched builds degrade gracefully (the newer feature simply doesn't happen). The version is
bumped in `docs/PROTOCOL.md` **first**, then traffic is gated on the negotiated version.

| Version | Milestone | What it added |
|---|---|---|
| **v0** | M0 | Envelope, framing, handshake (`hello`), `ping`, `status`. |
| **v1** | M7 | Clipboard sync (`ok`, `clipboard.set`, `clipboard.changed`). |
| **v2** | M8 | Notification bridge (`notification.posted`, `notification.removed`, `notification.dismiss`). |
| **v3** | (polish) | Extended device status: CPU load, RAM, uptime, Wi-Fi signal/link speed. |
| **v4** | (polish) | Dynamic theme: the phone's Material You palette (`themeLight`/`themeDark`) added to `status`. |
| **v5** | M12 | The **session layer**: many concurrent connections; the control connection issues a `sessionToken`; non-control connections open with a channel header; the **bulk channel (3)** (proven with app icons). |
| **v6** | M13 | **Pub/sub topics**; **rich notifications** (appPackage/category/conversation/actions; `notification.action`, `notification.reply`); **media** (`media.state`/`media.control`, album art over bulk). |
| **v7** | M14 | **Device actions**: `device.locate` (ring), `dnd.set` (+ optional `dndEnabled` on `status`). |
| **v8** | M14 polish | **Wallpaper & sound profiles**: `status` gains `wallpaperId` + `soundMode`; `sound.set`; wallpaper bytes via bulk. |
| **v9** | M15 | The **Direct TLS transport** (`tls.exchange`, `_linc._tcp` advertise + listen, `channel.open` dial-back). |
| **v10** | M17 | **Files everywhere & delights**: the **files channel (2)** list/pull/push; `photos.recent` + bulk `photo`; `continue.url`; `share.item`. |
| **v11** | M18a | The **Sync page**: `sync.config`; the **Messages lane** (`sms.list`, `sms.send`, `sms.received`). |
| **v12** | M18b | The **Calls lane**: `call.log`, `call.dial`, `call.decline`, and unsolicited `call.incoming`. |
| **v13** | M19 | The first **PC → phone pushes**: `pc.media.state` (desktop→phone) + `pc.media.control` (phone→desktop); `share.incoming`; the desktop side of `share.item kind:file`. |
| **v14** | M05 | The **reverse mirror**: the **pc-video channel (5)**; `pc.mirror.start`/`pc.mirror.stop`, `pc.displays.get`/`pc.displays`, `pc.input`, and `pc.textfocus` (desktop→phone: the PC's text-field focus, so the phone raises its soft keyboard to match). |

The folder/photo **sync engine** (M18c) added **no message types** — it is a desktop-side engine
built entirely on the v10 files channel and v11 `sync.config`, so the phone got zero new code.
The BLE beacon (M04) also added **no wire change**.

> **Dropped, never shipped:** `pc.display.attach` / `pc.display.detach` for an extend-mode virtual
> monitor. Extend mode (M06) was cut at the owner's decision; the current shipped protocol is
> **v14** and no v15 was minted.

## 5. Framing and envelope

- **Framing:** each message is a length-prefixed frame. The codec is `Framing.kt` (Kotlin) and its
  C# counterpart, implemented independently.
- **Envelope:** a small JSON object with a `v` (protocol version) field on the handshake, a
  message `type`, and a payload; the receiver tolerates unknown types and unknown fields.
- **Handshake:** the control connection sends `hello`, negotiates the lower of the two peers'
  versions, and (at v≥5) receives a `sessionToken` in the reply. All subsequent typed channels
  present that token.

## 6. Binary streams

- **Files channel (2):** a small list/pull/push protocol so browsing and transfer work over Direct
  TLS; over ADB the same `IFileService` uses ADB sync instead. Rename/delete/mkdir/screenshot stay
  ADB-only.
- **Bulk channel (3):** request/response for small binaries (app icons, album art, avatars,
  wallpaper thumbnail, photo thumbnails), keyed by ids handed out on the control channel.
- **Mirror channel (1):** scrcpy-server's own video+input protocol, phone→PC, ADB only.
- **pc-video channel (5):** desktop→phone H.264, scrcpy-style packet framing reused; config and
  keyframe flags in the PTS high bits. Input returns on the control channel as `pc.input`.

## 7. The BLE presence beacon (no wire change)

A single manufacturer-data section under company id **`0xFFFF`** (the SIG's testing/development
id), carrying `"LC"` and an 8-byte id equal to `SHA-256(phone's TLS certificate DER ‖
unixSeconds/300)` truncated. The desktop matches the slot ±1 for clock skew; the phone republishes
on slot boundaries so the id never drifts stale. Implemented twice (Kotlin advertiser + C#
scanner) per D-006, so `tools/blescan` exists to prove the two derivations agree — the one thing
that can silently diverge.
