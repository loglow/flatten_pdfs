#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
APP="$ROOT/Flatten PDFs.app"
SOURCE="$ROOT/Sources/main.swift"
PLIST="$ROOT/Resources/Info.plist"
ICON="$ROOT/Resources/FlattenPDFs.icns"
BUILD="$ROOT/.build"

printf '\nBuilding Flatten PDFs…\n\n'

if ! command -v xcrun >/dev/null 2>&1 || ! xcrun --find swiftc >/dev/null 2>&1; then
    echo "Apple's Command Line Developer Tools are required."
    echo "A macOS installer prompt should appear now. After installation, run this file again."
    xcode-select --install 2>/dev/null || true
    printf '\nPress Return to close this window.'
    read -r
    exit 1
fi

rm -rf "$APP" "$BUILD"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$BUILD"

export MACOSX_DEPLOYMENT_TARGET="11.0"

xcrun swiftc \
    -O \
    -whole-module-optimization \
    -framework AppKit \
    -framework PDFKit \
    -framework CoreGraphics \
    "$SOURCE" \
    -o "$APP/Contents/MacOS/FlattenPDFs"

cp "$PLIST" "$APP/Contents/Info.plist"
cp "$ICON" "$APP/Contents/Resources/FlattenPDFs.icns"

# Ad-hoc signing prevents the locally built bundle from appearing unsigned to macOS.
codesign --force --deep --sign - "$APP" >/dev/null

# Refresh Launch Services so Finder immediately recognizes PDFs as accepted files.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
    -f "$APP" >/dev/null 2>&1 || true

printf '\nBuilt successfully:\n%s\n\n' "$APP"
echo "You can move the app to Applications, keep it in the Dock, and drag PDFs onto it."
open -R "$APP"
printf '\nPress Return to close this window.'
read -r
