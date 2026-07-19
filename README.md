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

**macOS** — run `mac/Build.command`. Needs only Apple's Command
Line Developer Tools (macOS offers to install them). The app uses system
frameworks alone: AppKit, PDFKit, Core Graphics.

**Windows** has two interchangeable builds, both needing the free
[.NET SDK](https://dotnet.microsoft.com/download/dotnet) version 10 or later
(`winget install Microsoft.DotNet.SDK.10`; each script explains this if it
is missing and downloads PDFium — the PDF engine used inside Edge and
Chrome — on first run):

- `winforms\Build.cmd` — the classic-look build. Everything, PDFium
  included, is packed into a single `Flatten PDFs.exe`. Running needs only
  the free .NET Desktop Runtime (prompted with a download link if missing).
- `winui\Build.cmd` — the modern Fluent build (WinUI 3): Mica window
  backdrop, themed dialogs, native drag visuals. WinUI cannot pack into a
  single exe, so the output is a folder to keep together, and running also
  needs the free Windows App Runtime (likewise prompted).

## Shared spec

`shared/app-spec.json` is the single source of truth for every target's
user-facing strings, layout metrics, and version. The macOS app carries it
as a bundle resource, the Windows apps embed it in their executables, all
read it at startup, and each build stamps the version from it. Edit the
spec, rebuild, and every app updates.

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

The UI lives in [`mac/main.swift`](mac/main.swift) (Swift,
PDFKit), [`winforms/Program.cs`](winforms/Program.cs) (C#,
Windows Forms), and [`winui/`](winui/) (C#, WinUI 3 —
conventional XAML plus code-behind at the project root, where its build
targets expect them). The two Windows targets share their
non-UI core — spec loader, PDFium interop, flattening engine, and work
queue — via
[`shared/Core.cs`](shared/Core.cs), compiled into both projects.

The app's name, version, strings, and layout all come from the shared spec,
so the plumbing (`App.csproj`, `Build.command` / `Build.cmd`, `app.icns` /
`app.ico`) is name-agnostic and reusable as a starting point for other
small multi-target apps.
