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
  mid-stream: every request answered exactly once, 30 events delivered once and in order, a silent link
  found in ~8 s, a rebooted phone refused cleanly, a request that timed out never run later by a resume,
  another phone unable to take the session over, a route taking over while the old one is still up, a link
  lost before it was installed failing at once, v31 peers and the switch off unchanged. Beacon, pairing and
  check-code test vectors agree between C#, Kotlin and Python.
- **Phone**: built locally (`assembleDebug`) with all 258 unit tests green, including the vectors.
- **USB push**: the PC's `host:track-devices` loop run against a real adb server (connect, kill, restart).
- **PC**: every C# file except the XAML views compiled locally on Linux; every Linux-runnable harness
  green (`applaunchsim` and `packagesim` need Windows, same as before these changes).
- **CI**: see "CI" below.
- **Review**: an adversarial review of build A (each finding checked by two skeptics) found real bugs,
  all fixed in `0a52842` and covered by new tests where a test can reach them. The main ones: a timed-out
  SMS could be re-sent by a resume; the session could be offered to another phone; the `linc://pair`
  link could be opened from a web page with no way to tell (now a check code on both screens, local
  addresses only); a 15 s app repair could freeze the PC's window on Disconnect; the phone did network
  writes under its session lock.
- **Second review**, of those fixes (same method): a cable plugged in over Wi-Fi could be taken for a lost
  link and bounce between routes; closing the pairing screen could cut a just-paired phone's channels with
  background connections off; the phone's new handshake cap turned away the PC's own burst of thumbnail
  dials. All fixed in `a157459`, with smaller ones (a replayed beacon, a leaked probe socket, a lost boot
  announcement, a double-tapped pairing link). Also fixed after reading the library's source: its adb device
  monitor spins on a closed server connection, so the PC now speaks `host:track-devices` itself; and the
  installer's firewall rule has its own name. linclink.md §13 lists everything, plus the one thing left: a
  phone paired only by QR code starts a fresh session (pages reconnect once) on its first cable connection.

## Field test of build A (2026-10-23, Pixel 7 + Windows 11): what went wrong, and the fixes

What worked on your hardware: re-pairing after a reinstall, the PC dialling the phone on 46002 (32–641 ms),
beacon replies, sessions carrying on across routes ("nothing lost"), the first USB start (764 ms).

What didn't, and what changed (each rule checked in `linclinksim`, "the field test of build A"):

1. **Stuck in a 5 s loop ("Read timed out") for ten minutes.** Two things added up past the phone's 5 s wait
   for the PC's hello: channel dial-back waiters left behind by a failed request (every phone dial-in first
   waited 3 s for a header), and the supervisor's gate held by a 15 s app repair over a flapping cable. Now a
   waiter ends by itself (its caller giving up, 10 s at most, or its link ending; `DialBackWaiters`), the
   header wait is 1.5 s, and with the new engine a direct link waits at most 1 s for the gate before the
   attempt holding it is called off. Worst case before the PC's hello: 3.5 s.
2. **USB flapping after "fixed port 5555".** `adb tcpip 5555` restarts the phone's adb, which cut USB and
   wireless debugging. Never done with the new engine, and never while any ADB route is in use.
3. **"Install Linc?" while Linc was running.** An adb that dropped mid-check read as "not installed". Now only a
   clean answer from the phone's package manager counts; anything else offers nothing.
4. **Old words.** "…tap 'Start companion service'" is now "Open Linc on the phone (if it says it's stopped, tap
   the PC card, then Start Linc)"; the log's "The the USB cable under the link ended" reads properly.
5. **A forgotten address retried all night** (every 66 s, wireless debugging off on that network). It now waits
   30 min, or until the PC's network changes.

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

All on GitHub Actions, `build.yml` and the UI-test branches:

- **Build** green for the final code, `a157459`, on `claude/linc-link` and `ui-test/linc-link-a6`: the PC
  app, every simulator (`linclinksim` included), the installer compile (with the new firewall step), the
  phone APK and its 258 unit tests.
- **UI test** green on `ui-test/linc-link-a6` (`a157459`) and `a4`: 31 phone screenshots, 25 PC screenshots,
  the fake phone and fake PC answering ~135 messages, as before LincLink. The PC's Home shows "Connected over
  Wi-Fi" with the ADB chip; the phone's Settings shows the new engine switch on two lines; the phone's log
  shows "LincLink: waiting for the PC to dial this phone (port 46002)". The fake phone (v29) and fake PC (v30)
  are older peers, so this shows old peers still connect; v32 itself is proven by the simulator, not here.
- **Two CI flakes, neither Linc's** (both also happen on the commit before LincLink, `db7de01`, run on
  `ui-test/linclink-base-check-1` to check): the emulator's own launcher hanging ("Pixel Launcher isn't
  responding", runs `a3`, `a5`: only 5 phone screenshots), and `applaunchsim` failing on the Windows runner
  ("The handle is invalid", once on a docs-only commit).

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
8. **Cable and Wi-Fi together, ten minutes** (the field-test case). Phone on the cable and the same Wi-Fi, leave
   it. Home stays Connected; the PC's log has no "fixed port 5555" line and no endless "Read timed out" on the
   phone; USB doesn't come and go.
9. **No false install.** With Linc installed on the phone, the PC never asks to install it, even while the
   cable is plugged and unplugged.

## Paste back to the other session

> LincLink done on `claude/linc-link` @ <commit>. Build green. Read Vibe/claude-notes/linc-link/DONE.md.
