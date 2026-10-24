# Companion Protocol — v32 (LincLink; see the v-sections below for every version since v17)

The contract between Linc Desktop and the Linc Android companion service. Both apps implement this spec and share **no code** (see DECISIONS.md D-005, D-006). Changes to this document that alter the wire format require a version bump and entries in CHANGELOG.md and DECISIONS.md.

**Version history:** v0 — framing, handshake, ping, status. v1 (2026-07-09) — clipboard sync (`ok`, `clipboard.set`, `clipboard.changed`). v2 (2026-07-09) — notification bridge (`notification.posted`, `notification.removed`, `notification.dismiss`). v3 (2026-07-11) — extended device status: CPU load, RAM, uptime, Wi-Fi signal/link speed. v4 (2026-07-11) — dynamic theme: the phone's Material You palette (`themeLight`/`themeDark`) added to the `status` payload so the desktop matches the phone's colors. v5 (2026-07-11, M12) — the pipeline **session layer**: the phone accepts multiple concurrent connections; the control connection issues a `sessionToken`; non-control connections open with a channel header; the **bulk channel (3)** carries small binaries (proven with app icons). v6 (2026-07-11, M13) — **pub/sub topics** on the control channel (`subscribe`/`unsubscribe`; existing unsolicited messages become topic events, gated on subscription **only at negotiated ≥ 6** — see D-019), **rich notifications** (appPackage/category/conversation/actions on `notification.posted`; `notification.action`, `notification.reply`), and **media** (`media.state`/`media.control`, album art over bulk). v7 (2026-07-12, M14) — **device actions**: `device.locate` (toggle a full-volume ring), `dnd.set` (needs the phone's DND policy grant; `error` code `not-granted` otherwise), and an optional `dndEnabled` field on `status`. `wallpaper.changed` is dropped from the draft entirely — the Home page's phone-preview card uses a gradient from the live-synced Material You palette instead, which is wallpaper-derived and therefore matches by definition; Android 13+ blocks reading actual wallpaper bytes without `MANAGE_EXTERNAL_STORAGE` (D-020). v8 (2026-07-12, M14 polish) — **wallpaper & sound profiles**: `status` gains `wallpaperId` (present only when the phone holds the All-files-access grant — D-021 partially reverses D-020 for sideload builds; bytes via bulk kind `wallpaper`, a small pre-blurred thumbnail) and `soundMode`; new `sound.set` message (ring/vibrate/silent, same phone-side grant as DND). v9 (2026-07-12, M15) — the **Direct TLS transport** (D-022): `tls.exchange` pins self-signed certificates in both directions over the already-authenticated ADB link (trust-on-first-use); the desktop then advertises `_linc._tcp` and listens; the phone dials out with mutual TLS and the identical framing/session/channel scheme runs over it; `channel.open` lets the desktop request a dial-back connection for a typed channel (needed because the phone initiates every TLS connection). v10 (2026-07-13, M17) — **files everywhere & delights** (D-024): the **files channel (2)** carries a small list/pull/push protocol so file browsing and transfer work over Direct TLS (ADB links keep using ADB sync under the same desktop interface); `photos.recent` + bulk kind `photo` back the Home recent-photos strip; `continue.url` (open a link on the other device) and `share.item` (Android share-sheet "Send to PC") are promoted from draft to contract. v11 (2026-07-13, M18a) — the **Sync page**: `sync.config` persists which lanes are on, and the **Messages lane** (`sms.list`, `sms.send`, `sms.received`) ships — sideload-only, dangerous runtime permissions, no default-SMS-app needed (D-025). Calls and folder/photo sync are M18b/c. v12 (2026-07-13, M18b) — the **Calls lane**: `call.log`, `call.dial`, `call.decline`, and the unsolicited `call.incoming` (D-026); sideload-only, no PC audio (D-017). Folder/photo sync is M18c. v13 (2026-07-14, M19) — the first **PC → phone push** features behind the phone's new Home/Share screens (D-028): `pc.media.state` (desktop → phone) + `pc.media.control` (phone → desktop) mirror and control the PC's own media; `share.incoming` (desktop → phone) announces a file the desktop pushed for the phone's Share tab; `share.item` `kind:file` gains its desktop-side pull. All additions stay optional/version-negotiated; unknown newer types (and unknown fields inside known payloads) are ignored per the envelope rules, so mixed-version pairs degrade gracefully (the newer feature simply doesn't happen — a v≤4 peer never opens a channel, a v≤5 peer never subscribes and keeps receiving unsolicited events as before). v14 (2026-07-20, M05) — the **reverse mirror** (D-029): the **pc-video channel (5)**, the first desktop-served stream, carries H.264 from the desktop to the phone (DXGI capture → hardware Media Foundation encode → framed packets); `pc.mirror.start`/`pc.mirror.stop`, `pc.displays.get`/`pc.displays`, and `pc.input` (phone touches → desktop `SendInput`) drive it; `pc.textfocus` (desktop → phone) reports PC text-field focus so the phone's soft keyboard follows. A v≤13 peer never sends `pc.mirror.start`, so the mirror simply never starts. **v15** (2026-07-29, M5c) — **display control from the PC** (D-055): `display.rotation.set` and `display.brightness.set` let the desktop set the phone's orientation (auto / portrait / landscape) and its brightness (adaptive, or a 0–100 manual level), applied phone-side through `Settings.System` per D-001 — never via `adb shell settings put`. Both need the phone's `WRITE_SETTINGS` appop and answer `error` `not-granted` without it. `status` gains optional `rotationMode` / `brightnessAuto` / `brightnessLevel` so the PC widgets show the phone's real state. A v≤14 peer never sends or receives either type, so a mixed-version pair just loses the widgets. **v16** (2026-07-31, M6c) — the **installed-app inventory** (D-058): `apps.get` → `apps` returns the phone’s **launchable** packages (`package`, `label`, `versionName`, `system`) so the desktop can fill Home’s Apps section and, later, open each app in its own window (M7). Icons need no new machinery — bulk kind `appIcon` has carried them since v5, keyed by package name. There is deliberately no push event for installs/removals; the desktop re-requests on connect and reconciles against its per-device cache. **v19** (2026-09-20) — **`pc.mirror.keyframe`** (phone → desktop): the phone asks the PC to encode an IDR now, after it has lost sync. Purely a recovery hint, so a v≤18 desktop simply never receives it and the phone waits out the GOP instead. **v20** (2026-10-09) — **PC Remote, Windows-App style** (D-067): the desktop streams the PC's pointer (`pc.mirror.cursor`) so the phone draws it, link health once a second (`pc.mirror.stats`), and echoes `pc.mirror.ping` as `pc.mirror.pong`; `pc.input` gains `button` (right/middle click), `scroll` gains `precise` (raw wheel units); `pc.audio.start`/`pc.audio.stop` open the **pc-audio channel (6)** carrying the PC's sound as AAC-LC. Every addition is optional: a v≤19 desktop sends no cursor or stats, and the phone withholds right clicks (which an older desktop would turn into left clicks) and falls back to whole-notch scrolling. **v21** (2026-10-10) — **Windows Voice Access** (D-070): `pc.voiceaccess.set` (`{on, phoneMic}`) / `pc.voiceaccess.get` (phone → desktop) and `pc.voiceaccess.state` (desktop → phone, `{available, on, mic, cable, error?}`); the **pc-mic channel (7)** carries the phone's microphone to the PC as 16 kHz mono PCM. Later the same day (D-071, additive): `set` gains `installCable` and `state` gains `setup`, so the desktop can set the virtual cable up itself. (A first v21 draft for "private sessions" was built and withdrawn the same day, before release — D-068.) **v22** (2026-10-10) — **Windows tablet mode** (D-074): `pc.tabletmode.set` (`{on}`) / `pc.tabletmode.get` (phone → desktop) and `pc.tabletmode.state` (desktop → phone, `{on, scale?, windows, error?}`): Linc's own touch-friendly mode — the mirrored display's scale raised two steps and Explorer touch spacing on, restored afterwards; no admin. A v≤21 desktop never receives them; the bubble still shows Tablet mode and, tapped, says the PC needs updating. **v23** (2026-10-10) — **the phone as the PC's microphone** (D-075): `pc.mic.set` (`{on, installCable?}`, the Microphone tool: every app hears the phone) and `pc.mic.auto` (`{on, installCable?}`, PC Remote with "Phone" chosen: the cable is the default, and the phone's mic streams while a PC app is listening); `pc.voiceaccess.state` gains `micOn`, `tool`, `auto`, `listening`. With the phone chosen, the desktop also points Voice Access's own microphone setting at the cable. A v≤22 desktop never receives the new messages. **v24** (2026-10-11) — **clipboard pages** (D-083): `clipboard.get` (either direction, no payload) asks the peer for its clipboard; the peer answers with its ordinary clipboard message (`clipboard.changed` from the phone, `clipboard.set` from the desktop) only if it can read its clipboard right now and sync is on — Android allows that only while Linc is open, so the phone is often silent. Sent only to a peer that negotiated ≥ 24. Also (no version gate, an optional field older peers ignore): the desktop's `hello` carries `host`, the PC's name.

## Transport

- The Android companion service listens on an **abstract Unix domain socket** named `linc` (`LocalServerSocket("linc")`). Abstract sockets are not visible on the network and avoid TCP port conflicts on the phone.
- Linc Desktop reaches it through the ADB tunnel: `adb forward tcp:0 localabstract:linc` (letting ADB pick a free local port), then connects to `127.0.0.1:<port>`.
- All security derives from the ADB link: the socket is only reachable from paired, connected hosts via the ADB-over-TLS tunnel. The protocol itself carries no credentials in v0.
- **Since v9 there is a second transport — Direct TLS over LAN** (see §Direct TLS transport): mutual TLS with certificates pinned via `tls.exchange`, the phone dialing the desktop's advertised `_linc._tcp` endpoint. Framing, envelopes, session and channels are byte-identical over both transports.

## Framing

Each message is a frame:

```
[4-byte length, unsigned big-endian][UTF-8 JSON payload of exactly that length]
```

- Length counts the JSON bytes only (not the prefix).
- Maximum frame size: **1 MiB**. Receivers must close the connection on oversized frames. Bulk data (files) never travels over this protocol — it uses ADB sync.

## Message envelope

Every JSON payload is an object:

```json
{
  "v": 0,
  "type": "hello",
  "id": "d3b07384-uuid",
  "replyTo": null,
  "payload": { }
}
```

| Field | Type | Meaning |
|---|---|---|
| `v` | int | Protocol version. `0` for this spec. |
| `type` | string | Message type (below). |
| `id` | string | UUID, unique per message. |
| `replyTo` | string \| null | `id` of the message being answered, or `null` for unsolicited messages. |
| `payload` | object | Type-specific body; `{}` when empty. |

Unknown `type` values must be ignored (not an error). Unknown fields inside known payloads must be ignored. Each side must tolerate a peer whose version differs by ±1 and negotiate down to the lower version.

## Handshake

The desktop always sends first.

1. Desktop → Phone: `hello` with `{"app": "linc-desktop", "appVersion": "0.1.0", "minV": 0, "maxV": 3}`
2. Phone → Desktop: `hello` (`replyTo` set) with `{"app": "linc-android", "appVersion": "0.1.0", "v": 3}` — the phone picks the highest version both sides support.
3. If there is no overlap, the phone replies `error` with code `version-mismatch` and closes.

**Optional `author` (1.0.0.17, no version gate).** Both `hello` payloads may carry
`"author": "deloweProjects"` — open authorship information from each app's `Provenance` constants
(`Provenance.kt` / `Provenance.cs`, see `NOTICE`). It is purely informational: neither side reads,
checks or requires it, and an older peer ignores it under the unknown-fields rule above.

Both sides must gate version-specific messages on the negotiated `v` (e.g. no clipboard traffic on a v0 link).

No other message may be sent before the handshake completes.

## Message types (v0)

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `hello` | both | see Handshake | Version negotiation. |
| `ping` | both | `{}` | Liveness probe; peer must answer `pong` within 5 s. |
| `pong` | both | `{}` | Reply to `ping` (`replyTo` set). |
| `status.get` | desktop → phone | `{}` | Request a device status snapshot. |
| `status` | phone → desktop | `{"battery": 87, "charging": true, "storageFreeBytes": 12345, "storageTotalBytes": 67890, "wifiSsid": "…"}` | Status snapshot; also sent unsolicited on significant change. `wifiSsid` is `null` when unavailable (reading it requires location permission on modern Android, which the companion does not request in v0). |
| `error` | both | `{"code": "version-mismatch" \| "bad-frame" \| "internal", "message": "human-readable"}` | Failure report; `replyTo` set when tied to a request. |

## Message types (v1)

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `ok` | both | `{}` | Generic success reply (`replyTo` set). |
| `clipboard.set` | desktop → phone | `{"text": "…"}` | Set the phone clipboard; phone replies `ok`. |
| `clipboard.changed` | phone → desktop | `{"text": "…"}` | Unsolicited: the phone clipboard changed. Android 10+ only lets the focused app read the clipboard, so the phone sends this while the Linc app is visible — the desktop must not rely on it arriving for every copy. |

## Message types (v2)

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `notification.posted` | phone → desktop | `{"key": "…", "app": "WhatsApp", "title": "…", "text": "…", "postedAt": 1720512345678, "existing": false}` | Unsolicited: a notification appeared. `existing: true` marks the backlog pushed right after connecting — desktops list those without toasting. Ongoing/silent notifications are not relayed. Requires the user to grant notification access on the phone; without it, nothing is sent. |
| `notification.removed` | phone → desktop | `{"key": "…"}` | Unsolicited: a notification went away on the phone. |
| `notification.dismiss` | desktop → phone | `{"key": "…"}` | Cancel the notification on the phone; phone replies `ok` (also `ok` when the key is already gone). |

## Message types (v3)

No new message types — `status`/`status.get` gain new optional fields:

| Field | Type | Meaning |
|---|---|---|
| `cpuLoad1m` | double \| null | 1-minute load average from `/proc/loadavg`. `null` if unreadable (some OEM/SELinux builds restrict this). |
| `ramUsedBytes` | long \| null | `totalMem - availMem` from `ActivityManager.MemoryInfo`. No special permission required. |
| `ramTotalBytes` | long \| null | Companion field to `ramUsedBytes`. |
| `uptimeMillis` | long | `SystemClock.elapsedRealtime()` — always present, no permission needed. This is device **uptime**, not real usage-stats (which would need the special `PACKAGE_USAGE_STATS` grant); labelled honestly as uptime in the UI. |
| `wifiSignalLevel` | int \| null | 0-4, from `WifiManager.calculateSignalLevel(rssi)`. `null` when not on Wi-Fi. |
| `wifiLinkSpeedMbps` | int \| null | `WifiManager.getConnectionInfo().linkSpeed`. Deliberately reads only RSSI/link speed, never the SSID (which needs location permission on modern Android) — not worth the permission ask just for a signal indicator. |

## Message types (v4)

Still no new message types — `status` gains the phone's live color palette so the desktop matches it:

| Field | Type | Meaning |
|---|---|---|
| `themeLight` | object \| absent | The phone's Material You **light** palette as `{ "primary": "#RRGGBB", "onPrimary": "#RRGGBB", … }` — 18 M3 color roles keyed by name. Absent on Android 11 (no dynamic color there), in which case the desktop keeps its built-in palette, which is what the phone shows too. |
| `themeDark` | object \| absent | Same, for the phone's **dark** palette. The desktop picks light or dark to match its own current Windows theme. |

The phone derives these from `dynamicLightColorScheme`/`dynamicDarkColorScheme` (wallpaper-based Material You). The desktop retints its brush resources in place from whichever palette matches its current theme — so both apps always show the same colors, following the phone's wallpaper. Fields are additive/optional as with v3: a pre-v4 phone simply omits them and the desktop keeps its default palette.

## Protocol v5 — session layer & channels (M12, implemented)

v5 is the pipeline release, rolled out across M12–M18. **This section (session layer + bulk channel) is implemented as of M12.** The later subsections (pub/sub, rich notifications, media, device convenience, sync) are still **draft** and land in M13+; treat them as design, not contract, until their milestone ships.

### Session & channels

The phone-side listener accepts **multiple concurrent connections** through the single `adb forward` (scrcpy already proves multiple connections work over one tunnel). Connections are of two shapes, disambiguated by their **first frame** (same length-prefixed JSON framing):

- **Control connection** — first frame is the normal `hello` envelope (has a `"type"` field). This is exactly today's socket, unchanged, so a v≤4 peer is served identically. When the negotiated version is ≥ 5, the phone's `hello` **reply** payload additionally carries `"sessionToken": "uuid"`. There is exactly one control connection per session; it is always the first one opened.
- **Channel connection** — first frame is a channel-open header (has a `"channel"` field, no `"type"`), then the channel switches to its own format:

```json
{"channel": 3, "sessionToken": "uuid"}
```

**Pinned disambiguation rule (M12):** the phone parses the first frame as a JSON object. A top-level `channel` field ⇒ channel connection; otherwise it is parsed as a `hello` envelope ⇒ control/legacy connection. This lets a v5 desktop reach a v4 phone with zero risk: the control connection never sends a header (it can't — version isn't known until after `hello`), and channel headers are only ever sent v5↔v5 (the desktop opens channels only after negotiating ≥ 5).

- `channel`: `0` control (never opened as a header — see above), `1` mirror, `2` files, `3` bulk, `4` audio.
- A channel connection must present a `sessionToken` matching the one the control connection was issued, or the phone closes it silently. Channels 1/2/4 switch to their own binary formats (mirror = scrcpy-server's protocol in phase 1; files/audio defined during their milestones).

### Bulk channel (3)

Small binaries fetched by id — app icons in M12, later album art / wallpaper / thumbnails. After the channel-open header, the connection is a simple request/response loop:

1. Desktop → phone: one length-prefixed JSON frame `{"kind": "appIcon", "id": "<package-name>"}`.
2. Phone → desktop: one **binary** frame — a 4-byte unsigned big-endian length prefix followed by exactly that many bytes (a PNG for `appIcon`). A length of `0` means "not found / unavailable". The 1 MiB frame cap applies (icons are a few KB; larger bulk kinds will raise the cap when they land).
3. Repeat for further ids, or either side closes to end the channel.

`kind` is an open string. M12 defined `appIcon` (id = Android package name); v6 (M13) adds `largeIcon` (id = a `largeIconId` from `notification.posted`; served from a small in-memory cache, so ids from dismissed/old notifications may return 0-length) and `albumArt` (id = an `artId` from `media.state`, same cache semantics). Unknown kinds get a 0-length response.

## Message types (v6 — M13)

### Pub/sub on the control channel

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `subscribe` | desktop → phone | `{"topic": "media"}` | Start an event stream; phone replies `ok` (also `ok` for unknown topics — nothing will arrive). Topics: `status`, `notifications`, `media`, `clipboard` (plus `sync`, `battery` reserved for later milestones). |
| `unsubscribe` | desktop → phone | `{"topic": "media"}` | Stop it; phone replies `ok`. |

Existing unsolicited messages (`status`, `notification.*`, `clipboard.changed`) become events on their topics. **Gating rule (D-019): only on a link negotiated ≥ 6 does the phone withhold events until subscribed** — a v≤5 peer keeps receiving unsolicited events exactly as before (it has no way to subscribe). Subscriptions live for the duration of the control connection and reset on reconnect. The notification backlog (`existing: true` items) is pushed when `subscribe {"topic": "notifications"}` arrives, not at handshake; the current `media.state` is likewise pushed on `subscribe {"topic": "media"}`.

### Rich notifications (extends v2)

`notification.posted` gains optional fields (absent when unavailable): `appPackage`, `category` (the `Notification.category` string), `conversationTitle`, `largeIconId` (fetch via bulk kind `largeIcon`, cache per id; typically the sender's avatar), `actions: [{"index": 0, "title": "Reply", "allowsReply": true}]`. The app's own icon needs no dedicated id — fetch bulk kind `appIcon` with `appPackage` as the id.

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `notification.action` | desktop → phone | `{"key": "…", "index": 1}` | Fire a non-reply action's PendingIntent; phone replies `ok`, or `error` (code `internal`) when the notification/action is gone or firing failed. |
| `notification.reply` | desktop → phone | `{"key": "…", "index": 0, "text": "…"}` | Fill the action's RemoteInput with `text` and fire it; phone replies `ok` or `error` as above. |

### Media

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `media.state` | phone → desktop | `{"title", "artist", "album", "artId", "positionMs", "durationMs", "playing", "appPackage"}` | Snapshot of the primary active `MediaSession` (the playing one, else the most recent); sent on change while subscribed, and once on subscribe. All fields nullable except `playing`. `{"none": true}` when no session exists (e.g. the last player closed). No new permission — notification access already unlocks `MediaSessionManager`. |
| `media.control` | desktop → phone | `{"action": "play" \| "pause" \| "next" \| "prev" \| "seek", "positionMs": 0}` | Transport control on the primary session; phone replies `ok` (also `ok` when no session exists — the control is simply dropped). `positionMs` only for `seek`. |

## Message types (v7 — M14)

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `device.locate` | desktop → phone | `{}` | **Toggle** a locate ring: starts looping the default ringtone at maximum alarm volume (ignores silent mode; the previous volume is restored afterwards), auto-stopping after 30 s; a second `device.locate` while ringing stops it. Phone replies `ok`. |
| `dnd.set` | desktop → phone | `{"enabled": true}` | Turn Do Not Disturb on (priority-only interruption filter) or off. Phone replies `ok`, or `error` with code `not-granted` and a plain-language message when the user hasn't given Linc the phone's DND policy grant. |

`status` gains an optional field:

| Field | Type | Meaning |
|---|---|---|
| `dndEnabled` | bool \| null | Whether an interruption filter other than "allow all" is active. `null` when unreadable. Lets the desktop's DND toggle reflect the phone's real state (refreshed with the normal status cadence). |

New error code: `not-granted` — the request needs a phone-side permission/grant the user hasn't given; the `message` explains which and how in plain language.

## Message types (v8 — M14 polish)

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `sound.set` | desktop → phone | `{"mode": "normal" \| "vibrate" \| "silent"}` | Set the ringer mode. Needs the same DND policy grant as `dnd.set`; `error` code `not-granted` otherwise. Phone replies `ok`. |

`status` gains optional fields:

| Field | Type | Meaning |
|---|---|---|
| `soundMode` | string \| absent | Current ringer mode: `normal`, `vibrate`, or `silent`. |
| `wallpaperId` | string \| absent | Content id of the phone's current wallpaper **thumbnail**. Present only when the phone can read the wallpaper — Android 13+ requires the All-files-access grant (`MANAGE_EXTERNAL_STORAGE`), which Linc requests from its Settings screen on sideload builds (D-021). Changes when the wallpaper changes; the desktop fetches bytes via bulk kind `wallpaper` and caches per id. |

New bulk kind: `wallpaper` (id = a `wallpaperId` from status) — a **small pre-blurred PNG** (~48 px wide, aspect preserved). The phone downsamples aggressively so the desktop can stretch it into a frosted background without any client-side blur machinery; full-resolution wallpaper never crosses the wire.

## Direct TLS transport (v9 — M15)

The ADB-independent path (PIPELINE.md transport C; decisions D-013, D-022). ADB is not replaced — this is a sibling transport that keeps everything except ADB-bound features (mirror, and until M17 files/screenshot) working when wireless debugging is broken or off.

**Pairing (trust-on-first-use over ADB):** on a ≥ 9 link over ADB where the desktop has no pinned phone certificate, the desktop sends `tls.exchange`. Both sides store the peer's exact certificate bytes; validation later is byte-equality pinning, not chain building. The exchange rides the ADB-over-TLS tunnel, which is already mutually authenticated by the wireless-debugging pairing — no new user-facing step.

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `tls.exchange` | desktop → phone | `{"cert": "<base64 DER>", "tlsPort": 46001, "reversePort": 46011}` | Pin my certificate; here is where my TLS listener lives (`tlsPort` on this machine's LAN addresses, `reversePort` as `127.0.0.1:<reversePort>` on the phone when an `adb reverse` mapping is up). Phone replies with the same shape: `{"cert": "<base64 DER>"}` (its Android-KeyStore self-signed certificate). Re-sending replaces the pin. **The desktop sends this on every ADB connect**, not only when it has nothing pinned: the phone's certificate is destroyed when the app is uninstalled, so a reinstall invalidates the pin, and because unknown certificates are dropped silently by design, Direct TLS would then fail forever with no error anywhere. Re-pinning over the already-authenticated ADB link costs one round trip. |
| `channel.open` | desktop → phone | `{"channel": 3}` | TLS sessions only: the phone initiates every TLS connection, so the desktop can't dial extra channels itself. On receipt the phone dials a **new** TLS connection to the same endpoint, sends the normal `{channel, sessionToken}` header, then replies `ok` on the control channel (or `error` if the dial failed). The desktop correlates the inbound connection by session token + channel number. Over ADB this message is never sent — the desktop keeps dialing the forwarded port directly. |

**Connection model:**
- Desktop: generates a self-signed certificate once, listens with **mutual TLS** (client certificate required, pinned), advertises `_linc._tcp` on mDNS while the "Background connection" setting is on. Connections presenting unknown certificates are dropped silently.
- Phone: while its companion service runs (and its "Background connection"/presence setting is on) and no desktop is connected, it watches for the advert (plus tries `127.0.0.1:<reversePort>` when told one exists — the `adb reverse` path for cable-only setups) and dials out. The **desktop still speaks first**: the first frame on the phone-dialed control connection is the desktop's `hello`, so the phone's existing first-frame dispatch (D-018) works unchanged.
- Session semantics (token, channel headers, bulk framing) are identical to the ADB transport.
- A pre-v9 peer never receives `tls.exchange` and never dials; everything degrades to ADB-only exactly as today.

## Files channel (2) & convenience messages (v10 — M17)

### Files channel (2)

A typed channel (opened over ADB by dialing the forwarded port, or over Direct TLS via `channel.open` — same as bulk). After the `{channel:2, sessionToken}` header it is a **request/response loop**; each request is one length-prefixed JSON frame, each response one length-prefixed JSON header frame optionally followed by a raw binary body. Paths are absolute phone paths (e.g. `/sdcard/DCIM`). On Direct TLS the phone reads them with `java.io.File`, which needs the **All-files-access grant** (D-021 — the same grant wallpaper uses); without it operations reply `{"error":"not-granted"}`.

| Request | Response header | Body | Purpose |
|---|---|---|---|
| `{"op":"list","path":"…"}` | `{"entries":[{"name","size","dir","modified"}]}` or `{"error":"…"}` | — | Directory listing. |
| `{"op":"pull","path":"…"}` | `{"size":N}` or `{"error":"…"}` | N raw bytes | Download a file. |
| `{"op":"push","path":"…","size":N}` (then N raw bytes) | `{"ok":true}` or `{"error":"…"}` | — | Upload a file. |

Behaviour matches the ADB-sync path already used on ADB links, so the desktop's `IFileService` selects transport transparently and callers are unchanged.

### Photos

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `photos.recent` | desktop → phone | `{"limit": 20}` | Reply lists recent images from `MediaStore`: `{"photos":[{"id","path","takenAt"}]}`. Thumbnails via bulk kind `photo` (id = the photo id); full image via the files channel `pull` on `path`. No new permission on Android 13+ beyond the All-files grant already used. |

### Convenience

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `continue.url` | both | `{"url": "…"}` | Open the link on the other device's default browser/app; peer replies `ok`. |
| `share.item` | phone → desktop | `{"kind": "text" \| "url" \| "file", "text": "…", "path": "…"}` | Android share-sheet "Send to PC" target (unsolicited). `text`/`url` are shown/opened on the PC; `file` is pulled over the files channel from `path`. |

New bulk kind: `photo` (id = a `photos.recent` photo id) — a small JPEG thumbnail (~256 px), cached per id like other bulk kinds.

## Sync config & Messages lane (v11 — M18a)

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `sync.config` | desktop → phone | `{"folders":false,"photos":false,"messages":true,"calls":false}` | Enable/disable each Sync-page lane; the phone persists it and replies `ok`. Turning `messages` on requires the SMS grants (below) or the phone replies `error` code `not-granted`. |
| `sms.list` | desktop → phone | `{"limit":50}` | Reply lists recent messages newest-first: `{"messages":[{"address","body","date","incoming"}]}`. Needs `READ_SMS`; `error` code `not-granted` otherwise. |
| `sms.send` | desktop → phone | `{"address":"+1…","body":"…"}` | Send an SMS via `SmsManager`; replies `ok`, or `error` code `not-granted` (no `SEND_SMS`). No default-SMS-app needed (D-025). |
| `sms.received` | phone → desktop | `{"address","body","date"}` | Unsolicited: an SMS arrived (via the phone's `RECEIVE_SMS` receiver). Sent while the Messages lane is on. |

The Messages lane ships in **sideload builds only** (D-016); numbers are shown without contact names in v1 (no `READ_CONTACTS`). Folder/photo sync (`sync.event`) is M18c and remains draft below.

## Calls lane (v12 — M18b)

Sideload-only (D-016/D-026); no PC audio — the phone places/handles the call (D-017).

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `call.log` | desktop → phone | `{"limit":50}` | Reply lists recent calls newest-first: `{"calls":[{"number","type","date","duration"}]}`; `type` ∈ `incoming` \| `outgoing` \| `missed` \| `rejected`. Needs `READ_CALL_LOG`; `error` code `not-granted` otherwise. |
| `call.dial` | desktop → phone | `{"number":"+1…"}` | Place a call from the phone (`ACTION_CALL`, needs `CALL_PHONE`); replies `ok` or `error` `not-granted`. |
| `call.decline` | desktop → phone | `{}` | Reject the ringing / end the ongoing call (`TelecomManager.endCall`, needs `ANSWER_PHONE_CALLS`); replies `ok`. |
| `call.incoming` | phone → desktop | `{"number":"…","ringing":true}` | Unsolicited: a call started ringing (`ringing:true`) or stopped (`ringing:false`, sent when it ends/answers so the desktop dismisses its banner). Only while the Calls lane is on. `number` may be empty if the OS withholds it. |

## PC → phone media & Share (v13 — M19)

M19 adds the phone a **Home** screen and a **Share** tab, and with them the first **PC → phone push** features (D-028). The desktop sends these unsolicited to the phone, gated on negotiated ≥ 13 — a pre-v13 phone never receives them, and a pre-v13 desktop never sends them, so mixed pairs simply don't show the new widgets.

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `pc.media.state` | desktop → phone | `{"title","artist","playing","positionMs","durationMs","app"}` or `{"none":true}` | What's playing **on the PC** (Windows `GlobalSystemMediaTransportControlsSessionManager`). Sent on change and once when a phone connects. No album art in v1. All fields optional except `playing`. |
| `pc.media.control` | phone → desktop | `{"action":"play" \| "pause" \| "next" \| "prev"}` | Transport control on the PC's current session; **fire-and-forget** (the desktop executes it and does not reply). |
| `share.incoming` | desktop → phone | `{"name","path"}` | The desktop has just pushed a file to `path` on the phone (over the files channel, kind `push`) and announces it for the phone's Share tab. The phone opens it on tap via a FileProvider URI. |

`share.item` (v10) with `kind:"file"` is now **implemented desktop-side**: the phone's Share tab copies a picked file into `/sdcard/Download/Linc/outbox/<name>` and sends `{"kind":"file","path":"…"}`; the desktop pulls it over the files channel into `Downloads/Linc` and notifies. (Text/url share was already handled.)

The **clipboard widget** on the phone Home needs no wire change — the phone keeps a small in-memory history of clips as they pass through `ClipboardBridge` in either direction (v1 clipboard sync).

## Folder & Photos sync (M18c — no wire change)

M18c ships the folder/photo **sync engine** and **adds no message types** — it is a desktop-side engine built entirely on the existing v10 files channel (`list`/`pull`/`push`) and v11 `sync.config`. The protocol stays **v12**. The desktop polls the phone folder (`list`), pulls new/newer files, and pushes new/newer PC files, keeping persistent per-pair seen-state as the loop/duplicate guard (D-027). The phone needs no new code — it is a dumb file server behind the files channel (which already needs the All-files-access grant, D-021).

The draft `sync.event` (phone → desktop new-file push) is **dropped for v1** — the desktop-polling design makes it unnecessary and it would cost a phone-side `FileObserver` (battery + code). A real-time phone push remains a Future Idea; if it lands it bumps the version then.

## BLE presence beacon (M04 — no wire change, protocol stays v13)

A **sibling signal, not a transport** (D-034). The phone advertises a tiny Bluetooth-LE beacon; a paired desktop that hears it knows the phone is *physically near* and nothing else. **No data ever rides BLE**, in either direction, and the beacon is not connectable. It adds no message types and no version bump — the JSON protocol is untouched — but the on-air format is a contract between the two apps, so it is specified here.

**Advertisement.** A single manufacturer-data section, non-connectable, `ADVERTISE_MODE_LOW_POWER`, no device name and no TX power (the phone's name is not broadcast):

| Field | Value |
|---|---|
| Company id | `0xFFFF` — the Bluetooth SIG's id reserved for testing and development, the correct choice for an unregistered project. |
| Payload | `4C 43` (`"LC"`) followed by an 8-byte rotating id. 10 bytes total. |

**Rotating id.** `SHA-256(certificate DER ‖ slot)` truncated to its first 8 bytes, where `certificate` is the phone's Direct-TLS certificate (v9, the one the desktop pins via `tls.exchange`) and `slot` is `unixSeconds / 300` as 8 big-endian bytes.

Consequences, all deliberate:
- Only a desktop that has **already pinned this phone's certificate** can recognise the beacon — there is no shared secret otherwise, so an unpaired PC learns nothing.
- The id **changes every 5 minutes**, so a passive observer cannot follow the phone between rotations. The company id is shared with every other test-space beacon, so the magic bytes are a filter, not an identifier.
- The desktop matches against slot **±1** to absorb clock skew between two independent devices; the phone republishes on slot boundaries (not a flat interval, which would drift later every cycle).
- The phone's certificate lives in the Android KeyStore, which is **wiped when the app is uninstalled**. A reinstall therefore changes the beacon id — and would strand a desktop holding the old pin, which is why the desktop now re-runs `tls.exchange` on every ADB connect rather than only when nothing is pinned (see the v9 section).

**Degradation.** No Bluetooth radio on the PC, Bluetooth off on either side, `BLUETOOTH_ADVERTISE` not granted, or no certificate yet — each simply means no sightings. Everything else about Linc behaves exactly as it did before M04.

## v14 — Reverse mirror (Era 2, M05; D-029, D-039, D-040)

The mirror flips: the phone views and controls the PC. First **desktop-served stream**: a new **pc-video channel (5)** carries H.264 from the desktop to the phone.

**Channel 5 (pc-video).** Opened like any typed channel (`{channel: 5, sessionToken}`), but the **desktop is the server** — after the header it sends, the phone receives. It is opened by the **same dial-back mechanism for every transport**: the desktop sends `channel.open` and the phone dials a fresh TLS connection back (over LAN directly, or over the USB cable through D-022's `adb reverse` tunnel to the same TLS listener). This is simpler than the per-transport story the draft first imagined — there is exactly one path, and it works as long as Direct TLS is alive (which, post-M04, it is on every ADB connect). The phone dials back using its full candidate-endpoint list (reverse tunnel + advertised LAN addresses), not only the control link's endpoint, so channel 5 opens even when control is carried over ADB.

After the channel header the desktop writes a length-prefixed JSON config frame `{"codec":"h264","width","height","displayId"}`, then **video packets**: a 12-byte big-endian header — an 8-byte word (bit 63 = codec-config packet, bit 62 = keyframe, low 62 bits = PTS in microseconds) followed by a 4-byte payload length — then the Annex-B H.264 payload. This is the scrcpy framing shape M16a already understands, reused deliberately. The encoder re-sends SPS/PPS with every keyframe, and its first output is always an IDR, so a phone that attaches before streaming starts decodes from the first packet.

Control messages (control channel, fire-and-forget like `pc.media.control`):

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `pc.mirror.start` | phone → desktop | `{"displayId": 0, "maxSize"?, "fps"?, "bitrate"?}` | Ask the desktop to start streaming a display. Optional `fps`/`bitrate` carry the phone's chosen mirror quality (clamped desktop-side; absent = desktop default). The desktop opens channel 5 and streams; the config frame carries the real `{width,height}`. |
| `pc.mirror.stop` | both | `{}` | Stop the stream; either side may send. |
| `pc.displays.get` → `pc.displays` | phone → desktop → phone | `{"displays":[{"id","name","width","height","primary"}]}` | Enumerate PC displays for the picker. |
| `pc.input` | phone → desktop | `{"kind":"move"\|"down"\|"up"\|"scroll"\|"key"\|"text", "x","y" (0–65535 vs the video), "dx","dy", "keyCode", "down", "text", "mode":"touch"\|"trackpad"}` | Injected via `SendInput`. Coordinates map video→physical→virtual-desktop (see D-029/D-040). |
| `pc.textfocus` | desktop → phone | `{"focused": true\|false}` | A text field on the PC gained/lost keyboard focus (caret poll via `GetGUIThreadInfo`, while streaming). The phone raises/lowers its soft keyboard to match. A v≤13 peer never receives it; an old-v14 phone ignores the unknown type and just doesn't auto-raise the keyboard. |

**Dropped (was M06):** `pc.display.attach` / `pc.display.detach` for an extend-mode virtual monitor were never shipped and are **abandoned** — Extend mode was cut per user (see ROADMAP M06, D-030). No v15 was minted for extend; the v15 that exists is the display-control one below.

## v15 — Display control from the PC (Convergence, M5c; D-001, D-055)

The PC gets widgets that change the phone's **screen orientation** and **brightness**. Per **D-001** these ride the companion protocol and are applied phone-side through `Settings.System` — the desktop must **never** shell out to `adb shell settings put` for them.

Two new desktop → phone messages, both **request/reply** (the phone answers `ok` or `error`, `replyTo` set), both gated on negotiated **≥ 15**:

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `display.rotation.set` | desktop → phone | `{"mode": "auto" \| "portrait" \| "landscape"}` | `auto` → `ACCELEROMETER_ROTATION = 1`. `portrait`/`landscape` → `ACCELEROMETER_ROTATION = 0` plus `USER_ROTATION = 0` / `1` respectively. One message covers both the auto-rotate toggle and the force-orientation widget — deliberately not two types. |
| `display.brightness.set` | desktop → phone | `{"auto": true}` or `{"auto": false, "level": 0–100}` | `auto: true` → `SCREEN_BRIGHTNESS_MODE = SCREEN_BRIGHTNESS_MODE_AUTOMATIC`. `auto: false` → mode `MANUAL` and `SCREEN_BRIGHTNESS` set from `level`. **`level` is a 0–100 percentage on the wire, never a raw platform value** — the phone scales it to the device's real range, so the PC never needs to know the phone's brightness ceiling. `level` is required when `auto` is false and ignored when true. |

**Reply contract.** `ok` (`replyTo` set) on success. On failure, `error` (`replyTo` set) with:

- `not-granted` — the companion lacks `android.permission.WRITE_SETTINGS`. This is an **appop**, not a runtime permission: it cannot be granted by a dialog, only by the user in *Settings › Apps › Special app access › Modify system settings*. Check it with `Settings.System.canWrite(context)` **before** attempting the write, and return this code rather than letting `SecurityException` escape. Same shape as v7's `dnd.set` / v8's `sound.set` grant refusals — reuse their wording style.
- `internal` — a malformed payload (missing/unknown `mode`, `level` out of range, `level` absent while `auto` is false) or an unexpected write failure. Validate `level` phone-side too; do not trust the desktop to have clamped it.

**`status` gains three optional fields** (present only when the negotiated version is ≥ 15, and only when readable — `Settings.System` *reads* need no permission, so in practice always):

| Field | Type | Meaning |
|---|---|---|
| `rotationMode` | string | `"auto"`, `"portrait"`, or `"landscape"` — derived from `ACCELEROMETER_ROTATION` + `USER_ROTATION`. |
| `brightnessAuto` | bool | `SCREEN_BRIGHTNESS_MODE == AUTOMATIC`. |
| `brightnessLevel` | int | Current brightness as **0–100**, scaled down from the platform value. |

These exist so the widgets open showing the phone's real state instead of a guess, and so a change made on the phone itself is reflected on the PC. They are additive optional fields, so a v≤14 desktop reading a v15 phone's `status` simply ignores them per the envelope rules.

**Graceful degradation.** A v≤14 phone never receives either new type (the desktop gates on the negotiated version) and would ignore them anyway. A v≤14 desktop never sends them. So a mixed-version pair loses the widgets and nothing else — the desktop must **disable** (not hide) the widgets with a plain-language reason when the negotiated version is below 15.

## v16 — Installed-app inventory (Convergence, M6c; D-058)

The desktop needs the phone's list of **launchable** apps to fill Home's Apps section (M6c) and, later,
to open each one in its own window (M7). Icons need **no new machinery** — bulk kind `appIcon` has
existed since v5 with the package name as its id.

One new request/reply pair, gated on negotiated **≥ 16**:

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `apps.get` | desktop → phone | `{}` | Ask for the installed-app inventory. |
| `apps` | phone → desktop | `{"apps": [{"package", "label", "versionName", "system"}]}` | The reply (`replyTo` set). |

**Launchable only.** The phone returns only packages that resolve a launcher intent
(`CATEGORY_LAUNCHER`). A raw `getInstalledPackages()` dump is several hundred entries of framework
noise the user can neither see nor open, and it would bloat every reply. `system` is `true` when the
package carries `FLAG_SYSTEM`, so the desktop can group or de-emphasise them; it is **not** a reason to
omit them — plenty of launchable system apps (Camera, Settings, Photos) are ones users want.

**Field notes.** `label` is the user-visible name from the package manager, already localised — the
desktop must never derive a name from the package id. `versionName` is optional and may be absent or
empty on some builds; treat it as cosmetic. Sort order is **not** part of the contract: the phone may
return any order and the desktop sorts for display.

**Size.** A few hundred entries of short strings is tens of KB — comfortably inside the control
channel's frame cap, so this rides the control connection rather than the bulk channel. Icons stay on
bulk, fetched lazily per package and cached by the desktop.

**No push.** There is deliberately **no** unsolicited "app installed/removed" event. The desktop
re-requests on connect and reconciles against its cache; a phone-side `PackageManager` observer would
cost a broadcast receiver and battery for a list that changes a few times a month. If that ever proves
wrong it can be added later as its own version bump.

**Graceful degradation.** A v≤15 peer never sends or receives these, so the Apps section shows its
empty state with a plain-language reason and nothing else changes.

## v17 — Phone drives the PC: quick controls (Convergence, M4b; D-042)

The phone's Tools surface gains **power, volume and display-brightness control of the PC**. M4a already
shipped the keyboard and trackpad **with no wire change**, because `pc.input` (v14) covers them — this
version is only for the things `pc.input` cannot express.

**One new request/reply pair plus one state query, all gated on negotiated ≥ 17:**

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `pc.control` | phone → desktop | `{"action": <enum>, "level"?: 0–100, "step"?: int, "on"?: bool, "confirm"?: true}` | Perform a PC control action. **Request/reply** — the desktop answers `ok` or `error` with `replyTo` set. |
| `pc.state.get` | phone → desktop | `{}` | Ask for the PC's current controllable state. |
| `pc.state` | desktop → phone | `{"volume": 0–100, "muted": bool, "brightness": 0–100 \| null, "canBrightness": bool, "canSleep": bool, "canShutdown": bool}` | The reply (`replyTo` set). |

**Why request/reply and not fire-and-forget.** `pc.input` is fire-and-forget because a dropped mouse
delta is invisible and self-correcting. **A dropped `shutdown` is not**, and every action here can fail
for a reason the user needs to see. This follows v15's display-control precedent, not v14's input path.

### The action enum

| Action | Extra fields | Notes |
|---|---|---|
| `lock` | — | `LockWorkStation`. Always available. |
| `sleep` | — | Suspend. **Not destructive** — no confirm required; nothing is lost. |
| `shutdown` | **`confirm: true` required** | |
| `restart` | **`confirm: true` required** | |
| `volume.up` / `volume.down` | `step`? (default 1) | One `step` = one notch, matching a keyboard volume key. |
| `volume.set` | `level` 0–100 | |
| `volume.mute` | `on`? | `on` absent = **toggle**; `on` present = set explicitly. Explicit form exists so a widget showing mute state can be idempotent. |
| `brightness.set` | `level` 0–100 | The **PC's** display brightness, not the phone's. |

**`level` is a 0–100 percentage on the wire, never a raw platform value** — the same rule v15 set for
phone brightness, for the same reason: neither side should need to know the other's native range.

### Confirmation is enforced on the wire, not only in the UI

`shutdown` and `restart` **must** carry `confirm: true`; the desktop rejects them otherwise with
`needs-confirm`. The phone also shows its own confirmation dialog — **both, deliberately.** A UI-only
gate means one stray frame, one bug, or one replayed message can power off the user's PC. The wire flag
makes the safety gate cheap, explicit and testable from a harness without a running UI.

### Reply contract

`ok` (`replyTo` set) on success. On failure, `error` (`replyTo` set) with:

- `needs-confirm` — a destructive action arrived without `confirm: true`.
- `unsupported` — this PC cannot do it: no DDC/CI or WMI brightness control on the active display, or
  sleep/shutdown disabled by policy. **The phone should have known already from `pc.state`** — this is
  the backstop, not the primary path.
- `denied` — the OS refused. Most likely `shutdown`/`restart` without `SE_SHUTDOWN_NAME` enabled on the
  process token (a normal user process may enable it; enable it explicitly rather than assuming).
- `internal` — malformed payload (unknown `action`, `level` out of range, `level` missing where
  required) or an unexpected failure. **Validate desktop-side; never trust the phone to have clamped.**

### `pc.state` and the disable-don't-hide rule

PC brightness is the awkward one: it works on laptop panels (WMI) and on external monitors that honour
DDC/CI, and **not** on plenty of ordinary desktop monitors. `canBrightness` lets the phone **disable the
control with a plain-language reason** instead of offering a slider that silently does nothing — the
same posture v15 required of the desktop when the negotiated version is too low. `brightness` is `null`
when unreadable. `canSleep` / `canShutdown` cover policy-locked machines.

**No push.** There is deliberately **no** unsolicited state event when PC volume changes at the PC. The
phone requests `pc.state` when the Tools surface opens and re-requests after any `pc.control` it sends.
Same reasoning as v16's app inventory: a desktop-side volume-change listener costs a permanent
subscription for a value nobody is looking at most of the time. If that proves wrong it can be added
later under its own bump.

**Graceful degradation.** A v≤16 peer never sends or receives any of these. The phone **disables** the
quick-control widgets with a plain-language reason and Tools keeps working — keyboard and trackpad are
v14 and unaffected, which is exactly why M4a shipped first.

## v18 — Instant ADB link-up over a hotspot (Convergence, M13b; owner spec 2026-08-10)

**The core idea, and why it removes discovery entirely.** Every naive ADB-over-Wi-Fi path burns time on
discovery — mDNS browsing, ARP reads, subnet sweeps — all to answer one question: *what is the phone's
IP on this link?* **If the two apps already hold a socket over that same link, the OS answered it when
it established the socket.** The desktop reads the remote endpoint of its own control connection; that
address is correct by construction, cannot be stale, and cannot name a different device.

This is sound **on a hotspot link specifically**: a single L2 segment, no NAT between the hosts, so the
peer address the desktop sees is exactly the address `adbd` is reachable on. **It does not hold across a
router doing NAT or over any relay** — hence the `addrs` fallback and the same-subnet filter below.

So the only unknown left is the **port**, and the phone knows it. Discovery becomes a protocol message.

**Four messages, all gated on negotiated ≥ 18:**

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `adb.announce` | phone → desktop | `{"gen": int, "serial"?: str, "mode": "tls"\|"tcpip", "port": int, "verified": bool, "addrs": [{"ip","iface","prefix"}]}` | Unsolicited, on every state change. `verified` means the phone just completed a loopback connect to that port. |
| `adb.down` | phone → desktop | `{"gen": int, "reason": str}` | adbd went away. |
| `adb.arm` | desktop → phone | `{"gen": int, "prefer": "tcpip"\|"tls"}` | Ask the phone to arm or re-arm adbd. **`prefer` is ADVISORY — see below.** |
| `adb.ack` | desktop → phone | `{"gen": int, "ok": bool, "endpoint": str}` | Connection outcome, so the phone can stop retrying and log honestly. |

**`gen` is mandatory and is the whole defence against the worst bug in this design.** A monotonic counter
incremented **on every link event**. Without it, a role flip in flight (phone-AP → PC-AP) lets a stale
announcement from the previous topology arrive *after* the new one and point ADB at a dead address — an
intermittent failure that costs weeks. Implement the counter before anything that consumes these messages.

**The filter is `>=`, not `>` — accept equal, reject strictly lower** *(clarified 2026-08-11 after M13b
implemented strictly-greater)*. `gen` marks the **link epoch**, not the message. Several messages
legitimately share one epoch, and rejecting equals silently drops them:
- **The port-promotion case is the one that proves it.** Issuing `adb tcpip 5555` rebinds `adbd` **without
  any link event**, so the phone re-announces the new port under the *same* `gen`. A strictly-greater
  filter discards that announcement and the promotion optimisation silently never takes effect — a bug
  that would look like "promotion doesn't work" and cost a session to find.
- A `tls → tcpip` mode change, or a re-announce after a failed `adb.ack`, are the same shape.

**Within one epoch, last-write-wins:** a later announcement at the same `gen` supersedes the earlier.
Ordering inside an epoch is guaranteed by the control socket, which is a single ordered stream.

**`serial` is OPTIONAL, and identity is verified desktop-side** *(corrected 2026-08-11 — M13b found the
original spec unimplementable)*. **An unprivileged Android app cannot read its own ADB serial**
(`Build.getSerial()` has needed `READ_PRIVILEGED_PHONE_STATE` since Android 10), so requiring it would
reject every real announcement. The desktop already knows the serial it is paired to
(`DeviceRegistry.PairedSerial`), so **`get-serialno` is checked against that**, and an announced `serial`
is treated as a cross-check when present. This is strictly stronger than trusting the phone's claim.

**`verified` — never announce a port you have not just connected to.** The phone confirms adbd is
actually accepting via a loopback connect before announcing. This moves failure detection onto the
device, where it is instant and local, instead of making the desktop discover it by timing out. **The
desktop must treat `verified: false` as "do not attempt".** This matters because **the L3 link comes up
before `adbd` has re-bound to the new interface** — link-up is not readiness.

**`addrs` and the same-subnet rule.** Included even though the peer address normally makes it redundant,
because the control link may be running over something *other* than the hotspot (mobile data, LAN, the
`adb reverse` tunnel), in which case the peer address belongs to a different network and must not be
used. **The desktop must reject any announced address that is not inside one of its own interfaces'
subnets** — Android freely advertises mobile-data and secondary-interface addresses that are unreachable
from the hotspot segment, and each one costs a full connect timeout.

**Reachability is not identity.** A successful TCP connect proves only that *something* is listening.
The desktop requires all three: `adb connect` reports connected, `get-state` is exactly `device`, and
`get-serialno` matches the announced `serial`. **`unauthorized` on a TLS port means "not paired" — route
to pairing, do not retry. `offline` means stale server state — `adb disconnect` then retry once.**

**MAC addresses are never a key.** Android randomises MAC per SSID. **The ADB serial is the only stable
identity;** treat IP and MAC as hints with a TTL.

**`adb.arm`'s `prefer` is ADVISORY, and tcpip is the DESKTOP's job** *(corrected 2026-08-11 — M13d found
the original wording unimplementable)*. A non-root app **cannot** arm legacy tcpip: that needs
`service.adb.tcp.port` set via `setprop`, which requires the `shell` or `root` UID.
`WRITE_SECURE_SETTINGS` grants `Settings.Global`/`Secure` writes — enough for `adb_wifi_enabled`
(wireless debugging, TLS) and nothing more. **So the phone arms what it can and announces whatever
`adbd` lands on; that is correct behaviour, not a shortfall.** `prefer: "tcpip"` means *"I intend to
promote"*, and **the desktop performs the promotion itself with `adb tcpip 5555` once it holds any
working ADB connection.** Promotion does not survive reboot — it is an optimisation layered on the TLS
path, never the primary route.

**A re-announce that names a DIFFERENT endpoint must supersede a live link** *(added 2026-08-11)*.
Accepting the message at the `gen` filter is not sufficient: if the connect path short-circuits whenever
a link from that epoch is already up, the post-promotion announcement is admitted and then dropped one
layer down, and promotion silently never takes effect. **The rule: compare the announced endpoint with
the currently-connected one. Different → disconnect and redial. Identical and already verified → ignore,
idempotently.** Only act on `verified: true`, and rate-limit redials so a flapping phone cannot thrash
the link.

**Graceful degradation.** A v≤17 peer never sends or receives these; the link falls back to today's
behaviour (USB, wireless ADB on a shared LAN, or Direct TLS) and nothing regresses.

## v19 — Reverse-mirror recovery on a lossy link (2026-09-20)

A wireless link loses packets and stalls; H.264 does not forgive either. One frame that never reaches the phone corrupts every frame that references it, and the phone has no way to repair the stream by itself — it can only wait for the encoder's next IDR. One message closes that loop.

| Type | Direction | Payload | Purpose |
|---|---|---|---|
| `pc.mirror.keyframe` | phone → desktop | `{}` | The phone's decoder has lost sync (a packet it could not take in time, or a decoder restart). The desktop asks its encoder for an IDR on the next submitted frame. Rate-limited phone-side to at most one request every 500 ms — a bad second loses many frames, and one request each would answer a congested link with a burst of the largest frames the encoder makes. |

**Neither end delivers a backlog.** A mirror is a live view. The desktop sheds load by skipping frames *before* encoding and adapts the encoder's bit rate to the link (D-066), so in normal operation nothing is ever dropped after encoding; only a link that stops dead for over a second has its backlog discarded, followed by a keyframe. The phone discards everything after a gap until a keyframe arrives. The desktop rations IDRs, including these requests, to one per second. None of this changes the wire format — the packet header is exactly v14's.

**Gating:** `pc.mirror.keyframe` needs negotiated ≥ 19. Against an older desktop the phone never sends it and recovery falls back to the encoder's two-second GOP, which is the previous behaviour.

## v20 — PC Remote, Windows-App style (2026-10-09; D-067)

Remote Desktop feels instant on links where its video is visibly behind, because the pointer never travels with the video. v20 copies that, plus the rest of Windows App's session features. Control-channel messages; one new typed channel.

| Type | Direction | Payload | Notes |
|---|---|---|---|
| `pc.mirror.cursor` | desktop → phone | `{x, y, visible, id, shape?}` | The pointer in pixels of the mirrored display (may lie outside it). Sent on change, up to ~60 Hz, only while streaming. `id` names the cursor image; `shape` = `{w, h, hx, hy, bgra}` (base64 of w·h little-endian B,G,R,A pixels, straight alpha; `hx`/`hy` the hotspot) is sent the first time each `id` is seen in a session and cached by the phone. Monochrome "invert" pixels (the I-beam) arrive as white with a black outline. |
| `pc.mirror.stats` | desktop → phone | `{targetKbps, sentKbps, fps, delayMs, skipPct, dropped}` | Once a second while streaming: the D-066 controller's bit-rate target, what actually went out, frames sent per second, send-delay EWMA, the share of frames skipped before encode, cumulative post-encode drops. |
| `pc.mirror.ping` / `pc.mirror.pong` | phone → desktop / back | `{t}` | The desktop echoes the payload unchanged; `t` is the phone's own clock, so the phone alone computes the round trip. |
| `pc.input` (additions) | phone → desktop | `button`: `"left"` \| `"right"` \| `"middle"` on `down`/`up`; `precise: true` on `scroll` | Absent `button` is a left click (v14 behaviour). With `precise`, `dx`/`dy` are raw wheel units (120 = one notch) instead of notch counts. |
| `pc.audio.start` / `pc.audio.stop` | phone → desktop | `{}` | Start/stop the PC's sound. A start while one is running is ignored. |

**pc-audio channel (6).** Desktop-served, opened exactly like pc-video (`channel.open` → the phone dials back). A length-prefixed JSON config frame `{codec: "aac", sampleRate, channels}` (`sampleRate` is the PC's mix rate, 44100 or 48000), then packets framed exactly as pc-video (12-byte header; flags always 0), each one raw AAC-LC access unit (1024 samples, no ADTS). The phone builds the 2-byte AudioSpecificConfig from the config frame. The desktop keeps ≤ ~200 ms queued and drops the oldest beyond that; the phone writes to its AudioTrack non-blocking and drops overflow — sound is live or absent, never late. Loopback produces nothing while the PC is silent.

**Gating:** everything above needs negotiated ≥ 20. The phone never sends `button` other than left, `precise`, ping or audio to an older desktop.

## v21 — Windows Voice Access with the phone as microphone (2026-10-10; D-070)

| Type | Direction | Payload | Notes |
|---|---|---|---|
| `pc.voiceaccess.set` | phone → desktop | `{on, phoneMic, installCable?}` | Start/stop Windows Voice Access (`VoiceAccess.exe`). With `phoneMic` and a virtual audio cable present, the desktop opens pc-mic (7), plays the phone's audio into the cable's playback side and makes the cable's recording side the default microphone (all roles) for the session; the user's previous default is restored when it ends. The phone only sets `phoneMic` when it holds `RECORD_AUDIO`. `installCable: true` (D-071) means the user agreed on the phone to have the desktop set up VB-CABLE first when no cable is present; the desktop sends `state` with `setup: "installing"` while Windows asks for permission, then carries on. |
| `pc.voiceaccess.get` | phone → desktop | `{}` | Ask for the state. |
| `pc.voiceaccess.state` | desktop → phone | `{available, on, mic, cable, setup?, error?}` | Sent after every `set` and in answer to `get`. `available`: the PC has Voice Access (Windows 11 22H2+). `mic`: `"phone"` or `"pc"`. `cable`: a virtual cable ("CABLE Input"/"CABLE Output", or "Virtual … Input/Output") is installed. `setup` (D-071): `ready`, `needed` (the desktop can install it if asked), `installing`, or `failed`; absent from a desktop that can't install it, and the phone then offers nothing. `progress` (0–100, while `installing`): how much of the driver download has arrived; 100 means VB-Audio's installer is open. |

**pc-mic channel (7).** Desktop-requested, phone-served (the opposite of 5 and 6): after `channel.open` the phone dials back and writes a length-prefixed JSON config `{codec: "pcm16", sampleRate: 16000, channels: 1}`, then 20 ms packets of little-endian 16-bit PCM framed with the pc-video 12-byte header. The desktop buffers at most ~150 ms and drops older audio. Closing the channel ends the phone's recording.

## v24 — Clipboard pages (2026-10-11; D-083)

| Type | Direction | Payload | Notes |
|---|---|---|---|
| `clipboard.get` | either | `{}` | "Send me your clipboard" — the Clipboard pages' **Sync now**. Answered (or not) with the peer's normal clipboard push: `clipboard.changed` from the phone, `clipboard.set` from the desktop. The phone can only answer while Linc is open (Android 10+ clipboard rule); a clip marked sensitive, or a Windows clip excluded from clipboard history/monitoring, is never sent. No reply when it can't. |

`hello` (desktop → phone) gains an optional `host` (the PC's name), shown on the phone's Home and Connection sheet. Unknown fields are ignored, so it needs no version gate.

## v25 — Phone as PC speaker (2026-10-17; D-093)

| Type | Direction | Payload | Notes |
|---|---|---|---|
| `pc.speaker.set` | phone → desktop | `{on}` | `on: true` makes the virtual cable's playback side ("CABLE Input") the PC's default output for every role, then (re)starts pc-audio (channel 6), whose loopback follows the default output, so the PC's sound plays on the phone **instead of** its own speakers. The previous default output is written to `speaker-restore-output.txt` under the store root and restored on `on: false`, when channel 6 ends (the phone went away), on desktop exit, and at the next start after a crash. |
| `pc.speaker.state` | desktop → phone | `{on, cable, error?}` | Sent after every `set`. `cable`: a virtual cable is installed. `error`: `no-cable` (nothing to route into: set it up through Voice Access, D-071), `denied` (Windows refused the switch) or `audio` (pc-audio couldn't start; the switch was undone). |

`pc.state` (v17) also gains two optional, read-only fields for the phone's Home "Your PC" card: `battery: {percent, charging}` (absent on a PC without a battery) and `network: "wifi" | "ethernet"` (absent when unknown). Older phones ignore them.

## v26 — Play everywhere (2026-10-18; D-096)

The PC's sound on its own speakers, its Bluetooth devices and the phone's outputs at once, heard in
step, with a volume per device. Built on speaker mode's routing (v25): the cable is the default
output, Linc captures it once and renders it to every chosen PC device itself (so each can be
delayed) and to the phone over pc-audio.

**Timing model.** Every block carries the PC-clock (QPC, µs) time it was captured, `Tc`. Every
speaker aims to be heard at `Tc + D`; the desktop sets the group delay `D` from what each speaker
needs (rises at once, falls only after 20 s of needing 30 ms less). A speaker's "heard" time is its
own position clock plus a per-device hidden latency (a Bluetooth buffer, a TV's processing) that
the phone's microphone measures. Each speaker corrects its error by a jump when unlocked or more
than 40 ms out, otherwise by playing up to ±0.5% fast or slow (PI loop). The playout is pinned by
`tools/audiosyncsim` (desktop) and `AudioSyncTest` (phone), which must agree.

**pc-audio channel 6 with `clock`.** When the config frame has `"clock": true` (only Play
everywhere's stream; a v25 phone never sees it): packet timestamps are PC-clock capture times, and
the channel's back direction carries clock probes. Phone → desktop: 16 bytes, big-endian:
`int32 0x4C434C4B ("LCLK")`, `int32 0`, `int64 phone µs (t0)`. Desktop → phone: a packet with the
**config flag** set and a 24-byte payload: `int64 t0`, `int64 arrived (t1)`, `int64 leaving (t2)`
(PC µs). The phone keeps the quickest round trips and fits offset + drift over the last minute.

| Type | Direction | Payload | Notes |
|---|---|---|---|
| `audio.everywhere.set` | phone → desktop | `{on?, device?: {id, enabled?, volume? 0–100, muted?, manualMs?}, marginMs?, calibrate?, clicks?, resetAlignment?}` | Any of these; an empty `{}` just asks for the state. `on: true` turns speaker mode's routing on, restarts pc-audio with `clock`, and opens a render stream on each enabled PC output. `manualMs` (±500) is the user's nudge: positive plays earlier. `marginMs` (0–500) is added to the group delay. Answered with `audio.everywhere.state`. |
| `audio.everywhere.state` | desktop → phone | `{on, delayMs, marginMs, testing, note?, error?, devices: [{id, name, kind: "pc"\|"phone", bluetooth, enabled, volume, muted, autoMs, manualMs, hiddenUs, calibrated, status, offByMs, needMs, problem?}]}` | After every `set`, on a change of `D`, and every 2 s while on. Phone entries come from the phone's report; the desktop stores every device's settings in `audio-everywhere.json` under the store root. `testing`: `"calibrate"`, `"clicks"` or null. `error` as `pc.speaker.state`'s. |
| `audio.everywhere.report` | phone → desktop | `{outputs: [{id, name, bluetooth, needUs, status, offByUs}], clockReady, clock?: {uncertaintyUs, driftPpm}, calibration?: {seq, results: [{id, ok, errorUs, snr}], error?}}` | Every 2 s while the clocked stream plays. Phone output ids are `phone:speaker`, `phone:bt:<name>`, `phone:wired`, `phone:usb:<name>`; a new one is enabled by default. `needUs` includes the output's hidden latency. |
| `audio.everywhere.test` | desktop → phone | `{seq, mode: "calibrate"\|"clicks", sounds: [{id, atUs, kind: "chirp"\|"click"}], listenFromUs, listenToUs}` | Program sound is muted on every speaker until the last sound. Each speaker plays its own sounds so they are heard at `atUs` (PC clock). `calibrate`: each device, one at a time, 3 rounds, 800 ms apart, a 60 ms 1.5→6 kHz chirp; the phone records `listenFromUs`–`listenToUs`, finds each chirp by cross-correlation within −200/+600 ms of when it was due, and reports per device the median error (ok only when two rounds agree within 4 ms). The desktop adds `errorUs` to that device's measured hidden latency (refusing beyond ±600 ms). `clicks`: every device at once, twice a second, for the user to judge by ear. |

## v27 — Phone as Play everywhere's source, lip-sync, PC tools (2026-10-18; D-097)

**Lip-sync.** `pc.mirror.stats` gains `epochUs`: the PC-clock time (µs) of video timestamp 0.
With Play everywhere's clocked sound running, the phone measures each shown frame's delay
(`toPc(now) − (epochUs + pts)`) and, while PC Remote shows the picture, plays its own outputs at
that delay instead of the group delay.

**The phone as the source.** `audio.everywhere.set {source: "phone", on}` — the phone records
what its apps play (Android playback capture) and asks the PC to listen: the PC dials **channel
8** (pc-phone-audio). Phone → desktop: packets framed like pc-video (12-byte header). Sound
packets carry 16-bit little-endian stereo 48 kHz PCM, and their timestamp is the **PC-clock
moment the block must be heard** (the phone's own output's moment), so the PC plays each block at
exactly that time with no group delay of its own. Config-flagged packets are clock probes
(16 bytes: `"LCLK"`, 0, phone µs); the PC answers each on the back direction with a
config-flagged 24-byte packet (`t0`, arrived, leaving), as on channel 6. `{source: "pc", on: false}`
ends it. `audio.everywhere.state` gains `source: "pc" | "phone"`.

**PC tools.** All phone → desktop requests, answered with their own message types:

| Request | Answer | Notes |
|---|---|---|
| `pc.look.get {}` / `pc.look.set {appsDark?, systemDark?, accent? "#RRGGBB", autoAccent?, transparency?}` | `pc.look.state {appsDark, systemDark, transparency, accent, autoAccent, wallpaper?, error?}` | HKCU Personalize/DWM/Explorer\Accent, then `WM_SETTINGCHANGE "ImmersiveColorSet"`. |
| `pc.wallpaper.set {jpeg (base64, ≤ ~700 KB), fit: fill\|fit\|stretch\|center\|tile\|span}` | `pc.look.state` | Saved under the store root, a new name each time. |
| `pc.info.get {}` | `pc.info {name, user, os, uptimeMin, cpuPct, memTotalMb, memUsedMb, disks[], battery?}` | |
| `pc.apps.get {}` / `pc.apps.end {pid}` | `pc.apps {apps: [{pid, name, title, memMb}]}` | End: close the window, then end it after 3 s. |
| `pc.open {url}` or `{path}` | — | Shell-opened, as a double-click. |
| `pc.files.list {path}` (`""` = the starting places) | `pc.files.listing {path, parent, entries: [{name, path, dir, size, modified, drive?, place?}], truncated?, error?}` | Hidden/system entries skipped; 2000 max. |
| `pc.files.op {op, id, …}`: `mkdir {path, name}`, `rename {path, name}`, `delete {path}` (Recycle Bin), `open {path}`, `download {path}`, `upload {dir, name, size}` | `pc.files.done {op, id, ok, path?, error?}` | Transfers: the PC dials **channel 10**, writes a JSON frame `{id, op}` (+ `name`, `size` for a download), then the bytes flow PC → phone (download) or phone → PC (upload, `size` bytes or until close). |
| `pc.term.get {}` / `pc.term.open {shell: powershell\|cmd\|wsl, cols, rows}` | `pc.term.state {shells, running?, error?}` | The PC dials **channel 9** and runs the shell in a ConPTY. Both ways: `[type u8][length u32 BE][payload]`; 1 = UTF-8 text (VT output / keystrokes), 2 = resize (cols, rows as u16 BE), 3 = exit (i32 code, PC → phone). |

## Protocol draft — later milestones (not implemented)

Everything below is **draft until its milestone ships**. These message types are ignored by current peers per the envelope's unknown-type rule.

### v15 DRAFT — Extend mode — **DROPPED 2026-07-22** (was Era 2, M06; D-030)

Abandoned — Extend mode was cut per user (see ROADMAP M06, D-030). The `pc.display.attach` / `pc.display.detach` messages and the `displayId: "virtual"` extension to `pc.mirror.start` will **not** be built, and no v15 will be minted for them. Left here struck-through for provenance.

Later milestones extend these tables — each extension bumps the protocol version.

## Timeouts & lifecycle

- Handshake must complete within 5 s of TCP connect, else either side may close.
- Desktop pings every 15 s of idle; two missed pongs → treat the link as dead and let the ConnectionManager reconnect.
- Either side closing the socket is normal shutdown; no goodbye message in v0.

## v28 — sound output, PC screen curtain, the Device page reaching into the phone

Additive; gated on a negotiated version of 28 on both sides.

| Type | Direction | Payload |
|---|---|---|
| `pc.audio.outputs.get` | phone → desktop | `{}` |
| `pc.audio.outputs` | desktop → phone | `{outputs: [{id, name, bluetooth, default}]}` (also the answer to a set) |
| `pc.audio.output.set` | phone → desktop | `{id}`: make it Windows' default output |
| `pc.curtain.set` | phone → desktop | `{on}`: black, click-through, topmost window per monitor, `WDA_EXCLUDEFROMCAPTURE` so the capture still sees the desktop; lifts when the phone goes away |
| `pc.curtain.state` | desktop → phone | `{on, available, error?}` |
| `phone.sound.ask` | desktop → phone | `{on}`: on opens the playback-capture consent (directly with overlay permission, else a notification); off stops the phone source |
| `phone.usage.get` | desktop → phone | `{}` |
| `phone.usage` | phone → desktop | `{allowed, totalMin, unlocks, apps: [{package, name, minutes}]}`; `allowed:false` when usage access is off (the PC can grant it over adb: `appops set app.linc.android GET_USAGE_STATS allow`) |
| `phone.wallpaper.set` | desktop → phone | `{jpeg (base64, ≤ ~640 KB), target: home\|lock\|both}` |

`pc.info` gains optional Task Manager fields: `cpuName, cores, threads, cpuMhz, processes, handles, diskPct,
diskReadKBps, diskWriteKBps, netSendKbps, netRecvKbps, netName, gpuPct, gpuName, memCommittedMb, memCommitLimitMb`.
`pc.term.state.shells` may now also list `winps`, `wsl:<distro>` and `gitbash`. The phone's Play everywhere
output reports may carry status `clock` (waiting for the PC's clock). Phone colours and dark theme are set by
the desktop over adb (`settings put secure theme_customization_overlay_packages …`, `cmd uimode night`), not
by protocol.

## v29 — the phone's sound on the PC over Bluetooth

Additive; gated on a negotiated version of 29 on both sides. Windows becomes the phone's Bluetooth
speaker (A2DP sink, `Windows.Media.Audio.AudioPlaybackConnection`, Windows 10 2004+), so the phone
plays to the PC like to any Bluetooth speaker: nothing is recorded on the phone and Android shows no
capture prompt. The phone must be paired with the PC over Bluetooth once.

| Type | Direction | Payload |
|---|---|---|
| `pc.btaudio.set` | phone → desktop | `{on, names: [the phone's Bluetooth/device names]}`: the desktop picks the paired phone matching a name (else the only one, else the first) and opens or closes the connection |
| `pc.btaudio.get` | phone → desktop | `{}` |
| `pc.btaudio.state` | desktop → phone | `{on, supported, device?, error?}`; `error` is `not-paired`, `unsupported`, or Windows' own open status. Also sent unprompted when the connection closes (Bluetooth off, out of range) |

The PC's Device page opens the same connection itself, without the phone. `phone.sound.ask` (the
recording route) stays for v28 phones.

## v30 — the phone's Sync page and folder pairs

Additive; gated on a negotiated version of 30 on both sides. The Folders lane now keeps several
folders the same, each a **pair**: a PC folder and a phone folder, with a direction. The PC runs
the engine and keeps the setup (per phone, `KnownDevice.FolderPairs`); the phone's Sync page reads
and edits it. Older settings with one folder (`FolderSyncPcPath`/`FolderSyncPhonePath`) read as a
single pair with id `folders`, so its sync history carries over.

A pair is `{id, name, pcPath, phonePath, direction, enabled}`. `direction` is `both` (new and
changed files go both ways, the newer copy wins), `toPhone` (the PC's folder is copied to the
phone) or `toPc` (the phone's folder is copied to the PC). `id` never changes once made; the
engine files its per-file bookkeeping under it.

| Type | Direction | Payload |
|---|---|---|
| `sync.state.get` | phone → desktop | `{}` |
| `sync.state` | desktop → phone | `{lanes: {folders, photos, messages, calls}, pairs: [pair], status, photosPcPath}`. Also sent unprompted whenever the setup changes (from either side) or a sync pass finishes |
| `sync.pairs.set` | phone → desktop | `{pairs: [pair]}`, the whole list; a pair with no `pcPath` or `phonePath` is dropped, one with no `id` gets a new one |
| `sync.lane.set` | phone → desktop | `{lane, on}` |
| `sync.now` | phone → desktop | `{}`: run a sync pass now |

`sync.config` (v11) still tells the phone which lanes are on; the PC sends it after a lane changes.

## v31 — the phone's sound in Play everywhere, the way Mirror screen takes it (2026-10-20)

Additive; the phone sends these only when the negotiated version is 31 on both sides, so older
PCs never see them and older phones never receive `source: "mirror"`. No new message type and no
new channel.

| Type | Direction | Payload | Notes |
|---|---|---|---|
| `audio.everywhere.set` | phone → desktop | `{source: "mirror", on: true}` | "Whose sound: This phone's". The PC turns Play everywhere on as for its own sound (v26: cable as the default output, capture, every PC speaker, channel 6 with `clock` back to the phone) if it isn't already, then runs the bundled scrcpy with `--no-video --no-window --no-control --audio-source=playback --require-audio --audio-buffer=40` (**no** `--audio-dup`): the phone's media apps go quiet on the phone, scrcpy plays them into Windows' default output (the cable), and the existing pipeline plays them everywhere, the phone included, in step. Needs an adb route (USB or wireless debugging) and Android 13+. `{source: "pc", on: true}` stops scrcpy and keeps playing the PC's own sound; `{on: false}` ends it all. |
| `audio.everywhere.set` | phone → desktop | `{resync: true}` | "Re-sync": the PC re-aligns every PC speaker and drops the group delay to what the speakers need now (instead of waiting out its 20 s fall). The phone re-learns the PC's clock and re-aligns its own outputs on its own side. |
| `audio.everywhere.state` | desktop → phone | `source` may now be `"mirror"`; `error` may be `needs-adb` (no USB / wireless-debugging link: nothing was changed), `needs-android-13` (scrcpy said so and exited) or `mirror-stopped` (scrcpy ended on its own; Play everywhere was turned off) | |

While `source` is `"mirror"` the phone plays its copies with `USAGE_GAME` (scrcpy's playback capture
matches `USAGE_MEDIA` only, and its non-duplicating mix ignores the app's capture opt-out), and does not
take audio focus or show "Playing from your PC". Per-speaker offsets stay `device.manualMs` (the phone's
Advanced page now offers −400…+400 ms in 10 ms steps; the PC still accepts ±500) and Smooth / Tight sets
`marginMs` to 150 / 0; both were already in v26 and are saved per device in `audio-everywhere.json`.

### Optional status field (no version bump): `dndAccess`

`status` (the health probe's reply) may carry `dndAccess: bool`: whether the phone has given Linc
Do Not Disturb access (`NotificationManager.isNotificationPolicyAccessGranted`), which `dnd.set` and
`sound.set` need. The PC greys Ring / Vibrate / Silent and says where to allow it when it is
`false`; absent (older phones) counts as allowed, as before.

## v32 — LincLink: one session over many routes (2026-10-22, test builds A; Vibe/agent/design/linclink.md)

Offered only while the **new connection engine** is on (PC: Settings › Connection; phone: Settings ›
Connection; both on by default on the LincLink test builds). With it off, or with a peer at v31 or
below, the hello's `maxV` is 31 and **nothing in this section happens**: an old peer connects exactly
as before.

### Session, numbering, resume

- The desktop's `hello` carries `link: {session?, recv, keep?}`: the session it wants to resume (or
  none), the highest seq it received there, and the oldest of its own frames it still keeps.
- The phone's `hello` reply carries `link: {session, recv, resumed, id, gen}`: the session (new, or
  the one resumed), the highest seq it received, whether it resumed, the phone's LincLink id
  (SHA-256 of its certificate, first 16 hex digits — the same formula as the PC's `pcId`) and the
  link-secret generation it holds for this PC (0: none).
- The `pcId` that sessions and secrets are filed under is bound to the transport: on TLS it must be the
  id of the certificate the PC presented (a hello claiming another is refused); on the ADB socket the
  phone accepts `link.secret` only from the shell or root user (`adb forward`), not from another app.
- The desktop takes a resumed reply only when its `id` is the id of the phone the session belongs to,
  and offers a session only to that phone (by its pinned certificate); anything else is refused.
- The phone resumes only a session it still holds **for the same `pcId`**, at the same negotiated
  version, when `keep ≤ its recv + 1` and it can replay everything after the desktop's `recv`.
  Otherwise it starts a fresh session (pages see a reconnect, as before). It keeps a session with no
  transport for **60 s**; "Connected" stays on the phone's screen for 12 s of that.
- After the hello every control frame carries `seq` (1, 2, 3… per direction) and `ack` (the
  highest seq received). Both are envelope fields, omitted when null, never sent below v32. Each side
  keeps unacknowledged frames (at most 256 / 1 MiB) and, on a resume, sends again every frame after
  the other side's `recv`, right after the hello reply. A frame whose `seq` was already received is
  dropped unhandled: nothing happens twice (an `sms.send` replayed after a cable pull is sent once).
  The receiver accepts a resume offer as long as it covers what the other side already acknowledged
  (an offer written before a last ack on the old route is fine).
- A request the desktop gave up on (timed out or cancelled) while its frame is still unacknowledged is
  replaced in the replay buffer by `link.noop` under the same `seq`, so a resume never runs something
  the user was told failed. One already acknowledged ran, as a lost reply always could.
- `link.ack {ack, echo?}` (either way, **never numbered**): a bare acknowledgement, sent after 2 s or 32
  frames that no outgoing frame acknowledged. `echo: true` asks for one straight back: the desktop
  sends it after 4 s of hearing nothing and calls the transport dead if no frame arrives within 3 s
  (a half-open Wi-Fi link is found in ~7 s). The phone calls a transport dead after 12 s of silence.
- The `sessionToken` survives a resume, so typed channels opened later keep working. Channels already
  open (a file copy, a mirror stream) do not move: their owners reopen them, as before.

### Messages

| Type | Direction | Payload | Notes |
|---|---|---|---|
| `link.secret` | desktop → phone | `{secret: base64 (32 bytes), gen}` → `ok` | The pair's link secret, made by the PC per phone and sent once over an authenticated session (ADB or pinned TLS), again only when the phone's hello reports another `gen`. Keys the UDP beacon. The phone keeps it encrypted with an AES key in the Android KeyStore; the PC keeps it in `%LOCALAPPDATA%\Linc\link-secrets.json`, beside its TLS key. |
| `link.where` | phone → desktop | `{addrs: [IPv4…], port: 46002}` | Where the PC can dial this phone (its Wi-Fi/Ethernet/tethering addresses), sent on every session start and resume. |
| `link.pair` | phone → desktop, first frame | `{proof: base64, model}` → `{ok, name, pcId}` | Pairing by QR code, below. Accepted only inside the PC's pairing window. |
| `link.noop` | either way, numbered | `{}` | Does nothing; holds the `seq` of a request given up on (above). |
| `link.state` | desktop → phone, numbered | `{route: "usb"\|"wifi"\|"direct", adb: bool, adbVia?: "cable"\|"wireless"}` | How the two are connected, as the PC sees it: sent on connect and on every change. The phone shows exactly this ("Connected over USB" for `usb`, else "over Wi-Fi", plus the ADB chip); anything it doesn't understand is shown as nothing. `usb` includes a direct link riding adb reverse. |

### The phone's listener (TCP 46002)

The phone listens with mutual TLS (its KeyStore identity). A client gets past the handshake only with
the exact certificate of a PC this phone has paired with (or, during QR pairing, the PC whose
certificate hash is in the code). The desktop then speaks exactly as on a phone-dialled link: its
`hello` for control, or a `{channel, sessionToken}` header for **any** typed channel (on a link the PC
dialled, the PC dials its channels too, instead of `channel.open`, which would need the phone to dial
back through the PC's firewall).

### The UDP beacon (46003)

23 bytes: `"LK" ‖ 1 ‖ role ‖ tag (8) ‖ nonce (8) ‖ port (2, big-endian) ‖ flags`, with
`tag = HMAC-SHA256(linkSecret, "linc-beacon" ‖ role ‖ slot (8, big-endian) ‖ nonce)[..8]`,
`slot = unixSeconds / 30`, matched at ±1 slot. Roles: `P` probe (PC → broadcast, `port` = 46001),
`R` reply (phone → the probe's source, unicast, same nonce, `port` = 46002; Windows Firewall lets it
in for 3 s after the broadcast), `A` announce (phone → broadcast, on boot, Wi-Fi joined, screen on, a
transport lost). A PC accepts a reply only to a nonce it sent in the last 10 s. The PC sends one probe
per phone it holds a secret for, to 255.255.255.255 and every interface's subnet broadcast. Test vector
(pinned in tools/linclinksim and the phone's `LinkCoreTest`): secret `01..20`, role `R`, slot 59666666,
nonce `a0..a7` → tag `a59b781eddb9a453`.

### Pairing by QR code (no ADB)

The PC (Add a phone › Wi-Fi) shows `linc://pair?v=1&k=<SHA-256 of its certificate, hex>&a=<addresses>&p=46001&t=<16 bytes, base64url>&n=<name>`,
valid 5 minutes, one use. `a` lists only private IPv4 addresses (10/8, 172.16/12, 192.168/16,
169.254/16); the phone dials nothing else, so a link from a web page can't send it anywhere. The
phone's camera opens the link in Linc, which asks first and shows a **check code**,
`SHA-256("linc-check" ‖ k bytes ‖ token)`, first 4 bytes big-endian mod 1 000 000, as "123 456": the PC
shows the same under its QR code, and the owner pairs only if they match. If the new engine is off on
the phone, the same prompt says pairing turns it on. Then, raced: the
phone dials the PC at each address (TLS; the PC's certificate must hash to `k`), **and** answers the
PC's pairing probes (role `P` keyed by the token instead of a secret) so the PC can dial the phone's
listener. On whichever connection lands, the phone sends `link.pair` with
`proof = HMAC-SHA256(token, "linc-pair" ‖ SHA-256(phone cert) ‖ SHA-256(PC cert))`; the PC checks it in
constant time and pins the phone (a record keyed `linc:<phone id>` until an ADB connect supplies the
hardware serial, then merged), replies `ok`, and both sides connect as paired peers. Test vector: token
`00..0f`, phone cert `"phone"`, PC cert `"pc"` → `cbce564e…f95d8978`. Check code vector: `k` = `ab`×32,
token `00..0f` → `764 252`.
