#!/usr/bin/env bash
# Build script for Moonfin Jellyfin plugin
# Creates a release ZIP with proper structure for plugin manifest
# Works on Linux, macOS, and Windows (Git Bash/WSL)

set -e

VERSION="${1:-1.5.1.0}"
TARGET_ABI="${2:-10.10.0}"
BUILD_TIMESTAMP=$(date '+%Y-%m-%d %H:%M:%S')

# Get repo root (where this script lives)
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$ROOT_DIR/backend"
FRONTEND_DIR="$ROOT_DIR/frontend"
WEB_DIST_DIR="$FRONTEND_DIR/dist"

echo "Building Moonfin v${VERSION} for Jellyfin ${TARGET_ABI}..."
echo "Build Time: ${BUILD_TIMESTAMP}"

# Build the web plugin first
if [ -f "$FRONTEND_DIR/build.js" ]; then
    echo ""
    echo "--- Building web plugin ---"
    (cd "$FRONTEND_DIR" && node build.js)
else
    echo "Warning: frontend/build.js not found, skipping web build"
fi

# Sync web plugin output into backend embedded resources
if [ -d "$WEB_DIST_DIR" ]; then
    echo ""
    echo "--- Syncing web files ---"
    cp "$WEB_DIST_DIR/plugin.js" "$BACKEND_DIR/Web/plugin.js"
    cp "$WEB_DIST_DIR/plugin.css" "$BACKEND_DIR/Web/plugin.css"
    echo "Web files synced to backend/Web/"
else
    echo "Warning: frontend/dist/ not found, skipping web file sync"
fi

# Build the .NET plugin
echo ""
echo "--- Building server plugin ---"
dotnet build "$BACKEND_DIR/Moonfin.Server.csproj" -c Release

# Create release directory
RELEASE_DIR="$ROOT_DIR/release"
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"

# Copy DLL to release folder
cp "$BACKEND_DIR/bin/Release/net8.0/Moonfin.Server.dll" "$RELEASE_DIR/"

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
if [ -f "$MANIFEST_FILE" ]; then
    # Create timestamp in ISO 8601 format
    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%S")

    if command -v jq &> /dev/null; then
        jq --arg ver "$VERSION" \
           --arg abi "${TARGET_ABI}.0" \
           --arg sum "$CHECKSUM" \
           --arg time "$TIMESTAMP" \
           '.[0].versions[0].version = $ver |
            .[0].versions[0].targetAbi = $abi |
            .[0].versions[0].checksum = $sum |
            .[0].versions[0].timestamp = $time' \
           "$MANIFEST_FILE" > "${MANIFEST_FILE}.tmp" && mv "${MANIFEST_FILE}.tmp" "$MANIFEST_FILE"
        echo "Updated manifest.json with new checksum and version"
    else
        sed -i.bak -E "s/\"version\": \"[^\"]+\"/\"version\": \"$VERSION\"/" "$MANIFEST_FILE"
        sed -i.bak -E "s/\"checksum\": \"[^\"]+\"/\"checksum\": \"$CHECKSUM\"/" "$MANIFEST_FILE"
        sed -i.bak -E "s/\"timestamp\": \"[^\"]+\"/\"timestamp\": \"$TIMESTAMP\"/" "$MANIFEST_FILE"
        rm -f "${MANIFEST_FILE}.bak"
        echo "Updated manifest.json with new checksum and version (using sed)"
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
echo "Manifest updated: manifest.json"
echo "========================================="
echo ""
echo "Done!"
