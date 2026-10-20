# Showing that Linc is the owner's work

The owner asked for a way to tie the code to them, "even if someone has the source code". There are
two parts:
- (A) the **open fingerprint** added in 1.0.0.17;
- (B) the **traits already built into the code**, which a copier carries along without noticing.

Both are honest and visible. Nothing is hidden or obfuscated, nothing phones home, and there is no
kill switch. Keep it that way: hidden watermarks or tracking would make Linc look like malware and
would undermine the claim.

Line numbers were checked on 2026-10-20 and will drift; search for the names.

## A. The open fingerprint (1.0.0.17)

- **Provenance files:**
  - `Linc/ANDROID/app/src/main/java/app/linc/android/Provenance.kt`
  - `Linc/DESKTOP/Linc.Desktop/Provenance.cs`
- **Values in those files:**
  - Author: "deloweProjects"
  - Source: https://github.com/deloweProjects/Linc
  - Copyright: © 2026 deloweProjects
  - **Fingerprint** = SHA-256 of `Linc by deloweProjects — https://github.com/deloweProjects/Linc`,
    which is `93b660d11f6717cfe67f72a92881210977685b4954a673cadf67e2e11358f281`.
  - Anyone can recompute it; `NOTICE` shows how.
- **Where it shows up:**
  - The first log line of every run: "Linc <version> by deloweProjects — <source>", seen in CI logs.
  - Copied and exported logs begin with it.
  - Both `hello` messages carry `author: "deloweProjects"`.
  - The Android manifest has `<meta-data android:name="app.linc.author">`.
  - The desktop csproj has Company, Authors and Copyright set.
  - About on both apps.
  - `NOTICE` at the repo root.
- **Licence:**
  - `LICENSE` is MIT. Anyone may reuse the code but **must keep the copyright notice**; removing it
    breaks the licence.
  - If the owner wants stronger terms (credit in the UI, no rebranded copies), that means choosing a
    different licence. Doing it early is easier.

## B. Traits of the code's structure (what a copy keeps without noticing)

1. **The protocol is specific to Linc.**
   - Message names exist on both sides: `audio.everywhere.set`, `pc.btaudio.set/get`,
     `sync.pairs.set` (`Envelope.cs` ~113/151/158, `Protocol.kt` ~105/147).
   - `PROTOCOL_VERSION = 31`, with a version history v0–v31 in `PROTOCOL.md`.
2. **The clock probe.**
   - Magic `0x4C434C4B` ("LCLK") in `PhoneSource.kt` ~66.
   - A 16-byte probe and a 24-byte (t0/t1/t2) reply in `PcAudioStreamer.cs` ~68.
   - Documented in `PROTOCOL.md` ~632.
3. **The channel map** in `SocketServer.kt` ~823: files 2, bulk 3, video 5, audio 6, mic 7,
   phone-audio 8, terminal 9, pc-files 10. **Ports:** reverse 46011 (`TlsTransportService.cs` ~67),
   TLS 46001.
4. **Names.**
   - Mutex `Local\LincDesktopSingleInstance` (`App.xaml.cs` ~246; checked by
     `tools/cleanroomsim`).
   - Prefs `"linc"` with keys `wallpaper_background`, `floating_nav`, `app_font`.
   - Intent actions `app.linc.android.action.PHONE_SOURCE_START/STOP`, `.MIC`, `.PC_MEDIA`.
   - Notification channels `linc.share.progress`, `linc.pc.requests`.
   - Folders `Download/Linc`, `Pictures\Linc\Shared`.
5. **The design trail.**
   - About 650 `D-0xx` decision references in the code. They map to
     `Vibe/agent/opencode-docs/DECISIONS.md`: 97 dated entries, D-001 on 2026-07-07, up to D-097.
   - Milestone tags in comments (M12g, B2.4, …).
   - 67 comments that quote "the owner" with dates.
   - The Vibe master/agent planning system, with 55 task files.
   - `OWNER-CONCERNS-2026-10-19.md`.
   - A copy that keeps these comments points straight back to this repository's history.
6. **Test harnesses.** 35+ tools in `Linc/tools/` (`audiosyncsim`, `fingerprintsim`, `cleanroomsim`,
   `packagesim`, `syncsim`, `uishots` + `fakepc.py`). Each names its decision ID and scenario.
7. **Git and release history.**
   - First commit `e7262ed`, 2026-08-31, by delowe ("Linc 1.0.0-beta.1 - first public release").
   - Tags from v1.0.0-beta.1 to v1.0.0.17.
   - The GitHub releases and Telegram posts form a dated public trail.
8. **The APK signing key. This is the strongest proof.**
   - Every released APK carries certificate SHA-256
     `8182d31662613b85f418e3d8601997ff957d5b7104a1ddd25ad7eed9ecd9d969`, enforced in `release.yml`
     and `test-build.yml`.
   - Only the holder of that key can ship an APK that installs as an update over Linc. A copy cannot
     match it.

## C. Further steps, all open (the owner can pick)

1. **Sign commits and tags.**
   - Right now the tags are lightweight, and only 6 commits are SSH-signed.
   - Set up `git config commit.gpgsign true`, use `git tag -s` for releases, add the key to GitHub,
     and publish an `allowed_signers` file.
2. **Guard the APK key.**
   - Keep the keystore offline and backed up; it should otherwise exist only in the
     `ANDROID_DEBUG_KEYSTORE` secret.
   - Move to Play App Signing with an upload key.
   - Publish the certificate fingerprint in the README and NOTICE.
3. **Sign the checksums** (minisign, GPG or `ssh-keygen -Y sign`) and attach the signature to each
   release. Also put the hashes in the Telegram post.
4. **Independent timestamps.** OpenTimestamps on release tags, or a Software Heritage archive of the
   repo; both record dates no one controls.
5. **A documented canary sentence.** Put a distinctive sentence in NOTICE or DECISIONS.md and list it
   in NOTICE as a marker of origin. Text in the source and docs only, never anything covert in the
   binaries.

Build outputs (`Linc/ANDROID/app/build/`, an APK included) are gitignored. Keep it that way.
