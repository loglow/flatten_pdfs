#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
OUT="$ROOT/build"
APP="$OUT/Flatten PDFs.app"
SOURCES="$ROOT/Sources"
PLIST="$ROOT/Resources/Info.plist"
ICON="$ROOT/Resources/FlattenPDFs.icns"
SPEC="$ROOT/../shared/app-spec.json"

printf '\nBuilding Flatten PDFs…\n\n'

if ! command -v xcrun >/dev/null 2>&1 || ! xcrun --find swiftc >/dev/null 2>&1; then
    echo "Apple's Command Line Developer Tools are required."
    echo "A macOS installer prompt should appear now. After installation, run this file again."
    xcode-select --install 2>/dev/null || true
    printf '\nPress Return to close this window.'
    read -r
    exit 1
fi

rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

export MACOSX_DEPLOYMENT_TARGET="11.0"

xcrun swiftc \
    -O \
    -whole-module-optimization \
    -framework AppKit \
    -framework PDFKit \
    -framework CoreGraphics \
    "$SOURCES"/*.swift \
    -o "$APP/Contents/MacOS/FlattenPDFs"

cp "$PLIST" "$APP/Contents/Info.plist"
cp "$ICON" "$APP/Contents/Resources/FlattenPDFs.icns"

# The shared spec rides along as a bundle resource (the app reads it at
# launch), and its version stamps the bundle's Info.plist.
cp "$SPEC" "$APP/Contents/Resources/app-spec.json"
VERSION="$(plutil -extract version raw -o - "$SPEC")"
BUILDNUM="$(plutil -extract buildNumber raw -o - "$SPEC")"
/usr/libexec/PlistBuddy \
    -c "Set :CFBundleShortVersionString $VERSION" \
    -c "Set :CFBundleVersion $BUILDNUM" \
    "$APP/Contents/Info.plist"

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
