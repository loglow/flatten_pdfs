#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
OUT="$ROOT/build"
SOURCES="$ROOT/Sources"
PLIST="$ROOT/Resources/Info.plist"
ICON="$ROOT/Resources/app.icns"
SPEC="$ROOT/../shared/app-spec.json"

# The app's identity comes from the shared spec: the bundle and display name
# verbatim, the executable name with spaces stripped, and the bundle
# identifier as a lowercase slug.
NAME="$(plutil -extract name raw -o - "$SPEC")"
VERSION="$(plutil -extract version raw -o - "$SPEC")"
BUILDNUM="$(plutil -extract buildNumber raw -o - "$SPEC")"
EXEC="$(printf '%s' "$NAME" | tr -d ' ')"
SLUG="$(printf '%s' "$NAME" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9')"
APP="$OUT/$NAME.app"

printf '\nBuilding %s…\n\n' "$NAME"

if ! command -v xcrun >/dev/null 2>&1 || ! xcrun --find swiftc >/dev/null 2>&1; then
    echo "Apple's Command Line Developer Tools are required."
    echo "A macOS installer prompt should appear now. After installation, run this file again."
    xcode-select --install 2>/dev/null || true
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
    -o "$APP/Contents/MacOS/$EXEC"

cp "$PLIST" "$APP/Contents/Info.plist"
cp "$ICON" "$APP/Contents/Resources/app.icns"

# The shared spec rides along as a bundle resource (the app reads it at
# launch), and stamps the bundle's identity and version.
cp "$SPEC" "$APP/Contents/Resources/app-spec.json"
/usr/libexec/PlistBuddy \
    -c "Set :CFBundleName $NAME" \
    -c "Set :CFBundleDisplayName $NAME" \
    -c "Set :CFBundleExecutable $EXEC" \
    -c "Set :CFBundleIdentifier local.$SLUG.app" \
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

# Reveal the app in Finder, except when running unattended (CI is set).
if [ -z "${CI:-}" ]; then
    open -R "$APP"
fi
