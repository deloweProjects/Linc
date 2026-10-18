# Notes for Claude sessions on this repo

Start with `Vibe/START-HERE.md`; it explains the project and how work is organised.

## "Send to Telegram"

When the owner says **"send to telegram"** (or similar), post to the Linc Telegram channel
(t.me/LincApp) through the repository's own GitHub Actions workflows. Never ask for a bot token:
the credentials are repository secrets (`TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID`,
`TELEGRAM_API_ID`, `TELEGRAM_API_HASH`) and only the workflows use them.

- **Text**: dispatch `.github/workflows/telegram-message.yml` ("Send text to Telegram") on `main`
  with input `text`. Over 4096 characters is split automatically.
- **Release files**: `.github/workflows/telegram-upload.yml` ("Upload release to Telegram") runs
  by itself when a GitHub release is **published**, and posts that release's assets with a link.
  To (re)send one by hand, dispatch it on `main` with inputs `tag` (empty = latest release) and
  an optional `message`.

From a cloud session, use the GitHub MCP tool `actions_run_trigger` (method `run_workflow`,
workflow file name as the id, ref `main`). Then confirm the run finished green, and report a
failure with its log rather than saying it was sent. Details: `telegram-uploader/README.md`.

## Releases

- From a cloud session: dispatch `.github/workflows/release.yml` ("Release") on `main` with
  `version` (next after the newest GitHub tag) and `display_name`. It runs `release.ps1` on a
  Windows runner, publishes, and then dispatches the Telegram upload itself. Write the version's
  section in `Releases/RELEASE-NOTES.md` first, or `release.ps1` stops.
- Releases are cut by `Linc/tools/release.ps1`, on the owner's Windows PC or by `release.yml`. It builds the APK, the
  desktop publish, the Inno Setup installer and the zip, writes `Releases/CHECKSUMS.txt` and
  `Releases/update.json`, and with `-Publish` tags and uploads the GitHub release, which then
  posts itself to Telegram (above).
- **APK signature.** Released APKs are debug builds signed with the owner's debug key, and Android
  only installs an update signed with the same key. `release.yml` restores that key from the
  `ANDROID_DEBUG_KEYSTORE` secret, and stops without it unless `allow_new_apk_key` is ticked.
- Check the newest tag on GitHub before choosing a version number. Releases have been cut from
  `feature/quick-share-and-recognition`, ahead of `main`, so the version in `main` can lag.
