#!/usr/bin/env bash
# Telegram uploader (Linux / macOS / Git Bash). See README.md in this folder.
#
# Usage:
#   ./send.sh [-m "message"] file [file...]
#   ./send.sh -m "text only"
#
# Settings come from environment variables, or from a .env file next to this
# script (copy .env.example to .env):
#   TELEGRAM_BOT_TOKEN   bot token from @BotFather            (required)
#   TELEGRAM_CHAT_ID     channel id (-100...) or @channelname (required)
#   TELEGRAM_API_URL     local Bot API server, e.g. http://localhost:8081
#                        (optional; default is the public api.telegram.org)
#   TELEGRAM_MAX_MB      size cap per file (default 50 public, 2000 local)
#
# Exit code is 0 only if every file was sent.

set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"

# Load .env without overriding variables that are already set.
if [[ -f "$here/.env" ]]; then
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    [[ -z "$line" || "$line" == \#* || "$line" != *=* ]] && continue
    key="${line%%=*}"; key="${key// /}"
    val="${line#*=}"; val="${val#\"}"; val="${val%\"}"
    [[ -z "${!key:-}" ]] && export "$key=$val"
  done < "$here/.env"
fi

message=""
while getopts "m:h" opt; do
  case "$opt" in
    m) message="$OPTARG" ;;
    h) sed -n '2,16p' "$0"; exit 0 ;;
    *) exit 2 ;;
  esac
done
shift $((OPTIND - 1))

if [[ $# -eq 0 && -z "$message" ]]; then
  echo "usage: $0 [-m message] file [file...]" >&2
  exit 2
fi

: "${TELEGRAM_BOT_TOKEN:?TELEGRAM_BOT_TOKEN is not set (see README.md)}"
: "${TELEGRAM_CHAT_ID:?TELEGRAM_CHAT_ID is not set (see README.md)}"

base="${TELEGRAM_API_URL:-https://api.telegram.org}"
base="${base%/}"
api="$base/bot${TELEGRAM_BOT_TOKEN}"
if [[ "$base" == "https://api.telegram.org" ]]; then default_mb=50; else default_mb=2000; fi
max_mb="${TELEGRAM_MAX_MB:-$default_mb}"
max_bytes=$((max_mb * 1024 * 1024))

call() {
  local method="$1"; shift
  local out
  out=$(curl -sS --max-time 3600 "$api/$method" "$@") || { echo "Telegram $method: request failed" >&2; return 1; }
  if [[ "$out" != *'"ok":true'* ]]; then
    echo "Telegram $method failed: $out" >&2
    return 1
  fi
}

if [[ $# -eq 0 ]]; then
  # Telegram caps a message at 4096 characters; send longer text in parts.
  # A UTF-8 locale makes the slicing count characters, not bytes.
  locale -a 2>/dev/null | grep -qix 'c.utf-\?8' && export LC_ALL=C.UTF-8
  rest="$message"
  while [[ -n "$rest" ]]; do
    call sendMessage --form-string chat_id="$TELEGRAM_CHAT_ID" --form-string text="${rest:0:4096}"
    rest="${rest:4096}"
  done
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
  echo "uploading $f ($((size / 1024 / 1024)) MB)..."
  if call sendDocument --form-string chat_id="$TELEGRAM_CHAT_ID" -F document=@"$f" --form-string caption="${caption:0:1024}"; then
    echo "sent $f"
  else
    status=1
  fi
done
exit $status
