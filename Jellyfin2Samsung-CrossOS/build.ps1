# Cross-platform publish + package script
param(
    [string]$Configuration = "Release"
)

# ---- Ask for version & beta ----
$Version = Read-Host -Prompt "Enter version number (e.g. 1.8.3.4)"
if (-not $Version -or $Version -notmatch '^\d+(\.\d+){1,3}$') {
    Write-Host "Invalid version. Use e.g. 1.8.3.4" -ForegroundColor Red
    exit 1
}
$VersionTag = "v$Version"

$betaAnswer = Read-Host -Prompt "Is this a beta release? (y/N)"
$IsBeta = $false
switch -Regex ($betaAnswer.Trim().ToLower()) {
    '^(y|yes)$' { $IsBeta = $true }
    default     { $IsBeta = $false }
}
$ChannelSuffix = $(if ($IsBeta) { "-beta" } else { "" })

# ---- Names & paths ----
$ProjectName = "Apps2Samsung"   # executable name produced by dotnet publish
$ProductName = "Apps2Samsung"          # artifact prefix
$OutputRoot  = Join-Path $PSScriptRoot "publish"
$DistDir     = $OutputRoot

# ---- Helpers ----
function Ensure-CleanDir($path) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path | Out-Null
}
function Ensure-Dir($path) {
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
}

function Setup-MacOSPortable($arch) {
    $out = Join-Path $OutputRoot $arch
    Write-Host "Setting up macOS $arch portable folder..." -ForegroundColor Green
    Ensure-Dir $out

    # Copy Assets if present
    if (Test-Path (Join-Path $PSScriptRoot "Assets")) {
        Copy-Item (Join-Path $PSScriptRoot "Assets") $out -Recurse -Force
        Write-Host "Copied Assets to $arch build"
    }

    # postinstall.sh (clear quarantine, chmod, ad-hoc sign)
    $macScript = @'
#!/usr/bin/env bash
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
echo "[postinstall] Clearing quarantine..."
xattr -dr com.apple.quarantine "$DIR" || true
echo "[postinstall] Ensuring executables are runnable..."
chmod +x "$DIR/__EXECUTABLE__" || true
find "$DIR" -type f -name "*.sh" -exec chmod +x {} \; || true
if command -v codesign >/dev/null 2>&1; then
    echo "[postinstall] Ad-hoc signing Mach-O binaries (exe + dylibs)..."
    find "$DIR" -type f \( -perm -u+x -o -name "*.dylib" -o -name "*.so" \) -print0 | xargs -0 -n1 codesign --force -s - || true
fi
echo "[postinstall] Done."
echo "Run with: ./"__EXECUTABLE__""
'@ -replace '__EXECUTABLE__', $ProjectName

    # Force LF line endings
    $macScript = ($macScript -replace "`r`n", "`n") -replace "`r", "`n"

    $macScriptPath = Join-Path $out "postinstall.sh"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($macScriptPath, $macScript, $utf8NoBom)
}

function Setup-LinuxPortable() {
    $out = Join-Path $OutputRoot "linux-x64"
    Write-Host "Setting up Linux package..." -ForegroundColor Green
    Ensure-Dir $out

    if (Test-Path (Join-Path $PSScriptRoot "apps2samsung.desktop")) {
        Copy-Item (Join-Path $PSScriptRoot "apps2samsung.desktop") $out -Force
    }
    if (Test-Path (Join-Path $PSScriptRoot "Assets\jelly2sams.png")) {
        Copy-Item (Join-Path $PSScriptRoot "Assets\jelly2sams.png") $out -Force
    }

    # postinstall.sh (chmod)
    $linuxScript = @'
#!/usr/bin/env bash
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
echo "[postinstall] Ensuring executables are runnable..."
chmod +x "$DIR/__EXECUTABLE__" || true
find "$DIR" -type f -name "*.sh" -exec chmod +x {} \; || true
echo "[postinstall] Done."
echo "Run with: ./"__EXECUTABLE__""
'@ -replace '__EXECUTABLE__', $ProjectName

    # Force LF line endings
    $linuxScript = ($linuxScript -replace "`r`n", "`n") -replace "`r", "`n"

    $linuxScriptPath = Join-Path $out "postinstall.sh"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($linuxScriptPath, $linuxScript, $utf8NoBom)
}

function Make-Zip($sourceDir, $destZip) {
    Ensure-Dir ([System.IO.Path]::GetDirectoryName($destZip))
    if (Test-Path $destZip) { Remove-Item $destZip -Force }
    Compress-Archive -Path (Join-Path $sourceDir '*') -DestinationPath $destZip -Force
}

function Make-TarGz($sourceDir, $destTgz) {
    if (-not (Get-Command tar -ErrorAction SilentlyContinue)) {
        throw "tar not found. On recent Windows, `tar` (bsdtar) is included. Otherwise install it or switch macOS packaging to .zip."
    }
    Ensure-Dir ([System.IO.Path]::GetDirectoryName($destTgz))
    $fullDest = [System.IO.Path]::GetFullPath($destTgz)
    Push-Location $sourceDir
    try { tar -czf "$fullDest" . } finally { Pop-Location }
}

# ---- Clean ----
Ensure-CleanDir $OutputRoot
Ensure-CleanDir $DistDir

# ---- Publish ----
Write-Host "Publishing for Windows..." -ForegroundColor Green
dotnet publish -c $Configuration -r win-x64   -p:SelfContained=true -p:UseAppHost=true -o (Join-Path $OutputRoot "win-x64")

Write-Host "Publishing for macOS x64..." -ForegroundColor Green
dotnet publish -c $Configuration -r osx-x64   -p:SelfContained=true -p:UseAppHost=true -o (Join-Path $OutputRoot "osx-x64")

Write-Host "Publishing for Linux..." -ForegroundColor Green
dotnet publish -c $Configuration -r linux-x64 -p:SelfContained=true -p:UseAppHost=true -o (Join-Path $OutputRoot "linux-x64")

# ---- Post-publish setup ----
Setup-MacOSPortable "osx-x64"
Setup-LinuxPortable

# ---- Package ----
$winZip    = Join-Path $DistDir ("{0}-{1}{2}-win-x64.zip"      -f $ProductName, $VersionTag, $ChannelSuffix)
$osxX64Tgz = Join-Path $DistDir ("{0}-{1}{2}-osx-x64.tar.gz"   -f $ProductName, $VersionTag, $ChannelSuffix)
$linuxZip  = Join-Path $DistDir ("{0}-{1}{2}-linux-x64.tar.gz"    -f $ProductName, $VersionTag, $ChannelSuffix)

Make-Zip   (Join-Path $OutputRoot "win-x64")    $winZip
Make-TarGz (Join-Path $OutputRoot "osx-x64")    $osxX64Tgz
Make-TarGz (Join-Path $OutputRoot "linux-x64")  $linuxZip

# ---- Done ----
Write-Host ""
Write-Host ("Build complete! Artifacts in {0}:" -f $DistDir) -ForegroundColor Yellow
Write-Host (" - {0}" -f [IO.Path]::GetFileName($winZip))
Write-Host (" - {0}" -f [IO.Path]::GetFileName($osxX64Tgz))
Write-Host (" - {0}" -f [IO.Path]::GetFileName($linuxZip))
Write-Host ""
Write-Host "macOS/Linux users: run ./postinstall.sh after extracting." -ForegroundColor Cyan

Invoke-Item $DistDir