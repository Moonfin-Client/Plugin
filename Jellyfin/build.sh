#!/usr/bin/env bash
# Build script for Moonfin Jellyfin plugin
# Creates a release ZIP with proper structure for plugin manifest
# Works on Linux, macOS, and Windows (Git Bash/WSL)
# Usage: ./build.sh [version] [target-abi] [source-url]

set -e

VERSION="${1:-2.0.2.0}"
TARGET_ABI="${2:-10.10.0}"
SOURCE_URL="${3:-}"
BUILD_TIMESTAMP=$(date '+%Y-%m-%d %H:%M:%S')

# Get repo root (where this script lives)
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$ROOT_DIR/backend"
FRONTEND_DIR="$ROOT_DIR/frontend"
VERIFY_PROJECT="$ROOT_DIR/tools/verify-plugin/VerifyPlugin.csproj"
VERIFY_DIR="$ROOT_DIR/tools/verify-plugin"

echo "Building Moonfin v${VERSION} for Jellyfin ${TARGET_ABI}..."
echo "Build Time: ${BUILD_TIMESTAMP}"

# Resolve dotnet binary (PATH first, then local user install)
DOTNET_BIN="dotnet"
if ! command -v "$DOTNET_BIN" &> /dev/null; then
    if [ -x "$HOME/.dotnet/dotnet" ]; then
        DOTNET_BIN="$HOME/.dotnet/dotnet"
    else
        echo ""
        echo "Error: dotnet command not found."
        echo "Install .NET 8 SDK, or install locally with:"
        echo "  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh"
        echo "  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir \"\$HOME/.dotnet\""
        exit 127
    fi
fi

# Validate expected Flutter web bundle location
if [ ! -f "$FRONTEND_DIR/index.html" ]; then
    echo ""
    echo "Warning: frontend/index.html not found."
    echo "Run Mobile-Desktop/build-web-plugin.sh before packaging if you need bundled web assets."
fi

# Optional push relay key, read from a gitignored .env at the repo root. Without
# one the build still succeeds and the plugin ships with push disabled.
RELAY_APP_KEY=""
ENV_FILE="$ROOT_DIR/../.env"
if [ -f "$ENV_FILE" ]; then
    RELAY_APP_KEY="$(grep -E '^[[:space:]]*MOONFIN_RELAY_APP_KEY[[:space:]]*=' "$ENV_FILE" \
        | tail -1 | cut -d= -f2- | tr -d '"'"'"' \r')"
fi
if [ -n "$RELAY_APP_KEY" ]; then
    echo "Push relay key found, stamping it into this build."
else
    echo "No push relay key, building with push disabled."
fi

# Build the .NET plugin from clean Release state so the package cannot reuse an
# assembly produced from an older project or checkout path.
echo ""
echo "--- Building server plugin ---"
PUBLISH_DIR="$BACKEND_DIR/bin/Release/net8.0/publish"
rm -rf "$BACKEND_DIR/bin/Release" "$BACKEND_DIR/obj/Release"
rm -rf "$VERIFY_DIR/bin/Release" "$VERIFY_DIR/obj/Release"
"$DOTNET_BIN" publish "$BACKEND_DIR/Moonfin.Server.csproj" \
    -c Release \
    -o "$PUBLISH_DIR" \
    -p:AssemblyVersion="$VERSION" \
    -p:FileVersion="$VERSION" \
    -p:Version="$VERSION" \
    -p:MoonfinRelayAppKey="$RELAY_APP_KEY"

echo ""
echo "--- Verifying server plugin ---"
"$DOTNET_BIN" run --project "$VERIFY_PROJECT" -c Release -- "$PUBLISH_DIR/Moonfin.Server.dll" "$VERSION"

# Create release directory
RELEASE_DIR="$ROOT_DIR/release"
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"

# Copy the plugin DLL plus its bundled dependencies
cp "$PUBLISH_DIR/Moonfin.Server.dll" "$RELEASE_DIR/"
cp "$PUBLISH_DIR/SharpCompress.dll" "$RELEASE_DIR/"

# Bundle Flutter web files next to plugin DLL for local/sideload installs
if [ -f "$FRONTEND_DIR/index.html" ]; then
    mkdir -p "$RELEASE_DIR/frontend"
    cp -R "$FRONTEND_DIR/." "$RELEASE_DIR/frontend/"
    rm -rf "$RELEASE_DIR/frontend/node_modules"
    rm -f "$RELEASE_DIR/frontend/package.json" "$RELEASE_DIR/frontend/package-lock.json"
fi

# Generate meta.json for plugin discovery
PLUGIN_GUID="8c5d0e91-4f2a-4b6d-9e3f-1a7c8d9e0f2b"
TIMESTAMP_ISO=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
cat > "$RELEASE_DIR/meta.json" <<EOF
{
  "category": "General",
  "changelog": "",
  "description": "Moonfin brings a modern TV-style UI to Jellyfin web. Features include: custom navbar, media bar with featured content, Jellyseerr integration, and cross-device settings synchronization.",
  "guid": "${PLUGIN_GUID}",
  "name": "Moonfin",
  "overview": "Custom UI and settings sync for Jellyfin",
  "owner": "RadicalMuffinMan",
  "targetAbi": "${TARGET_ABI}.0",
  "timestamp": "${TIMESTAMP_ISO}",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": true,
  "assemblies": ["Moonfin.Server.dll"]
}
EOF

# Create the ZIP file
ZIP_NAME="Moonfin.Server-${VERSION}.zip"
rm -f "$ROOT_DIR/$ZIP_NAME"
cd "$RELEASE_DIR"
zip -r "$ROOT_DIR/$ZIP_NAME" .
cd "$ROOT_DIR"

# Calculate MD5 checksum (cross-platform)
if command -v md5sum &> /dev/null; then
    CHECKSUM=$(md5sum "$ZIP_NAME" | awk '{print toupper($1)}')
elif command -v md5 &> /dev/null; then
    CHECKSUM=$(md5 -q "$ZIP_NAME" | tr '[:lower:]' '[:upper:]')
elif command -v certutil &> /dev/null; then
    # Windows fallback (Git Bash)
    CHECKSUM=$(certutil -hashfile "$ZIP_NAME" MD5 2>/dev/null | sed -n '2p' | tr -d ' ' | tr '[:lower:]' '[:upper:]')
else
    CHECKSUM="UNABLE_TO_CALCULATE"
fi

# Update manifest.json
MANIFEST_FILE="$ROOT_DIR/manifest.json"
MANIFEST_UPDATED="no"
if [ "${SKIP_MANIFEST_UPDATE:-0}" != "1" ] && [ -f "$MANIFEST_FILE" ]; then
    # Create timestamp in ISO 8601 format
    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%S")

    if command -v jq &> /dev/null; then
        jq --arg ver "$VERSION" \
           --arg abi "${TARGET_ABI}.0" \
           --arg sum "$CHECKSUM" \
           --arg time "$TIMESTAMP" \
                     --arg source "$SOURCE_URL" \
                     --arg zip "$ZIP_NAME" \
           '.[0].versions[0].version = $ver |
            .[0].versions[0].targetAbi = $abi |
            .[0].versions[0].checksum = $sum |
                        .[0].versions[0].timestamp = $time |
                        .[0].versions[0].sourceUrl =
                            (if $source != "" then $source
                             else (.[0].versions[0].sourceUrl | sub("[^/]+$"; $zip))
                             end)' \
           "$MANIFEST_FILE" > "${MANIFEST_FILE}.tmp" && mv "${MANIFEST_FILE}.tmp" "$MANIFEST_FILE"
        echo "Updated manifest.json with new checksum and version"
          MANIFEST_UPDATED="yes"
    else
        echo "Error: jq not found. manifest.json was NOT updated." >&2
        echo "Install jq, or update versions[0] manually:" >&2
        echo "  version=$VERSION  targetAbi=${TARGET_ABI}.0  checksum=$CHECKSUM  timestamp=$TIMESTAMP  sourceUrl=$SOURCE_URL" >&2
        exit 1
    fi
fi

# Cleanup
rm -rf "$RELEASE_DIR"

echo ""
echo "========================================="
echo "Build complete!"
echo "Build Time: ${BUILD_TIMESTAMP}"
echo "========================================="
echo "ZIP file: $ZIP_NAME"
echo "MD5 Checksum: $CHECKSUM"
echo "Manifest updated: $MANIFEST_UPDATED"
echo "========================================="
echo ""
echo "Done!"
