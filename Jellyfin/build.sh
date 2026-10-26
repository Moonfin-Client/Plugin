#!/usr/bin/env bash
# Build script for Moonfin Jellyfin plugin
# Creates a release ZIP with proper structure for plugin manifest
# Works on Linux, macOS, and Windows (Git Bash/WSL)
# Usage: ./build.sh [version] [source-url]

set -e

# The target ABI used to be the second argument. It now comes from MoonfinCatalogAbis, so
# reject the old form rather than publish an ABI as the download URL.
if [ $# -gt 2 ] || { [ -n "${2:-}" ] && [[ ! "$2" =~ ^https?:// ]]; }; then
    echo "Usage: ./build.sh [version] [source-url]" >&2
    echo "The target ABI argument was removed; set MoonfinCatalogAbis in backend/Moonfin.Server.csproj instead." >&2
    exit 1
fi

VERSION="${1:-2.4.0.0}"
SOURCE_URL="${2:-}"
BUILD_TIMESTAMP=$(date '+%Y-%m-%d %H:%M:%S')
# Release tags are the three-part version, so 2.4.0.0 is published under the tag 2.4.0
RELEASE_TAG="${VERSION%.*}"

# Get repo root (where this script lives)
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$ROOT_DIR/backend"
FRONTEND_DIR="$ROOT_DIR/frontend"
VERIFY_PROJECT="$ROOT_DIR/tools/verify-plugin/VerifyPlugin.csproj"
VERIFY_DIR="$ROOT_DIR/tools/verify-plugin"

# Every Jellyfin version a release is listed for, newest first. The lowest is the minimum
# server and goes into meta.json.
CATALOG_ABIS=$(sed -n 's:.*<MoonfinCatalogAbis>\(.*\)</MoonfinCatalogAbis>.*:\1:p' "$BACKEND_DIR/Moonfin.Server.csproj" \
    | tr ',' '\n' | tr -d ' \r' | sort -t. -k1,1nr -k2,2nr -k3,3nr)
if [ -z "$CATALOG_ABIS" ] || echo "$CATALOG_ABIS" | grep -qvE '^[0-9]+\.[0-9]+\.[0-9]+$'; then
    echo "Error: MoonfinCatalogAbis in Moonfin.Server.csproj must be a comma-separated list of x.y.z versions." >&2
    exit 1
fi
TARGET_ABI=$(echo "$CATALOG_ABIS" | tail -1)

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
  "description": "Moonbase is the Moonfin server plugin for Jellyfin, powering settings sync, the Moonfin Web app at /Moonfin/Web/, a built-in theme editor with custom theme APIs, media bar and ratings integrations, and Seerr proxy/SSO with admin defaults and broadcasts across Moonfin clients.",
  "guid": "${PLUGIN_GUID}",
  "name": "Moonbase",
  "overview": "Moonfin server plugin for Jellyfin with web app hosting, settings sync, theming, and Seerr integrations",
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
        # Prepend rather than assign into versions[0], so cutting a release can't overwrite
        # the previous one's entry.
        EXISTING=$(jq -r --arg ver "$VERSION" \
            '[.[0].versions[] | select(.version == $ver)] | length' "$MANIFEST_FILE")
        if [ "$EXISTING" != "0" ]; then
            echo "Error: manifest.json already lists version $VERSION." >&2
            echo "Bump the version, or remove that entry first if you are deliberately re-cutting it." >&2
            exit 1
        fi

        # One entry per catalog ABI, all pointing at the same zip.
        ABIS_JSON=$(echo "$CATALOG_ABIS" | jq -R '. + ".0"' | jq -sc .)
        jq --arg ver "$VERSION" \
           --argjson abis "$ABIS_JSON" \
           --arg sum "$CHECKSUM" \
           --arg time "$TIMESTAMP" \
           --arg source "$SOURCE_URL" \
           --arg zip "$ZIP_NAME" \
           --arg tag "$RELEASE_TAG" \
           '.[0].versions |= ((if $source != "" then $source
                               else ((.[0].sourceUrl | sub("/download/[^/]+/[^/]+$"; ""))
                                     + "/download/" + $tag + "/" + $zip)
                               end) as $url
                | [$abis[] | {
                    version: $ver,
                    changelog: "",
                    targetAbi: .,
                    sourceUrl: $url,
                    checksum: $sum,
                    timestamp: $time,
                    dependencies: []
                }] + .)' \
           "$MANIFEST_FILE" > "${MANIFEST_FILE}.tmp" && mv "${MANIFEST_FILE}.tmp" "$MANIFEST_FILE"
        echo "Prepended version $VERSION to manifest.json for Jellyfin $(echo $CATALOG_ABIS | tr ' ' ',') (changelogs left blank, fill each in)"

        # The catalog URL people add points at the manifest in the repo root, not this one.
        REPO_ROOT_MANIFEST="$(cd "$ROOT_DIR/.." && pwd)/manifest.json"
        if [ -f "$REPO_ROOT_MANIFEST" ]; then
            cp "$MANIFEST_FILE" "$REPO_ROOT_MANIFEST"
            echo "Synced the repo root manifest.json"
        fi
        MANIFEST_UPDATED="yes"
    else
        echo "Error: jq not found. manifest.json was NOT updated." >&2
        echo "Install jq, or prepend one entry per targetAbi manually:" >&2
        echo "  version=$VERSION  targetAbi=$(echo $CATALOG_ABIS | sed 's/\([^ ]*\)/\1.0/g; s/ /,/g')  checksum=$CHECKSUM  timestamp=$TIMESTAMP  sourceUrl=$SOURCE_URL" >&2
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
