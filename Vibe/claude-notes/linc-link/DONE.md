# LincLink — build A: done, and what's verified

_2026-10-22, branch `claude/linc-link`. Design: `Vibe/agent/design/linclink.md` (§13 lists what changed
while building). Protocol: `Vibe/agent/opencode-docs/PROTOCOL.md` § v32._

## What was built (build A)

| Piece | PC | Phone |
|---|---|---|
| Resumable session (v32: seq/ack, replay, dedupe, keepalive) | `CompanionClient` | `SocketServer.serveLinkControl` / `LinkControl` |
| Lingering on a lost transport (12 s, nothing shown) + racing every route | `ConnectionSupervisor` (`OnTransportLost`, `DialPhoneAsync`, `SearchLoopAsync`) | presence dials while a session waits (`SocketServer.wantsPc`) |
| The PC dials the phone (TCP 46002, mutual TLS, outbound) | `TlsTransportService.DialPhoneAsync`, channels via `DialPhoneChannelAsync` | `LinkListener` |
| UDP beacon (46003): probes, replies, announcements | `LincLinkService` | `LinkBeaconService` |
| Per-network memory of the winning route | `LincLinkService` (`link-memory.json`) | pure code only (see linclink.md §13) |
| Link secret (keys the beacon) | `LinkSecretStore` (`link-secrets.json`), sent as `link.secret` | `LinkStore` (KeyStore AES box) |
| "Connected" + ADB chip; ADB-only = connected while the app is started | `LinkBrain.Presence`, Home's chip and caption | — |
| App repair over ADB (start, probe every 500 ms for 15 s, then the poll's backoff; every step's time logged) | `ConnectionManager.ReachCompanionAsync` | — |
| Cable seen at once (adb `track-devices`) | `UsbWatcherService.EnablePush` | — |
| Pairing without a cable (QR → `linc://pair`, both directions raced) | `OpenPairing`, `CompletePairingAsync`, pairing probes | `PairActivity`, `LinkPairingClient` |
| Always on | — | `BootReceiver` (boot, update), start on app open, Wi-Fi/screen announce |
| Firewall rule | installer task (private/domain, Windows asks once) | — |
| The switch | Settings › Connection › New connection engine (on) | Settings › Connection › New connection engine (on) |

Off (either side) means protocol v31 and exactly the old paths. A v31 phone or PC connects as before.

## What is verified, and where

- **Simulator** (`Linc/tools/linclinksim`, run by `build.yml`): the pure rules, and the PC's real
  `CompanionClient` against a fake v32 phone that follows the phone's code, over in-memory links cut
  mid-stream: 50 requests answered exactly once, 30 events delivered once and in order, a silent link
  found in ~8 s, a rebooted phone refused cleanly, v31 peers and the switch off unchanged. Beacon and
  pairing test vectors agree between C#, Kotlin and Python.
- **Phone**: built locally (`assembleDebug`) with all 253 unit tests green, including the vectors.
- **PC**: every C# file except the XAML views compiled locally on Linux; every Linux-runnable harness
  green (`applaunchsim` and `packagesim` need Windows, same as before these changes).
- **CI**: see "CI" below.
- **Review**: an adversarial review of build A (each finding checked by two skeptics) found real bugs,
  all fixed in `0a52842` and covered by new tests where a test can reach them. The main ones: a timed-out
  SMS could be re-sent by a resume; the session could be offered to another phone; the `linc://pair`
  link could be opened from a web page with no way to tell (now a check code on both screens, local
  addresses only); a 15 s app repair could freeze the PC's window on Disconnect; the phone did network
  writes under its session lock. linclink.md §13 lists them all.

## What is NOT verified (needs your hardware)

Nothing below has run on a real phone or PC. Each is written against the documented APIs and compiled,
but only your test can say it works:

- the phone's TLS listener with its KeyStore key, and Windows' TLS client presenting the PC's key (the
  PC dialling the phone);
- UDP broadcast on your Wi-Fi, and Windows letting the phone's reply in without the firewall rule;
- the boot receiver on your Android version, the camera opening the `linc://pair` link;
- the installer's firewall step (one Windows permission prompt);
- the cause of "USB fails the first time": not proven. The new path retries patiently and logs each
  step's time ("Linc on the phone answered N ms after…"), so the first log from your PC will show it.

## CI

_(filled in below once the runs finish)_

## How to switch engines

PC: Settings › Connection › New connection engine. Phone: Settings › Connection › New connection engine.
Either side off is enough to fall back to v31; turning it changes take effect at once (the link reconnects).

## Your test checklist (build A)

Phone and PC on the same Wi-Fi, Linc open on the PC. After each step, if something is off: PC Settings ›
Logs and phone Settings › Logs › Copy log, and send both.

1. **USB, first time.** Phone not running Linc (swipe it away), plug the cable in. Within a few seconds Home
   says "Connected over USB" (or briefly "Connected — starting Linc on your phone…") and the **ADB** dot is
   green. No "can't reach the Linc app".
2. **Cable out with Wi-Fi.** Unplug. Home should still say Connected (now "over Wi-Fi") within ~12 s and
   never "Lost the connection"; the ADB dot goes grey if wireless debugging is off.
3. **Same Wi-Fi, nothing else.** On the phone, open the Connection sheet (tap the PC card on Home), Stop Linc, then Start Linc. Connected in a
   few seconds with no cable.
4. **Phone restart.** Restart the phone, don't open anything. It should connect on its own once it's on Wi-Fi.
5. **Pair without a cable.** PC: Add a phone › Wi-Fi. Point the phone's camera at the top code (the Linc
   one) and tap the link. The phone shows six digits: they must match the ones under the code on the PC.
   Tap Pair. It should say Paired and connect.
6. **Blip.** While connected over Wi-Fi, turn the phone's Wi-Fi off for 3 seconds and back on. Nothing
   should show on the PC.
7. **Old way.** Turn the new engine off on the PC: it reconnects the way it always did.

## Paste back to the other session

> LincLink done on `claude/linc-link` @ <commit>. Build <green/red>. Read Vibe/claude-notes/linc-link/DONE.md.
