#!/usr/bin/env bash
# Starts a local Telegram Bot API server in Docker so send.sh can upload files
# up to 2000 MB (the public API stops at 50 MB). Needs Docker.
#
#   ./start-local-server.sh          start (or restart) the server
#   ./start-local-server.sh --stop   stop it and hand the bot back to the public API
#
# Reads TELEGRAM_BOT_TOKEN, TELEGRAM_API_ID and TELEGRAM_API_HASH from the
# environment or from .env next to this script.

set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
name=telegram-bot-api
port=8081

if [[ -f "$here/.env" ]]; then
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    [[ -z "$line" || "$line" == \#* || "$line" != *=* ]] && continue
    key="${line%%=*}"; key="${key// /}"
    val="${line#*=}"; val="${val#\"}"; val="${val%\"}"
    [[ -z "${!key:-}" ]] && export "$key=$val"
  done < "$here/.env"
fi

command -v docker >/dev/null || { echo "Docker is not installed; stay on the public API (50 MB limit)." >&2; exit 1; }

if [[ "${1:-}" == "--stop" ]]; then
  # close moves the bot off this server so the public API can be used again (after ~10 min).
  [[ -n "${TELEGRAM_BOT_TOKEN:-}" ]] && curl -sS "http://localhost:$port/bot$TELEGRAM_BOT_TOKEN/close" >/dev/null || true
  docker rm -f "$name" >/dev/null 2>&1 || true
  echo "Local Bot API server stopped. Remove TELEGRAM_API_URL from .env to use the public API."
  exit 0
fi

: "${TELEGRAM_BOT_TOKEN:?TELEGRAM_BOT_TOKEN is not set (see README.md)}"
: "${TELEGRAM_API_ID:?TELEGRAM_API_ID is not set (see README.md)}"
: "${TELEGRAM_API_HASH:?TELEGRAM_API_HASH is not set (see README.md)}"

# A bot must log out of the public server before a local server can use it.
# Harmless if it is already logged out.
curl -sS "https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/logOut" >/dev/null || true

docker rm -f "$name" >/dev/null 2>&1 || true
docker run -d --name "$name" --restart unless-stopped \
  -p "127.0.0.1:$port:8081" \
  -e TELEGRAM_API_ID -e TELEGRAM_API_HASH -e TELEGRAM_LOCAL=1 \
  -v telegram-bot-api-data:/var/lib/telegram-bot-api \
  aiogram/telegram-bot-api:latest >/dev/null

for _ in $(seq 30); do
  if [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$port/bot$TELEGRAM_BOT_TOKEN/getMe")" == 200 ]]; then
    echo "Local Bot API server is running on http://localhost:$port"
    echo "Make sure .env has: TELEGRAM_API_URL=http://localhost:$port"
    exit 0
  fi
  sleep 2
done
echo "Server did not come up. Check: docker logs $name" >&2
exit 1
