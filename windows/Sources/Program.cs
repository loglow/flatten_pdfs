// Flatten PDFs for Windows.
//
// A small native Windows app that bakes visible PDF annotations (stamps,
// highlights, drawings, text boxes, signatures, form fields) into ordinary
// page content and replaces each PDF in place. It is the Windows counterpart
// of the macOS build: same behavior, standard Windows interface and
// conventions (menu bar, Open... / Ctrl+O, drag-and-drop, message boxes)
// rather than macOS ones, and it follows the system light or dark theme.
//
// The macOS version relies on PDFKit, a system framework unique to macOS that
// both renders annotations and writes real (vector) PDFs. Windows has no
// in-box equivalent, so the flattening engine here is PDFium -- the same PDF
// engine used inside Microsoft Edge and Chrome. PDFium's FPDFPage_Flatten
// bakes annotation appearances into page content as real, still-selectable
// vector content, and FPDF_SaveAsCopy preserves page rotation and document
// metadata automatically.
//
// This single source file is compiled with the in-box .NET Framework C#
// compiler (csc.exe); see "Build Flatten PDFs.cmd". It targets C# 5 language
// features so no SDK, Visual Studio, or NuGet is required -- only pdfium.dll
// beside the executable, which the build script downloads once.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Flatten PDFs")]
[assembly: AssemblyProduct("Flatten PDFs")]
[assembly: AssemblyDescription("Flattens PDF annotations into page content and replaces each file in place.")]
[assembly: AssemblyVersion("1.7.0.0")]
[assembly: AssemblyFileVersion("1.7.0.0")]

namespace FlattenPDFs
{
    // ---------------------------------------------------------------------
    // PDFium native interop.
    //
    // Only the handful of PDFium C API functions this app needs are declared.
    // FPDF_CALLCONV is __stdcall on Windows, which is the DllImport default
    // (CallingConvention.Winapi) on this platform. The executable and
    // pdfium.dll are both built for x64.
    // ---------------------------------------------------------------------
    internal static class Pdfium
    {
        private const string Dll = "pdfium.dll";

        [DllImport(Dll)]
        internal static extern void FPDF_InitLibrary();

        [DllImport(Dll)]
        internal static extern void FPDF_DestroyLibrary();

        [DllImport(Dll)]
        internal static extern IntPtr FPDF_LoadMemDocument(
            IntPtr dataBuffer, int size, [MarshalAs(UnmanagedType.LPStr)] string password);

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
        // pointer; it must return non-zero on success. FPDF_CALLCONV maps to
        // __stdcall on Windows.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int WriteBlockCallback(IntPtr self, IntPtr data, uint size);

        [StructLayout(LayoutKind.Sequential)]
        internal struct FPDF_FILEWRITE
        {
            public int Version;
            public IntPtr WriteBlock;
        }
    }

    // ---------------------------------------------------------------------
    // Flattening.
    // ---------------------------------------------------------------------

    // Thrown for expected, user-facing failures (not a real PDF, encrypted,
    // no pages, validation failed). The message is shown in the log verbatim.
    internal sealed class FlattenException : Exception
    {
        public FlattenException(string message) : base(message) { }
    }

    internal struct FlattenResult
    {
        public int PageCount;
        public int FlattenedAnnotationCount;
        public int RemovedLinkCount;
        public bool Changed;
    }

    internal sealed class PdfFlattener
    {
        // Annotation subtypes that draw no content of their own. A document
        // whose only annotations are these gains nothing visible from
        // flattening -- and links would lose their targets -- so they do not
        // trigger a rewrite. (Popup is the companion window of a note.)
        private const int FPDF_ANNOT_LINK = 2;
        private const int FPDF_ANNOT_POPUP = 16;

        private const int FLAT_NORMALDISPLAY = 0;
        private const int FLATTEN_FAIL = 0;

        private const uint FPDF_NO_INCREMENTAL = 2;
        private const uint FPDF_ERR_PASSWORD = 4;

        private static bool _initialized;
        private static readonly object _initLock = new object();

        // PDFium keeps global state; initialize it once. All PDFium calls in
        // this app run on the single worker thread, so they never overlap.
        private static void EnsureInitialized()
        {
            lock (_initLock)
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
        public static bool IsPdfPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            string extension = Path.GetExtension(path);
            return extension != null &&
                   extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        }

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

            // Load from memory (rather than by path) so .NET handles Unicode
            // file names; the buffer stays pinned until the document is closed
            // because PDFium reads it lazily.
            GCHandle inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            IntPtr document = IntPtr.Zero;
            try
            {
                document = Pdfium.FPDF_LoadMemDocument(
                    inputHandle.AddrOfPinnedObject(), input.Length, null);
                if (document == IntPtr.Zero)
                {
                    uint error = Pdfium.FPDF_GetLastError();
                    if (error == FPDF_ERR_PASSWORD)
                    {
                        throw new FlattenException(
                            "The PDF is encrypted or locked. Unlock it first, then try again.");
                    }
                    throw new FlattenException("The PDF could not be opened.");
                }

                int pageCount = Pdfium.FPDF_GetPageCount(document);
                if (pageCount <= 0)
                {
                    throw new FlattenException("The PDF contains no pages.");
                }

                int flattenableCount;
                int linkCount;
                CountAnnotations(document, pageCount, out flattenableCount, out linkCount);

                // Do not rewrite a file that has nothing visible to flatten.
                // This also protects documents whose only annotations are
                // hyperlinks, which a flatten pass could drop.
                if (flattenableCount == 0)
                {
                    return MakeResult(pageCount, 0, 0, false);
                }

                // Bake each page's annotations and form fields into real page
                // content.
                for (int i = 0; i < pageCount; i++)
                {
                    IntPtr page = Pdfium.FPDF_LoadPage(document, i);
                    if (page == IntPtr.Zero)
                    {
                        throw new FlattenException(
                            string.Format("Page {0} could not be read.", i + 1));
                    }
                    try
                    {
                        int flattenStatus = Pdfium.FPDFPage_Flatten(page, FLAT_NORMALDISPLAY);
                        if (flattenStatus == FLATTEN_FAIL)
                        {
                            throw new FlattenException(
                                string.Format("Page {0} could not be flattened.", i + 1));
                        }
                    }
                    finally
                    {
                        Pdfium.FPDF_ClosePage(page);
                    }
                }

                byte[] output = SaveDocument(document);
                if (output == null || output.Length == 0)
                {
                    throw new FlattenException("A flattened PDF could not be created.");
                }

                // Validate the new PDF fully before touching the original: it
                // must reopen, keep the same page count, and retain no
                // flattenable annotations. Only after that does the file get
                // swapped, so a failure leaves the original untouched.
                int outputLinkCount;
                ValidateOutput(output, pageCount, out outputLinkCount);

                int removedLinks = linkCount - outputLinkCount;
                if (removedLinks < 0)
                {
                    removedLinks = 0;
                }

                // Close the source document (releasing the input buffer) before
                // replacing the file on disk.
                Pdfium.FPDF_CloseDocument(document);
                document = IntPtr.Zero;

                WriteAtomically(path, output);

                return MakeResult(pageCount, flattenableCount, removedLinks, true);
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

        private static void CountAnnotations(
            IntPtr document, int pageCount, out int flattenable, out int links)
        {
            flattenable = 0;
            links = 0;
            for (int i = 0; i < pageCount; i++)
            {
                IntPtr page = Pdfium.FPDF_LoadPage(document, i);
                if (page == IntPtr.Zero)
                {
                    throw new FlattenException(
                        string.Format("Page {0} could not be read.", i + 1));
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
                            int subtype = Pdfium.FPDFAnnot_GetSubtype(annotation);
                            if (subtype == FPDF_ANNOT_LINK)
                            {
                                links++;
                            }
                            else if (subtype != FPDF_ANNOT_POPUP)
                            {
                                flattenable++;
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
        }

        // Serializes the (already flattened) document to a byte array through
        // FPDF_SaveAsCopy. FPDF_NO_INCREMENTAL writes a clean, fully rewritten
        // file rather than appending changes.
        private static byte[] SaveDocument(IntPtr document)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                Pdfium.WriteBlockCallback writeBlock = delegate(IntPtr self, IntPtr data, uint size)
                {
                    try
                    {
                        int length = (int)size;
                        byte[] chunk = new byte[length];
                        Marshal.Copy(data, chunk, 0, length);
                        buffer.Write(chunk, 0, length);
                        return 1;
                    }
                    catch
                    {
                        return 0;
                    }
                };

                Pdfium.FPDF_FILEWRITE fileWrite = new Pdfium.FPDF_FILEWRITE();
                fileWrite.Version = 1;
                fileWrite.WriteBlock = Marshal.GetFunctionPointerForDelegate(writeBlock);

                int ok = Pdfium.FPDF_SaveAsCopy(document, ref fileWrite, FPDF_NO_INCREMENTAL);
                // Keep the delegate alive until the native call, which invokes
                // it synchronously, has returned.
                GC.KeepAlive(writeBlock);

                if (ok == 0)
                {
                    return null;
                }
                return buffer.ToArray();
            }
        }

        private static void ValidateOutput(byte[] output, int expectedPageCount, out int linkCount)
        {
            linkCount = 0;
            GCHandle handle = GCHandle.Alloc(output, GCHandleType.Pinned);
            IntPtr document = IntPtr.Zero;
            try
            {
                document = Pdfium.FPDF_LoadMemDocument(
                    handle.AddrOfPinnedObject(), output.Length, null);
                if (document == IntPtr.Zero)
                {
                    throw new FlattenException(
                        "The flattened PDF failed validation, so the original was not changed.");
                }

                int pages = Pdfium.FPDF_GetPageCount(document);
                if (pages != expectedPageCount)
                {
                    throw new FlattenException(
                        "The flattened PDF failed validation, so the original was not changed.");
                }

                int remainingFlattenable;
                CountAnnotations(document, pages, out remainingFlattenable, out linkCount);
                if (remainingFlattenable != 0)
                {
                    throw new FlattenException(
                        "The flattened PDF failed validation, so the original was not changed.");
                }
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
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                directory = ".";
            }
            string temporary = Path.Combine(
                directory,
                "." + Path.GetFileName(path) + ".flatten-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, data);
                try
                {
                    File.Replace(temporary, path, null);
                }
                catch (Exception replaceError)
                {
                    if (replaceError is IOException ||
                        replaceError is PlatformNotSupportedException ||
                        replaceError is UnauthorizedAccessException)
                    {
                        File.Copy(temporary, path, true);
                        File.Delete(temporary);
                    }
                    else
                    {
                        throw;
                    }
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

        private static FlattenResult MakeResult(
            int pageCount, int flattened, int removedLinks, bool changed)
        {
            FlattenResult result = new FlattenResult();
            result.PageCount = pageCount;
            result.FlattenedAnnotationCount = flattened;
            result.RemovedLinkCount = removedLinks;
            result.Changed = changed;
            return result;
        }
    }

    // ---------------------------------------------------------------------
    // User interface.
    // ---------------------------------------------------------------------
    internal sealed class MainForm : Form
    {
        private const string AppVersion = "1.7";

        // Status markers in the log, written as Unicode escapes so the source
        // file's encoding never affects them: check mark, en dash, ballot X,
        // and an em-dash separator.
        private const string MarkOk = "\u2713";   // check mark
        private const string MarkSkip = "\u2013"; // en dash
        private const string MarkFail = "\u2717"; // ballot X
        private const string Dash = " \u2014 ";    // em-dash separator

        private readonly PdfFlattener _flattener = new PdfFlattener();
        private readonly BlockingCollection<string[]> _queue = new BlockingCollection<string[]>();
        private readonly string[] _initialFiles;

        private TextBox _log;
        private Label _detail;
        private Button _openButton;
        private Button _clearButton;
        private MenuStrip _menu;
        private ToolStripMenuItem _openMenuItem;

        private int _activeBatches;
        private bool _dragHighlighted;
        private Color _idleBackColor;
        private Color _idleLogColor;

        public MainForm(string[] initialFiles)
        {
            _initialFiles = initialFiles ?? new string[0];
            BuildInterface();

            Thread worker = new Thread(WorkerLoop);
            worker.IsBackground = true;
            worker.Name = "FlattenPDFs.worker";
            worker.Start();
        }

        private void BuildInterface()
        {
            Text = "Flatten PDFs";
            ClientSize = new Size(680, 460);
            MinimumSize = new Size(520, 380);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // Fall back to the default window icon.
            }

            // Menu bar.
            MenuStrip menu = new MenuStrip();

            ToolStripMenuItem fileMenu = new ToolStripMenuItem("&File");
            _openMenuItem = new ToolStripMenuItem("&Open...", null, OnOpen);
            _openMenuItem.ShortcutKeys = Keys.Control | Keys.O;
            ToolStripMenuItem exitItem = new ToolStripMenuItem("E&xit", null, OnExit);
            fileMenu.DropDownItems.Add(_openMenuItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(exitItem);

            ToolStripMenuItem helpMenu = new ToolStripMenuItem("&Help");
            ToolStripMenuItem aboutItem = new ToolStripMenuItem("&About Flatten PDFs", null, OnAbout);
            helpMenu.DropDownItems.Add(aboutItem);

            menu.Items.Add(fileMenu);
            menu.Items.Add(helpMenu);
            MainMenuStrip = menu;
            _menu = menu;

            // The whole window is the drop target; the content sits directly
            // on the form with standard margins.
            TableLayoutPanel content = new TableLayoutPanel();
            content.Dock = DockStyle.Fill;
            content.ColumnCount = 1;
            content.RowCount = 4;
            content.Padding = new Padding(12);
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            Label title = new Label();
            title.Text = "Drop PDF files here";
            title.Font = new Font("Segoe UI", 18f, FontStyle.Regular);
            title.AutoSize = false;
            title.TextAlign = ContentAlignment.MiddleCenter;
            title.Dock = DockStyle.Fill;
            title.Height = 44;
            title.Margin = new Padding(0, 4, 0, 0);

            _detail = new Label();
            _detail.Text = "Annotations will be flattened and each PDF will be permanently updated.";
            _detail.AutoSize = false;
            _detail.TextAlign = ContentAlignment.MiddleCenter;
            _detail.Dock = DockStyle.Fill;
            _detail.Height = 24;
            _detail.Margin = new Padding(0, 0, 0, 8);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.Anchor = AnchorStyles.None;
            buttons.Margin = new Padding(0, 0, 0, 12);

            _openButton = new Button();
            _openButton.Text = "Select PDFs...";
            _openButton.AutoSize = true;
            _openButton.Padding = new Padding(8, 2, 8, 2);
            _openButton.Click += OnOpen;

            _clearButton = new Button();
            _clearButton.Text = "Clear Log";
            _clearButton.AutoSize = true;
            _clearButton.Padding = new Padding(8, 2, 8, 2);
            _clearButton.Click += OnClearLog;

            buttons.Controls.Add(_openButton);
            buttons.Controls.Add(_clearButton);

            // A borderless LogBox (see below) avoids the themed Edit-control
            // frame: no resting bottom line and no accent underline or caret
            // on focus. Text remains selectable and copyable.
            _log = new LogBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.WordWrap = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BorderStyle = BorderStyle.None;
            _log.Font = new Font("Consolas", 9.5f);
            _log.Dock = DockStyle.Fill;
            _log.TabStop = false;

            content.Controls.Add(title, 0, 0);
            content.Controls.Add(_detail, 0, 1);
            content.Controls.Add(buttons, 0, 2);
            content.Controls.Add(_log, 0, 3);

            Controls.Add(content);
            Controls.Add(menu);

            // Accept drops anywhere over the window and its content.
            EnableDrop(this);
            EnableDrop(content);
            EnableDrop(title);
            EnableDrop(_detail);
            EnableDrop(buttons);
            EnableDrop(_log);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTheme();
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

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (HasPdf(e.Data))
            {
                e.Effect = DragDropEffects.Copy;
                SetHighlight(true);
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            SetHighlight(false);
            string[] files = GetDroppedFiles(e.Data);
            if (files.Length > 0)
            {
                EnqueueBatch(files);
            }
        }

        private void OnDragLeave(object sender, EventArgs e)
        {
            // DragLeave also fires when the pointer crosses from one control
            // onto another inside the window, which would flicker the
            // highlight. Only clear it once the pointer is truly outside.
            Point pointer = PointToClient(Cursor.Position);
            if (!ClientRectangle.Contains(pointer))
            {
                SetHighlight(false);
            }
        }

        // Windows has no system-wide drop-target style for plain windows, so
        // the whole window tints toward the selection highlight color while a
        // valid drag is over it. Labels and panels follow the form's BackColor
        // automatically (ambient properties); the log is tinted explicitly.
        private void SetHighlight(bool highlighted)
        {
            if (_dragHighlighted == highlighted)
            {
                return;
            }
            _dragHighlighted = highlighted;
            if (highlighted)
            {
                BackColor = Blend(_idleBackColor, SystemColors.Highlight, 0.20);
                _log.BackColor = Blend(_idleLogColor, SystemColors.Highlight, 0.12);
            }
            else
            {
                BackColor = _idleBackColor;
                _log.BackColor = _idleLogColor;
            }
        }

        private static Color Blend(Color baseColor, Color tint, double amount)
        {
            return Color.FromArgb(
                baseColor.R + (int)((tint.R - baseColor.R) * amount),
                baseColor.G + (int)((tint.G - baseColor.G) * amount),
                baseColor.B + (int)((tint.B - baseColor.B) * amount));
        }

        private static bool HasPdf(IDataObject data)
        {
            return GetDroppedFiles(data).Length > 0;
        }

        private static string[] GetDroppedFiles(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
            {
                return new string[0];
            }
            string[] all = data.GetData(DataFormats.FileDrop) as string[];
            if (all == null)
            {
                return new string[0];
            }
            List<string> pdfs = new List<string>();
            foreach (string path in all)
            {
                if (PdfFlattener.IsPdfPath(path))
                {
                    pdfs.Add(path);
                }
            }
            return pdfs.ToArray();
        }

        // ------- Menu / button actions -------

        private void OnOpen(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Select PDFs";
                dialog.Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*";
                dialog.Multiselect = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    EnqueueBatch(dialog.FileNames);
                }
            }
        }

        private void OnClearLog(object sender, EventArgs e)
        {
            _log.Clear();
        }

        private void OnExit(object sender, EventArgs e)
        {
            Close();
        }

        private void OnAbout(object sender, EventArgs e)
        {
            MessageBox.Show(
                this,
                "Flatten PDFs " + AppVersion,
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
            List<string> pdfs = new List<string>();
            foreach (string path in files)
            {
                if (PdfFlattener.IsPdfPath(path))
                {
                    pdfs.Add(path);
                }
            }
            if (pdfs.Count == 0)
            {
                AppendLog("No PDF files were selected.");
                return;
            }

            Interlocked.Increment(ref _activeBatches);
            SetBusy(true);
            _queue.Add(pdfs.ToArray());
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
                    catch (FlattenException error)
                    {
                        PostLog(MarkFail + " " + name + Dash + error.Message);
                    }
                    catch (Exception error)
                    {
                        PostLog(MarkFail + " " + name + Dash + error.Message);
                    }
                }

                int remaining = Interlocked.Decrement(ref _activeBatches);
                if (remaining == 0)
                {
                    PostBatchComplete();
                }
            }
        }

        private static string FormatResult(string name, FlattenResult result)
        {
            if (!result.Changed)
            {
                return MarkSkip + " " + name + Dash + "no flattenable annotations; unchanged.";
            }

            string annotations = result.FlattenedAnnotationCount == 1 ? "annotation" : "annotations";
            string pages = result.PageCount == 1 ? "page" : "pages";
            string line = MarkOk + " " + name + Dash +
                          string.Format("flattened {0} {1} on {2} {3}.",
                              result.FlattenedAnnotationCount, annotations,
                              result.PageCount, pages);
            if (result.RemovedLinkCount > 0)
            {
                string links = result.RemovedLinkCount == 1 ? "hyperlink" : "hyperlinks";
                line += string.Format(" Note: {0} {1} removed.", result.RemovedLinkCount, links);
            }
            return line;
        }

        // ------- Thread-marshaled UI updates -------

        private void PostLog(string line)
        {
            Post((Action)delegate { AppendLog(line); });
        }

        private void PostBatchComplete()
        {
            Post((Action)delegate
            {
                SetBusy(false);
                System.Media.SystemSounds.Asterisk.Play();
            });
        }

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
                // The window was torn down between the IsHandleCreated check
                // and this call (e.g. the user quit mid-batch). ObjectDisposed-
                // Exception derives from this, so both cases are handled here.
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

        // ------- Light / dark theme -------
        //
        // WinForms (.NET Framework) has no built-in dark-mode support, so the
        // app applies it directly: the system setting is read from the
        // registry, a handful of colors are set (everything else inherits via
        // WinForms' ambient properties), the title bar is switched through
        // DWM, and WM_SETTINGCHANGE re-applies the theme when the user flips
        // the system setting while the app is running.

        private const int WM_SETTINGCHANGE = 0x001A;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero &&
                "ImmersiveColorSet".Equals(Marshal.PtrToStringUni(m.LParam),
                                           StringComparison.OrdinalIgnoreCase))
            {
                ApplyTheme();
            }
        }

        private static bool IsSystemDark()
        {
            try
            {
                object value = Microsoft.Win32.Registry.GetValue(
                    "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows" +
                    "\\CurrentVersion\\Themes\\Personalize",
                    "AppsUseLightTheme", 1);
                return value is int && (int)value == 0;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyTheme()
        {
            bool dark = IsSystemDark();
            _dragHighlighted = false;

            _idleBackColor = dark ? Color.FromArgb(32, 32, 32) : SystemColors.Control;
            _idleLogColor = dark ? Color.FromArgb(25, 25, 25) : SystemColors.Window;

            BackColor = _idleBackColor;
            ForeColor = dark ? Color.FromArgb(240, 240, 240) : SystemColors.ControlText;
            _detail.ForeColor = dark ? Color.FromArgb(160, 160, 160) : SystemColors.GrayText;
            _log.BackColor = _idleLogColor;
            _log.ForeColor = dark ? Color.FromArgb(228, 228, 228) : SystemColors.WindowText;

            StyleButton(_openButton, dark);
            StyleButton(_clearButton, dark);
            ApplyMenuTheme(dark);

            // Dark title bar. Attribute 20 on Windows 10 1903+/11; 19 on 1809.
            if (IsHandleCreated)
            {
                int value = dark ? 1 : 0;
                if (DwmSetWindowAttribute(Handle, 20, ref value, 4) != 0)
                {
                    DwmSetWindowAttribute(Handle, 19, ref value, 4);
                }
            }
        }

        private static void StyleButton(Button button, bool dark)
        {
            if (dark)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Color.FromArgb(45, 45, 45);
                button.ForeColor = Color.FromArgb(240, 240, 240);
                button.FlatAppearance.BorderColor = Color.FromArgb(96, 96, 96);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(62, 62, 62);
            }
            else
            {
                button.FlatStyle = FlatStyle.Standard;
                // Color.Empty reverts both to their ambient/default values.
                button.BackColor = Color.Empty;
                button.ForeColor = Color.Empty;
                button.UseVisualStyleBackColor = true;
            }
        }

        private void ApplyMenuTheme(bool dark)
        {
            _menu.Renderer = dark
                ? new ToolStripProfessionalRenderer(new DarkMenuColorTable())
                : new ToolStripProfessionalRenderer();
            Color text = dark ? Color.FromArgb(240, 240, 240) : SystemColors.MenuText;
            foreach (ToolStripItem top in _menu.Items)
            {
                top.ForeColor = text;
                ToolStripMenuItem item = top as ToolStripMenuItem;
                if (item != null)
                {
                    foreach (ToolStripItem child in item.DropDownItems)
                    {
                        child.ForeColor = text;
                    }
                }
            }
        }

        // Colors for the menu bar and its drop-downs in dark mode. The
        // professional renderer takes everything else from this table.
        private sealed class DarkMenuColorTable : ProfessionalColorTable
        {
            private static readonly Color Surface = Color.FromArgb(32, 32, 32);
            private static readonly Color Popup = Color.FromArgb(43, 43, 43);
            private static readonly Color Hover = Color.FromArgb(62, 62, 62);
            private static readonly Color Line = Color.FromArgb(96, 96, 96);

            public override Color MenuStripGradientBegin { get { return Surface; } }
            public override Color MenuStripGradientEnd { get { return Surface; } }
            public override Color ToolStripDropDownBackground { get { return Popup; } }
            public override Color ImageMarginGradientBegin { get { return Popup; } }
            public override Color ImageMarginGradientMiddle { get { return Popup; } }
            public override Color ImageMarginGradientEnd { get { return Popup; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Popup; } }
            public override Color MenuItemPressedGradientEnd { get { return Popup; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Popup; } }
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }
}
