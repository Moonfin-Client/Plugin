param(
    [string]$Version = "2.1.0.0",
    [string]$TargetAbi = "4.9.1.90"
)

$ErrorActionPreference = "Stop"

$ProjectFile = "Emby.Plugins.Moonfin\Emby.Plugins.Moonfin.csproj"
$PluginName  = "Emby.Plugins.Moonfin"
$OutputDir   = "release"
$PackageName = "Moonfin.Emby"

Write-Host "Building Moonfin Emby Plugin v$Version..."

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

dotnet build $ProjectFile `
    -c Release `
    /p:AssemblyVersion=$Version `
    /p:FileVersion=$Version `
    "/p:MoonfinRelayAppKey=$RelayAppKey"

$BuildOut = "Emby.Plugins.Moonfin\bin\Release\netstandard2.1"
$DllPath = "$BuildOut\$PluginName.dll"

if (-not (Test-Path $DllPath)) {
    Write-Error "Build output not found at $DllPath"
    exit 1
}

# Assemble release folder
if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
New-Item -ItemType Directory -Path $OutputDir | Out-Null

Copy-Item $DllPath $OutputDir
# SharpCompress is not host-provided; ship it alongside the plugin (used for .7z ROM extraction).
Copy-Item "$BuildOut\SharpCompress.dll" $OutputDir

# Bundle web assets if present
if (Test-Path "web\index.html") {
    Copy-Item -Recurse "web" "$OutputDir\web"
}

# Create ZIP
$ZipName = "$PackageName-$Version.zip"
if (Test-Path $ZipName) { Remove-Item $ZipName }
Compress-Archive -Path "$OutputDir\*" -DestinationPath $ZipName

# Compute MD5
$Md5 = (Get-FileHash $ZipName -Algorithm MD5).Hash

Write-Host ""
Write-Host "Build complete!"
Write-Host "  Package : $ZipName"
Write-Host "  Version : $Version"
Write-Host "  MD5     : $Md5"
Write-Host ""
Write-Host "Install: copy $OutputDir\$PluginName.dll to your Emby plugins\ directory and restart Emby."
