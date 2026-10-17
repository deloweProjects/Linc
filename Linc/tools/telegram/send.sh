#!/usr/bin/env bash
# Send build/release files to a Telegram chat or channel through a bot.
#
# Setup (once):
#   1. Message @BotFather on Telegram, run /newbot, copy the token.
#   2. Add the bot to your channel as an admin (needs "Post messages").
#   3. Find the chat id: post anything in the channel, then open
#      https://api.telegram.org/bot<TOKEN>/getUpdates and read chat.id
#      (channels look like -100xxxxxxxxxx). A public channel can use @name.
#   4. Export both, or set them as environment secrets:
#        export TELEGRAM_BOT_TOKEN=123456:ABC...
#        export TELEGRAM_CHAT_ID=-1001234567890
#
# Usage:
#   send.sh [-m "caption/message"] file [file...]
#   send.sh -m "text only"
#
# The public Bot API caps bot uploads at 50 MB per file. A self-hosted Bot API
# server (https://github.com/tdlib/telegram-bot-api) raises that to 2000 MB;
# point the script at it with:
#   export TELEGRAM_API_URL=http://localhost:8081
# Before a bot first uses a local server, call logOut once on the public API:
#   curl https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/logOut
# TELEGRAM_MAX_MB overrides the size cap. Larger files are skipped with a
# warning.

set -euo pipefail

message=""

while getopts "m:h" opt; do
  case "$opt" in
    m) message="$OPTARG" ;;
    h) sed -n '2,27p' "$0"; exit 0 ;;
    *) exit 2 ;;
  esac
done
shift $((OPTIND - 1))

: "${TELEGRAM_BOT_TOKEN:?TELEGRAM_BOT_TOKEN is not set}"
: "${TELEGRAM_CHAT_ID:?TELEGRAM_CHAT_ID is not set}"

base="${TELEGRAM_API_URL:-https://api.telegram.org}"
base="${base%/}"
api="$base/bot${TELEGRAM_BOT_TOKEN}"
if [[ "$base" == "https://api.telegram.org" ]]; then default_mb=50; else default_mb=2000; fi
max_mb="${TELEGRAM_MAX_MB:-$default_mb}"
max_bytes=$((max_mb * 1024 * 1024))

if [[ $# -eq 0 && -z "$message" ]]; then
  echo "usage: $0 [-m message] file [file...]" >&2
  exit 2
fi

call() {
  local method="$1"; shift
  local out
  out=$(curl -sS --max-time 3600 "$api/$method" "$@")
  if [[ "$out" != *'"ok":true'* ]]; then
    echo "Telegram $method failed: $out" >&2
    return 1
  fi
}

if [[ $# -eq 0 ]]; then
  call sendMessage -F chat_id="$TELEGRAM_CHAT_ID" -F text="$message"
  echo "sent message"
  exit 0
fi

status=0
for f in "$@"; do
  if [[ ! -f "$f" ]]; then
    echo "skip: $f is not a file" >&2; status=1; continue
  fi
  size=$(stat -c %s "$f" 2>/dev/null || stat -f %z "$f")
  if (( size > max_bytes )); then
    echo "skip: $f is $((size / 1024 / 1024)) MB (limit is $max_mb MB)" >&2; status=1; continue
  fi
  sha=$( (sha256sum "$f" 2>/dev/null || shasum -a 256 "$f") | cut -d' ' -f1)
  caption="$(basename "$f")"$'\n'"SHA256: $sha"
  [[ -n "$message" ]] && caption="$message"$'\n\n'"$caption"
  if call sendDocument -F chat_id="$TELEGRAM_CHAT_ID" -F document=@"$f" -F caption="${caption:0:1024}"; then
    echo "sent $f"
  else
    status=1
  fi
done
exit $status
