// Shared core for the Windows targets (winforms/ and winui/): the spec
// loader, the PDFium interop, and the flattening engine. Both project files
// compile this file alongside their own Sources; nothing in here touches UI.

using System.Runtime.InteropServices;
using System.Text.Json;

namespace App;

// ---------------------------------------------------------------------------
// Shared spec.
//
// shared/app-spec.json is the single source of truth for the user-facing
// strings, layout metrics (macOS points == 96-DPI pixels), and version
// shared with the macOS app. The build embeds it into the executable (see
// App.csproj); it is deserialized once at startup. Each app declares
// only the fields it uses -- unknown JSON keys are ignored.
// ---------------------------------------------------------------------------
internal static class Spec
{
    public static string Name => Data.Name;
    public static string Version => Data.Version;
    public static SpecStrings Strings => Data.Strings;
    public static SpecLayout Layout => Data.Layout;

    private static readonly SpecData Data = Load();

    private static SpecData Load()
    {
        using Stream stream = typeof(Spec).Assembly.GetManifestResourceStream("app-spec.json")
            ?? throw new InvalidOperationException("The embedded app-spec.json resource is missing.");
        return JsonSerializer.Deserialize<SpecData>(
                   stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("app-spec.json could not be parsed.");
    }

    internal sealed class SpecData
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public SpecStrings Strings { get; set; } = new();
        public SpecLayout Layout { get; set; } = new();
    }

    internal sealed class SpecStrings
    {
        public string DropTitle { get; set; } = "";
        public string DropDetail { get; set; } = "";
        public string SelectPdfsButton { get; set; } = "";
        public string ClearLogButton { get; set; } = "";
        public string NoPdfsSelected { get; set; } = "";
        public string UnchangedMessage { get; set; } = "";
        public string QuitConfirmMessage { get; set; } = "";
        public string ErrorNotPdf { get; set; } = "";
        public string ErrorCannotOpen { get; set; } = "";
        public string ErrorLocked { get; set; } = "";
        public string ErrorNoPages { get; set; } = "";
        public string ErrorPageReadFailed { get; set; } = "";
        public string ErrorPageFlattenFailed { get; set; } = "";
        public string ErrorSaveFailed { get; set; } = "";
        public string ErrorValidationFailed { get; set; } = "";
    }

    internal sealed class SpecLayout
    {
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public int MinWindowWidth { get; set; }
        public int MinWindowHeight { get; set; }
        public int Padding { get; set; }
        public int Spacing { get; set; }
        public int SpacingAfterTitle { get; set; }
        public int SpacingAfterDetail { get; set; }
        public int SpacingAfterButtons { get; set; }
        public int ButtonGap { get; set; }
        public int TitleFontSize { get; set; }
        public int DetailFontSize { get; set; }
        public int LogFontSize { get; set; }
        public int DropOutlineWidth { get; set; }
    }
}

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
            throw new FlattenException(Spec.Strings.ErrorNotPdf);
        }
        if (!File.Exists(path))
        {
            throw new FlattenException(Spec.Strings.ErrorCannotOpen);
        }

        byte[] input;
        try
        {
            input = File.ReadAllBytes(path);
        }
        catch
        {
            throw new FlattenException(Spec.Strings.ErrorCannotOpen);
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
                    ? new FlattenException(Spec.Strings.ErrorLocked)
                    : new FlattenException(Spec.Strings.ErrorCannotOpen);
            }

            int pageCount = Pdfium.FPDF_GetPageCount(document);
            if (pageCount <= 0)
            {
                throw new FlattenException(Spec.Strings.ErrorNoPages);
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
                    throw new FlattenException(Spec.Strings.ErrorPageReadFailed.Replace("{n}", (i + 1).ToString()));
                }
                try
                {
                    if (Pdfium.FPDFPage_Flatten(page, FLAT_NORMALDISPLAY) == FLATTEN_FAIL)
                    {
                        throw new FlattenException(Spec.Strings.ErrorPageFlattenFailed.Replace("{n}", (i + 1).ToString()));
                    }
                }
                finally
                {
                    Pdfium.FPDF_ClosePage(page);
                }
            }

            byte[] output = SaveDocument(document)
                ?? throw new FlattenException(Spec.Strings.ErrorSaveFailed);

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
                throw new FlattenException(Spec.Strings.ErrorPageReadFailed.Replace("{n}", (i + 1).ToString()));
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
        string failed = Spec.Strings.ErrorValidationFailed;

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
