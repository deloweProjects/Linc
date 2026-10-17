# Decisions

Important technical and architectural decisions, with reasoning. Add new entries at the bottom with an ID, date, status (Proposed / Accepted / Superseded), and rationale. Do not delete superseded entries — mark them and link the replacement.

---

## D-001: ADB is the transport, but never the interface
**Date:** 2026-07-07 · **Status:** Accepted

Linc is built on ADB (wireless debugging) because it provides, for free: authenticated encrypted transport (ADB-over-TLS), file sync, shell access, port forwarding, and compatibility with scrcpy. The trade-off — pairing friction and ADB's rough edges — is absorbed by Linc itself: both apps wrap every ADB interaction in guided UI. **Users should rarely need to interact with ADB directly**; a raw adb command surfacing to the user is a bug.

## D-002: Minimum Android version is 11 (API 30)
**Date:** 2026-07-07 · **Status:** Accepted

Wireless debugging with in-band pairing (QR / pairing code) only exists on Android 11+. Supporting older versions would require USB-first pairing plus `adb tcpip` fallbacks that reset on reboot — directly undermining the automatic-reconnection goal. Cut scope: Android 11+ only.

## D-003: Windows app in C#/.NET 8 with WinUI 3
**Date:** 2026-07-07 · **Status:** Accepted (provisional until M2 validates it)

Considered: Electron (heavy, non-native feel), Compose Multiplatform (shared Kotlin, but weaker Windows integration for tray/toasts/Explorer drag-drop and immature desktop video story), Tauri (young desktop APIs), WPF (older stack). .NET 8 + WinUI 3 gives native Windows integration — tray, toast notifications, drag-and-drop with Explorer — which the vision depends on. `AdvancedSharpAdbClient` removes the need to shell out for most ADB operations. Revisit only if WinUI tooling friction proves costly by end of M2.

## D-004: Embed scrcpy rather than building a mirroring pipeline
**Date:** 2026-07-07 · **Status:** Accepted

scrcpy is best-in-class, actively maintained, and permissively licensed. Building our own capture/encode/decode pipeline would dominate the project's effort for a worse result. Linc manages scrcpy as a child process behind `MirrorService`; if deeper embedding (rendering into our own window) is needed later, that's an isolated change behind the same service interface.

## D-005: Companion protocol = versioned JSON over an ADB-forwarded localhost socket
**Date:** 2026-07-07 · **Status:** Accepted

Features ADB can't provide (notifications, clipboard events, rich device status) need an app-to-app channel. Reusing the ADB tunnel (`adb forward` to a localhost socket on the phone) means no extra network exposure, no extra auth story, and it works wherever the ADB link works. JSON with an explicit version field is chosen over binary formats for debuggability at this scale; each side must tolerate one version of skew. Revisit if profiling shows serialization is a real bottleneck (unlikely — bulk data moves over ADB sync, not the protocol).

## D-006: No shared code between the two apps — shared spec only
**Date:** 2026-07-07 · **Status:** Accepted

Kotlin (Android) and C# (Windows) have no practical shared-code path without adopting KMP everywhere (see D-003). The only shared artifact is the companion protocol specification in docs. This keeps each app idiomatic to its platform and keeps the contract explicit.

## D-007: Google account integration is deferred and scope-gated
**Date:** 2026-07-07 · **Status:** Proposed

Milestone M9 exists in the roadmap, but its scope is deliberately undefined until core connectivity features are stable. It must justify itself against the mission (does account identity make linking/reconnection meaningfully better?) before any implementation. Record the scoping outcome as a new decision when M9 begins.

## D-008: Material 3 Expressive is the design language on both platforms
**Date:** 2026-07-08 · **Status:** Accepted

Chosen at the user's direction for a consistent, branded look across phone and PC. On Windows, WinUI's Fluent defaults are overridden by a hand-built token dictionary (`DESKTOP/Linc.Desktop/Themes/MaterialExpressive.xaml`): M3 color roles (light + dark), full-radius "pill" buttons, extra-large card shapes, bold display typography. Interaction states (hover/press) still come from the default WinUI templates — full Material state layers would require control retemplating; revisit at M10 (Polish). On Android, the app now uses Material You dynamic color, which the desktop mirrors live (see D-011); the hand-authored palette is just the fallback. Full `MaterialExpressiveTheme` (Compose's own expressive component set, not just colors) remains future work once that Compose Material 3 API is stable.

## D-009: Settings page includes real ADB shell access, reversing D-001's default posture
**Date:** 2026-07-11 · **Status:** Accepted

D-001 says a raw adb command surfacing to the user is a bug — the general rule for this app. The Settings page's "Run an ADB shell command" box is a deliberate, explicit exception, added at the user's direct request after being asked to confirm the tradeoff (they chose full power over a read-only-diagnostics alternative). It's scoped narrowly: one clearly-labelled, cautioned box on one page, reusing the exact same `AdbClient.ExecuteRemoteCommandAsync` primitive already used internally for `getprop`/`mv`/`rm`/`mkdir` — no new ADB capability, just a raw-input UI for it. Every other feature in the app still treats ADB as invisible plumbing; this does not reopen D-001 generally.

## D-010: Shared static brand palette instead of live cross-device theme sync
**Date:** 2026-07-11 · **Status:** Superseded by D-011

Original decision: a static hand-transcribed shared palette, no protocol theme message. This misread the requirement — the user wanted Material You dynamic color on the phone with the desktop following it live. Superseded the same day; the hardcoded palette survives only as the offline/Android-11 fallback (see D-011).

## D-011: Material You dynamic color on the phone, synced live to the desktop
**Date:** 2026-07-11 · **Status:** Accepted

The phone uses Material You dynamic color (`dynamicLightColorScheme`/`dynamicDarkColorScheme`, wallpaper-derived, Android 12+). It sends its resolved light and dark palettes to the desktop in every `status` snapshot (protocol v4, `themeLight`/`themeDark`). The desktop's `ThemeSyncService` retints the `SolidColorBrush` resources from `MaterialExpressive.xaml` in place to match, picking light or dark to follow its own Windows theme. So both apps always show the same colors, driven by the phone's wallpaper at runtime — not a hardcoded palette (that remains only as the fallback when dynamic color is unavailable or no phone is connected). Chosen over a static shared palette (D-010) because the user explicitly wanted the phone's dynamic theme to propagate to the desktop.

## D-012: The Linc Pipeline — typed channels as separate connections, not in-band multiplexing
**Date:** 2026-07-11 · **Status:** Accepted (design; implementation M12+)

All app-to-app communication is unified into one session layer (PIPELINE.md): a single listener accepting multiple concurrent connections, each opened with a small header naming its channel (0 control JSON, 1 mirror, 2 files, 3 bulk, 4 audio). Chosen over an in-band multiplexing layer (channel-tagged frames on one connection) because separate connections get flow control from the OS for free — a stalled bulk transfer can't starve notifications — and scrcpy proves multiple connections through one `adb forward` works. Also chosen over one-socket-per-service forwards to keep the forward/port bookkeeping flat. Scope note: v1 folder-sync semantics are deliberately minimal (new files only, newest-wins, no delete propagation) — full bidirectional sync is a rabbit hole.

## D-013: Dual transport — keep every ADB path, add Direct TLS
**Date:** 2026-07-11 · **Status:** Accepted

ADB is not being replaced (D-001 stands): USB-ADB, wireless-debugging ADB, and `adb reverse` all remain live transports. Added alongside them: a Direct TLS app-to-app transport over LAN — mutual TLS with self-signed certificates exchanged during the one-time pairing flow, desktop advertising `_linc._tcp` on mDNS, phone-initiated connection. Rationale: wireless debugging is the fragile link (some OEMs drop it on reboot); Direct TLS keeps everything except mirroring working without it. The transport supervisor races all viable paths, prefers by measured latency, and fails over live. Mirroring stays ADB-only — input injection requires shell UID, which no normal app can hold.

## D-014: Standing presence — background connection hunting, on by default
**Date:** 2026-07-11 · **Status:** Accepted

Once paired, both apps continuously hunt for each other (desktop: mDNS listen+advertise, USB watch; phone: presence loop in the existing foreground service), so the link is typically up before the user asks. Event-triggered scans (network change, USB attach, screen-on) over tight polling; multicast lock only during scan windows; widening intervals when the peer is absent. A "Background connection" toggle on both apps (default on) preserves the old behavior for anyone who wants it. Honest limit: LAN-only wake — push-wake through Doze/cellular is FCM territory and belongs to the deferred M9 scope; BLE presence is a Future Idea.

## D-015: Mirror phase 1 — bundle scrcpy-server, replace the desktop client (partially supersedes D-004)
**Date:** 2026-07-11 · **Status:** Accepted

D-004 embedded scrcpy as a child process; that external-exe dependency now goes away. Phase 1: bundle `scrcpy-server.jar` (Apache-2.0) in Linc Desktop, push/launch it ourselves, and render in-app — H.264 via Media Foundation into a `SwapChainPanel`, input per scrcpy's documented protocol, audio as a pipeline channel. Also unlocks per-app windows (`--start-app` + virtual displays). Phase 2 (own `linc-agent.dex` via `app_process`) is deliberately unscheduled — only if phase 1's seams hurt. D-004's core judgment (don't rebuild capture/encode) survives; only the client side is ours now.

## D-016: SMS ships in sideload builds only
**Date:** 2026-07-11 · **Status:** Accepted

Google Play's SMS/Call-Log permission policy would likely reject Linc as an SMS handler. Decision: the Messages lane of the Sync page uses `READ_SMS`/`SEND_SMS` and ships only in sideloaded/GitHub builds; the Play build compiles the feature out. RCS is closed to third parties and out of scope entirely.

## D-017: Call support ships without PC audio
**Date:** 2026-07-11 · **Status:** Accepted

The Calls lane ships the tractable half first: call log, incoming-call toast on the PC, decline/silence, and dial-from-PC (the phone places the call). Routing call audio through PC speakers/mic — Phone Link does this by presenting the PC as a Bluetooth HFP headset — is a large subsystem and is deferred to Future Ideas. Same sideload caveat as D-016 for `READ_CALL_LOG` on Play.

## D-018: Session layer keeps the control connection headerless; only channels carry a header
**Date:** 2026-07-11 · **Status:** Accepted (implements the M12 part of D-012)

The v5 draft (PIPELINE.md) sketched *every* connection — including control — opening with a `{channel, sessionToken}` header. Implementing M12 exposed a backward-compatibility conflict: the desktop can't know the phone speaks v5 until *after* the `hello` handshake, but a uniform header would have to precede `hello` — so a v5 desktop would send a header a v4 phone can't parse, breaking the legacy path (D-005's ±1 version tolerance). Resolution: **the control connection stays exactly today's socket** (first frame is `hello`, unchanged); its `hello` reply merely gains an optional `sessionToken` at negotiated ≥ 5. **Only non-control connections send the channel header**, and those only ever happen v5↔v5. The phone disambiguates the first frame by shape — a top-level `channel` field ⇒ typed channel, otherwise ⇒ `hello`/legacy. This pins the "exact disambiguation rule to be pinned during M12" note in PROTOCOL.md, preserves zero-change legacy interop, and avoids inventing a pre-`hello` capability negotiation. Bulk channel (3) is the first channel implemented (request `{kind,id}` → length-prefixed binary reply, 0-length = not found); mirror/files/audio follow in their milestones.

## D-019: M13 ships as protocol v6; pub/sub gating applies only at ≥ 6; wallpaper moves to M14
**Date:** 2026-07-11 · **Status:** Accepted

The pipeline plan labelled everything "v5", but M12 already shipped v5 to hardware as the session layer alone. If M13's pub/sub semantics also claimed v5, an M13 phone would withhold notification/clipboard events from an M12 desktop that negotiated v5 but has no concept of subscribing — silently breaking a working feature between our own builds. So M13 bumps to **v6**, per PROTOCOL.md's own rule that each extension bumps the version: the phone gates events on subscriptions only when the negotiated version is ≥ 6, and any ≤ 5 peer keeps today's unsolicited behavior. Consequences pinned at the same time: the notification backlog and the current media state are pushed on `subscribe`, not at handshake (they would otherwise be dropped before the desktop subscribes). Also: `wallpaper.changed` was listed under M13 but is deferred to M14 — its only consumer is M14's phone-preview card, and Android 13+ blocks wallpaper reads without `MANAGE_EXTERNAL_STORAGE`, so on the test Pixel 7 the implementable path is the Material You palette-gradient fallback, which is M14 UI work anyway.

## D-020: No wallpaper transfer — the phone preview card uses the synced palette gradient; M14 ships as v7
**Date:** 2026-07-12 · **Status:** Accepted

Following through on D-019's deferral: `wallpaper.changed` is **dropped from the draft entirely**, not just deferred. Android 13+ blocks `WallpaperManager` reads without `MANAGE_EXTERNAL_STORAGE` (a Play-hostile permission), so real wallpaper bytes are unobtainable on any modern phone including the test device. The Home page's phone-preview card instead renders a gradient from the Material You palette that already syncs live in every status snapshot (D-011) — and since that palette is wallpaper-derived, the gradient matches the wallpaper's tones by definition (PIPELINE.md's own fallback note). Revisit only if a legitimate wallpaper-read path appears. M14's remaining protocol needs (`device.locate` toggle-ring, `dnd.set` with a `not-granted` error code, `dndEnabled` on `status`) ship as **v7** per the version-per-extension rule; screenshot pull, clipboard history, and battery alerts need no protocol at all (ADB shell + desktop-side state).

## D-021: Wallpaper thumbnail via the All-files-access grant (partially reverses D-020); sound profiles join DND; v8
**Date:** 2026-07-12 · **Status:** Accepted

The user tested M14 and asked for the real wallpaper as a blurred Home-page background. The legitimate read path D-020 was waiting for exists after all for **sideload builds**: `MANAGE_EXTERNAL_STORAGE` ("All files access"), a special grant the user gives once from a Settings deep-link — same distribution posture as SMS (D-016; the Play build simply never shows the option). Design points: the wallpaper id rides the existing `status` snapshot (no `wallpaper.changed` message — that stays dead); the phone sends only a **~48 px pre-blurred thumbnail** over bulk (privacy + bandwidth: the desktop wants a frosted backdrop, so downsampling *is* the blur and full-res never leaves the phone); the palette gradient remains the fallback whenever the grant is absent. Also in v8 at user request: `sound.set` (ring/vibrate/silent) beside DND — same `NotificationManager` policy grant, same `not-granted` error path — with `soundMode` on status so the UI reflects reality. Mirror-launch latency was reported in the same session: that is scrcpy child-process startup, structurally fixed by M16's in-app client, not patched here.

## D-022: Direct TLS transport — TOFU cert exchange over ADB, phone dials everything, first-wins failover (v9)
**Date:** 2026-07-12 · **Status:** Accepted

M15 implementation pins three things PIPELINE.md left open. **(1) Certificate exchange is trust-on-first-use over the ADB tunnel**, not a new pairing-flow step: the ADB link is already mutually authenticated by wireless-debugging pairing, so the first v9 handshake over it exchanges self-signed certs via `tls.exchange` (desktop: `CertificateRequest` PFX under `%LOCALAPPDATA%\Linc`; phone: the Android-KeyStore self-signed certificate that comes with a generated key). Validation is exact-bytes pinning. **(2) The phone initiates every TLS connection** (desktop advertises `_linc._tcp` + listens; phone presence loop dials — including `127.0.0.1:<reversePort>` when the desktop has an `adb reverse` mapping up, the cable-only path). Because typed channels are desktop-initiated, v9 adds `channel.open`: the desktop asks, the phone dials back with the normal channel header. Protocol roles are unchanged — the desktop still sends `hello` first on the phone-dialed socket, so the D-018 first-frame dispatch works untouched. **(3) Transport selection v1 is first-wins + failover with static preference (USB-ADB over TLS over wireless-ADB when simultaneous), showing the active path and measured ping RTT on the Device page — not live latency racing with a warm standby.** Full racing is deferred until real usage shows the simple scheme flapping; recorded so nobody mistakes the simplification for the design intent. Scope note: on a TLS-only link, mirror stays impossible (shell UID — D-013) and files/screenshot stay unavailable until M17 moves files onto channel 2; the UI says so in plain language instead of failing.

Post-verification addendum (same day): the first hardware pass failed for two desktop reasons now fixed and re-verified — mDNS must advertise only real-LAN IPv4s (multi-adapter PCs otherwise advertise unreachable virtual/link-local addresses; all candidates also ride a TXT `addrs` record and the phone tries each) and the app must be single-instance (a stray second copy stole the TLS port and silently killed the listener).

## D-023: Mirror phase 1 implementation shape — scrcpy-server over its own socket, MediaStreamSource decode, staged delivery
**Date:** 2026-07-12 · **Status:** Accepted

Pinning M16 (D-015) implementation details. **(1) The mirror keeps scrcpy's own transport in phase 1**: scrcpy-server runs via `app_process` over its own `localabstract:scrcpy_<scid>` forward, not our session layer — PIPELINE's channel 1 remains "scrcpy's protocol, ADB only". **(2) The server binary is sourced from the user's scrcpy installation during development** (the `scrcpy-server` file sits beside scrcpy.exe, which `ToolLocator` already finds; the version string the server demands as its first argument is parsed from `scrcpy.exe --version`). True bundling of a pinned jar happens at M10 packaging — what M16 removes is launching **scrcpy.exe**, not the file dependency. **(3) Decode path is `MediaStreamSource` → Media Foundation → `MediaPlayerElement`** (Annex-B H.264 samples fed straight in, `RealTimePlayback` on) rather than hand-rolled `IMFTransform` + `SwapChainPanel` — same decoder underneath, a fraction of the interop, revisit only if latency disappoints. **(4) Staged delivery**: 16a = in-app video + mouse/keyboard input + crash recovery (scrcpy.exe no longer launched); 16b = audio (channel 4 semantics); 16c = per-app windows (`--start-app` + virtual displays). Each stage is hardware-verified before the next starts.

**Status update (same day): the MediaStreamSource client shipped, was hardware-tested, and lost — reverted to the scrcpy.exe child process.** Two structural failures: the bounded drop-oldest frame queue discarded compressed P-frames under load, corrupting the picture until the next keyframe ("scratching"), and MediaPlayer paces presentation by clock, accumulating latency, where scrcpy decodes every frame and drops only at display. Fixing both properly means an IMFTransform/SwapChainPanel renderer — the exact rabbit hole D-023 point (3) tried to avoid. At the user's direction ("copy scrcpy exactly"), the proven child-process engine is back (window branded via `--window-title`), to be **bundled** with Linc at M10 so the install dependency disappears — "engulf scrcpy" now means shipping it, not reimplementing its client. The in-app client lives in git history (M16a commit `8b6ef06`) if phase 2 ever revisits it. The fixed session-startup race handling and modern window title bars survive the revert.

## D-024: Files over Direct TLS — a small list/pull/push protocol on channel 2, one desktop interface; v10
**Date:** 2026-07-13 · **Status:** Accepted

M17 makes file browsing, transfer, and the recent-photos strip work on a TLS-only link. Design calls: **(1) The files channel (2) carries a minimal request/response protocol** (list / pull / push, length-prefixed JSON header + optional raw body — see PROTOCOL.md) rather than reusing ADB sync semantics wholesale; it mirrors what the existing ADB-sync `FileService` does closely enough that the desktop's `IFileService` picks the transport transparently (`RawDevice != null` ⇒ ADB sync; else ⇒ `TlsFileService` over `channel.open`). Callers (Files page, screenshot-pull, photos) are unchanged. **(2) The phone reads/writes with `java.io.File`, reusing the All-files-access grant (D-021)** that wallpaper already needs — no new permission, and the same sideload posture (the Play build, which won't have the grant, degrades to ADB-only files). Requests without the grant reply `{"error":"not-granted"}` and the desktop shows the plain-language guidance. **(3) Photos strip = `photos.recent` (MediaStore query) + bulk kind `photo` for thumbnails + files-channel `pull` for the full image** — thumbnails ride the cheap cached bulk path, full images the streaming files path, so the strip is snappy and a click is a real download. **(4) `continue.url` and `share.item` are promoted from draft** — small, high-value, no new infrastructure (share is an Android `ACTION_SEND` activity that hands the payload to the running service's outbox). Screen mirroring and screenshot-pull stay ADB-only (shell UID / `screencap`); everything else now works ADB-free.

## D-025: M18 Sync page staged; Messages first; SMS is sideload-only and needs no default-SMS-app; v11
**Date:** 2026-07-13 · **Status:** Accepted

The Sync page (folders / photos / messages / calls) is too large for one pass, so M18 is staged and each stage is hardware-verified before the next: **18a = Sync page shell + Messages (SMS) lane**, **18b = Calls lane** (call log, incoming-call toast, dial-from-PC, decline), **18c = Folder sync v1 + Photos lane** (the sync engine — new-files-only, newest-wins, no delete propagation per D-012, with the 24-hour soak as its verify). Folder sync is deliberately last: D-012 already flags it as the rabbit hole, and it carries the loop/duplicate risk. Messages first because it is self-contained, highly visible, and probe-testable. SMS specifics: **sending needs only the `SEND_SMS` runtime permission — NOT default-SMS-app status** (default-app is only required to *write* the SMS provider / mark-as-read, which v1 doesn't do); reading uses `READ_SMS`, incoming uses `RECEIVE_SMS`. All three are dangerous runtime permissions the user grants from a lane-enable prompt, and the whole Messages feature ships **sideload-only** (D-016) — the Play build compiles it out. `sync.config` (booleans per lane) persists the user's lane choices on the phone. v11 adds `sync.config`, `sms.list`, `sms.send`, `sms.received`; contact-name resolution (READ_CONTACTS) is skipped in v1 (numbers only) to avoid a fourth permission.

## D-026: Calls lane — dial-from-PC + decline, no PC audio; permission set; v12
**Date:** 2026-07-13 · **Status:** Accepted

M18b implements D-017's tractable half of calls. Permissions (all dangerous runtime, granted from the phone's Settings screen, sideload-only per D-016): `READ_CALL_LOG` (the log), `CALL_PHONE` (place a call via `ACTION_CALL` — a true dial-from-PC, not just opening the dialer), `READ_PHONE_STATE` (detect the ringing state for the incoming-call toast), `ANSWER_PHONE_CALLS` (reject/end via `TelecomManager.endCall()` — the "decline" button). Call **audio stays on the phone** — routing it to the PC is the Bluetooth-HFP subsystem deferred to Future Ideas (D-017). The incoming-call number may be withheld by the OS on modern Android even with these grants; the desktop shows "Incoming call" without a number in that case rather than requesting the extra `READ_PHONE_NUMBERS`/default-dialer status. `call.incoming` carries a `ringing` bool (true on ring, false on end) so the desktop can raise and dismiss its banner from one message type. Ring detection uses `TelephonyCallback` (API 31+; the min-SDK-30 path would use the deprecated `PhoneStateListener`, not wired since the fleet is API 36) registered by the companion service only while the Calls lane is on.

## D-027: Folder/Photos sync v1 — desktop-orchestrated polling over the files channel, persistent seen-state as the loop guard; no protocol bump
**Date:** 2026-07-13 · **Status:** Accepted

M18c implements the sync engine (D-012's "deliberately minimal" bidirectional folder sync) as the last piece of the pipeline era. Design calls:

**(1) The engine lives entirely on the desktop; the phone stays a dumb file server.** It reuses the M17 files channel (`list`/`pull`/`push`, transport-transparent over ADB and Direct TLS via `FileServiceRouter`, D-024) and the v11 `sync.config` lanes. **No new wire messages, no new phone-side code** — so M18c ships *without a protocol version bump* (stays v12). The draft `sync.event` push is **dropped for v1**, the same way `wallpaper.changed` was dropped in D-020: a phone-side `FileObserver` emitting real-time events is more battery and more code than v1 warrants. Desktop polling is actually the most battery-friendly path (the phone only answers `list` requests; no wakelock, no watcher). Real-time phone push is a Future Idea if the poll latency ever matters.

**(2) Persistent per-pair seen-state on the desktop is the whole anti-loop / anti-duplicate mechanism.** Under `%LOCALAPPDATA%\Linc/sync-state.json`, per folder-pair, a map `name → {pcSize, pcMtime, phoneSize, phoneMtime}` recording the last-observed reality of each already-synced file. Reconcile logic per name (top-level files only in v1 — non-recursive, directories skipped):
- **not in map, present one side only** → new file → copy to the other side, record it.
- **not in map, present both** (independent same-name) → newest-wins by mtime → copy newer over older, record.
- **in map, changed one side only** → propagate that change (newest-wins degenerates to the changed side).
- **in map, changed both sides** → newest-wins by mtime.
- **in map, present one side only** → the file was *deleted* on the other side → **do nothing** (no delete propagation, and critically no resurrection — the map entry stops it being re-seen as "new").
- **in map, neither side changed** → nothing. ← this is the loop stopper: after a push we record the phone's *resulting* mtime, so the next pass sees "unchanged both sides" instead of "phone newer" and never pushes back.

mtimes are compared with a 2-second tolerance because ADB sync stores mtime at second granularity (a pushed file's phone mtime rounds down from the PC's sub-second value). After a pull the desktop explicitly sets the local file's `LastWriteTimeUtc` to the phone's `modified` so both sides stay comparable; after a push the phone's mtime is deterministically the PC file's mtime (the files channel already sets it), so both directions record comparable values without a re-list.

**(3) Newest-wins needs overwrite semantics, which the M17 transfer methods don't have** (`PullAsync`/`PushAsync` auto-rename on conflict, right for the Files page, wrong here). The engine gets overwrite by pull-to-temp-then-`File.Move(overwrite)` for phone→PC, and `DeleteAsync` + `PushAsync` for PC→phone. New-file copies never hit a conflict (the name is absent on the destination), so they use the plain methods and keep their exact name.

**(4) Triggers: reconcile on connect, on a 60 s timer while connected, and on PC-side `FileSystemWatcher` events (bidirectional pairs only).** A `SemaphoreSlim(1,1)` serialises passes; overlapping triggers coalesce. **Lanes:** the Folders lane is a user-chosen bidirectional pair (PC folder via picker ↔ a phone path, default `/sdcard/Download`); the Photos lane is a fixed **pull-only** pair (`/sdcard/DCIM/Camera` → `Pictures/Linc/Photos`) so new camera shots land on the PC but PC files never push into the camera roll. The 24-hour soak in ROADMAP M18 is met by a scripted round-trip that exercises new-file-both-ways, newest-wins conflict, and delete-no-resurrection, asserting no duplicates and no loops.

## D-028: M19 — the phone gets a Home + Share; first PC→phone push (media, share); protocol v13
**Date:** 2026-07-14 · **Status:** Accepted

The Android app grows two screens — a **Home** (mirroring the desktop's daily surface) and a **Share** tab — and with them the first features that flow **PC → phone**, which the protocol had never done before (it was desktop-drives-everything: requests down, unsolicited events up). Design calls:

**(1) Reverse-direction messages are desktop-sent unsolicited, gated ≥ 13, no phone subscription.** The pub/sub model (D-019) has the *desktop* subscribing to *phone* topics; inventing phone-subscribes-to-desktop for two message types isn't worth it. Instead the desktop just sends `pc.media.state` / `share.incoming` on the control connection whenever relevant (and once on connect), gated on the negotiated version ≥ 13. The phone's control read-loop already dispatches by type and ignores unknown types, so this needed only new cases, and a new fire-and-forget `CompanionClient.SendAsync` on the desktop (all prior desktop sends awaited a reply). `pc.media.control` flows phone → desktop fire-and-forget (the phone taps play; the desktop executes on its session; no reply).

**(2) PC media is read from Windows `GlobalSystemMediaTransportControlsSessionManager` (GSMTC).** A desktop `PcMediaService` tracks the current session, sends title/artist/playing/position/duration on every change, and executes `pc.media.control` via `TryPlay/Pause/SkipNext/SkipPrevious`. **No album art in v1** — the reverse bulk path doesn't exist (bulk is phone-served) and inlining art as base64 bloats the framed control channel; deferred. The phone shows a compact PC-media widget with transport buttons.

**(3) Share is two-way over the existing files channel + one new announce message.** Phone → PC reuses `share.item kind:file` (v10) — the Share tab copies the picked file into `/sdcard/Download/Linc/outbox/<name>` (needs the All-files grant, D-021, like the other file features) and sends the path; the desktop's new `ShareService` pulls it into `Downloads/Linc`. PC → phone: the desktop's "Send to phone" (a Files-page command) pushes a picked file to `/sdcard/Download/Linc/<name>` over the files channel, then sends `share.incoming {name,path}`; the phone lists it in the Share tab and opens it on tap via a `FileProvider` URI (new provider + `file_paths.xml`).

**(4) The clipboard widget needs no protocol.** Clipboard already syncs both ways (v1); the phone now keeps a small in-memory `ClipboardHistoryStore` (a `StateFlow` list, capped) recorded as clips pass through `ClipboardBridge` in either direction, and the Home clipboard widget lists them with tap-to-copy. History is deliberately not persisted (same privacy posture as the desktop's clipboard history — never logged, never written to disk).

Scope note: this is built in **one pass** across both apps at the user's direction, verified on hardware afterwards. Home becomes the phone's default landing screen (Status/Logs/Settings follow).

## D-029: Reverse mirror — the phone views and controls the PC (Era 2 M05)
**Date:** 2026-07-19 · **Status:** Accepted (design; implementation M05)

The mirror machinery flips direction: DXGI Desktop Duplication capture → Media Foundation H.264 encode (hardware where available) → new pc-video channel (5), the pipeline's first desktop-served stream — reusing the scrcpy-style 12-byte packet framing M16a already taught both apps, and MediaCodec decode on the phone. Control is the easy half on Windows: phone touches arrive as `pc.input` messages and are injected with `SendInput` — no shell-UID problem in this direction, so reverse mirror works over **any** transport including pure Direct TLS. Two input modes (direct touch + trackpad). Rationale: highest-value remaining feature per effort — both codecs' plumbing exists from M16a/M19, only capture (Desktop Duplication) and injection (`SendInput`) are new surface.

## D-030: Extend mode uses a bundled signed Indirect Display Driver, not a hand-written one
**Date:** 2026-07-19 · **Status:** Dropped 2026-07-22 — Extend mode was cut per user (the M05 reverse mirror covers the need; a true second-monitor mode isn't wanted). The IddCx virtual-display driver work described below is **not being done**; no code was written. Retained for the record.

Making Windows *extend* onto the phone requires a virtual monitor, which requires an Indirect Display Driver (IddCx) — the spacedesk/Duet approach. Writing and signing our own is a WDK + attestation-signing project that would dominate the milestone for no user-visible gain. Decision: bundle an existing open-source, signed virtual display driver (MIT-licensed IddSample derivatives are the candidates; pick the healthiest at implementation time), with Linc owning silent install/health/uninstall so the user never sees a driver moment (D-001 spirit). This is the one external binary left after D-023's scrcpy bundling — same posture: bundled, managed, invisible. Extend mode = that virtual display + the D-029 stream pointed at it, DPI-matched to the phone panel.

## D-031: Self-verification is standing policy — the user only runs physically-unautomatable checks
**Date:** 2026-07-19 · **Status:** Accepted (user direction)

Every milestone must exhaust automatable verification before handover: unit tests, desktop-emulating ADB probes, scripted round-trips (syncsim-style harnesses), and ADB screenshots to visually inspect the phone UI. The hand-to-user list contains only what genuinely needs a human: physical movement (Wi-Fi range, USB plug/unplug), real calls/SMS to real contacts, and subjective feel (latency, animation). Handing the user a check that a probe could have run is a process bug. (Respect the standing operational rule: don't script phone taps while the user may be using the phone — ask.)

## D-032: Offline device memory — the UI never blanks on disconnect
**Date:** 2026-07-19 · **Status:** Accepted (design; implementation M02)

Last-known per-device state persists to `%LOCALAPPDATA%\Linc/cache\<serial>\` and renders dimmed with a "last seen" stamp when disconnected; live data replaces it on reconnect. Privacy split: innocuous state (stats, wallpaper, palette, photo thumbnails, media metadata) persists; sensitive text (clipboard clips, notification bodies) remains in-memory/session-only exactly as today, unless a future explicit opt-in changes that. Rationale: Linc should feel like the phone's page that is sometimes live, not a connection tool that forgets.

## D-033: Multi-device via Chrome-style tabs in the title bar
**Date:** 2026-07-19 · **Status:** Accepted (design; implementation M03)

One tab per known device in the custom title bar, "+" to pair more. Architecturally: the per-device service bundle (supervisor, connection manager, caches, per-device settings) is instantiated per device instead of app-singleton; the registry holds N devices. Tabs show presence (live / nearby / last seen) and render from live data or the D-032 cache; closing a tab is not unpairing. Chosen over a device-switcher dropdown because the tab metaphor gives cached "phone pages" a natural home and matches the user's mental model.

## D-034: Bluetooth LE presence is a wake-up hint, never a data path
**Date:** 2026-07-19 · **Status:** Accepted (design; implementation M04)

The phone advertises a small BLE beacon (pair-derived, rotating ID); the desktop background-scans and treats a sighting purely as a trigger (fire Wi-Fi discovery now; show "Nearby"). No protocol data ever rides BLE. Degrades to a no-op on PCs without Bluetooth and when the phone denies BT permissions. Rationale: Continuity-style instant wake without a whole BLE transport's complexity or reliability burden.

## D-035: The desktop does the phone's chores over ADB — service auto-start and PC-driven onboarding
**Date:** 2026-07-19 · **Status:** Accepted (design; implementation M01)

Whenever an ADB link exists and the companion isn't answering, the desktop starts it (`am start-foreground-service`) — after installs, reboots, crashes; the service is never "off" while ADB is around. Onboarding becomes PC-driven: the desktop guides the user, pairs, installs a bundled companion APK (`adb install`), grants what ADB can legitimately grant (notification listener via `cmd notification allow_listener`, runtime permissions via `pm grant`), and starts the service. This upgrades D-001 ("ADB is the transport, never the interface") from passive plumbing to active caretaking — the user should do less at every step.

## D-036: Verification harnesses are committed repo assets, not scratch
**Date:** 2026-07-19 · **Status:** Accepted (implements D-031)

D-031 made self-verification standing policy, but the harnesses that did it (the M17/M18 ADB probes, the M18c `syncsim`) lived in an uncommitted `scratchpad/` and were gone by M00 — so every milestone rebuilt them from nothing and the "pending hardware pass" list could never be cleared cheaply. They now live in **`tools/`** and are committed:

- **`tools/probe`** — a desktop-emulating ADB probe. Forwards `localabstract:linc` exactly as the desktop does, handshakes, and exercises every lane (status/v3/v4/v7/v8 fields, pub-sub backlog, bulk, photos, files list + byte-exact push/pull, sync lanes). `--write` enables the files round-trip; `--send-pc-media` pushes a v13 `pc.media.state` so the phone side can be inspected on its own. It deliberately never probes `sms.send` or `call.dial` — those would text or ring a real contact.
- **`tools/syncsim`** — compiles the real `SyncEngine.cs` against fakes and runs D-027's scenarios (new-both-ways, re-sync no-op, newest-wins each direction, delete = no-propagate/no-resurrect). It runs them under **both** transports' semantics: ADB sync (push auto-renames, delete works) and Direct TLS (push overwrites in place, no delete). It backs up and restores the real `sync-state.json`, and keeps the Photos lane off so it can never touch the user's real `Pictures/Linc/Photos`.

The two-transport matrix earned itself immediately: it caught the PC→phone overwrite failing on ADB-free links (M00 entry in CHANGELOG.md). Rule of thumb: if a check is worth running once by hand, it is worth adding to one of these two.

## D-037: Device tabs switch one service bundle rather than running N concurrently
**Date:** 2026-07-20 · **Status:** Accepted (implements D-033)

D-033 and ROADMAP M03 describe the per-device state bundle "becoming N instances". Implementing it exposed a scope judgement worth recording. **What shipped is one live bundle that the tab strip re-points**, not N concurrent supervisors: exactly one device is *active* at a time, its tab is the live one, and every other tab renders that phone's M02 cache.

Reasoning: N-concurrent means N ADB links, N TLS sessions, N sync engines and N mirrors coexisting — and with one phone on the bench **none of that is verifiable**, so it would have shipped unexercised code through connection, sync and sharing simultaneously, which is the exact failure BRAIN.md warned about. Every user-visible M03 criterion (a tab per phone, presence at a glance, cached pages, no state bleeding, closing ≠ unpairing) is met by switching, and switching *is* testable with one real device plus a cached one.

The mechanism is deliberately invisible to callers. **Every per-device setting moved onto the `KnownDevice` record** — address, pinned TLS certificate, sync lanes, folder-sync pair — and `IDeviceRegistry` resolves the old property names against whichever device is active. So `ConnectionManager`, `TlsTransportService`, `SyncEngine` and the view models are untouched by multi-device, and *that indirection is the whole anti-bleed mechanism*: there is no longer a global place for one phone's certificate or lanes to live. A pre-M03 settings file folds its globals into the adopted device on load.

Two events, deliberately distinct: `ActiveDeviceChanged` means "the user picked another tab" and makes the supervisor drop its link and re-hunt; **connecting to a phone must not raise it**, or it would tear down the connection that just succeeded, from inside the supervisor's own gate (a deadlock as well as a drop). `KnownDevicesChanged` is the harmless repaint signal.

Upgrade path if it is ever wanted: `RetargetActiveDevice` is the seam. Instantiating N bundles means constructing the services per serial and letting the tab pick which one the pages bind to, rather than telling one bundle to change targets. Nothing in this design forecloses that; it just declines to build it before a second phone exists to prove it.

## D-038: The BLE beacon is identified by a rotating hash of the pairing certificate
**Date:** 2026-07-20 · **Status:** Accepted (implements D-034)

D-034 fixed the policy — BLE is a wake-up hint, never a data path — and left the identifier open. M04 pins it: the beacon is a single manufacturer-data section under company id **`0xFFFF`** (the SIG's testing/development id, the honest choice for an unregistered project), carrying `"LC"` and an 8-byte id equal to `SHA-256(phone's TLS certificate DER ‖ unixSeconds/300)` truncated. Format in PROTOCOL.md; implemented twice per D-006, and the two halves agreeing is the one thing an end-to-end test must prove (`tools/blescan` exists for exactly that).

Why the TLS certificate: it is already the pair's shared secret, exchanged over the authenticated ADB link and pinned both ways (D-022), so no new key material, no new exchange, and no new user step. Why rotating: a static id would let any passive listener follow the phone forever, which is a real cost for a feature whose entire value is a small convenience. Why five minutes: long enough that republishing is negligible battery, short enough that correlation is uninteresting. The desktop matches slot ±1 because the two clocks are independent; the phone republishes on slot boundaries rather than every 300 s, since a flat delay drifts later on every cycle and would eventually fall outside that tolerance.

**The consequence that mattered more than the feature:** the phone's certificate lives in the Android KeyStore, which is destroyed when the app is uninstalled. So a reinstall silently invalidates whatever the desktop has pinned — and because a desktop drops unknown certificates *silently by design* (D-022), Direct TLS then fails forever with no error on either side. This was already true and already happening on the dev machine before M04; the beacon merely made it visible, because a mismatched id is loud where a refused TLS handshake is not. M01 made it routine by giving the desktop the ability to install the companion itself. Fix: the desktop re-sends `tls.exchange` on **every** ADB connect rather than only when it has nothing pinned. PROTOCOL.md already specified re-sending as the way to replace a pin; the implementation just never used it.

## D-039: The reverse mirror encodes on the GPU only — no software H.264 fallback
**Date:** 2026-07-20 · **Status:** Accepted (implements D-029)

M05 captures with DXGI Desktop Duplication and encodes with a **hardware** Media Foundation H.264 MFT, and refuses in plain language on a PC that has none. There is deliberately no software-encoder path.

Why: enumerating the encoders on this machine showed the hardware MFT (`AMDh264Encoder`) accepts **ARGB32** input directly, which is byte-identical to the `B8G8R8A8` texture Desktop Duplication produces. So the captured frame goes to the encoder untouched — no colour conversion, no CPU copy, nothing leaves the GPU. Microsoft's software `H264 Encoder MFT` accepts only YUV (IYUV/YV12/NV12/YUY2), so supporting it means writing and maintaining a whole BGRA→NV12 stage **that can never be exercised on any machine which has hardware encode** — including every machine available to test on. That is the same trap D-037 refused for M03: shipping a substantial unexercised path through a feature that otherwise works. A clear refusal beats a fallback nobody has ever run. The seam is small if it is ever genuinely needed: `PcVideoEncoder.SelectHardwareEncoder` is the only place that filters on `MFT_ENUM_FLAG_HARDWARE`.

Related package decision: **Vortice.Direct3D11 / Vortice.DXGI 3.6.2** are the D3D11/DXGI binding (the desktop had no D3D dependency before M05). Media Foundation is **not** taken as a package — the handful of interfaces M05 needs are hand-rolled in `Services/MediaFoundation.cs`, which is less surface than adopting another binding.

Three things that cost real time and are written into that file's comments so they are not rediscovered:
- An **async MFT answers `MF_E_TRANSFORM_ASYNC_LOCKED` to every call**, including merely enumerating its input types, until `MF_TRANSFORM_ASYNC_UNLOCK` is set on its attribute store. Before unlocking, the hardware encoder looks like it supports no input formats at all.
- **The `IMFActivate`'s attributes are not the transform's.** One encoder here reported no async flag on its activate and `async=1` on the transform itself. Always ask the transform.
- **`GetUINT32` and friends leave their out-param undefined when the attribute is absent.** Reading it without checking the HRESULT yields confident garbage — it produced a wrong reading of every encoder's async/D3D flags until the return code was checked.

## D-040: Reverse-mirror pacing is deadline-driven, and capture is decoupled from the encoder's event pump
**Date:** 2026-07-20 · **Status:** Accepted (implements D-029)

`PcMirrorSource` runs two threads: a **pump** that does nothing but drain the async MFT's event queue (handing encoded frames straight out), and a **feeder** that owns capture and submits on an absolute-deadline schedule. They meet at a semaphore counting the encoder's free input slots.

Both halves of that shape were forced by measurement, not taste, and each wrong version looked plausible:
- **One loop doing both** stalls output while it waits on capture: an idle desktop makes `AcquireNextFrame` block for its whole timeout, and `METransformHaveOutput` events sit undrained meanwhile. Measured p95 of **76 ms** against a 16 ms median — pure added mirror lag from the loop shape alone.
- **Sleeping a fixed interval after the work** undershoots, because each cycle costs (slot wait + capture + sleep). Set to 30 fps it delivered **20 fps**.
- **Submitting as soon as a frame arrives** looks like the low-latency choice and instead removes pacing altogether — the encoder free-ran at **56 fps**, and unbounded it exceeds 100 fps, all of it wasted CPU and bandwidth. Frames are therefore drained until the deadline and only the freshest is submitted, which costs nothing since a newer frame supersedes an older one anyway.

Two further behaviours are load-bearing rather than incidental. Capture rotates a **ring of textures** because the encoder keeps reading a submitted texture after `ProcessInput` returns; reusing one staging texture tears in a way that reads as an encoder bug. And a **static screen resends the last frame** — Desktop Duplication reports only changed frames, so without that the stream simply stops, which on the phone is indistinguishable from a frozen mirror and produces no error anywhere. Both are asserted by `tools/mirrorsim`.

---

# Convergence phase (D-041…) — new milestone series (gemini-docs/ROADMAP.md, 2026-07-22)

## D-041: Vendor scrcpy for tracking + bundle it; brand the window via built-in flags, not a source patch
**Date:** 2026-07-22 · **Status:** Accepted; **method revised 2026-07-23** · **Supersedes the "bundle at Polish" half of D-023**

**Revision (2026-07-23), the as-built method:** the original plan was to fork scrcpy and edit
`app/src/screen.c` (an `SDL_WINDOW_BORDERLESS` patch) to strip the window chrome — which forces
building scrcpy's SDL/FFmpeg client from source on Windows (the real risk). That is **not needed.**
scrcpy already exposes the frameless, no-title, custom-icon look through built-in options:
`--window-borderless`, `--window-title "Linc"`, and the `SCRCPY_ICON_PATH` environment variable —
which is precisely the stated end goal ("look like just the phone's screen, no frame/title"). So M3
(a) vendors upstream source as a **git submodule at `SCRCPY/Default`** (upstream-trackable,
pristine, LICENSE kept) for provenance and future updates, (b) **bundles the official prebuilt
Windows binaries** (matched `scrcpy.exe` + `scrcpy-server` + DLLs + adb) under `SCRCPY/Custom/bin/`
and ships them with the desktop (satisfying D-023's "bundle so users install nothing"), and (c)
brands purely at **runtime** — `MirrorService` launches the bundled scrcpy with the borderless/title
flags and the Linc icon env var. **No scrcpy C/SDL source is edited and nothing is built from
source.** `SCRCPY/NOTICE.md` records this (Apache-2.0; presentation-only customization; engine
untouched). A truly *custom-drawn* top bar (a visible branded bar with controls) is the only thing
that would require the source build; it is deliberately deferred and would be a separate milestone
on top of the `SCRCPY/Default` submodule. Trade-off to watch: a borderless window can't be dragged
by a title bar — if that hurts, add a slim custom bar later (the deferred source work).

The desktop currently runs stock `scrcpy.exe` as a child process, located from PATH / winget / a bundled dir (`MirrorService`/`ToolLocator`). To ship a branded, chromeless mirror *and* keep pulling Genymobile's fixes, scrcpy comes in as a **git submodule** at `Linc/SCRCPY` pointing at the official Apache-2.0 repo (fall back to a **fork** with `git remote add upstream` if the submodule fights the build; **never** a flat unzip/copy, which kills future syncing). The one modification — remove the title text + icon and make the title bar blend away (`SDL_WINDOW_BORDERLESS`, or a matching-color bar if borderless hurts drag/resize) — is confined to the SDL **window-presentation** layer (`app/src/screen.c` + the SDL window flag/title calls, confirmed against the pulled version) and kept as a small isolated patch so upstream merges rarely conflict. **Capture, encode, transport, and input are never touched.** `LICENSE` stays; a `NOTICE.md` records that this is a modified scrcpy build (UI/window chrome only). The modified client becomes Linc's bundled scrcpy, replacing the PATH/winget lookup. This is the foundation for Desktop Mode (D-046), per-app windows (D-047), audio routing (D-048), and editable scrcpy settings. Risk noted: building scrcpy's SDL/FFmpeg client on Windows is real work; the upstream prebuilt server jar can stay.

## D-042: The phone drives the PC — a `pc.control` message, and a video-less Tools surface
**Date:** 2026-07-22 · **Status:** Accepted (planned, M3)

The phone already sends `pc.input` (→ `SendInput`) and `pc.media.control` — the exact precedent for phone→desktop control. A new **`pc.control`** control message (protocol bump from v14) carries an action enum: lock, sleep, shutdown, restart, volume up/down/mute, brightness. The desktop executes via Win32 (`LockWorkStation`, `SetSuspendState`, `InitiateShutdown`/`ExitWindowsEx`, the audio-endpoint volume API). **Destructive actions (shutdown/restart) require an explicit confirmation** — an easy-to-miss but essential safety gate. New `PcControlService` mirrors `PcInputService`/`PcMediaService`. The phone's **Tools page** (remote keyboard + touchpad + buttons) drives the PC **without** the mirror video by lifting the input path out of `MirrorActivity` and reusing `pc.input`; because Windows input injection needs no shell UID, all of this works over any transport, including pure Direct TLS.

## D-043: Phone settings are changed via companion protocol messages, not raw ADB
**Date:** 2026-07-22 · **Status:** Accepted (planned, M4)

Desktop widgets that control the phone (force landscape, auto-rotate, brightness, etc.) send a companion **protocol message** the phone applies through `Settings.System` (with `WRITE_SETTINGS`), rather than the desktop issuing raw `settings put` over ADB. This honors D-001 (ADB is never the interface) and keeps the behavior identical across transports (a Direct-TLS-only link has no ADB shell). The Device page also gains a **Tools** grouping (mirror / desktop-mode / apps / scrcpy-settings) and **editable scrcpy settings** (quality/bitrate/resolution/crop/flags) surfaced from `MirrorService`'s command-line builder and persisted per device.

## D-044: A SQLite data layer (`LincStore`) becomes the persistence foundation
**Date:** 2026-07-22 · **Status:** Accepted (planned, M5)

The new asks — notification history, an offline action queue, a per-device app-list cache — want real queries and retention, which JSON files don't serve well. A **SQLite** store (`Microsoft.Data.Sqlite`) named `LincStore` holds known devices, per-device offline cache, notification history, the offline queue, and the app-list cache. Migration is **incremental**: `settings.json` is imported on first run (the registry already migrates a pre-M03 file, so this fits the established pattern) and behavior stays identical; `sync-state.json` and the `cache/<serial>/` blobs can migrate opportunistically. `tools/devicesim` is extended to cover the store so the multi-device semantics stay proven.

## D-045: Persistent notification history is opt-in — a scoped, consented reversal of D-032's default
**Date:** 2026-07-22 · **Status:** Accepted (planned, M6)

D-032 keeps notification **bodies** off disk by default, and the cache record structurally can't hold them. Saving notifications for days/months necessarily puts bodies on disk, so it ships as an **explicitly opt-in** feature (**default off**) with a **retention setting** (e.g. 7/30/90 days) and a **clear** action, stored in `LincStore` (D-044). Turning it on is the user consenting; the structural privacy guarantee of D-032 continues to hold for every *other* path (clipboard text is still never persisted or logged). Icon/art blobs are stored by hash to bound growth.

## D-046: Android Desktop Mode comes from Android itself (a virtual display), not a PC-side driver
**Date:** 2026-07-22 · **Status:** Accepted (planned, M8) · **Reframes the need M06 Extend was dropped for**

M06 Extend (a bundled signed IddCx virtual monitor on the PC, D-030) was dropped. The genuinely useful capability — real desktop windowing streamed to the PC — is delivered by switching on **Android's own desktop mode** and capturing it with the scrcpy pipeline we already own. The clean path is scrcpy **`--new-display`** (a virtual display Android renders in freeform/desktop mode, often without touching any global setting), with `force_desktop_mode_on_external_displays` as an older-device fallback. No PC-side display driver is built or signed; the feature is a toggle in the Device-page Tools that reuses `MirrorService` + the vendored scrcpy (D-041). Requires **capability/version detection** and a plain-language fallback, since virtual-display + desktop-mode support varies by Android version and OEM, and DRM/secure surfaces won't capture.

## D-047: Per-app windows via one virtual display per app, with a per-device app-list cache
**Date:** 2026-07-22 · **Status:** Accepted (planned, M9)

The Home **Apps** widget opens a chosen phone app as its **own** resizable PC window by launching that app onto a fresh **virtual display** (scrcpy `--new-display` + `am start --display <id> -n <pkg>/<activity>`) and streaming that display through the existing pipeline — the same machinery as Desktop Mode (D-046), scoped to one app instead of the whole screen. The installed-app list is fetched once per device (`pm list packages` + `PackageManager` labels, icons over the **bulk channel** + `LargeIconCache`) via a small `apps.list` control message, **cached in `LincStore` keyed by device id**, and only **diffed** (new/removed) on later connects so the widget is instant. Closing a window tears down its virtual display; the cache persists. Open risks: launchable-activity resolution, multi-display input routing, and window lifecycle.

## D-048: Audio routing rides channel 4 via the vendored scrcpy; the reverse direction is staged
**Date:** 2026-07-22 · **Status:** Accepted (planned, M10)

"PC as speaker for the phone" is the designed-but-unbuilt **audio channel (4)**: the vendored scrcpy's audio capture (Android 11+) decoded and played on the PC, ADB-bound like the mirror. "Phone as keyboard for the PC" already ships via `pc.input` (D-042). "Phone as speaker/mic for the PC" is the genuinely new direction (PC audio capture → phone playback, and the mic reverse) and is **staged as its own step after** phone→PC audio works, rather than being smuggled into the same milestone.

## D-049: The custom scrcpy window is a from-source, iPhone-Mirroring-style redesign — a milestone after M3
**Date:** 2026-07-23 · **Status:** Accepted (planned, milestone after M3) · **Builds on D-041**

D-041 (revised) brands the mirror window cheaply at runtime via scrcpy's built-in
`--window-borderless` / `--window-title` / `SCRCPY_ICON_PATH` (M3, low risk, no build). The owner
then specified a richer target modelled on Apple's **iPhone Mirroring**: bare edge-to-edge video at
rest, a **thin hover-reveal top bar** with close/minimize only (no icon/title), that bar being the
**drag zone** (except on the buttons), subtle **rounded corners**, and a **drop shadow**. Full
behaviour + mechanism in `SCRCPY-WINDOW-SPEC.md`. This **requires editing scrcpy's own
window-creation / `WndProc` / SDL-render code and building scrcpy from source on Windows** — Win32
`WM_NCCALCSIZE` (drop the OS title bar), `WM_NCHITTEST → HTCAPTION` (draggable strip),
`TrackMouseEvent` (fade), SDL-drawn buttons, `DwmExtendFrameIntoClientArea` (shadow),
`DWMWA_WINDOW_CORNER_PREFERENCE` (rounded, Win11+). It **supersedes** M3's interim flag look and
changes the bundled binary from the official prebuilt to our custom build. **Scope boundary:** only
window-creation/event/render code — never capture, input, or the wire protocol. **Sequencing:** it
is the milestone *after* M3, and its **first step is proving the from-source Windows build of
unmodified scrcpy**; if that environment can't be stood up cleanly, stop before touching window
code. Highest-risk task in the project — small checkpointed steps, most-trusted agent.

## D-050: Our from-source scrcpy must be built `-Dportable=true`
**Date:** 2026-07-23 · **Status:** Accepted (implemented, M3.5b-1) · **Builds on D-041/D-049**

The from-source client must be built with meson `-Dportable=true`. Without it, scrcpy's
`SCRCPY_PATH_DEFAULT` bakes in the MSYS2 install prefix and resolves `scrcpy-server` (and adb/icon)
at `C:/msys64/mingw64/share/scrcpy/…`, which doesn't exist on any machine but the build box — the
mirror aborts with *"…/scrcpy-server does not exist … Server connection failed."* `-Dportable=true`
sets the `PORTABLE` config (`app/meson.build`), making scrcpy load `scrcpy-server` from the exe's own
directory — which is exactly how we ship it (`SCRCPY/Custom/bin/`). The official Genymobile prebuilt
was already portable, which is why M3's bundle worked and the first M3.5b-1 build (missing the flag)
did not. Non-intrusive verification: `scrcpy.exe --list-encoders` pushes and starts the server without
opening a mirror window — a clean encoder list proves the portable server path resolves.

## D-051: The custom scrcpy window keeps native resize + maximize (amends D-049's "close/minimize only")
**Date:** 2026-07-23 · **Status:** Accepted · **Amends D-049 / `SCRCPY-WINDOW-SPEC.md`**

The iPhone-Mirroring spec (D-049) called for a window with **close/minimize only** — no resize, no
maximize. On first hands-on test of the frameless build the owner wanted to **resize and expand
(maximize)** the mirror window, so the custom window keeps normal window management: drag-to-resize
on every edge/corner and maximize (double-click caption / Aero-snap / Win+↑ now, plus a maximize
button when the control bar lands in b-2c). Mechanism is the standard custom-chrome recipe: **retain
`WS_THICKFRAME | WS_MAXIMIZEBOX`** while still returning 0 from `WM_NCCALCSIZE` (so resize/snap work
with no visible frame), add resize-border hit-testing in `WM_NCHITTEST`, clamp maximize to the
monitor work area via `WM_GETMINMAXINFO`, and drop the rounded region while maximized. Rationale: the
mirror is a real desktop window the owner arranges alongside other apps, and the same resizable
frameless chrome is reused by later features (M6 pop-out panels, M7 per-app windows), so building it
in now is cheap and load-bearing. Trade-off vs. strict iPhone-Mirroring parity accepted deliberately.

## D-052: The mirror control bar is deferred to a Polish backlog; M8 (Desktop Mode) takes M4's slot
**Date:** 2026-07-27 · **Status:** Accepted · **Defers part of D-049 / M3.5b-2c; reorders `ROADMAP.md`**

The hover control bar (min/max/close) consumed three sessions without landing a visible result. After
the HiDPI coordinate fix was applied in source and cleanly rebuilt (verified: `bin/scrcpy.exe`
byte-identical to `src/x/app/scrcpy.exe`, `control_bar.c` present in `meson.build`, render wired in
`display.c`), the owner still saw no buttons. Root cause is now understood as **design, not
arithmetic**: the bar only becomes `visible` once the cursor is already inside the button cluster,
which is `SC_CB_BTN_H` (30) *physical* pixels tall — ~24 logical px at 125% scaling — in the extreme
top-right corner, while the rest of the top strip is `HTCAPTION` and therefore never delivers
`SDL_MOUSEMOTION` to the client at all. Revealing invisible buttons requires hitting an invisible
sliver. The fix is the whole-strip reveal already scoped as b-2c-3, not more coordinate work.

**Decision:** the window is **good enough to ship as-is** — frameless, rounded, draggable, resizable
and maximizable are all owner-verified, and every window operation remains reachable via Aero-snap,
Win+↑, double-click-caption, and Alt+F4. The control bar moves to a **Polish backlog** worked after
the feature milestones. **M3.5 is therefore closed as substantially done.**

## D-053: Desktop Mode ships as a SECOND SCREEN, not a windowing desktop — the platform refuses
**Date:** 2026-07-28 · **Status:** Accepted · **Amends `DESKTOP-MODE-SPEC.md` (owner spec) / M8**

**Evidence (GLM read-only diagnostic, 2026-07-28, Pixel 7 build `CP31.260623.005`, Android 17 /
SDK 37):**
- `pm list features` does **NOT** declare `android.software.freeform_window_management`,
  `android.software.pc_mode`, or `android.software.desktop_mode`. The only relevant declared
  feature is `android.software.activities_on_secondary_displays`.
- `dumpsys activity` shows our virtual display created correctly at the requested geometry, but
  classified `mWindowingMode=fullscreen, mActivityType=home` — a plain virtual display running the
  launcher fullscreen, not a desktop-windowing display.
- `getprop | findstr desktop|freeform` returns **no matches**; the three
  `persist.wm.debug.desktop_mode*` props exist as **empty, unset slots**.

**The settings-key red herring (record this — it cost us two sessions).** Android's `settings`
store is an arbitrary key-value bag: `settings put global <anything> 1` **always succeeds and
always reads back**, whether or not any platform code consumes that key. M3.5-era reports that
`development_force_resizable_activities` / `development_enable_freeform_windows_support` "do not
exist" simply meant *unset*; after our own `ApplyWindowingModeAsync` wrote them they read `1` — and
nothing changed, because **declared platform features are a build-time decision, not a
settings-driven capability**. Never again infer capability from a settings key round-tripping.

**Decision:** Desktop Mode **ships as-is, honestly described as a second screen** — a separate
virtual display you can run apps on, at a tunable resolution/DPI, with apps running fullscreen on
it. We do **not** write the `persist.wm.debug.desktop_mode*` properties (owner declined; unproven
on an Android 17 preview build and it mutates their daily-driver phone). The freeform/windowing
half of the owner's spec is **not achievable on this hardware** and is formally dropped from M8.
**Real per-app windowing is redirected to M7 (Apps windows)**, which gets there a different way —
one `--new-display` per app plus `am start`, giving each app its own PC window, which is the
outcome the owner actually wanted.

**Consequences:** the inert freeform settings writes should stop (they change nothing and imply a
capability we don't have); the Desktop Mode UI copy must describe a second screen rather than a
desktop; `AutoFullscreenApps` / `LaunchAppsFreeform` / `DefaultWindowMode` / `ResizableWindows`
become no-ops on this device and must not be presented as working controls.

**Reorder:** **M8 (Desktop Mode) takes M4's slot and is the next milestone**; **M4 (phone-as-remote
Tools suite) moves into M8's old slot.** Rationale: M8 depends only on M3's Custom scrcpy, which is
finished and stable, so it is unblocked today; M4 requires a protocol bump (v14→v15) plus new
surfaces on both apps, so deferring it costs nothing and keeps the wire format frozen a while longer.
Both milestones are independent of each other, so the swap carries no dependency risk. M5's
"editable scrcpy settings" now lands after Desktop Mode has proven the settings-plumbing pattern.

## D-054: The mirror preset combo is a SHORTCUT; per-device `MirrorSettings` is the single source of truth
**Date:** 2026-07-29 · **Status:** Accepted · **Milestone:** M5b

Before M5b the normal screen mirror had exactly one control — a three-item `MirrorPreset` combo
(High quality / Balanced / Performance) — and the scrcpy args were otherwise hardcoded. M5b makes
them editable and per-device, which raised the obvious question: are the preset and the editable
settings two independent inputs?

**Decision: no — there is one input.** `MirrorSettings` (persisted on `KnownDevice.Mirror`) is the
only thing `MirrorService.StartAsync` ever reads. Selecting a preset **writes** that preset's
`MaxSize` + `VideoBitRate` into the device's persisted `MirrorSettings` and leaves the other fields
alone; the combo then shows whichever preset matches the persisted values exactly, and **shows no
selection at all when none matches** (we deliberately do NOT invent a "Custom" entry — a fake
preset would become a fourth thing to keep in sync). `HomeViewModel`'s mirror quick-action reads the
same record instead of hardwiring Balanced, so both entry points agree.

**Consequence to remember:** "default" now has two meanings and they must be kept collapsed onto
one. A device whose `Mirror` field is still null must launch **byte-identically to the old Balanced
preset** (`-b 8M -m 1280 … --stay-awake`) — an untouched install may not silently change quality.
The M5b implementation achieved that with a `MirrorSettings.BalancedDefaults` static substituted for
null by `DeviceRegistry.Mirror`, while the record's own positional defaults stayed at `MaxSize = 0`
(native) — so "Reset to defaults" landed somewhere different from the never-touched baseline. That
split is a wart, not the decision: **the record's defaults, the null substitute, and the Reset target
are all meant to be the same values.** Any future change here keeps them equal.

## D-055: PC-driven display control is protocol v15, applied through `Settings.System` — never raw ADB
**Date:** 2026-07-29 · **Status:** Accepted · **Milestone:** M5c · **Upholds D-001**

M5's third piece gives the PC widgets that change the phone's orientation and brightness. There were
two ways to do it and only one of them is allowed here.

**Rejected: `adb shell settings put system ...` from the desktop.** It would have worked today with
zero phone-side code, and Desktop Mode already shells out to adb for its one-time setup — so the
precedent looked inviting. It is still wrong. **D-001 says users never touch ADB and features ride
the companion protocol**; ADB is a transport of last resort, absent entirely on a Direct-TLS-only
link, which would make these widgets silently dead on exactly the connection we are moving toward.
Two sessions of M8 were also lost to trusting `settings put` (D-053) — writes that succeed and read
back while the platform ignores them. A phone-side implementation gets a real success/failure answer.

**Decision: protocol v15**, two request/reply messages — `display.rotation.set`
(`auto`/`portrait`/`landscape`, one type covering both the auto-rotate toggle and the force-orientation
widget) and `display.brightness.set` (`{auto: true}` or `{auto: false, level: 0–100}`) — plus three
optional `status` fields (`rotationMode`, `brightnessAuto`, `brightnessLevel`) so the widgets open
showing the phone's real state rather than a guess. Full wire spec in `PROTOCOL.md` › v15.

**Two things worth remembering:**
- **Brightness crosses the wire as a 0–100 percentage, never a raw platform value.** The phone owns
  the scaling to its own range, so the desktop never needs to know the device's brightness ceiling —
  which differs per device and is not reliably discoverable.
- **`WRITE_SETTINGS` is an appop, not a runtime permission.** No dialog can grant it; the user must
  toggle it in *Settings › Apps › Special app access › Modify system settings*. So the phone checks
  `Settings.System.canWrite()` **first** and answers `error` `not-granted` with a plain-language
  route to that screen, exactly as v7's `dnd.set` does for Do-Not-Disturb access. Never let the
  `SecurityException` escape — a stack trace is not a user-facing error.

## D-056: ONE live doc set for every agent — the per-agent doc folders are retired
**Date:** 2026-07-31 · **Status:** Accepted · **Supersedes the "sync the incoming folder" step in AGENTS-REGISTRY**

The project has carried three parallel working-doc folders — `gemini-docs/` (Antigravity),
`claude-docs/` (Claude Code) and `opencode-docs/` — each holding a full copy of the project-reality
docs (BRAIN, ROADMAP, PROTOCOL, DECISIONS, CHANGELOG, FEATURES, specs). The rule was that the planner
syncs the incoming folder at every agent switch.

**In practice the sync never kept up.** `gemini-docs/` froze at M8c. `claude-docs/` froze around
M2b/M3 — its `BRAIN.md` is 164 lines against the live 301, describes M5 as "PC-controls-phone widgets"
still unbuilt, and predates M8, D-052, D-053, D-054 and D-055 entirely. Handing an agent that folder
would actively mislead it. And the cost of a switch — reconciling 13 files — was high enough that it
discouraged using the right agent for a job, which is the opposite of what the scheme was for.

**Decision: `opencode-docs/` is the single live doc set, read by whichever agent is active.** Only the
**behavioural** guides stay per-agent, as small standalone files: `Vibe/agent/OPENCODE.md`,
`Vibe/agent/GEMINI.md`, `Vibe/agent/claude-docs/CLAUDE.md`, plus the shared `AGENTS.md` and
`GUARDRAILS.md`. An agent switch now costs one line in the registry instead of a 13-file merge.

**The other folders' project-reality copies are DEAD.** Do not read them, do not update them, do not
sync them. They are kept only as historical snapshots. `claude-docs/CLAUDE.md` is the one file in
that folder still in use.

**Cheap follow-up, not urgent:** the live folder's name is now misleading, since it is no longer
specific to the opencode runner. Renaming it to `live-docs/` would touch many cross-references, so it
is deferred rather than done — but **read `opencode-docs/` as "the live docs", not "opencode's docs".**

## D-057: Verification harnesses must NEVER touch the real `%LOCALAPPDATA%\Linc` store
**Date:** 2026-07-31 · **Status:** Accepted · **Supersedes the per-harness "backup/restore" convention**

**This has now destroyed the owner's real data twice.** In M2a a killed `devicesim` run skipped its
restore step and left the real Pixel 7 pairing overwritten with fake test devices. In M6b,
`homelayoutsim` — written one session earlier — appended `TEST-UNKNOWN` and `TEST-ALL-HIDDEN` to the
real `KnownDevices` and repointed `PairedSerial` at a fake, because **two of its nine sections called
`SavePairedDevice`/`SaveHome` outside the backup/restore `try/finally`.** The agent noticed and
repaired it, but the pairing certificate was one careless section away from being lost.

**The backup/restore convention is the wrong shape.** It is opt-in per code block, invisible when
omitted, and silently absent in new sections written by someone following the surrounding style. Four
harnesses (`devicesim`, `desktopsim`, `mirrorsettingssim`, `homelayoutsim`) write to the real store;
their coverage varies; the failure mode is destroying the one file the product cannot regenerate.

**Decision: make it structurally impossible.** `DeviceRegistry` gains an **optional root-path
constructor parameter** defaulting to today's `%LOCALAPPDATA%\Linc`. Production callers are unchanged.
**Every harness constructs it against a fresh temp directory** and deletes it afterwards, so no
harness can reach the real file even if it is killed mid-run, and no future section can forget the
ritual — there is no ritual left to forget.

**Consequences:**
- The `devicesim` footgun **disappears**. The standing "never pipe or kill devicesim" warning, and the
  "do not run devicesim" line carried in every task file since M8, can both be retired once this lands.
- Backup/restore blocks come out of all four harnesses — dead weight once the path is redirected.
- **A harness that needs to prove real-path behaviour** (e.g. that the default path resolves to
  `%LOCALAPPDATA%\Linc`) asserts the *path string*, never by writing to it.

## D-058: The app inventory is a pull on connect, not a push — and it is launchable-apps-only
**Date:** 2026-07-31 · **Status:** Accepted · **Milestone:** M6c · **Protocol v16**

Home's Apps section (M6c) and per-app windows (M7) both need to know what is installed on the phone.
Three choices had to be made and are recorded here so they are not relitigated.

**1. Launchable apps only.** The phone returns only packages resolving a `CATEGORY_LAUNCHER` intent.
A full `getInstalledPackages()` is several hundred framework packages the user can neither recognise
nor open. `system` is reported as a flag so the desktop can group or de-emphasise, but system apps are
**not** filtered out — Camera, Settings and Photos are exactly the ones people want to reach.

**2. Pull on connect, no push event.** No unsolicited "app installed/removed" message. The desktop
asks on connect and reconciles against its per-device cache. A phone-side `PackageManager` observer
would cost a permanent broadcast receiver and battery to track a list that changes a few times a
month. This mirrors the reasoning that dropped the draft `sync.event` at v10.

**3. Icons ride the existing bulk channel.** Bulk kind `appIcon`, keyed by package name, has existed
since **v5** — it was the kind that proved the bulk channel. M6c adds **no** icon protocol; it fetches
lazily per package and caches the PNG desktop-side. Do not invent a second icon path.

**Consequence for the cache — closes part of a known hole.** The per-device app cache lives under
`%LOCALAPPDATA%\Linc/cache\<serial>\`, which H1's D-057 did **not** cover: only `settings.json` was
protected from harnesses. The app cache must therefore take its root from the **same** injected path
`DeviceRegistry` uses, so a harness pointed at a temp root cannot write into the owner's real cache
either. Any future store added under `%LOCALAPPDATA%\Linc` inherits this rule.

## D-059: Per-app windows use scrcpy's own `--start-app`, not `am start --display`
**Date:** 2026-07-31 · **Status:** Accepted · **Milestone:** M7

`ROADMAP.md` has said since the Convergence plan was written that opening a phone app in its own PC
window would mean a per-app `--new-display` **plus** `am start --display <id> -n <pkg>/<activity>` over
ADB. That was true when the plan was drafted. **It is no longer true**, and building it would have been
a fragile detour.

**Our vendored scrcpy (3.3.4) supports `--start-app` natively.** Its own `--help` gives the exact
combination we want: `scrcpy --new-display --start-app=+org.mozilla.firefox`. A leading `+`
force-stops the app first; a leading `?` does a fuzzy name search (we always pass an exact package,
so we use `+<package>`).

**Decision: one `scrcpy` process per app window**, launched with `--new-display=WxH/DPI
--start-app=+<package>`. Consequences, all of them improvements:
- **No `am start`, no ADB shell in the launch path** — which honours **D-001** far better than the
  original plan and keeps working on a Direct-TLS-only link. It also means we never have to resolve a
  launchable **activity** name; the package alone is enough, and activity resolution was the fiddliest
  part of the old approach.
- **The display's lifetime is the process's lifetime.** scrcpy creates the virtual display, starts the
  app on it, and tears the display down when the window closes. We do not have to track display ids or
  clean them up by hand — the failure mode that would have leaked orphan displays disappears.
- **This is where D-053's dropped windowing goal lands.** M8's Desktop Mode could not give per-app
  windows because the platform declares no freeform support. One scrcpy window per app reaches the
  same outcome by a different route, and needs no platform capability we do not already use.

`ROADMAP.md`'s M7 wording (`am start --display <id> -n <pkg>/<activity>`) is superseded by this entry.

## D-060: The store root is injected everywhere — D-057 is finished, and a `--data-root` switch exists

**2026-08-07 (M12g).** D-057 gave `DeviceRegistry` an injectable root, but only `LincStore` and
`AppCatalog` ever used it. **`DeviceCacheService`, `LogService`, `SyncEngine` and `TlsTransportService`
each resolved `%LOCALAPPDATA%\Linc` directly**, so only `settings.json` was really protected. All four
now take `IDeviceRegistry.RootPath`, and `cleanroomsim` guards the **exact call count** at the three
sanctioned sites (`DeviceRegistry.DefaultRootPath` once, `ToolLocator`'s Android-SDK lookup twice).

A **`--data-root <path>`** command-line switch injects that root from outside the process. It is `null`
on every normal launch, so production paths are unchanged. **It exists because
`Environment.GetFolderPath(LocalApplicationData)` ignores the `LOCALAPPDATA` environment variable** —
it resolves through the Windows known-folder API — so a child Linc process could not be sandboxed the
obvious way. Measured, not assumed.

**Consequence:** the user-facing folders (`MyPictures`, `UserProfile` — `Pictures/Linc`,
`Downloads/Linc`) are deliberately **not** under the injected root. `LocalApplicationData` is our private
store; the user's own folders never move. `SyncEngine` touches both and must keep them separate.

## D-061: v17 quick controls are request/reply, and destructive actions are confirmed ON THE WIRE

**2026-08-09 (M4b).** `pc.input` is fire-and-forget because a dropped mouse delta is invisible and
self-correcting. **A dropped `shutdown` is not.** So `pc.control` follows v15's display-control shape:
request/reply, `ok` or `error` with `replyTo` set.

`shutdown` and `restart` require **`confirm: true` on the wire** as well as a phone-side dialog.
**Both, deliberately** — a UI-only gate means one stray frame, one bug or one replayed message can power
off the user's PC, and a wire flag is testable from a harness with no UI running.

**Also settled here:** PC brightness ships **WMI only**; DDC/CI for external monitors is out of scope
indefinitely. `pc.state` carries `canBrightness`/`canSleep`/`canShutdown` so the phone **disables, never
hides**. And `canSleep` must come from `GetPwrCapabilities` (S3 **or** Modern Standby) — the obvious
`IsPwrSuspendAllowed()` knows only about legacy S3 and reports `false` on machines where sleep works.

## D-062: On a hotspot link, discovery is deleted — the peer address IS the answer

**2026-08-10 (M13, owner spec).** Every ADB-over-Wi-Fi path burns time on discovery (mDNS, ARP, subnet
sweeps) to answer one question: what is the phone's IP on this link? **If the two apps already hold a
socket over that link, the OS answered it when the socket was established.** The desktop reads its own
control connection's remote endpoint; it is correct by construction and costs a syscall.

Valid **on a hotspot specifically** — one L2 segment, no NAT — and **not** across a router doing NAT or
any relay. Hence v18's `addrs` fallback and the **same-subnet filter**, which is load-bearing: Android
advertises mobile-data and secondary-interface addresses that are unreachable from the hotspot segment,
and each unfiltered one costs a full connect timeout.

The only remaining unknown is the port, and the phone knows it — **so discovery becomes a protocol
message** (`adb.announce`). Two rules are non-negotiable: a **monotonic `gen` counter** (a role flip in
flight otherwise lets a stale announcement point ADB at a dead address — intermittent, and the most
expensive bug in the design), and **never announce a port you have not just loopback-connected to**
(the L3 link comes up before `adbd` rebinds, so link-up is not readiness).

**Role selection needs no discovery either:** on a hotspot the AP is always at every client's gateway
address, so `gateway == my address or absent → AP, listen; else → client, dial the gateway`. Identical
code both sides, symmetric in either role.

## D-063: The `WRITE_SECURE_SETTINGS` grant rides M2b's onboarding — D-001 is not weakened

**2026-08-10 (M13c).** Phone-side self-arming needs
`pm grant app.linc.android android.permission.WRITE_SECURE_SETTINGS`, which needs a cable exactly once.
That looks like a D-001 violation ("users never touch ADB or a terminal") — **it is not, because
onboarding (M2b) already attaches over ADB with a cable and injects the companion APK.** The grant goes
into that existing flow, right after the install, invisibly.

**It must be idempotent and must never fail onboarding.** If the grant is refused or the OEM blocks it,
log and continue; the feature degrades and everything else still works. **Never build a second ADB path
for it** — one flow, one cable moment, or the decision is void.

## D-064: Agent reports are written to `Vibe/agent/reports/<ID>.md`, not only pasted

**2026-08-07, hardened 2026-08-09.** Three consecutive reports were truncated in transit, and the cut
always fell before the acceptance results, the negative proofs and the conclusions — exactly what QA
depends on. **Every task file now requires the agent to write its full report to disk as well as
printing it**, and that report is the one `.md` an agent may write ("do not edit `.md` files" means the
*project* docs). In a chained session each milestone's report is written **before** the next begins, so
a session that dies mid-chain does not take the earlier findings with it.

**Companion rule:** §0.1 must ask for **file mtimes**, not just contents. Sessions here die mid-task
often enough that a later one routinely finds work already on disk, and **`git diff` measures against
HEAD, not session start** — so an honest agent will report a predecessor's work as its own. Timestamps
are the only thing that has ever settled these.

## D-065: The reverse mirror drops frames rather than delivering them late

**2026-09-20 (protocol v19).** The mirror was built and tuned over the USB cable, where the link is
never the bottleneck, and it carried two assumptions that only a wireless link falsifies.

**The first: that writing a frame is free.** Frames were written to the channel from the MFT pump
thread — the single thread that drains the encoder's event queue. Over Wi-Fi a full send buffer blocks
that write, so the encoder stops being serviced and capture stalls behind it; the picture freezes with
nothing in any log, and when the link recovers the whole backlog is delivered and the view is now
seconds behind the PC with no mechanism to catch up. **Frames now leave through a dedicated writer
thread, and a backlog the link cannot carry is discarded, not queued.** A mirror is a live view, not a
recording: a frame of a screen nobody is looking at any more is worth less than the bandwidth it costs.

**The second: that no packet is ever lost.** H.264 is unforgiving about this — one missing frame
corrupts every frame that references it — and the phone silently dropped packets whenever a decoder
input buffer was not immediately free, with nothing anywhere asking for repair. **Loss is now explicit
on both ends and both ends skip the same span:** whoever drops (the desktop shedding a backlog, the
phone failing to feed) discards everything until the next IDR and asks for one (`pc.mirror.keyframe`,
v19), so the picture resumes clean and current instead of smearing. The encoder's two-second GOP is the
floor under that, and covers a v18 desktop that has no such message.

**Rejected: adapting the bitrate to the link.** It is the textbook answer and it is a larger, stateful
mechanism that needs real measurement to tune honestly — and it does not remove either bug above, both
of which bite at any bitrate. The quality picker stays the user's lever for now. If the drop counter
in the log turns out to be busy at Smooth on a healthy link, that is the evidence that would justify
building it.

**Superseded 2026-09-25 by D-066** — the drop counter was busy, and the rejected alternative is built.

## D-066: The mirror sheds load before encoding, follows the link's bit rate, and never pads

**2026-09-25.** D-065 set its own test: "if the drop counter turns out to be busy, that is the evidence".
The owner's first real Wi-Fi session after it (2026-09-25, 21:30–22:02) logged **184 drops in 18 bursts**,
the worst **47 in 17 s** — always exactly 11 frames, every ~0.37 s. That signature is a loop, not noise:
a link carrying less than the stream fills the queue, the backlog is dropped, an IDR is requested (the
largest frame there is), sending it fills the queue again. Dropping was never going to catch up because
nothing asked the encoder to send less. Four changes, each measured in `mirrorsim`:

**1. The encoder never pads.** Measured on an idle screen at an 8 Mb/s target: **7.2 Mb/s on the wire,
half of it filler NALs (type 12)** — and the encoder's *default* mode was identical, so the mirror had
always done this. A screen nobody was touching kept Wi-Fi saturated full-time; every real change queued
behind padding. D-065's claim that CBR "keeps the stream inside the bandwidth" was wrong: it keeps the
stream *at* the bandwidth. Now peak-constrained VBR (mean = max = target) plus a **QP floor of 20**, which
stops VBR spending headroom re-refining an unchanged image: idle **0.55 Mb/s** (VBR alone: 3.35; QP 26:
0.38 but softer text). Low-delay VBR was tried and silently ignored by the AMD MFT.

**2. Load is shed before encoding, not after.** The capture loop asks the sender before each encode and
skips while two frames are still queued or on the wire. A frame never encoded breaks no reference, needs
no IDR, and costs only smoothness. Post-encode dropping remains only for a link that stops dead (>1.5 s).
The first controller dropped after encoding and, on a simulated 2.5 Mb/s link, **still dropped ~18
frames/s after cutting to the floor**; skip-before-encode dropped **zero**.

**3. The bit rate follows the link (AIMD).** Congestion = skip ratio >25% over a second, or send delay
(EWMA) >150 ms: ×0.75, at most every 1.5 s (the encoder takes ~1 s to settle — cutting every 0.7 s
overshot to the 600 kb/s floor on a link carrying four times that). Headroom = skip <5% and delay <60 ms
for 4 s after any cut: ×1.15 every 2 s, up to the phone's chosen ceiling. The AMD MFT honours live
changes (measured 6.7 → 1.53 Mb/s on motion). Result on the 2.5 Mb/s simulated link: steady target
2.15 Mb/s, 2.09 Mb/s delivered, 0 drops. Frame PTS is now wall-clock, so with skipped frames "N kb/s"
still means N per real second. IDRs are rationed to one per second, including the phone's v19 requests.

**4. No hidden buffers on either end.** The pc-video socket's send buffer is capped at 64 KB — Windows
otherwise auto-tunes to megabytes, which on a 3 Mb/s link is seconds of picture committed to being late
*and* makes every write return instantly, blinding the controller. 64 KB over a 20 ms RTT still allows
~26 Mb/s. On the phone, PC Remote holds a `WIFI_MODE_FULL_LOW_LATENCY` lock while visible: Wi-Fi power
save parks packets at the AP between beacons (~100 ms+), which is both stutter and a false congestion
signal. Android honours the lock only in the foreground with the screen on — exactly while mirroring.

**Measurement note.** Rate control cannot be tested on an idle screen: a correct VBR encoder sends a
fraction of any target, so "the output followed the target" is unfalsifiable. `mirrorsim` now shows a
window of scrolling noise (`Motion.cs`) during those scenarios. **Not yet verified on the owner's link.**

## D-067: PC Remote copies Windows App — the phone draws the pointer, and the session gets RDP's tools

**2026-10-09.** The owner asked for the mirror to be as smooth over Wi-Fi as Microsoft's **Windows App**
(the Remote Desktop client), and to take its features. Two routes were offered: use real RDP (Windows'
own server + FreeRDP on the phone), or port RDP's tricks into Linc's own stream. **The owner chose to
port.** Real RDP would be smoother still but locks the PC's own screen for the session and needs the
Windows password (a PIN will not do) — it stops being a mirror.

**What makes RDP feel instant is not its video.** It sends the pointer separately and lets the client
draw it, so the thing your eye tracks moves with your finger even while the picture is a few frames
behind. Linc's mirror had **no pointer at all**: Desktop Duplication never includes it in the captured
image. So the biggest change is that one:

1. **The phone draws the pointer.** The desktop samples `GetCursorInfo` at ~60 Hz on its own thread —
   not the duplication's pointer metadata, which only arrives alongside a captured frame, and D-066
   skips frames exactly when the link is congested — and sends position on change plus each distinct
   cursor image once (`pc.mirror.cursor`). The control channel carries it, so it is never queued behind
   video. While a finger is driving, the phone trusts its own position and ignores the PC's (older)
   reports for 400 ms; after that the PC is authoritative again (its own mouse, a dialog that warps it).
   Measured on this PC: arrow, I-beam and hand all render (32×32, 90–285 opaque px).
2. **Two pointer modes,** as Windows App has: Touch (pointer where you touch) and Mouse (a trackpad
   with acceleration). Right click is press-and-hold or a two-finger tap; two fingers scroll smoothly in
   raw wheel units (`precise`), or pan when zoomed in Touch mode. Mouse-mode dragging is hold-then-drag,
   not tap-then-drag: the latter either delays every click by a double-tap window or shows the PC a
   click followed by a press — a double-click-drag, which maximises a window grabbed by its title bar.
   The gesture engine is pure Kotlin (`PointerGestures`), 12 unit tests.
3. **A connection bar:** quality dot (Good/Fair/Poor from the PC's send delay and skip ratio, the
   phone's round trip and the frames actually shown), round trip, Mb/s, fps; pointer mode, keyboard,
   extra keys, sound, display, quality, fit, end. A picture that stops for 2 s gets a keyframe request;
   at 8 s the stream is restarted. A dropped stream keeps the last frame under "Reconnecting…" and the
   first retry after a drop is 1.5 s, not 5.
4. **Extra keys with sticky modifiers.** Ctrl/Alt/Shift/Win latch for one key (tap) or lock (tap
   twice) and apply to the phone keyboard too, so Ctrl then "c" on Gboard is Ctrl+C. Arrow/Delete/
   Home/End/Win are now sent as extended keys with scan codes — without the flag they arrive as their
   numpad twins to apps that read scan codes.
5. **The PC's sound plays on the phone** (pc-audio, channel 6). WASAPI loopback → Windows' in-box AAC
   encoder MFT → raw AAC-LC; MediaCodec + a low-latency AudioTrack on the phone. AAC because both ends
   ship a codec, so no library was taken on (the project already hand-rolls its Media Foundation
   interop). Measured on this PC: 48 kHz, ~126 kb/s on the wire, ~44 packets/s while sound plays.

**Not copied, deliberately.** RDP's dirty-region codecs: H.264 already spends almost nothing on
unchanged macroblocks, and D-066's VBR + QP floor put an idle screen at 0.55 Mb/s — there is little
left to win at the codec, and the perceived-latency win is the pointer. RDP's UDP transport with FEC is
the remaining large lever if real-link testing shows TCP head-of-line stalls; it is not built.

**Not yet verified on the owner's phone and Wi-Fi.** `mirrorsim` covers the desktop natives (pointer
capture, buttons/extended keys, audio capture + encode); the phone side is unit-tested and builds.

## D-068: "Remote PC (private)" blacks out the PC with capture-excluded windows, not RDP — **withdrawn 2026-10-10**

**2026-10-09.** After D-067 the owner asked for "a Windows App type of use — not mirror but control
without showing on screen": drive the PC from the phone while the PC's own monitor does not show it.
RDP does this by moving the user into a remote session, which is why D-067 turned it down (it needs
the Windows password and a full RDP client on the phone). The same *experience* is available inside
Linc's own stream:

- Each monitor gets a borderless topmost black window with `SetWindowDisplayAffinity(
  WDA_EXCLUDEFROMCAPTURE)`. The compositor shows it on the glass but leaves it out of every capture,
  Desktop Duplication included, so the phone sees the real desktop underneath. **Measured:** the
  curtain drawn in magenta, 0 of 49 sampled captured pixels were magenta (`mirrorsim`).
- It is layered + `WS_EX_TRANSPARENT` and answers `HTTRANSPARENT`, so the phone's injected clicks land
  on the windows below. Topmost is re-asserted every second.
- **Escape hatch:** a low-level keyboard hook lifts the curtain on a *physical* Esc (`LLKHF_INJECTED`
  tells the phone's keys apart) and tells the phone. Ctrl+Alt+Del works as always. The windows die with
  the process, so nothing can strand a PC black.
- **Lock on exit**, default on, as RDP leaves a PC: when the stream ends while private, the PC locks.
  A restart for a new display/quality keeps the curtain and does not lock; Linc exiting only restores.
- Limitation: higher-band shell surfaces (Start menu, toasts) still draw over the curtain when opened.

Same round, same owner request: the status pill became a draggable **bubble** (ring = link quality,
number = fps) opening a panel of stats tiles (ping, frame rate, speed, picture) and a 4-column grid of
controls; the extra-keys row became a **floating PC keyboard** — drawn by Linc, draggable and
resizable, sticky modifiers, Fn layer, multi-touch typing, repeat on ⌫/Del/arrows. Characters are sent
as Unicode text unless Ctrl/Alt/Win is latched, so symbols are right on any PC keyboard layout while
shortcuts (Ctrl+/, Ctrl+Shift+T) are real key chords. The phone's own keyboard stays available.

Not verified on the owner's phone yet.

**Withdrawn 2026-10-10** at the owner's request ("remove remote pc feature"), before any release: the
curtain, its v21 messages and the Home card are gone, and v21 was reused for D-070. The status bubble
and the floating keyboard from the same round stay (phone-only, no wire change) and are reworked in D-069.

## D-069: The bubble is Material 3 Expressive; the encoder was never ignoring its bit rate

**2026-10-10.** Three owner requests, one finding.

**1. Material 3 Expressive.** The status bubble and its panel are now Compose (`PcRemoteOverlay`) on
`MaterialExpressiveTheme`: the bubble is a `MaterialShapes.Cookie9Sided` whose icon is the connection
(full / half / broken Wi-Fi in green / amber / red — the owner asked for icons, not the fps number),
springing to the nearer edge with the theme's motion scheme and turning as it opens; the panel uses
connected `ToggleButton` groups (Touch | Mouse, Floating | Phone keyboard) and `TonalToggleButton`s.
**Version choice:** `material3 1.4.0` stable ships only the Expressive *tokens* — the components were
pulled before release; `1.5.0-beta01` has them but requires compileSdk 37 and an AGP newer than 8.7.
`1.4.0-alpha18` is the last release exposing the components at compileSdk 35 / AGP 8.6+, so it is pinned
(an alpha: its Expressive APIs are experimental). `material-icons-extended` grew the APK 26 → 61 MB for
~20 icons, so the 21 Material Symbols (Rounded, filled) used are inlined as path data (`LincSymbols`);
`material-icons-core` is added explicitly because 1.4 stopped pulling it in.

**2. Keyboard.** "Pop up on text fields" is a setting (panel switch), default on. The floating keyboard
gains a 🎤 key: the phone's own `SpeechRecognizer`, partial results shown on the keyboard, the final
text typed on the PC. `RECORD_AUDIO` is requested at first use, never up front.

**3. "Fix the encoder thing" — there was nothing to fix in the encoder.** After D-067 the AMD MFT
appeared to ignore live bit-rate cuts. Measured properly, one target per process: under the mirrorsim
motion pattern (per-pixel random noise) the encoder produced **~7.9 Mb/s at every target from 1.5 to
6 Mb/s, but 12.1 at 12 and 15.9 at 16** — it follows its target exactly; per-pixel noise simply cannot
be made smaller than ~8 Mb/s on this encoder. A rebuild-on-ignored-change scheme was built, worked
mechanically, and was **reverted**: rebuilding cannot make incompressible content smaller and costs an
IDR each time. What stays:
- **`IMFShutdown` on encoder dispose.** Disposing never released a pump thread parked in `GetEvent`, so
  every mirror session leaked a blocked thread. Found because the rebuild needed it.
- **A compressible motion pattern** (`Motion.Start(block)`, coarse noise blocks). With it, the D-066
  slow-link scenario passes on the unchanged D-066 code: 2.5 Mb/s link, steady target 1.54 Mb/s,
  1.64 Mb/s delivered, 0 drops. The mid-stream-change scenario still cannot pass on this encoder: the
  QP floor of 20 caps what any content asks for at high targets, while hard content cannot reach low
  ones, so no single pattern exercises both ends. It is a test-content limit, recorded rather than
  hidden.

## D-070: Windows Voice Access, with the phone as the PC's microphone

**2026-10-10.** Owner: "a tool under Tools to activate Voice Access on Windows and use the phone as mic
… also an option in the Mirror PC bubble." Protocol v21, pc-mic channel 7.

Windows only lets a **driver** create a microphone, and Voice Access listens to the default one. So the
phone's audio (16 kHz mono PCM — speech needs no more, 256 kb/s) is played into a **virtual audio
cable** (VB-CABLE, free; or any "Virtual … Input/Output" pair), and the cable's recording side is made
the default microphone for the session through `IPolicyConfig` — the undocumented but long-stable
interface Windows' own Sound settings use — and the user's own default is restored afterwards (and on
app exit). Linc does not ship or install a driver: without a cable, Voice Access still starts on the
PC's own microphone and the phone explains how to add one. *(Superseded 2026-10-10 by D-071: Linc now
sets the cable up itself.)* At most ~150 ms of audio is buffered; older
audio is dropped, because a voice command heard late is worse than one clipped.

Tested: the upsampler and cable-name matching (`mirrorsim`); the state messages and the v21 gate
(`PcVoiceAccessTest`). **Not** run here: switching the default microphone (it changes the owner's
system setting) and the end-to-end path (this PC has no cable installed).

## D-071: Linc sets up the virtual microphone itself — VB-CABLE, fetched from its maker, with consent

**2026-10-10.** Owner, on D-070 needing a manual VB-CABLE install: "the app can very well get some
drivers of that and do it itself." Supersedes D-070's "Linc does not ship or install a driver".

**The constraint.** Windows only lets a kernel driver create a microphone, and on a Secure-Boot
Windows 11 that driver must be Microsoft-signed (attestation or WHQL); test-signing is not something a
user-facing app can ask for. Candidates evaluated:

| Driver | Licence | Signed release? | Speaker → mic loopback? |
|---|---|---|---|
| VirtualDrivers/Virtual-Audio-Driver | MIT (+MS-PL SysVAD code) | **No** — README: "requires test signing"; no GitHub releases | **No** — stock SysVAD: the mic plays `ToneGenerator`, the speaker writes to a file (`SaveData`) |
| JannesP/AudioMirror | MIT | No — "isn't finished … You've been warned"; no releases | Yes |
| syams86/Virtual-Audio-Pipeline, amanison/OpenAudioCable | GPL-2.0 / GPL-3.0 | No | Yes / unclear |
| VB-Audio VB-CABLE | Donationware; redistribution needs a licence | **Yes** | Yes |

No open-source option installs on a normal PC. Signing our own (an AudioMirror fork) needs an EV
certificate and a Partner Center hardware account — only the owner can get those; it stays the path to
a fully self-owned driver later. Asked, the owner chose "do what most devs do": apps that turn a phone
into a PC microphone almost all rely on VB-CABLE.

**So:** Linc never bundles VB-CABLE. With the user's agreement — asked on the phone (Tools → Voice
Access, or the bubble) with a plain explanation, or from desktop Settings → Phone as microphone — the PC
downloads VB-Audio's own `VBCABLE_Driver_Pack45.zip` from `download.vb-audio.com`, **pinned by
SHA-256** (`B950E39F…16BFB`, 1,318,877 bytes, served with Last-Modified 2025-12-05 — VB-Audio
replaces packs in place, and a mismatch stops the install), extracts it under
`%LOCALAPPDATA%\Linc\drivers`, and **opens VB-Audio's own setup, unmodified and visible** (one UAC
prompt), where the user clicks *Install Driver* (or *Remove Driver*). Checked here without running
it: the zip tests clean, `VBCABLE_Setup_x64.exe` has a valid Authenticode signature (VB-Audio's EV
certificate) and the driver catalog is signed by *Microsoft Windows Hardware Compatibility
Publisher*, so it installs under Secure Boot. Pack 45 has no ARM64 setup; its x64 setup carries the
ARM64 driver.

**Licence — an open question for the owner.** The pack's readme allows copying it "AS IS without
any modification" but says "It is not allowed to integrate the VB-CABLE package in another software
installation procedure without Author agreement." That is why Linc does not bundle it, does not run
it silently (no unattended switches are documented anyway) and leaves the install to VB-Audio's own
window. Whether fetching and opening it still counts as integration is VB-Audio's call; asking them
(vb-audio.com contact) before wide distribution is recommended. It then waits up to 20 s for the CABLE endpoints and carries on
into the phone-mic session. `pc.voiceaccess.state` gains `setup` (`ready` / `needed` / `installing` /
`failed`) so the phone says "Setting up the microphone… approve the Windows prompt on the PC" instead
of sending the user to vb-audio.com. A cable already installed (VB-CABLE or any "Virtual … Input/
Output" pair) is used as before.

**The default microphone is always put back.** The user's own default is written to
`%LOCALAPPDATA%\Linc\voiceaccess-restore-mic.txt` before switching, restored when the session ends,
when Linc exits (Exit in the tray — the DI container is never disposed, so D-070's "on app exit" was in
fact not happening), and on the next start after a crash or kill.

Tested: package pin and setup selection (`mirrorsim` "D-071 virtual cable"), phone state parsing and
the old-PC fallback (`PcVoiceAccessTest`). **Not** run here: the install itself (it changes the
owner's drivers) — an owner check.

## D-072: The bubble "crash" was an ANR from a concave shadow; channels dial back by racing

**2026-10-10.** Owner: Mirror PC "slow to load, laggy, and tapping the bubble crashes the app and
drops the PC link".

**It was not a crash and not Compose version skew.** The suspected skew (material3 1.4.0-alpha18 on a
2024.12 BOM) does not exist at runtime: Gradle resolves every Compose module to the 1.8.1/1.8.2 line
that alpha18's own metadata requires. The phone's crash buffer was empty; the system log had
`am_anr … MirrorActivity is not responding. Waited 5000ms for MotionEvent`, then the kill that took the
companion service — and the PC link — with it. All four ANR dumps of the day (`dumpsys dropbox
data_app_anr`) show the main thread in `HardwareRenderer.syncAndDrawFrame` waiting on a RenderThread
inside `SkBaseShadowTessellator::computeConcaveShadow`, 17–36 s of CPU each: the bubble's
`Modifier.shadow(10.dp, Cookie9Sided)` asked Skia for an elevation shadow on a concave path, re-made
every frame it rotated or recomposed. That same render-thread burn (Linc at 87–117 % CPU) was the lag
the owner saw before ever tapping. **Fix:** the shadow is a circle under the cookie; Expressive
shapes clip, never cast shadows — a unit test fails on any `shadow`/`Elevation` next to
`MaterialShapes`.

**Slow load.** The desktop log showed the encoder ready 1.9 s after the request, the video channel up
at 7.1 s and PC sound 5.6 s after that. Each phone dial-back tried the PC's advertised addresses one
by one with a 4 s timeout, and this PC advertises a LAN address the phone (on the PC's hotspot) can't
reach. Now the phone races plain TCP probes to every candidate and does TLS once, to the first that
answers (`EndpointRace`). Only probes race: a second TLS connection that closed without its channel
header would look to the PC like a new control link.

Also: the phone's activity log is mirrored to logcat (tag `Linc`) so timings can be read over adb.
Unchanged: D-066 rate control, the 60 Hz cursor (cheap — a `View.invalidate()`), PC audio auto-start.

## D-073: A log anyone can copy, and staying connected in the background

**2026-10-10.** Owner: a friend's phone "connects and drops" over wireless; add a log the friend can
copy, and a fix.

The PC only declares a drop after ~30 s without a status reply (3 probes, 10 s reply timeout), so
these are real link deaths, not impatience. Without the friend's log the cause can't be proven; the
usual one is the phone's power saving: Linc never asked to be exempt from battery optimisation, so
with the screen off Android (and harder still, makers' battery managers) can freeze it.

- **Settings → Stay connected in the background** opens Android's own exemption prompt
  (`REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`; Linc is sideloaded, so Play's policy on it does not
  apply).
- While the companion service runs, `ConnectionWatch` holds a high-performance Wi-Fi lock (honoured
  up to Android 13) and logs screen off/on, Doze, battery saver and Wi-Fi lost/back. A Direct-TLS link
  that ends is logged with how long it lasted and the socket error.
- **Logs → Copy** (phone) builds a report: app version, phone model and Android version, connection
  state, background setting, exemption, battery saver/Doze, standby bucket, Android's recorded
  reasons for Linc's last process deaths (`getHistoricalProcessExitReasons`), and the log, which is
  now also kept in a ~256 KB file so it survives the app being killed. **Logs → Copy log** (PC)
  copies Linc's version, Windows and the last two days of the PC log.

## D-074: A hardware encoder is per adapter; a failed mirror start must give the screen back

**2026-10-17.** Owner pasted a PC log (1.0.0.7, Windows 26300; two Intel UHD adapters, one with the
display, and a Quick Sync H.264 MFT for each). Mirror PC never started: the first attempt failed
`START_OF_STREAM` with `0xC00D3E85` (`MF_E_HW_MFT_FAILED_START_STREAMING`), every retry after it
with `E_INVALIDARG`.

- **Encoder choice.** A hardware MFT only takes textures from its own adapter, named in
  `MFT_ENUM_ADAPTER_LUID`. `PcVideoEncoder` took MFTEnumEx's first hit, which on this PC can be the
  displayless adapter's. It now orders encoders on the capture device's adapter first
  (`PcScreenCapture.AdapterLuid`) and falls through to the next one when one won't start, logging
  each refusal. D-039 (hardware only) stands.
- **Cleanup on failure.** Nothing disposed the capture when the encoder threw, and DXGI allows one
  Desktop Duplication per output per process, so the leak *was* the `E_INVALIDARG` ("the calling
  application is already duplicating this desktop output"). `PcMirrorSource`, `PcScreenCapture` and
  `PcVideoEncoder` now release what they built before rethrowing.
- **Log honesty.** A drop with no failed probe no longer reports "Detection took -1 ms"; an empty
  WinRT Bluetooth error shows its HRESULT; "Phone connected directly" names the peer, to explain a
  burst of a dozen of them 100 ms apart while connected (cause not yet known — next log).
- **Harness debt paid (found by the CI session's first Linux run).** `PcVoiceAccess` and
  `VirtualCablePackage` resolved `LocalApplicationData` themselves (cleanroomsim); they now take
  `IDeviceRegistry.RootPath` at startup, so `--data-root` is honoured. `updatesim` check 7 had
  `1.0.0-beta.1` hard-coded as "shipped"; it now reads `<Version>` from the csproj and only requires
  `minimumSupported` not to exceed it. `QsConnection.SafeName` spells out Windows' invalid name
  characters instead of trusting the host OS's list.

Not verified on the owner's hardware yet: the encoder fallback needs a multi-adapter PC.

