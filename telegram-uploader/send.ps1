# Telegram uploader (Windows PowerShell 5.1+ / PowerShell 7). See README.md in this folder.
#
# Usage:
#   .\send.ps1 [-Message "message"] file [file...]
#   .\send.ps1 -Message "text only"
#
# Settings come from environment variables, or from a .env file next to this
# script (copy .env.example to .env). Same variables as send.sh:
#   TELEGRAM_BOT_TOKEN, TELEGRAM_CHAT_ID, TELEGRAM_API_URL, TELEGRAM_MAX_MB
#
# Uses curl.exe, which ships with Windows 10 1803 and later.
# Exit code is 0 only if every file was sent.

param(
    [Alias("m")][string]$Message = "",
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Files = @()
)

$ErrorActionPreference = "Continue"

# Load .env without overriding variables that are already set.
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

if ($Files.Count -eq 0 -and -not $Message) {
    Write-Error "usage: send.ps1 [-Message text] file [file...]"
    exit 2
}

$token = $env:TELEGRAM_BOT_TOKEN
$chat = $env:TELEGRAM_CHAT_ID
if (-not $token) { Write-Error "TELEGRAM_BOT_TOKEN is not set (see README.md)"; exit 2 }
if (-not $chat) { Write-Error "TELEGRAM_CHAT_ID is not set (see README.md)"; exit 2 }

$base = if ($env:TELEGRAM_API_URL) { $env:TELEGRAM_API_URL.TrimEnd("/") } else { "https://api.telegram.org" }
$api = "$base/bot$token"
$defaultMb = if ($base -eq "https://api.telegram.org") { 50 } else { 2000 }
$maxMb = if ($env:TELEGRAM_MAX_MB) { [int]$env:TELEGRAM_MAX_MB } else { $defaultMb }
$maxBytes = [int64]$maxMb * 1024 * 1024

function Invoke-Telegram([string]$method, [string[]]$formArgs) {
    $out = & curl.exe -sS --max-time 3600 "$api/$method" @formArgs
    if ($LASTEXITCODE -ne 0) { Write-Warning "Telegram ${method}: request failed"; return $false }
    try { $res = $out | ConvertFrom-Json } catch { $res = $null }
    if (-not $res -or -not $res.ok) { Write-Warning "Telegram $method failed: $out"; return $false }
    return $true
}

if ($Files.Count -eq 0) {
    if (Invoke-Telegram "sendMessage" @("--form-string", "chat_id=$chat", "--form-string", "text=$Message")) {
        Write-Host "sent message"; exit 0
    }
    exit 1
}

$status = 0
foreach ($f in $Files) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) {
        Write-Warning "skip: $f is not a file"; $status = 1; continue
    }
    $item = Get-Item -LiteralPath $f
    $mb = [math]::Floor($item.Length / 1MB)
    if ($item.Length -gt $maxBytes) {
        Write-Warning "skip: $f is $mb MB (limit is $maxMb MB)"; $status = 1; continue
    }
    $sha = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLower()
    $caption = "$($item.Name)`nSHA256: $sha"
    if ($Message) { $caption = "$Message`n`n$caption" }
    if ($caption.Length -gt 1024) { $caption = $caption.Substring(0, 1024) }
    Write-Host "uploading $f ($mb MB)..."
    $ok = Invoke-Telegram "sendDocument" @(
        "--form-string", "chat_id=$chat",
        "-F", "document=@$($item.FullName)",
        "--form-string", "caption=$caption")
    if ($ok) { Write-Host "sent $f" } else { $status = 1 }
}
exit $status
