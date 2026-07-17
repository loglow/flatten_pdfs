# Flatten PDFs for Windows

The Windows counterpart of the macOS app. Same job: for each PDF you give it,
it renders visible annotations — stamps, highlights, drawings, text boxes,
signatures, and form fields — into ordinary page content, validates the
result, and only then replaces the original file in place.

It uses standard Windows conventions rather than mimicking macOS: a normal
menu bar, **File ▸ Open…** / **Ctrl+O**, drag-and-drop, the standard
notification sound on completion, and ordinary message dialogs. It follows
the system's light or dark theme, including while running.

## Requirements

- Windows 10 or 11, 64-bit.
- **To build:** the free [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
  (version 10 or later) — one-time install, e.g.
  `winget install Microsoft.DotNet.SDK.10`. The build script checks for it and
  shows these instructions if it is missing.
- **To run** on a machine you didn't build on: the free .NET Desktop Runtime.
  If it is missing, launching the app shows a prompt with a direct download
  link. (The SDK already includes it, so the build machine needs nothing
  extra.)

## Build the app

1. Double-click **Build Flatten PDFs.cmd**.
2. The first build downloads the PDF engine (`pdfium.dll`) automatically. This
   needs an internet connection once; afterwards the file is reused.
3. When it finishes, Explorer opens the **build** folder and selects
   **Flatten PDFs.exe**.

The builder compiles a native 64-bit executable and places it in the `build`
folder next to `pdfium.dll`.

## Use

- Drag one or more PDF files **onto `Flatten PDFs.exe`**, or
- Launch the app and **drop PDFs into the window**, or
- Use **File ▸ Open…** (**Ctrl+O**) to pick PDFs.

The same files are replaced in place; their names and locations do not change.
Each result is reported in the log with a ✓ (flattened), – (nothing to
flatten; left unchanged), or ✗ (error) marker. **Ctrl+O** opens files;
**Alt+F4** or **File ▸ Exit** quits. **Help ▸ About Flatten PDFs** shows the
version.

Keep `pdfium.dll` in the same folder as the `.exe`. You can move the whole
`build` folder anywhere and pin the `.exe` to the Start menu or taskbar.

## How it works

The app writes the flattened PDF to a hidden temporary file in the same folder,
confirms it reopens, keeps the same page count, and contains no remaining
flattenable annotations, and then atomically swaps it over the original. A
failure leaves the original untouched.

Flattening is performed by **PDFium** — the PDF engine used inside Microsoft
Edge and Chrome (BSD-licensed). macOS ships its own PDF framework (PDFKit);
Windows has no in-box equivalent, so PDFium supplies the same capability:
`FPDFPage_Flatten` bakes annotation appearances into real, still-selectable
vector page content, and page rotation and document metadata are preserved.

## Important limitations

These mirror the macOS version:

- Keep a backup the first time you use the app on an important document.
- Flattening intentionally removes interactivity. Links, form fields,
  comments, and annotation metadata will no longer behave as annotations.
  If a PDF's only annotations are hyperlinks, it is left unchanged so those
  links are not lost; when a page is flattened, any hyperlinks it contained
  may be removed, and the log notes how many.
- Bookmarks, accessibility tags, digital signatures, and some document
  metadata may not survive the flatten.
- Password-protected or locked PDFs are rejected rather than overwritten.
- Only visible annotations are baked into the page.
- Existing cryptographic PDF signatures are invalidated by any modification,
  including flattening.

## Source

The complete implementation is in `Sources/Program.cs` — a single C# file
(Windows Forms on modern .NET) plus PDFium via P/Invoke, with the project
defined by `FlattenPDFs.csproj`. Light/dark theming comes from Windows Forms'
built-in color-mode support. The app icon is `Resources/app.ico`.
