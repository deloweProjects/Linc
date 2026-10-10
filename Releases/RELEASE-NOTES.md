# Linc release notes

## Where the binaries are

**Not in this repository.** Every build ships as an asset attached to the **GitHub Release for its
tag** — <https://github.com/deloweProjects/Linc/releases>. The desktop zip is ~171 MB and GitHub
hard-rejects tracked files over 100 MB, so `Releases/` holds only the notes, the checksums, and the
update manifest.

Verify what you download against [`CHECKSUMS.txt`](CHECKSUMS.txt) before running it.

**Version numbers.** From 1.0.0.2 on, the version both apps report and `update.json` compares is
numeric and four-part (`1.0.0.2`, `1.0.0.3`, …) and strictly ordered. Names like "1.0.0-beta.2" are
display names only — the update check ignores anything after a `-`.

---

## 1.0.0.10 ("1.0.0-beta.10") — 2026-10-11

(1.0.0.3 to 1.0.0.9 were local test builds and were never released.) Install both apps (protocol v23).

- **Adding and removing phones flows properly.** Closing a phone's tab asks whether to disconnect
  (it stays paired) or remove it. "Add a phone" lets you pick a phone you've used before, or add a new
  one by USB, Wi-Fi QR code, or "already set up"; it checks the connection and tells you whether the
  phone was new or already added.

- **First setup and adding a phone are reliable**: Linc restarts its connection engine if it has
  stopped, recognises a phone paired over Wi-Fi as wireless (not a USB cable it then thinks was
  pulled), and no longer cuts the brand-new connection straight after setup.
- **One phone stays one phone**: its saved apps, photos and messages no longer split into several
  copies depending on how it connected.

- **Mirror PC on Intel graphics**: encoders that won't take the screen's pixels directly get a picture
  converted on the GPU; a failed start no longer blocks every retry.
- **Staying connected**: Home tells you when Android may stop Linc in the background, with one tap to
  allow it (and the phone maker's own auto-start screen on itel, Tecno, Infinix, Xiaomi, Oppo, Vivo).

- **Mirror PC on PCs with two graphics chips** (e.g. Intel + NVIDIA laptops): Linc now tries every
  hardware video encoder, the screen's own chip first, instead of giving up after the first.
- **Over a direct Wi-Fi link**, fetching many app icons at once no longer stalls.

- **Voice Access now actually hears your phone.** It keeps its own microphone setting and ignored the
  Windows default; Linc now points it at the phone while you've chosen the phone.
- **Choose Phone or PC microphone** in the PC Remote bubble, or on the Voice Access card.
- **Microphone tool** (Tools): use your phone as the PC's microphone in any app — calls, dictation.
- **Automatic in Mirror PC**: with Phone chosen, whenever an app on the PC starts listening, it hears
  your phone. The phone's mic only runs while something is listening.

- **Voice Access's microphone setup now finishes.** The download from vb-audio.com used to give up
  after 5 minutes on that server's slow connection; it now downloads in pieces, picks up where it
  stopped, and shows its progress on the phone. A copy you downloaded yourself into Downloads works too.
- The app icon's L is smaller.

Install both apps (protocol v22).

- **Tablet mode** in the PC Remote bubble makes a PC without a touch screen finger-friendly: everything
  on the mirrored screen gets bigger (two scale steps, e.g. 125 % → 175 %) and File Explorer gets touch
  spacing. No Windows prompt; it is all put back when you switch it off or leave PC Remote.
- **The app icon is the right size** (it was drawn too big and cropped) and follows themed icons.
- **Material You everywhere**: the bubble and panel take the phone's wallpaper colours, and the app
  follows dark mode.

---

## 1.0.0.2 ("1.0.0-beta.2") — 2026-10-10

Everything since beta.1 that was only in local test builds, plus this round's fixes. Install both
apps (protocol v21).

**PC Remote (Mirror PC), Windows-App style** — D-066…D-070

- Wi-Fi rate control: the PC follows the link's real bit rate and sheds frames before encoding
  instead of queueing them (D-066).
- The PC's pointer is drawn on the phone, so it moves the moment your finger does; Touch and Mouse
  (trackpad) modes, press-and-hold / two-finger tap for right click, smooth two-finger scroll,
  link stats and ping, and the PC's sound on the phone (D-067).
- A Material 3 Expressive status bubble and panel, a floating PC keyboard with voice typing, and
  the keyboard popping up on PC text fields (D-069). The "private session" mode was withdrawn (D-068).
- Windows Voice Access from the phone, hearing through the phone's microphone (D-070).

**This round** — D-071, D-072

- **Tapping the bubble no longer freezes and kills Linc** (and drops the PC link). It was an
  "app not responding": the bubble's shadow followed its cookie shape, which Android's renderer
  takes seconds per frame to draw. That was also the lag before the tap.
- **Mirror PC starts faster**: the phone no longer waits out a 4-second timeout on each PC address
  it can't reach when opening the picture and sound channels.
- **Voice Access sets up the phone-as-microphone itself.** After you agree on the phone, the PC
  downloads VB-CABLE (VB-Audio's free virtual audio cable) from vb-audio.com, checks it against a
  pinned SHA-256, and opens VB-Audio's own installer: approve Windows' prompt, click Install Driver. Remove it from desktop
  Settings → Phone as microphone. Your own default microphone is put back after each session, on
  Exit, and after a crash.
- **Wi-Fi link dropping**: Settings → "Stay connected in the background" on the phone stops Android
  freezing Linc when the screen is off — the usual cause. Linc also keeps Wi-Fi awake while connected.
- **Copy log** on the Logs page of both apps: paste it to whoever is helping you. The phone's
  includes why Android ended Linc recently and survives restarts.

---

## 1.0.0-beta.1 — reissued 2026-09-20

**The beta.1 assets were rebuilt and replaced in place.** Same version number, different binaries:
this build carries the reverse-mirror wireless fixes (protocol v19, D-065) — the mirror no longer
freezes or falls behind on a Wi-Fi link, recovers from lost packets instead of smearing, and no
longer fights its own retries while starting. See the changelog for the full list.

Replacing rather than reissuing as beta.2 was deliberate: the original assets had **zero downloads**,
so nobody held the old files, and `UpdateDecision` parses `1.0.0-beta.1` and `1.0.0-beta.2`
identically (both to `1.0.0`) — a beta.2 would have been invisible to every installed copy until the
numeric version scheme queued in `Vibe/agent/tasks/M20.md` lands. `CHECKSUMS.txt` and
`update.json` were updated in the same change; **the hashes published before 2026-09-20 no longer
match anything** and any copy of them should be discarded.

`latest` and `minimumSupported` are unchanged, so no installed copy is prompted.

---

## 1.0.0-beta.1 — first public beta

The first build of Linc that someone other than its author can install. Everything below has been
exercised against a real phone and PC; see `Vibe/agent/reports/` for the per-milestone evidence.

**What works**

- **File manager** — browse, transfer both ways, delete/rename/mkdir, screenshot.
- **Two-way mirroring** — the phone on the PC (via a vendored, branded scrcpy), and the PC on the
  phone as a reverse mirror with touch, pinch-zoom and keyboard input.
- **Per-app windows** and **Desktop Mode** for apps that support it.
- **Clipboard sync**, **notifications** (with optional on-disk history, off by default),
  **media remote**, **messages and calls**.
- **Offline device cache**, so the app is useful with the phone away.
- **Hotspot link-up in ~500 ms** over ADB, plus a direct TLS channel when ADB is not available.
- **Multiple paired phones**, one active at a time.

**What is rough** — the beta gate, honestly (source: `Vibe/agent/reports/M18.md`)

- The APK is **debug-signed**. There is no release keystore yet, so Android will warn on install and
  an eventual properly-signed build will not upgrade over it — you will have to uninstall first.
- The desktop build is **Debug-configuration WinUI**; it is large and starts slower than it should.
- Reverse-mirror input has been verified by hand, not by automation — `adb input tap` cannot drive
  the mirror surface, so there is no regression test behind it.
- `Linc/Documentation/`'s eleven chapters are **stale by more than a dozen milestones** and
  banner-flagged as such. `Vibe/agent/opencode-docs/` is the current picture.
- No CI. Every build in this repo was produced and verified on one machine.

---

## How releasing works

`Releases/update.json` **is** the release mechanism. Both apps read it from this repo's raw URL,
which is compiled into each build as a constant rather than being a user-visible setting.

```json
{
  "latest": "1.0.0-beta.2",
  "minimumSupported": "1.0.0-beta.1",
  "notes": "One short line shown to the user.",
  "desktop": { "url": "https://github.com/.../Linc-Desktop-1.0.0-beta.2.zip", "sha256": "..." },
  "android": { "url": "https://github.com/.../Linc-Companion-1.0.0-beta.2.apk", "sha256": "..." }
}
```

| Field | What it does |
| --- | --- |
| `latest` | The newest version. Any build below it is offered an **optional** update it can dismiss or skip. |
| `minimumSupported` | **Raising this forces the update** for every build below it, and a user cannot skip past it. |
| `notes` | One short line, shown to the user verbatim. |
| `desktop` / `android` | Where to download, and the SHA256 the download must match. |

To ship a release: publish the artefacts as a GitHub Release for the tag, then **edit
`Releases/update.json` and commit it.** That commit is the release.

Two safety rules are built into both apps and enforced by `Linc/tools/updatesim`:

- A manifest that is unreachable, malformed, or full of garbage is treated as **no update** — never
  as a forced one. A typo here cannot brick installed copies.
- A download whose SHA256 does not match is **refused, not run**, and the user is told plainly.

Users can turn the whole thing off: **Settings → Updates → "Keep Linc up to date"**, which is on by
default. Off means no checks at all, ever, and the app points at the Releases page instead.
