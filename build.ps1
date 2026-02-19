# Build script for Moonfin Jellyfin plugin
# Creates a release ZIP with proper structure for plugin manifest
# Usage: .\build.ps1 [-Version "1.1.0.0"] [-TargetAbi "10.10.0"]

param(
    [string]$Version = "1.4.0.0",
    [string]$TargetAbi = "10.10.0"
)

$ErrorActionPreference = "Stop"

$BuildTimestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$RootDir = $PSScriptRoot
$BackendDir = Join-Path $RootDir "backend"
$FrontendDir = Join-Path $RootDir "frontend"
$WebDistDir = Join-Path $FrontendDir "dist"

Write-Host "Building Moonfin v${Version} for Jellyfin ${TargetAbi}..."
Write-Host "Build Time: ${BuildTimestamp}"

# Build the web plugin first
$BuildJs = Join-Path $FrontendDir "build.js"
if (Test-Path $BuildJs) {
    Write-Host ""
    Write-Host "--- Building web plugin ---"
    Push-Location $FrontendDir
    node build.js
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Web plugin build failed" }
    Pop-Location
} else {
    Write-Host "Warning: frontend/build.js not found, skipping web build"
}

# Sync web plugin output into backend embedded resources
if (Test-Path $WebDistDir) {
    Write-Host ""
    Write-Host "--- Syncing web files ---"
    Copy-Item (Join-Path $WebDistDir "plugin.js") (Join-Path $BackendDir "Web\plugin.js") -Force
    Copy-Item (Join-Path $WebDistDir "plugin.css") (Join-Path $BackendDir "Web\plugin.css") -Force
    Write-Host "Web files synced to backend/Web/"
} else {
    Write-Host "Warning: frontend/dist/ not found, skipping web file sync"
}

# Build the .NET plugin
Write-Host ""
Write-Host "--- Building server plugin ---"
$CsprojPath = Join-Path $BackendDir "Moonfin.Server.csproj"
dotnet build $CsprojPath -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

# Create release directory
$ReleaseDir = Join-Path $RootDir "release"
if (Test-Path $ReleaseDir) { Remove-Item $ReleaseDir -Recurse -Force }
New-Item -ItemType Directory -Path $ReleaseDir | Out-Null

# Copy DLL to release folder
$DllPath = Join-Path $BackendDir "bin\Release\net8.0\Moonfin.Server.dll"
Copy-Item $DllPath $ReleaseDir

# Create the ZIP file
$ZipName = "Moonfin.Server-${Version}.zip"
$ZipPath = Join-Path $RootDir $ZipName
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path (Join-Path $ReleaseDir "*") -DestinationPath $ZipPath

# Calculate MD5 checksum
$Hash = (Get-FileHash $ZipPath -Algorithm MD5).Hash.ToUpper()

# Update manifest.json
$ManifestFile = Join-Path $RootDir "manifest.json"
if (Test-Path $ManifestFile) {
    $Timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss")
    $Manifest = Get-Content $ManifestFile -Raw | ConvertFrom-Json

    $Manifest[0].versions[0].version = $Version
    $Manifest[0].versions[0].targetAbi = "${TargetAbi}.0"
    $Manifest[0].versions[0].checksum = $Hash
    $Manifest[0].versions[0].timestamp = $Timestamp

    $Manifest | ConvertTo-Json -Depth 10 | Set-Content $ManifestFile -Encoding UTF8
    Write-Host "Updated manifest.json with new checksum and version"
}

# Cleanup
Remove-Item $ReleaseDir -Recurse -Force

Write-Host ""
Write-Host "========================================="
Write-Host "Build complete!"
Write-Host "Build Time: ${BuildTimestamp}"
Write-Host "========================================="
Write-Host "ZIP file: $ZipName"
Write-Host "MD5 Checksum: $Hash"
Write-Host "Manifest updated: manifest.json"
Write-Host "========================================="
Write-Host ""
Write-Host "Done!"
