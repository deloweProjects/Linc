<#
.SYNOPSIS
  Build, test, package and (optionally) publish a Linc release. 1.0.0.11, D-085.

.EXAMPLE
  .\Linc\tools\release.ps1 -Version 1.0.0.11 -DisplayName "1.0.0-beta.11"
  .\Linc\tools\release.ps1 -Version 1.0.0.11 -DisplayName "1.0.0-beta.11" -Publish

.DESCRIPTION
  1. Stamps the version into both apps (desktop csproj; Android versionName/versionCode).
  2. Builds and tests the phone (unit tests, then the APK the desktop bundles).
  3. Publishes the desktop (Beta.pubxml: self-contained, .pri and VC++ DLL fixes) and runs the
     pure harnesses (linksim, onboardsim, storesim).
  4. Builds Linc-Setup-<ver>.exe (Inno Setup 6) and the portable zip.
  5. Writes Releases\CHECKSUMS.txt, Releases\update.json and the RELEASE-NOTES header.
  6. Fills "Linc prod" and removes older builds there, skipping anything in use.
  7. Prints the git/gh commands — and runs them only with -Publish: push, a GitHub release
     with every asset (small ones first, big ones retried), HEAD-check every URL, then merge to
     main by PR (which is what makes update.json live).

  Releases are published as GitHub's "Latest" by default: Komi Store (komistore.app) and
  /releases/latest ignore pre-releases, so a pre-release is invisible there. Pass -Prerelease for
  a test build that should stay out of stores (update.json still offers it to installed copies).

  Code signing: set LINC_SIGN_PFX (path) and LINC_SIGN_PASSWORD, or LINC_SIGN_THUMBPRINT (a cert in
  the user's store), and the exe and installer are signed with signtool. Nothing is signed otherwise.

  Re-runnable; stops at the first failure with the step that failed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')] [string] $Version,
    [Parameter(Mandatory)] [string] $DisplayName,
    [string] $Notes,
    [switch] $Publish,
    [switch] $Prerelease,
    [switch] $SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$LincDir    = Join-Path $RepoRoot 'Linc'
$Desktop    = Join-Path $LincDir 'DESKTOP\Linc.Desktop'
$Android    = Join-Path $LincDir 'ANDROID'
$Releases   = Join-Path $RepoRoot 'Releases'
$ProdDir    = Join-Path (Split-Path $RepoRoot -Parent) 'Linc prod'
$Date       = Get-Date -Format 'yyyy-MM-dd'
$Tag        = "v$Version"
$Repo       = 'deloweProjects/Linc'
$MSBuild    = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$Iscc       = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$Utf8NoBom  = New-Object System.Text.UTF8Encoding $false

$ApkName    = "Linc-Companion-$Version-$Date.apk"
$ZipName    = "Linc-Desktop-$Version-$Date.zip"
$FolderName = "Linc-Desktop-$Version-$Date"
$SetupName  = "Linc-Setup-$Version.exe"
$Out        = Join-Path $env:TEMP "linc-release-$Version"

function Step([string] $what) { Write-Host ""; Write-Host "==> $what" -ForegroundColor Cyan }
function Fail([string] $why) { throw "release.ps1: $why" }
function Run([string] $exe, [string[]] $arguments, [string] $what) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { Fail "$what failed (exit $LASTEXITCODE)" }
}
function WriteText([string] $path, [string] $text) { [System.IO.File]::WriteAllText($path, $text, $Utf8NoBom) }
function Sha256([string] $path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash }
function Size([string] $path) { (Get-Item -LiteralPath $path).Length }

# --------------------------------------------------------------------------------------------
Step "Checking the tools"
if (-not (Test-Path $MSBuild)) { Fail "VS MSBuild not found at $MSBuild (dotnet build can't build WinUI)" }
if (-not $Iscc) { Fail "Inno Setup 6 is not installed (ISCC.exe). Install it from https://jrsoftware.org/isdl.php" }
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
$env:ANDROID_HOME = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
if (-not (Test-Path (Join-Path $env:JAVA_HOME 'bin\java.exe'))) { Fail "Android Studio's JBR not found at $env:JAVA_HOME" }
if ($Publish -and -not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail "the GitHub CLI (gh) is needed for -Publish" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

# --------------------------------------------------------------------------------------------
Step "Stamping $Version into both apps"
$csproj = Join-Path $Desktop 'Linc.Desktop.csproj'
$text = [System.IO.File]::ReadAllText($csproj)
foreach ($element in 'Version', 'AssemblyVersion', 'FileVersion', 'InformationalVersion') {
    $text = [regex]::Replace($text, "<$element>[^<]*</$element>", "<$element>$Version</$element>")
}
WriteText $csproj $text
$gradle = Join-Path $Android 'app\build.gradle.kts'
$code = [int]($Version.Split('.')[3]) + 1   # 1.0.0.10 shipped as versionCode 11
$text = [System.IO.File]::ReadAllText($gradle)
$text = [regex]::Replace($text, 'versionCode = \d+', "versionCode = $code")
$text = [regex]::Replace($text, 'versionName = "[^"]*"', "versionName = `"$Version`"")
WriteText $gradle $text

# --------------------------------------------------------------------------------------------
Step "Building and testing the phone app"
Push-Location $Android
try {
    $tasks = if ($SkipTests) { @('assembleDebug') } else { @('testDebugUnitTest', 'assembleDebug') }
    Run '.\gradlew.bat' (@('--offline') + $tasks) 'The Android build'
} finally { Pop-Location }
$apk = Join-Path $Android 'app\build\outputs\apk\debug\app-debug.apk'
if (-not (Test-Path $apk)) { Fail "no APK at $apk" }
Copy-Item $apk (Join-Path $Out $ApkName) -Force

# --------------------------------------------------------------------------------------------
Step "Publishing the desktop app"
if (Get-Process Linc.Desktop -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Desktop*" }) {
    Fail "a Linc built from this repo is running; quit it from the tray first (your installed Linc can stay running)"
}
Run $MSBuild @($csproj, '-restore', '-t:Publish', '-p:PublishProfile=Beta', '-p:Configuration=Release', '-p:Platform=x64', '-v:m', '-nologo') 'The desktop publish'
$publishDir = Join-Path $Desktop 'bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish'
foreach ($must in 'Linc.Desktop.exe', 'Linc.Desktop.pri', 'Assets\companion.apk', 'vcruntime140.dll') {
    if (-not (Test-Path (Join-Path $publishDir $must))) { Fail "the publish is missing $must (see Beta.pubxml)" }
}

if (-not $SkipTests) {
    Step "Running the pure harnesses"
    Push-Location $LincDir
    try {
        foreach ($sim in 'linksim', 'onboardsim', 'storesim') {
            Run 'dotnet' @('run', '-c', 'Release', '--project', "tools\$sim") "tools\$sim"
        }
    } finally { Pop-Location }
}

# --------------------------------------------------------------------------------------------
$sign = $null
if ($env:LINC_SIGN_PFX -or $env:LINC_SIGN_THUMBPRINT) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { Fail "a signing certificate is configured but signtool.exe was not found (install the Windows SDK)" }
    $sign = if ($env:LINC_SIGN_PFX) {
        "`"$($signtool.FullName)`" sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f `"$env:LINC_SIGN_PFX`" /p `"$env:LINC_SIGN_PASSWORD`""
    } else {
        "`"$($signtool.FullName)`" sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 $env:LINC_SIGN_THUMBPRINT"
    }
    Step "Signing Linc.Desktop.exe"
    cmd /c "$sign `"$(Join-Path $publishDir 'Linc.Desktop.exe')`""
    if ($LASTEXITCODE -ne 0) { Fail "signing Linc.Desktop.exe failed" }
}

# --------------------------------------------------------------------------------------------
Step "Building $SetupName"
$isccArgs = @("/DAppVersion=$Version", "/DDisplayName=$DisplayName", "/DSourceDir=$publishDir", "/DOutputDir=$Out", '/Q')
if ($sign) { $isccArgs += @('/DSign', "/Slinc=$sign `$f") }
$isccArgs += (Join-Path $LincDir 'installer\Linc.iss')
Run $Iscc $isccArgs 'The installer build'

Step "Building the portable zip"
$folder = Join-Path $Out $FolderName
if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
Copy-Item $publishDir $folder -Recurse
$zip = Join-Path $Out $ZipName
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$setup = Join-Path $Out $SetupName
$apkOut = Join-Path $Out $ApkName
$files = Get-ChildItem $publishDir -Recurse -File
Write-Host ("Publish folder: {0:N0} files, {1:N0} MB.  Setup: {2:N1} MB.  Zip: {3:N1} MB.  APK: {4:N1} MB." -f `
    $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), ((Size $setup) / 1MB), ((Size $zip) / 1MB), ((Size $apkOut) / 1MB))

# --------------------------------------------------------------------------------------------
Step "Writing CHECKSUMS.txt, update.json and the release notes header"
$base = "https://github.com/$Repo/releases/download/$Tag"
$checksums = @"
Linc builds - SHA256 checksums
==============================

Verify before running:  Get-FileHash <filename> -Algorithm SHA256

$Version ("$DisplayName", $Date) - GitHub release $Tag.

$(Sha256 $setup)  $SetupName
$(Sha256 $zip)  $ZipName
$(Sha256 $apkOut)  $ApkName

Linc-Setup installs Linc for your Windows account (no admin). The zip is the same app, portable.

File sizes (bytes):

    $($SetupName.PadRight(42)) $('{0,13:N0}' -f (Size $setup))
    $($ZipName.PadRight(42)) $('{0,13:N0}' -f (Size $zip))
    $($ApkName.PadRight(42)) $('{0,13:N0}' -f (Size $apkOut))
"@
WriteText (Join-Path $Releases 'CHECKSUMS.txt') $checksums
WriteText (Join-Path $Out 'CHECKSUMS.txt') $checksums

# Edited in place, value by value, so the file keeps its layout and comments (PowerShell 5.1's
# ConvertTo-Json rewrites the whole thing). minimumSupported is never touched.
$manifestPath = Join-Path $Releases 'update.json'
$json = [System.IO.File]::ReadAllText($manifestPath)
function SetJson([string] $pattern, [string] $value) {
    $script:json = [regex]::Replace($script:json, $pattern, { param($m) $m.Groups[1].Value + $value + '"' }.GetNewClosure(), 'Singleline')
}
SetJson '("latest"\s*:\s*")[^"]*"' $Version
if ($Notes) { SetJson '("notes"\s*:\s*")[^"]*"' ($Notes -replace '"', "'") }
SetJson '("desktop"\s*:\s*\{\s*"url"\s*:\s*")[^"]*"' "$base/$SetupName"
SetJson '("desktop"\s*:\s*\{\s*"url"\s*:\s*"[^"]*",\s*"sha256"\s*:\s*")[^"]*"' (Sha256 $setup)
SetJson '("android"\s*:\s*\{\s*"url"\s*:\s*")[^"]*"' "$base/$ApkName"
SetJson '("android"\s*:\s*\{\s*"url"\s*:\s*"[^"]*",\s*"sha256"\s*:\s*")[^"]*"' (Sha256 $apkOut)
$null = $json | ConvertFrom-Json   # fail loudly if the edit broke the JSON
WriteText $manifestPath $json

$notesPath = Join-Path $Releases 'RELEASE-NOTES.md'
$notesText = [System.IO.File]::ReadAllText($notesPath)
$header = "## $Version (`"$DisplayName`") — $Date"
if ($notesText -notmatch [regex]::Escape("## $Version (")) {
    $notesText = ([regex]'(?m)^---\s*$').Replace($notesText, "---`r`n`r`n$header`r`n`r`n(Write the notes for $Version here.)`r`n", 1)
    WriteText $notesPath $notesText
    Fail "RELEASE-NOTES.md had no section for $Version; a header was added — write the notes, then run again"
}
$section = [regex]::Match($notesText, "(?s)## $([regex]::Escape($Version)) \(.*?(?=\r?\n## |\z)").Value
WriteText (Join-Path $Out 'release-notes.md') $section

# --------------------------------------------------------------------------------------------
Step "Filling $ProdDir"
New-Item -ItemType Directory -Force -Path $ProdDir | Out-Null
foreach ($f in $setup, $zip, $apkOut, (Join-Path $Out 'CHECKSUMS.txt')) { Copy-Item $f $ProdDir -Force }
$prodFolder = Join-Path $ProdDir $FolderName
if (-not (Test-Path $prodFolder)) { Copy-Item $folder $prodFolder -Recurse }
$kept = @()
Get-ChildItem $ProdDir | Where-Object {
    $_.Name -match '^Linc-(Desktop|Companion|Setup)-' -and $_.Name -notmatch [regex]::Escape($Version)
} | ForEach-Object {
    $item = $_   # inside catch, $_ is the error record
    if ($item.PSIsContainer) {
        # Rename-probe: a folder an exe or adb server is running from can't be renamed.
        $probe = "$($item.FullName).probe"
        try { Rename-Item $item.FullName $probe -ErrorAction Stop; Rename-Item $probe $item.FullName -ErrorAction Stop }
        catch { $kept += $item.Name; return }
        try { Remove-Item $item.FullName -Recurse -Force -ErrorAction Stop } catch { $kept += $item.Name }
    } else {
        try { Remove-Item $item.FullName -Force -ErrorAction Stop } catch { $kept += $item.Name }
    }
}
if ($kept) { Write-Host "Left in place because in use: $($kept -join ', ')" -ForegroundColor Yellow }

# --------------------------------------------------------------------------------------------
$branch = (git -C $RepoRoot rev-parse --abbrev-ref HEAD).Trim()
$prereleaseFlag = if ($Prerelease) { '--prerelease' } else { '' }
$commands = @(
    "git -C `"$RepoRoot`" add -A",
    "git -C `"$RepoRoot`" commit -m `"Linc $Version ($DisplayName)`"",
    "git -C `"$RepoRoot`" push origin $branch",
    "gh release create $Tag --repo $Repo --target $branch --title `"Linc $DisplayName`" --notes-file `"$Out\release-notes.md`" $prereleaseFlag",
    "gh release upload $Tag --repo $Repo `"$apkOut`" `"$Out\CHECKSUMS.txt`" --clobber",
    "gh release upload $Tag --repo $Repo `"$setup`" --clobber",
    "gh release upload $Tag --repo $Repo `"$zip`" --clobber",
    "(HEAD-check every asset URL)",
    "gh pr create --repo $Repo --base main --head $branch --title `"Linc $Version`" --body ...",
    "gh pr merge --repo $Repo --merge"
)
if (-not $Publish) {
    Step "Done (not published). To publish, run again with -Publish, which does:"
    $commands | ForEach-Object { Write-Host "  $_" }
    return
}

Step "Publishing $Tag"
git -C $RepoRoot add -A; if ($LASTEXITCODE) { Fail 'git add' }
git -C $RepoRoot diff --cached --quiet
if ($LASTEXITCODE) {
    git -C $RepoRoot commit -m "Linc $Version ($DisplayName)`n`nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    if ($LASTEXITCODE) { Fail 'git commit' }
}
Run 'git' @('-C', $RepoRoot, 'push', 'origin', $branch) 'git push'

gh release view $Tag --repo $Repo *> $null
if ($LASTEXITCODE) {
    $create = @('release', 'create', $Tag, '--repo', $Repo, '--target', $branch, '--title', "Linc $DisplayName", '--notes-file', "$Out\release-notes.md")
    if ($Prerelease) { $create += '--prerelease' }
    Run 'gh' $create 'Creating the GitHub release'
}
function Upload([string] $path) {
    for ($try = 1; $try -le 5; $try++) {
        gh release upload $Tag --repo $Repo $path --clobber
        if ($LASTEXITCODE -eq 0) { return }
        Write-Host "  upload of $(Split-Path $path -Leaf) failed (try $try of 5); retrying in 20 s" -ForegroundColor Yellow
        Start-Sleep -Seconds 20
    }
    Fail "uploading $(Split-Path $path -Leaf) failed 5 times"
}
foreach ($asset in $apkOut, (Join-Path $Out 'CHECKSUMS.txt'), $setup, $zip) { Upload $asset }

Step "Checking every asset URL answers"
foreach ($name in $ApkName, 'CHECKSUMS.txt', $SetupName, $ZipName) {
    $url = "$base/$name"
    try { $r = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -MaximumRedirection 5 }
    catch { Fail "HEAD $url failed: $($_.Exception.Message)" }
    Write-Host "  $($r.StatusCode)  $url"
}

Step "Merging to main (this makes update.json live)"
$pr = gh pr list --repo $Repo --head $branch --base main --state open --json number --jq '.[0].number'
if (-not $pr) {
    Run 'gh' @('pr', 'create', '--repo', $Repo, '--base', 'main', '--head', $branch, '--title', "Linc $Version ($DisplayName)",
        '--body', "Release $Tag. Assets verified (HEAD-checked) before this merge.`n`n🤖 Generated with [Claude Code](https://claude.com/claude-code)") 'Creating the PR'
    $pr = gh pr list --repo $Repo --head $branch --base main --state open --json number --jq '.[0].number'
}
Run 'gh' @('pr', 'merge', "$pr", '--repo', $Repo, '--merge') 'Merging the PR'
Step "Released $Tag."
