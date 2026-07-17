// Flatten PDFs for Windows.
//
// A small native Windows app that bakes visible PDF annotations (stamps,
// highlights, drawings, text boxes, signatures, form fields) into ordinary
// page content and replaces each PDF in place. It is the Windows counterpart
// of the macOS build: same behavior, standard Windows interface and
// conventions (menu bar, Open... / Ctrl+O, drag-and-drop, message boxes)
// rather than macOS ones, and it follows the system light or dark theme
// through Windows Forms' built-in color-mode support.
//
// The macOS version relies on PDFKit, a system framework unique to macOS that
// both renders annotations and writes real (vector) PDFs. Windows has no
// in-box equivalent, so the flattening engine here is PDFium -- the same PDF
// engine used inside Microsoft Edge and Chrome. PDFium's FPDFPage_Flatten
// bakes annotation appearances into page content as real, still-selectable
// vector content, and FPDF_SaveAsCopy preserves page rotation and document
// metadata automatically.
//
// The app targets modern .NET (see FlattenPDFs.csproj) and is built with the
// .NET SDK via "Build Flatten PDFs.cmd". Running it requires the free .NET
// Desktop Runtime; the only other dependency is pdfium.dll beside the
// executable, which the build script downloads once.

using System.Collections.Concurrent;
using System.Media;
using System.Runtime.InteropServices;

namespace FlattenPDFs;

// ---------------------------------------------------------------------------
// PDFium native interop.
//
// Only the handful of PDFium C API functions this app needs are declared.
// FPDF_CALLCONV is __stdcall on Windows, which is the DllImport default
// (CallingConvention.Winapi) on this platform. The executable and pdfium.dll
// are both built for x64.
// ---------------------------------------------------------------------------
internal static class Pdfium
{
    private const string Dll = "pdfium.dll";

    [DllImport(Dll)]
    internal static extern void FPDF_InitLibrary();

    [DllImport(Dll)]
    internal static extern IntPtr FPDF_LoadMemDocument(
        IntPtr dataBuffer, int size, [MarshalAs(UnmanagedType.LPStr)] string? password);

    [DllImport(Dll)]
    internal static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Dll)]
    internal static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Dll)]
    internal static extern uint FPDF_GetLastError();

    [DllImport(Dll)]
    internal static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(Dll)]
    internal static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Dll)]
    internal static extern int FPDFPage_Flatten(IntPtr page, int flag);

    [DllImport(Dll)]
    internal static extern int FPDFPage_GetAnnotCount(IntPtr page);

    [DllImport(Dll)]
    internal static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);

    [DllImport(Dll)]
    internal static extern int FPDFAnnot_GetSubtype(IntPtr annotation);

    [DllImport(Dll)]
    internal static extern void FPDFPage_CloseAnnot(IntPtr annotation);

    [DllImport(Dll)]
    internal static extern int FPDF_SaveAsCopy(
        IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags);

    // The output sink FPDF_SaveAsCopy writes to. WriteBlock is a function
    // pointer; it must return non-zero on success.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int WriteBlockCallback(IntPtr self, IntPtr data, uint size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FPDF_FILEWRITE
    {
        public int Version;
        public IntPtr WriteBlock;
    }
}

// ---------------------------------------------------------------------------
// Flattening.
// ---------------------------------------------------------------------------

// Thrown for expected, user-facing failures (not a real PDF, encrypted, no
// pages, validation failed). The message is shown in the log verbatim.
internal sealed class FlattenException(string message) : Exception(message)
{
}

internal readonly record struct FlattenResult(
    int PageCount, int FlattenedAnnotationCount, int RemovedLinkCount, bool Changed);

internal sealed class PdfFlattener
{
    // Annotation subtypes that draw no content of their own. A document whose
    // only annotations are these gains nothing visible from flattening -- and
    // links would lose their targets -- so they do not trigger a rewrite.
    // (Popup is the companion window of a note.)
    private const int FPDF_ANNOT_LINK = 2;
    private const int FPDF_ANNOT_POPUP = 16;

    private const int FLAT_NORMALDISPLAY = 0;
    private const int FLATTEN_FAIL = 0;

    private const uint FPDF_NO_INCREMENTAL = 2;
    private const uint FPDF_ERR_PASSWORD = 4;

    private static bool _initialized;
    private static readonly Lock InitLock = new();

    // PDFium keeps global state; initialize it once. All PDFium calls in this
    // app run on the single worker thread, so they never overlap.
    private static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (!_initialized)
            {
                Pdfium.FPDF_InitLibrary();
                _initialized = true;
            }
        }
    }

    // A PDF is identified by its extension. Unlike macOS, Windows offers no
    // cheap content-type probe here, but files reaching this app arrive as
    // real paths from Explorer or the Open dialog and effectively always
    // carry a .pdf extension.
    public static bool IsPdfPath(string path) =>
        !string.IsNullOrEmpty(path) &&
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public FlattenResult FlattenInPlace(string path)
    {
        EnsureInitialized();

        if (!IsPdfPath(path))
        {
            throw new FlattenException("The item is not a PDF file.");
        }
        if (!File.Exists(path))
        {
            throw new FlattenException("The PDF could not be opened.");
        }

        byte[] input;
        try
        {
            input = File.ReadAllBytes(path);
        }
        catch
        {
            throw new FlattenException("The PDF could not be opened.");
        }

        // Load from memory (rather than by path) so .NET handles Unicode file
        // names; the buffer stays pinned until the document is closed because
        // PDFium reads it lazily.
        GCHandle inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        IntPtr document = IntPtr.Zero;
        try
        {
            document = Pdfium.FPDF_LoadMemDocument(
                inputHandle.AddrOfPinnedObject(), input.Length, null);
            if (document == IntPtr.Zero)
            {
                throw Pdfium.FPDF_GetLastError() == FPDF_ERR_PASSWORD
                    ? new FlattenException(
                        "The PDF is encrypted or locked. Unlock it first, then try again.")
                    : new FlattenException("The PDF could not be opened.");
            }

            int pageCount = Pdfium.FPDF_GetPageCount(document);
            if (pageCount <= 0)
            {
                throw new FlattenException("The PDF contains no pages.");
            }

            (int flattenableCount, int linkCount) = CountAnnotations(document, pageCount);

            // Do not rewrite a file that has nothing visible to flatten. This
            // also protects documents whose only annotations are hyperlinks,
            // which a flatten pass could drop.
            if (flattenableCount == 0)
            {
                return new FlattenResult(pageCount, 0, 0, Changed: false);
            }

            // Bake each page's annotations and form fields into real page
            // content.
            for (int i = 0; i < pageCount; i++)
            {
                IntPtr page = Pdfium.FPDF_LoadPage(document, i);
                if (page == IntPtr.Zero)
                {
                    throw new FlattenException($"Page {i + 1} could not be read.");
                }
                try
                {
                    if (Pdfium.FPDFPage_Flatten(page, FLAT_NORMALDISPLAY) == FLATTEN_FAIL)
                    {
                        throw new FlattenException($"Page {i + 1} could not be flattened.");
                    }
                }
                finally
                {
                    Pdfium.FPDF_ClosePage(page);
                }
            }

            byte[] output = SaveDocument(document)
                ?? throw new FlattenException("A flattened PDF could not be created.");

            // Validate the new PDF fully before touching the original: it
            // must reopen, keep the same page count, and retain no
            // flattenable annotations. Only after that does the file get
            // swapped, so a failure leaves the original untouched.
            int outputLinkCount = ValidateOutput(output, pageCount);
            int removedLinks = Math.Max(0, linkCount - outputLinkCount);

            // Close the source document (releasing the input buffer) before
            // replacing the file on disk.
            Pdfium.FPDF_CloseDocument(document);
            document = IntPtr.Zero;

            WriteAtomically(path, output);

            return new FlattenResult(pageCount, flattenableCount, removedLinks, Changed: true);
        }
        finally
        {
            if (document != IntPtr.Zero)
            {
                Pdfium.FPDF_CloseDocument(document);
            }
            if (inputHandle.IsAllocated)
            {
                inputHandle.Free();
            }
        }
    }

    private static (int Flattenable, int Links) CountAnnotations(IntPtr document, int pageCount)
    {
        int flattenable = 0;
        int links = 0;
        for (int i = 0; i < pageCount; i++)
        {
            IntPtr page = Pdfium.FPDF_LoadPage(document, i);
            if (page == IntPtr.Zero)
            {
                throw new FlattenException($"Page {i + 1} could not be read.");
            }
            try
            {
                int count = Pdfium.FPDFPage_GetAnnotCount(page);
                for (int a = 0; a < count; a++)
                {
                    IntPtr annotation = Pdfium.FPDFPage_GetAnnot(page, a);
                    if (annotation == IntPtr.Zero)
                    {
                        continue;
                    }
                    try
                    {
                        switch (Pdfium.FPDFAnnot_GetSubtype(annotation))
                        {
                            case FPDF_ANNOT_LINK:
                                links++;
                                break;
                            case FPDF_ANNOT_POPUP:
                                break;
                            default:
                                flattenable++;
                                break;
                        }
                    }
                    finally
                    {
                        Pdfium.FPDFPage_CloseAnnot(annotation);
                    }
                }
            }
            finally
            {
                Pdfium.FPDF_ClosePage(page);
            }
        }
        return (flattenable, links);
    }

    // Serializes the (already flattened) document to a byte array through
    // FPDF_SaveAsCopy. FPDF_NO_INCREMENTAL writes a clean, fully rewritten
    // file rather than appending changes. Returns null on failure.
    private static byte[]? SaveDocument(IntPtr document)
    {
        using MemoryStream buffer = new();
        Pdfium.WriteBlockCallback writeBlock = (_, data, size) =>
        {
            try
            {
                byte[] chunk = new byte[(int)size];
                Marshal.Copy(data, chunk, 0, chunk.Length);
                buffer.Write(chunk, 0, chunk.Length);
                return 1;
            }
            catch
            {
                return 0;
            }
        };

        Pdfium.FPDF_FILEWRITE fileWrite = new()
        {
            Version = 1,
            WriteBlock = Marshal.GetFunctionPointerForDelegate(writeBlock)
        };

        int ok = Pdfium.FPDF_SaveAsCopy(document, ref fileWrite, FPDF_NO_INCREMENTAL);
        // Keep the delegate alive until the native call, which invokes it
        // synchronously, has returned.
        GC.KeepAlive(writeBlock);

        return ok == 0 || buffer.Length == 0 ? null : buffer.ToArray();
    }

    private static int ValidateOutput(byte[] output, int expectedPageCount)
    {
        const string failed =
            "The flattened PDF failed validation, so the original was not changed.";

        GCHandle handle = GCHandle.Alloc(output, GCHandleType.Pinned);
        IntPtr document = IntPtr.Zero;
        try
        {
            document = Pdfium.FPDF_LoadMemDocument(
                handle.AddrOfPinnedObject(), output.Length, null);
            if (document == IntPtr.Zero)
            {
                throw new FlattenException(failed);
            }

            int pages = Pdfium.FPDF_GetPageCount(document);
            if (pages != expectedPageCount)
            {
                throw new FlattenException(failed);
            }

            (int remainingFlattenable, int linkCount) = CountAnnotations(document, pages);
            if (remainingFlattenable != 0)
            {
                throw new FlattenException(failed);
            }
            return linkCount;
        }
        finally
        {
            if (document != IntPtr.Zero)
            {
                Pdfium.FPDF_CloseDocument(document);
            }
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    // Writes to a temporary file in the same directory, then swaps it over
    // the original. File.Replace is atomic on NTFS; a copy+delete fallback
    // covers file systems that do not support it (FAT/exFAT, some shares).
    private static void WriteAtomically(string path, byte[] data)
    {
        string directory = Path.GetDirectoryName(path) is { Length: > 0 } parent ? parent : ".";
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.flatten-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, data);
            try
            {
                File.Replace(temporary, path, null);
            }
            catch (Exception e) when (
                e is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                File.Copy(temporary, path, overwrite: true);
                File.Delete(temporary);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    // A leftover temp file is harmless; ignore.
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// User interface.
// ---------------------------------------------------------------------------
internal sealed class MainForm : Form
{
    // Status markers in the log, written as Unicode escapes so the source
    // file's encoding never affects them: check mark, en dash, ballot X, and
    // an em-dash separator.
    private const string MarkOk = "\u2713";   // check mark
    private const string MarkSkip = "\u2013"; // en dash
    private const string MarkFail = "\u2717"; // ballot X
    private const string Dash = " \u2014 ";    // em-dash separator

    private readonly PdfFlattener _flattener = new();
    private readonly BlockingCollection<string[]> _queue = [];
    private readonly string[] _initialFiles;

    // A borderless LogBox (see below) avoids the themed Edit-control frame:
    // no resting bottom line and no accent underline or caret on focus. Text
    // remains selectable and copyable.
    private readonly LogBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        WordWrap = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Window,
        ForeColor = SystemColors.WindowText,
        // 8.25 pt = 11 px, matching the Mac app's 11 px monospaced log font.
        Font = new Font("Consolas", 8.25f),
        Dock = DockStyle.Fill,
        TabStop = false
    };

    // Layout dimensions and font sizes mirror the macOS app. macOS points map
    // 1:1 to pixels here (at 96 DPI), and to Windows font points at x0.75:
    // the Mac's 24 pt semibold title is Segoe UI Semibold 18 pt, its 13 pt
    // detail text is 9.75 pt, and its 20/12/20 paddings carry over directly.
    //
    // Fonts scale with the display DPI automatically (they are in points),
    // but pixel-valued properties -- window size, paddings, margins -- do
    // not, and WinForms' form auto-scaling is unreliable for hand-built
    // forms. So the actual DPI is measured once and every pixel dimension is
    // multiplied explicitly through Px().
    private readonly float _scale;

    private int Px(int value) => (int)MathF.Round(value * _scale);

    private readonly TableLayoutPanel _content = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 4
    };

    private readonly Label _detail = new()
    {
        Text = "Annotations will be flattened and each PDF will be permanently updated.",
        Font = new Font("Segoe UI", 9.75f),
        ForeColor = SystemColors.GrayText,
        AutoSize = true,
        // Anchor None centers an auto-sized control in its table cell.
        Anchor = AnchorStyles.None
    };

    private readonly Button _openButton = new()
    {
        Text = "Select PDFs...",
        AutoSize = true
    };

    private readonly Button _clearButton = new()
    {
        Text = "Clear Log",
        AutoSize = true
    };

    private readonly ToolStripMenuItem _openMenuItem = new("&Open...")
    {
        ShortcutKeys = Keys.Control | Keys.O
    };

    private int _activeBatches;
    private bool _dragHighlighted;

    public MainForm(string[] initialFiles)
    {
        _initialFiles = initialFiles;
        // Creating a Graphics forces the handle into existence, so the DPI it
        // reports is the real one for this window, on every scaling setup.
        using (Graphics g = CreateGraphics())
        {
            _scale = g.DpiX / 96f;
        }
        BuildInterface();

        new Thread(WorkerLoop) { IsBackground = true, Name = "FlattenPDFs.worker" }.Start();
    }

    private void BuildInterface()
    {
        Text = "Flatten PDFs";
        StartPosition = FormStartPosition.CenterScreen;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // Fall back to the default window icon.
        }

        // Menu bar.
        _openMenuItem.Click += OnOpen;
        ToolStripMenuItem exitItem = new("E&xit");
        exitItem.Click += (_, _) => Close();
        ToolStripMenuItem fileMenu = new("&File");
        fileMenu.DropDownItems.Add(_openMenuItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(exitItem);

        ToolStripMenuItem aboutItem = new("&About Flatten PDFs");
        aboutItem.Click += OnAbout;
        ToolStripMenuItem helpMenu = new("&Help");
        helpMenu.DropDownItems.Add(aboutItem);

        MenuStrip menu = new();
        menu.Items.Add(fileMenu);
        menu.Items.Add(helpMenu);
        MainMenuStrip = menu;

        // The whole window is the drop target; the content sits directly on
        // the form. The active-drop outline is painted in _content's padding
        // ring (see OnContentPaint).
        _content.Padding = new Padding(Px(20));
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _content.Paint += OnContentPaint;

        Label title = new()
        {
            Text = "Drop PDF files here",
            Font = new Font("Segoe UI Semibold", 18f),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, Px(12))
        };

        _detail.Margin = new Padding(0, 0, 0, Px(12));

        _openButton.Click += OnOpen;
        _openButton.Padding = new Padding(Px(8), Px(2), Px(8), Px(2));
        _openButton.Margin = new Padding(0, 0, Px(8), 0);
        _clearButton.Click += (_, _) => _log.Clear();
        _clearButton.Padding = new Padding(Px(8), Px(2), Px(8), Px(2));
        _clearButton.Margin = new Padding(0);

        FlowLayoutPanel buttons = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, Px(20))
        };
        buttons.Controls.Add(_openButton);
        buttons.Controls.Add(_clearButton);

        _log.Margin = new Padding(0);

        _content.Controls.Add(title, 0, 0);
        _content.Controls.Add(_detail, 0, 1);
        _content.Controls.Add(buttons, 0, 2);
        _content.Controls.Add(_log, 0, 3);

        Controls.Add(_content);
        Controls.Add(menu);

        // Same starting and minimum sizes as the macOS app, plus the menu
        // strip's height: the Mac has no in-window menu bar, so matching its
        // 650x430 (min 520x360) means matching the *content* area below the
        // menu.
        int menuHeight = menu.PreferredSize.Height;
        ClientSize = new Size(Px(650), Px(430) + menuHeight);
        MinimumSize = new Size(Px(520), Px(360) + menuHeight);

        // Accept drops anywhere over the window and its content.
        EnableDrop(this);
        EnableDrop(_content);
        EnableDrop(title);
        EnableDrop(_detail);
        EnableDrop(buttons);
        EnableDrop(_log);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_initialFiles.Length > 0)
        {
            EnqueueBatch(_initialFiles);
        }
    }

    // ------- Drag and drop -------

    private void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnDragEnter;
        control.DragDrop += OnDragDrop;
        control.DragLeave += OnDragLeave;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (GetDroppedFiles(e.Data).Length > 0)
        {
            e.Effect = DragDropEffects.Copy;
            SetHighlight(true);
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        SetHighlight(false);
        string[] files = GetDroppedFiles(e.Data);
        if (files.Length > 0)
        {
            EnqueueBatch(files);
        }
    }

    private void OnDragLeave(object? sender, EventArgs e)
    {
        // DragLeave also fires when the pointer crosses from one control onto
        // another inside the window, which would flicker the highlight. Only
        // clear it once the pointer is truly outside.
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position)))
        {
            SetHighlight(false);
        }
    }

    // Windows has no system-wide drop-target style for plain windows, so an
    // accent-color outline around the window content indicates an active
    // drop. It is stroked inside _content's padding ring, where no child
    // control can cover it. SystemColors.Highlight reflects the system accent
    // and the active light/dark color mode when read.
    private void SetHighlight(bool highlighted)
    {
        if (_dragHighlighted == highlighted)
        {
            return;
        }
        _dragHighlighted = highlighted;
        _content.Invalidate();
    }

    private void OnContentPaint(object? sender, PaintEventArgs e)
    {
        if (!_dragHighlighted)
        {
            return;
        }
        Rectangle bounds = _content.ClientRectangle;
        bounds.Inflate(-Px(2), -Px(2));
        using Pen pen = new(SystemColors.Highlight, 3f * _scale);
        e.Graphics.DrawRectangle(pen, bounds);
    }

    private static string[] GetDroppedFiles(IDataObject? data)
    {
        if (data?.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }
        return [.. paths.Where(PdfFlattener.IsPdfPath)];
    }

    // ------- Menu / button actions -------

    private void OnOpen(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Select PDFs",
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            EnqueueBatch(dialog.FileNames);
        }
    }

    private void OnAbout(object? sender, EventArgs e)
    {
        MessageBox.Show(
            this,
            $"Flatten PDFs {Application.ProductVersion}",
            "About Flatten PDFs",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_activeBatches > 0)
        {
            DialogResult answer = MessageBox.Show(
                this,
                "Files are still being processed. Quit anyway?",
                "Flatten PDFs",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        _queue.CompleteAdding();
        base.OnFormClosing(e);
    }

    // ------- Work queue -------

    private void EnqueueBatch(string[] files)
    {
        string[] pdfs = [.. files.Where(PdfFlattener.IsPdfPath)];
        if (pdfs.Length == 0)
        {
            AppendLog("No PDF files were selected.");
            return;
        }

        Interlocked.Increment(ref _activeBatches);
        SetBusy(true);
        _queue.Add(pdfs);
    }

    private void WorkerLoop()
    {
        foreach (string[] batch in _queue.GetConsumingEnumerable())
        {
            foreach (string path in batch)
            {
                string name = Path.GetFileName(path);
                try
                {
                    FlattenResult result = _flattener.FlattenInPlace(path);
                    PostLog(FormatResult(name, result));
                }
                catch (Exception error)
                {
                    PostLog($"{MarkFail} {name}{Dash}{error.Message}");
                }
            }

            if (Interlocked.Decrement(ref _activeBatches) == 0)
            {
                PostBatchComplete();
            }
        }
    }

    private static string FormatResult(string name, FlattenResult result)
    {
        if (!result.Changed)
        {
            return $"{MarkSkip} {name}{Dash}no flattenable annotations; unchanged.";
        }

        string annotations = result.FlattenedAnnotationCount == 1 ? "annotation" : "annotations";
        string pages = result.PageCount == 1 ? "page" : "pages";
        string line = $"{MarkOk} {name}{Dash}flattened {result.FlattenedAnnotationCount} " +
                      $"{annotations} on {result.PageCount} {pages}.";
        if (result.RemovedLinkCount > 0)
        {
            string links = result.RemovedLinkCount == 1 ? "hyperlink" : "hyperlinks";
            line += $" Note: {result.RemovedLinkCount} {links} removed.";
        }
        return line;
    }

    // ------- Thread-marshaled UI updates -------

    private void PostLog(string line) => Post(() => AppendLog(line));

    private void PostBatchComplete() => Post(() =>
    {
        SetBusy(false);
        SystemSounds.Asterisk.Play();
    });

    // Marshals an action to the UI thread, tolerating the window being torn
    // down (e.g. the user quit while a batch was still running).
    private void Post(Action action)
    {
        if (!IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed between the check and the call; ignore.
        }
    }

    private void AppendLog(string line)
    {
        if (_log.TextLength > 0)
        {
            _log.AppendText("\r\n");
        }
        _log.AppendText(line);
    }

    private void SetBusy(bool busy)
    {
        _openButton.Enabled = !busy;
        _openMenuItem.Enabled = !busy;
    }
}

// A read-only log TextBox that never shows a caret. Suppressing the caret
// (HideCaret's hide count is cumulative, so calling it after every message
// keeps it hidden for good) means the control never looks focused, while
// mouse selection and Ctrl+C continue to work normally.
internal sealed class LogBox : TextBox
{
    [DllImport("user32.dll")]
    private static extern bool HideCaret(IntPtr hWnd);

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (IsHandleCreated)
        {
            HideCaret(Handle);
        }
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Follow the system light/dark setting, including changes while the
        // app is running. (Still marked experimental; see WFO5001 in the
        // project file.)
        Application.SetColorMode(SystemColorMode.System);
        Application.Run(new MainForm(args));
    }
}
