# Flatten PDFs for macOS

> **Windows?** A native Windows version with the same behavior lives in
> [`windows/`](windows/) — see [windows/README.md](windows/README.md).

A small native macOS app that accepts one or more PDFs by:

- dragging them onto the app icon;
- dragging them into the app window; or
- clicking **Select PDFs…** or pressing **Command-O**.

For each PDF, the app renders visible PDF annotations—such as stamps, highlights, drawings, text boxes, and signatures—into ordinary page content. It validates the new PDF and only then replaces the original file.

## Build the app

1. Double-click **Build Flatten PDFs.command**.
2. If macOS asks to install the Command Line Developer Tools, install them and run the builder again.
3. Finder will reveal **Flatten PDFs.app** when the build succeeds.
4. Move the app to **Applications** or the Dock.

The builder compiles a native app for the Mac on which it is run and ad-hoc signs it locally.

## Use

Drag one or more PDF files onto **Flatten PDFs.app**. The same files are replaced in place; their names and locations do not change. Press **Command-O** to select PDFs, **Command-Q** to quit, or **Command-W** to close the window and quit. The standard **Flatten PDFs → About Flatten PDFs** panel displays the installed version number.

The app first writes a hidden temporary PDF in the same folder, validates its page count and confirms that it contains no annotation objects, and then swaps it over the original. A failure leaves the original untouched.

## Important limitations

- Keep a backup the first time you use the app on an important document.
- Flattening intentionally removes interactivity. Links, form fields, comments, embedded files, and annotation metadata will no longer behave as annotations.
- Bookmarks, advanced PDF page boxes, accessibility tags, digital signatures, encryption, and some document metadata may not survive the rendering pass.
- Password-protected or locked PDFs are rejected rather than overwritten.
- Only visible annotations are baked into the page. Hidden annotations are discarded.
- Existing cryptographic PDF signatures will be invalidated by any modification, including flattening.

## Source

The complete implementation is in `Sources/main.swift`. It uses only macOS system frameworks: AppKit, PDFKit, Core Graphics, and Uniform Type Identifiers. The app icon is included as a native `.icns` resource.
