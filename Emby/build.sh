#!/usr/bin/env bash
set -e

VERSION="${1:-2.0.1.0}"
TARGET_ABI="${2:-4.9.1.90}"
PROJECT="Emby.Plugins.Moonfin/Emby.Plugins.Moonfin.csproj"
OUTPUT_DIR="release"
PLUGIN_NAME="Emby.Plugins.Moonfin"
PACKAGE_NAME="Moonfin.Emby"

echo "Building Moonfin Emby Plugin v${VERSION}..."

# Find dotnet
DOTNET_CMD="dotnet"
if ! command -v dotnet &>/dev/null; then
    if [ -f "$HOME/.dotnet/dotnet" ]; then
        DOTNET_CMD="$HOME/.dotnet/dotnet"
    else
        echo "ERROR: dotnet not found. Install .NET SDK from https://dotnet.microsoft.com/download"
        exit 1
    fi
fi

# Build
$DOTNET_CMD build "$PROJECT" \
    -c Release \
    /p:AssemblyVersion="$VERSION" \
    /p:FileVersion="$VERSION"

BUILD_OUT="Emby.Plugins.Moonfin/bin/Release/netstandard2.1"
DLL_PATH="$BUILD_OUT/${PLUGIN_NAME}.dll"

if [ ! -f "$DLL_PATH" ]; then
    echo "ERROR: Build output not found at $DLL_PATH"
    exit 1
fi

# Assemble release folder
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

cp "$DLL_PATH" "$OUTPUT_DIR/"
# SharpCompress is not host-provided; ship it alongside the plugin (used for .7z ROM extraction).
cp "$BUILD_OUT/SharpCompress.dll" "$OUTPUT_DIR/"

# Bundle Flutter web assets if present
if [ -d "web" ] && [ -f "web/index.html" ]; then
    echo "Bundling web assets..."
    cp -r "web" "$OUTPUT_DIR/"
elif [ -d "Emby.Plugins.Moonfin/web" ] && [ -f "Emby.Plugins.Moonfin/web/index.html" ]; then
    cp -r "Emby.Plugins.Moonfin/web" "$OUTPUT_DIR/"
fi

# Create ZIP
ZIP_NAME="${PACKAGE_NAME}-${VERSION}.zip"
rm -f "$ZIP_NAME"
cd "$OUTPUT_DIR" && zip -r "../${ZIP_NAME}" . && cd ..

# Compute checksum
if command -v md5sum &>/dev/null; then
    CHECKSUM=$(md5sum "$ZIP_NAME" | awk '{print $1}' | tr '[:lower:]' '[:upper:]')
elif command -v md5 &>/dev/null; then
    CHECKSUM=$(md5 -q "$ZIP_NAME" | tr '[:lower:]' '[:upper:]')
else
    CHECKSUM="N/A"
fi

echo ""
echo "Build complete!"
echo "  Package : ${ZIP_NAME}"
echo "  Version : ${VERSION}"
echo "  MD5     : ${CHECKSUM}"
echo ""
echo "Install: copy everything in ${OUTPUT_DIR}/ (${PLUGIN_NAME}.dll, SharpCompress.dll, and the web/ folder) into your Emby plugins/ directory and restart Emby."
echo "The web/ folder must sit beside the dll, otherwise the web frontend answers with a 404."
