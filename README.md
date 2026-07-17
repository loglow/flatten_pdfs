# Flatten PDFs

Native macOS and Windows apps that flatten PDF annotations — stamps,
highlights, drawings, text boxes, signatures, and form fields — into
ordinary page content, replacing each PDF in place.

Give either app PDFs by dragging them onto the app icon, dropping them into
the window, or clicking **Select PDFs…** (**⌘O** / **Ctrl+O**). Each file is
flattened to a temporary file, validated (it must reopen, keep its page
count, and contain no remaining annotations), and only then atomically
swapped over the original — a failure leaves the original untouched. Names
and locations never change, results are reported in the log, and both apps
follow the system light/dark appearance.

## Build

Each platform builds with one double-click and puts the finished app in a
`build` folder next to the script.

**macOS** — run `mac/Build Flatten PDFs.command`. Needs only Apple's Command
Line Developer Tools (macOS offers to install them). The app uses system
frameworks alone: AppKit, PDFKit, Core Graphics.

**Windows** — run `windows\Build Flatten PDFs.cmd`. Needs the free
[.NET SDK](https://dotnet.microsoft.com/download/dotnet) version 10 or later
(`winget install Microsoft.DotNet.SDK.10`); the script explains this if it
is missing, and on first run downloads PDFium — the PDF engine used inside
Edge and Chrome — which ships as `pdfium.dll` beside the executable. Keep
the contents of `build` together. Machines that only run the app need the
free .NET Desktop Runtime; launching without it shows a download prompt.

## Shared spec

`shared/app-spec.json` is the single source of truth for both apps'
user-facing strings, layout metrics, and version. The macOS app carries it
as a bundle resource, the Windows app embeds it in the executable, both read
it at startup, and each build stamps the version from it. Edit the spec,
rebuild, and both apps update.

## Important limitations

- Keep a backup the first time you use the app on an important document.
- Flattening intentionally removes interactivity: links, form fields,
  comments, and annotation metadata no longer behave as annotations. A PDF
  whose only annotations are hyperlinks is left unchanged so they are not
  lost; when a page is flattened, the log notes how many hyperlinks it
  removed.
- Bookmarks, accessibility tags, and some document metadata may not survive.
- Password-protected or locked PDFs are rejected rather than overwritten.
- Only visible annotations are baked into the page.
- Existing cryptographic PDF signatures are invalidated by any modification,
  including flattening.

## Source

One file per platform: [`mac/Sources/main.swift`](mac/Sources/main.swift)
(Swift, PDFKit) and
[`windows/Sources/Program.cs`](windows/Sources/Program.cs) (C#, Windows
Forms, PDFium via P/Invoke; project file
[`windows/FlattenPDFs.csproj`](windows/FlattenPDFs.csproj)).
