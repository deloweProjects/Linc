# Telegram uploader

Posts Linc's release files (the `.exe`, `.apk`, etc.) to a Telegram channel, with
each file's SHA256 in the caption. It works three ways:

| Where | How | Max file size |
|---|---|---|
| **GitHub** (automatic) | Publish a release → the workflow posts its files | 2000 MB |
| **Your PC** (or your local agent) | `send.cmd file.exe` | 50 MB (2000 MB with the local server) |
| **Linux / macOS / Git Bash** | `./send.sh file.apk` | same |

## What's in this folder

| File | What it does |
|---|---|
| `send.cmd` / `send.ps1` | Windows: upload files or post a message |
| `send.sh` | Linux, macOS, Git Bash: same thing |
| `start-local-server.cmd` / `.ps1` / `.sh` | Optional: runs a local Bot API server in Docker so the PC can send files over 50 MB |
| `.env.example` | Template for your settings. Copy it to `.env` |
| `../.github/workflows/telegram-upload.yml` | The GitHub workflow |

---

## One-time setup

### 1. Create the bot and get the channel id
1. In Telegram, open **@BotFather**, send `/newbot`, follow the prompts, copy the **token**.
2. Open your channel → Administrators → Add admin → pick your bot → allow **Post messages**.
3. Post any message in the channel, then open this in a browser (put your token in):
   `https://api.telegram.org/bot<TOKEN>/getUpdates`
   Find `"chat":{"id":-100...` — that number is your **chat id**.
   (For a public channel you can use `@channelname` instead.)

### 2. Get an API id and hash (needed for files over 50 MB)
Go to **https://my.telegram.org** → log in with your phone → **API development tools** →
create an app (any name) → copy **api_id** and **api_hash**.

### 3. GitHub: add four repository secrets
Repo on GitHub → **Settings → Secrets and variables → Actions → New repository secret**:

| Name | Value |
|---|---|
| `TELEGRAM_BOT_TOKEN` | the bot token |
| `TELEGRAM_CHAT_ID` | the chat id (e.g. `-1001234567890`) |
| `TELEGRAM_API_ID` | api_id |
| `TELEGRAM_API_HASH` | api_hash |

### 4. PC: create `.env`
Copy `.env.example` to `.env` in this folder and fill in the same values.
`.env` is git-ignored, so it never gets committed.

---

## Using it

### GitHub
- **Automatic:** publish a release (with files attached) → the files land in the channel.
- **By hand:** Actions tab → **Upload release to Telegram** → **Run workflow**.
  Leave "tag" empty for the latest release.

### PC (you or your local agent)
```bat
telegram-uploader\send.cmd path\to\Linc-Setup.exe path\to\linc.apk
telegram-uploader\send.cmd -Message "Linc 1.0.0-beta.2" path\to\Linc-Setup.exe
telegram-uploader\send.cmd -Message "Build finished"
```
It prints `sent <file>` for each file and returns exit code 0 only when everything was
sent, so an agent can check the result.

### Files over 50 MB on the PC
Needs [Docker Desktop](https://www.docker.com/products/docker-desktop/) running.
```bat
telegram-uploader\start-local-server.cmd
```
Then uncomment `TELEGRAM_API_URL=http://localhost:8081` in `.env`. Uploads now go up to
2000 MB. To go back to normal: `start-local-server.cmd -Stop` and comment the line out again.

---

## Good to know
- The bot can only be on **one server at a time**. Moving it between the public API and a
  local server takes a moment, and after leaving the public API it **can't return to it for
  about 10 minutes**. In practice: right after the GitHub workflow runs, give it 10 minutes
  before a normal (no local server) upload from the PC.
- Don't run the PC local server and the GitHub workflow at the same time.
- Never commit `.env` or paste the token anywhere public. If it leaks, send `/revoke` to
  @BotFather to get a new one.

## Troubleshooting
| Message | Fix |
|---|---|
| `TELEGRAM_BOT_TOKEN is not set` | Create `.env` (PC) or the repo secret (GitHub) |
| `chat not found` | Wrong chat id, or the bot isn't an admin of the channel |
| `Unauthorized` | Wrong token, or it was revoked |
| `Logged out` / `Bad Request: logged out` | The bot just left the public API: wait 10 minutes or use the local server |
| `skip: ... limit is 50 MB` | Use the local server, or let the GitHub workflow send it |
| `Local Bot API server did not start` | Check `TELEGRAM_API_ID` / `TELEGRAM_API_HASH` |
