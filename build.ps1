# Build script for Moonfin Jellyfin plugin
# Creates a release ZIP with proper structure for plugin manifest
# Usage: .\build.ps1 [-Version "1.1.0.0"] [-TargetAbi "10.10.0"]

param(
    [string]$Version = "1.9.1.0",
    [string]$TargetAbi = "10.10.0"
)

$ErrorActionPreference = "Stop"

$BuildTimestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$RootDir = $PSScriptRoot
$BackendDir = Join-Path $RootDir "backend"
$FrontendDir = Join-Path $RootDir "frontend"

Write-Host "Building Moonfin v${Version} for Jellyfin ${TargetAbi}..."
Write-Host "Build Time: ${BuildTimestamp}"

# Validate expected Flutter web bundle location
$FrontendIndex = Join-Path $FrontendDir "index.html"
if (-not (Test-Path $FrontendIndex)) {
    Write-Host ""
    Write-Host "Warning: frontend/index.html not found."
    Write-Host "Run Mobile-Desktop/build-web-plugin.sh before packaging if you need bundled web assets."
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

# Copy the plugin DLL plus its bundled dependencies
$DllPath = Join-Path $BackendDir "bin\Release\net8.0\Moonfin.Server.dll"
Copy-Item $DllPath $ReleaseDir
$SharpCompressPath = Join-Path $BackendDir "bin\Release\net8.0\SharpCompress.dll"
Copy-Item $SharpCompressPath $ReleaseDir

# Bundle Flutter web files next to plugin DLL for local/sideload installs
if (Test-Path $FrontendIndex) {
    $ReleaseFrontend = Join-Path $ReleaseDir "frontend"
    New-Item -ItemType Directory -Path $ReleaseFrontend -Force | Out-Null
    Copy-Item (Join-Path $FrontendDir "*") $ReleaseFrontend -Recurse -Force

    $NodeModules = Join-Path $ReleaseFrontend "node_modules"
    if (Test-Path $NodeModules) { Remove-Item $NodeModules -Recurse -Force }

    $PackageJson = Join-Path $ReleaseFrontend "package.json"
    if (Test-Path $PackageJson) { Remove-Item $PackageJson -Force }

    $PackageLock = Join-Path $ReleaseFrontend "package-lock.json"
    if (Test-Path $PackageLock) { Remove-Item $PackageLock -Force }
}

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
    # Read as UTF-8 explicitly, Get-Content defaults to the system codepage on
    # Windows PowerShell 5.1 and would mangle non-ASCII characters
    $Manifest = [System.IO.File]::ReadAllText($ManifestFile) | ConvertFrom-Json
    $Manifest = @($Manifest)

    $Manifest[0].versions[0].version = $Version
    $Manifest[0].versions[0].targetAbi = "${TargetAbi}.0"
    $Manifest[0].versions[0].checksum = $Hash
    $Manifest[0].versions[0].timestamp = $Timestamp

    $Json = ConvertTo-Json -InputObject $Manifest -Depth 10
    if ($Manifest.Count -eq 1 -and -not $Json.TrimStart().StartsWith('[')) {
        $Json = "[$Json]"
    }
    [System.IO.File]::WriteAllText($ManifestFile, $Json, (New-Object System.Text.UTF8Encoding $false))
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
