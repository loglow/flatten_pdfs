#!/bin/bash
# Builds the Linux (GTK) app into gtk/build/.
#
# "Build" here means assemble: the app is Python, so this script checks the
# GTK dependencies, fetches PDFium on first run (cached in gtk/lib/, which
# is gitignored), and lays out a self-contained build/ folder containing
# the app, the shared spec, the PDFium library, a launcher named after the
# app, and a ready-to-install .desktop entry.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
SPEC="$ROOT/../shared/app-spec.json"
OUT="$ROOT/build"
LIBDIR="$ROOT/lib"

# Name and version come from the shared spec, like every other target.
NAME="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['name'])" "$SPEC")"
VERSION="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['version'])" "$SPEC")"
SLUG="$(python3 -c "import json,sys;print(''.join(c for c in json.load(open(sys.argv[1]))['name'].lower() if c.isalnum()))" "$SPEC")"

echo
echo "Building $NAME $VERSION..."
echo

# The app needs the GTK 4 and libadwaita Python bindings at runtime; check
# now so the failure is a clear message instead of an ImportError later.
if ! python3 -c "import gi; gi.require_version('Gtk', '4.0'); gi.require_version('Adw', '1'); from gi.repository import Gtk, Adw" 2>/dev/null; then
    echo "The GTK 4 and libadwaita Python bindings are required."
    echo "Install them with your distribution's package manager:"
    echo "  Debian/Ubuntu:  sudo apt install python3-gi gir1.2-gtk-4.0 gir1.2-adw-1"
    echo "  Fedora:         sudo dnf install python3-gobject gtk4 libadwaita"
    echo "  Arch:           sudo pacman -S python-gobject gtk4 libadwaita"
    exit 1
fi

# PDFium, downloaded once from the same prebuilt releases the Windows
# targets use.
case "$(uname -m)" in
    x86_64) PDFIUM_ARCH="x64" ;;
    aarch64 | arm64) PDFIUM_ARCH="arm64" ;;
    *) echo "Unsupported architecture: $(uname -m)"; exit 1 ;;
esac
if [ ! -f "$LIBDIR/libpdfium.so" ]; then
    echo "libpdfium.so not found. Downloading a prebuilt copy..."
    mkdir -p "$LIBDIR"
    TMP="$(mktemp -d)"
    trap 'rm -rf "$TMP"' EXIT
    curl -fsSL -o "$TMP/pdfium.tgz" \
        "https://github.com/bblanchon/pdfium-binaries/releases/latest/download/pdfium-linux-$PDFIUM_ARCH.tgz"
    tar -xzf "$TMP/pdfium.tgz" -C "$TMP" lib/libpdfium.so
    mv "$TMP/lib/libpdfium.so" "$LIBDIR/libpdfium.so"
    echo "libpdfium.so ready."
    echo
fi

rm -rf "$OUT"
mkdir -p "$OUT"
cp "$ROOT/main.py" "$OUT/"
cp "$SPEC" "$OUT/app-spec.json"
cp "$LIBDIR/libpdfium.so" "$OUT/"
cp "$ROOT/app.png" "$OUT/"

# A launcher named after the app, so the build folder reads like the other
# targets' output.
cat > "$OUT/$NAME" <<LAUNCHER
#!/bin/sh
exec python3 "\$(dirname "\$0")/main.py" "\$@"
LAUNCHER
chmod +x "$OUT/$NAME" "$OUT/main.py"

# A desktop entry with absolute paths into build/. Copying it into
# ~/.local/share/applications adds the app to the launcher and to PDF
# "Open With" menus — the Linux equivalent of dropping files on the app
# icon on the other platforms.
cat > "$OUT/$SLUG.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=$NAME
Exec="$OUT/$NAME" %F
Icon=$OUT/app.png
MimeType=application/pdf;
Categories=Utility;
Terminal=false
DESKTOP

echo
echo "Built successfully:"
echo "  $OUT/$NAME"
echo
echo "Run it directly, or copy $SLUG.desktop into ~/.local/share/applications"
echo "to add it to your launcher and to PDF \"Open With\" menus."
echo
