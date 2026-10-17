# Starts a local Telegram Bot API server in Docker so send.ps1 can upload files
# up to 2000 MB (the public API stops at 50 MB). Needs Docker Desktop running.
#
#   .\start-local-server.ps1          start (or restart) the server
#   .\start-local-server.ps1 -Stop    stop it and hand the bot back to the public API
#
# Reads TELEGRAM_BOT_TOKEN, TELEGRAM_API_ID and TELEGRAM_API_HASH from the
# environment or from .env next to this script.

param([switch]$Stop)

$ErrorActionPreference = "Continue"
$name = "telegram-bot-api"
$port = 8081

$envFile = Join-Path $PSScriptRoot ".env"
if (Test-Path $envFile) {
    foreach ($line in Get-Content $envFile) {
        $line = $line.Trim()
        if (-not $line -or $line.StartsWith("#") -or -not $line.Contains("=")) { continue }
        $key, $val = $line.Split("=", 2)
        $key = $key.Trim(); $val = $val.Trim().Trim('"')
        if (-not [Environment]::GetEnvironmentVariable($key)) {
            [Environment]::SetEnvironmentVariable($key, $val, "Process")
        }
    }
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error "Docker is not installed. Install Docker Desktop, or stay on the public API (50 MB limit)."
    exit 1
}

if ($Stop) {
    # close moves the bot off this server so the public API can be used again (after ~10 min).
    if ($env:TELEGRAM_BOT_TOKEN) {
        & curl.exe -sS "http://localhost:$port/bot$($env:TELEGRAM_BOT_TOKEN)/close" | Out-Null
    }
    & docker rm -f $name 2>$null | Out-Null
    Write-Host "Local Bot API server stopped. Remove TELEGRAM_API_URL from .env to use the public API."
    exit 0
}

foreach ($k in "TELEGRAM_BOT_TOKEN", "TELEGRAM_API_ID", "TELEGRAM_API_HASH") {
    if (-not [Environment]::GetEnvironmentVariable($k)) { Write-Error "$k is not set (see README.md)"; exit 2 }
}

# A bot must log out of the public server before a local server can use it.
# Harmless if it is already logged out.
& curl.exe -sS "https://api.telegram.org/bot$($env:TELEGRAM_BOT_TOKEN)/logOut" | Out-Null

& docker rm -f $name 2>$null | Out-Null
& docker run -d --name $name --restart unless-stopped `
    -p "127.0.0.1:${port}:8081" `
    -e TELEGRAM_API_ID -e TELEGRAM_API_HASH -e TELEGRAM_LOCAL=1 `
    -v telegram-bot-api-data:/var/lib/telegram-bot-api `
    aiogram/telegram-bot-api:latest | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "docker run failed"; exit 1 }

for ($i = 0; $i -lt 30; $i++) {
    $r = & curl.exe -s -o NUL -w "%{http_code}" "http://localhost:$port/bot$($env:TELEGRAM_BOT_TOKEN)/getMe"
    if ($r -eq "200") {
        Write-Host "Local Bot API server is running on http://localhost:$port"
        Write-Host "Make sure .env has: TELEGRAM_API_URL=http://localhost:$port"
        exit 0
    }
    Start-Sleep -Seconds 2
}
Write-Error "Server did not come up. Check: docker logs $name"
exit 1
