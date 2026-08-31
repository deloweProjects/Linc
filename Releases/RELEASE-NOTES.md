# Linc release notes

## Where the binaries are

**Not in this repository.** Every build ships as an asset attached to the **GitHub Release for its
tag** — <https://github.com/deloweProjects/Linc/releases>. The desktop zip is ~171 MB and GitHub
hard-rejects tracked files over 100 MB, so `Releases/` holds only the notes, the checksums, and the
update manifest.

Verify what you download against [`CHECKSUMS.txt`](CHECKSUMS.txt) before running it.

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
