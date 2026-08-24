# Build script for Moonfin Jellyfin plugin
# Creates a release ZIP with proper structure for plugin manifest
# Usage: .\build.ps1 [-Version "1.1.0.0"] [-TargetAbi "10.10.0"] [-SourceUrl "https://..."] [-SkipManifestUpdate]

param(
    [string]$Version = "2.1.0.0",
    [string]$TargetAbi = "10.10.0",
    [string]$SourceUrl = "",
    [switch]$SkipManifestUpdate
)

$ErrorActionPreference = "Stop"

$BuildTimestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$RootDir = $PSScriptRoot
$BackendDir = Join-Path $RootDir "backend"
$FrontendDir = Join-Path $RootDir "frontend"
$VerifyDir = Join-Path $RootDir "tools\verify-plugin"
$VerifyProject = Join-Path $VerifyDir "VerifyPlugin.csproj"

Write-Host "Building Moonfin v${Version} for Jellyfin ${TargetAbi}..."
Write-Host "Build Time: ${BuildTimestamp}"

# Validate expected Flutter web bundle location
$FrontendIndex = Join-Path $FrontendDir "index.html"
if (-not (Test-Path $FrontendIndex)) {
    Write-Host ""
    Write-Host "Warning: frontend/index.html not found."
    Write-Host "Run Mobile-Desktop/build-web-plugin.sh before packaging if you need bundled web assets."
}

# Build the .NET plugin from clean Release state so the package cannot reuse an
# assembly produced from an older project or checkout path.
Write-Host ""
Write-Host "--- Building server plugin ---"
$CsprojPath = Join-Path $BackendDir "Moonfin.Server.csproj"
$PublishDir = Join-Path $BackendDir "bin\Release\net8.0\publish"
$ReleaseBinDir = Join-Path $BackendDir "bin\Release"
$ReleaseObjDir = Join-Path $BackendDir "obj\Release"
$VerifyReleaseBinDir = Join-Path $VerifyDir "bin\Release"
$VerifyReleaseObjDir = Join-Path $VerifyDir "obj\Release"
if (Test-Path $ReleaseBinDir) { Remove-Item $ReleaseBinDir -Recurse -Force }
if (Test-Path $ReleaseObjDir) { Remove-Item $ReleaseObjDir -Recurse -Force }
if (Test-Path $VerifyReleaseBinDir) { Remove-Item $VerifyReleaseBinDir -Recurse -Force }
if (Test-Path $VerifyReleaseObjDir) { Remove-Item $VerifyReleaseObjDir -Recurse -Force }
# Optional push relay key, read from a gitignored .env at the repo root. Without
# one the build still succeeds and the plugin ships with push disabled.
$RelayAppKey = ""
$EnvFile = Join-Path $PSScriptRoot "..\.env"
if (Test-Path $EnvFile) {
    $match = Select-String -Path $EnvFile -Pattern '^\s*MOONFIN_RELAY_APP_KEY\s*=' | Select-Object -Last 1
    if ($match) { $RelayAppKey = ($match.Line -split '=', 2)[1].Trim().Trim('"').Trim("'") }
}
if ($RelayAppKey) {
    Write-Host "Push relay key found, stamping it into this build."
} else {
    Write-Host "No push relay key, building with push disabled."
}

dotnet publish $CsprojPath -c Release -o $PublishDir `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version `
    -p:Version=$Version `
    "-p:MoonfinRelayAppKey=$RelayAppKey"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host ""
Write-Host "--- Verifying server plugin ---"
dotnet run --project $VerifyProject -c Release -- (Join-Path $PublishDir "Moonfin.Server.dll") $Version
if ($LASTEXITCODE -ne 0) { throw "plugin verification failed" }

# Create release directory
$ReleaseDir = Join-Path $RootDir "release"
if (Test-Path $ReleaseDir) { Remove-Item $ReleaseDir -Recurse -Force }
New-Item -ItemType Directory -Path $ReleaseDir | Out-Null

# Copy the plugin DLL plus its bundled dependencies
$DllPath = Join-Path $PublishDir "Moonfin.Server.dll"
Copy-Item $DllPath $ReleaseDir
$SharpCompressPath = Join-Path $PublishDir "SharpCompress.dll"
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

# Generate meta.json for plugin discovery
$PluginGuid = "8c5d0e91-4f2a-4b6d-9e3f-1a7c8d9e0f2b"
$TimestampIso = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$Meta = [ordered]@{
    category = "General"
    changelog = ""
    description = "Moonfin brings a modern TV-style UI to Jellyfin web. Features include: custom navbar, media bar with featured content, Jellyseerr integration, and cross-device settings synchronization."
    guid = $PluginGuid
    name = "Moonfin"
    overview = "Custom UI and settings sync for Jellyfin"
    owner = "RadicalMuffinMan"
    targetAbi = "${TargetAbi}.0"
    timestamp = $TimestampIso
    version = $Version
    status = "Active"
    autoUpdate = $true
    assemblies = @("Moonfin.Server.dll")
}
$MetaJson = ConvertTo-Json -InputObject $Meta -Depth 10
$MetaPath = Join-Path $ReleaseDir "meta.json"
[System.IO.File]::WriteAllText($MetaPath, $MetaJson, (New-Object System.Text.UTF8Encoding $false))

# Create the ZIP file
$ZipName = "Moonfin.Server-${Version}.zip"
$ZipPath = Join-Path $RootDir $ZipName
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path (Join-Path $ReleaseDir "*") -DestinationPath $ZipPath

# Calculate MD5 checksum
$Hash = (Get-FileHash $ZipPath -Algorithm MD5).Hash.ToUpper()

# Update manifest.json
$ManifestFile = Join-Path $RootDir "manifest.json"
$ManifestUpdated = $false
if (-not $SkipManifestUpdate -and (Test-Path $ManifestFile)) {
    $Timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss")
    # Read as UTF-8 explicitly, Get-Content defaults to the system codepage on
    # Windows PowerShell 5.1 and would mangle non-ASCII characters
    $Manifest = [System.IO.File]::ReadAllText($ManifestFile) | ConvertFrom-Json
    $Manifest = @($Manifest)

    $Manifest[0].versions[0].version = $Version
    $Manifest[0].versions[0].targetAbi = "${TargetAbi}.0"
    $Manifest[0].versions[0].checksum = $Hash
    $Manifest[0].versions[0].timestamp = $Timestamp
    if (-not [string]::IsNullOrWhiteSpace($SourceUrl)) {
        $Manifest[0].versions[0].sourceUrl = $SourceUrl
    } else {
        $Manifest[0].versions[0].sourceUrl = $Manifest[0].versions[0].sourceUrl -replace '[^/]+$', $ZipName
    }

    $Json = ConvertTo-Json -InputObject $Manifest -Depth 10
    if ($Manifest.Count -eq 1 -and -not $Json.TrimStart().StartsWith('[')) {
        $Json = "[$Json]"
    }
    [System.IO.File]::WriteAllText($ManifestFile, $Json, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "Updated manifest.json with new checksum and version"
    $ManifestUpdated = $true
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
Write-Host "Manifest updated: $ManifestUpdated"
Write-Host "========================================="
Write-Host ""
Write-Host "Done!"
