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
// The app targets modern .NET (see App.csproj) and is built with the
// .NET SDK via "Build.cmd". Running it requires the free .NET
// Desktop Runtime; the only other dependency is pdfium.dll beside the
// executable, which the build script downloads once.

using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace App;

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
        Font = new Font("Consolas", Spec.Layout.LogFontSize * PointScale * ConsolasCorrection),
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
    // macOS point sizes convert to Windows font points at 72/96.
    private const float PointScale = 0.75f;

    // Consolas renders visually smaller than the Mac's SF Mono at the same
    // nominal size, so the log font gets this correction on top of the point
    // conversion -- the 1.2 was matched by eye against the Mac app.
    private const float ConsolasCorrection = 1.2f;

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
        Text = Spec.Strings.DropDetail,
        Font = new Font("Segoe UI", Spec.Layout.DetailFontSize * PointScale),
        ForeColor = SystemColors.GrayText,
        AutoSize = true,
        // Anchor None centers an auto-sized control in its table cell.
        Anchor = AnchorStyles.None
    };

    private readonly ThemedButton _openButton = new()
    {
        Text = Spec.Strings.SelectPdfsButton,
        AutoSize = true
    };

    private readonly ThemedButton _clearButton = new()
    {
        Text = Spec.Strings.ClearLogButton,
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

        new Thread(WorkerLoop) { IsBackground = true, Name = "worker" }.Start();
    }

    private void BuildInterface()
    {
        Text = Spec.Name;
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

        ToolStripMenuItem aboutItem = new("&About " + Spec.Name);
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
        _content.Padding = new Padding(Px(Spec.Layout.Padding));
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _content.Paint += OnContentPaint;

        Label title = new()
        {
            Text = Spec.Strings.DropTitle,
            Font = new Font("Segoe UI Semibold", Spec.Layout.TitleFontSize * PointScale),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, Px(Spec.Layout.SpacingAfterTitle))
        };

        _detail.Margin = new Padding(0, 0, 0, Px(Spec.Layout.SpacingAfterDetail));

        _openButton.Click += OnOpen;
        _openButton.Padding = new Padding(Px(8), Px(2), Px(8), Px(2));
        _openButton.Margin = new Padding(0, 0, Px(Spec.Layout.ButtonGap), 0);
        _clearButton.Click += (_, _) => _log.Clear();
        _clearButton.Padding = new Padding(Px(8), Px(2), Px(8), Px(2));
        _clearButton.Margin = new Padding(0);

        FlowLayoutPanel buttons = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, Px(Spec.Layout.SpacingAfterButtons))
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
        ClientSize = new Size(Px(Spec.Layout.WindowWidth), Px(Spec.Layout.WindowHeight) + menuHeight);
        // MinimumSize is an outer-window size. Adding the horizontal frame
        // keeps the minimum content width at the spec value (the invisible
        // resize borders otherwise eat about 7px per side); the height stays
        // outer-based, the same feel as the mac app's frame-height minimum.
        int frameWidth = Width - ClientSize.Width;
        MinimumSize = new Size(
            Px(Spec.Layout.MinWindowWidth) + frameWidth,
            Px(Spec.Layout.MinWindowHeight) + menuHeight + Px(MacMinHeightParity));

        // Accept drops over the content area only -- the same region the
        // outline encloses. The form itself is deliberately not registered:
        // a form-level registration would cover the title bar and menu too.
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
        control.DragOver += OnDragOver;
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
        DragImage.Enter((Control)sender!, e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Effect = _dragHighlighted ? DragDropEffects.Copy : DragDropEffects.None;
        DragImage.Over(e);
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        DragImage.Drop(e);
        SetHighlight(false);
        string[] files = GetDroppedFiles(e.Data);
        if (files.Length > 0)
        {
            EnqueueBatch(files);
        }
    }

    private void OnDragLeave(object? sender, EventArgs e)
    {
        DragImage.Leave();
        // DragLeave also fires when the pointer crosses from one control onto
        // another inside the drop area, which would flicker the highlight.
        // Only clear it once the pointer is truly outside the content area.
        if (!_content.ClientRectangle.Contains(_content.PointToClient(Cursor.Position)))
        {
            SetHighlight(false);
        }
    }

    // Windows has no system-wide drop-target style for plain windows, so an
    // accent-color outline around the content area indicates an active drop,
    // stroked inside _content's padding ring where no child control covers it.

    // Windows 11 rounds window corners by 8 logical pixels; the outline
    // follows that curvature on all four corners (equal rounding at the top
    // is a design choice -- the window edge there is straight).
    private const int WindowCornerRadius = 8;

    // The mac app's minimum window reads about this much taller than the
    // outer-based minimum here; matched by eye for cross-target parity.
    private const int MacMinHeightParity = 4;

    // Both Windows targets outline drops with the user's accent color.
    // WinForms has no accent API, so it is read from the DWM registry value
    // (ABGR), falling back to the classic selection color.
    private static readonly Color AccentColor = ReadAccentColor();

    private static Color ReadAccentColor()
    {
        try
        {
            if (Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
                    "AccentColor", null) is int abgr)
            {
                return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            }
        }
        catch
        {
        }
        return SystemColors.Highlight;
    }

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

        // Stroke centered on a half-thickness-inset path, so the outline's
        // outer edge sits flush against the window edge (an inner stroke,
        // like the other targets). Corner arcs run concentric with the
        // window's rounding.
        float thickness = Spec.Layout.DropOutlineWidth * _scale;
        float inset = thickness / 2f;
        float radius = MathF.Max(1f, WindowCornerRadius * _scale - inset);
        float diameter = 2 * radius;
        RectangleF bounds = _content.ClientRectangle;
        bounds.Inflate(-inset, -inset);

        using GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using Pen pen = new(AccentColor, thickness);
        e.Graphics.DrawPath(pen, path);
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
            $"{Spec.Name} {Spec.Version}",
            "About " + Spec.Name,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_activeBatches > 0)
        {
            DialogResult answer = MessageBox.Show(
                this,
                Spec.Strings.QuitConfirmMessage,
                Spec.Name,
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
            AppendLog(Spec.Strings.NoPdfsSelected);
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
            return $"{MarkSkip} {name}{Dash}{Spec.Strings.UnchangedMessage}";
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

    // ------- Live theme switching -------
    //
    // Windows broadcasts ImmersiveColorSet several times per theme flip, so
    // the refresh is debounced onto a quiet message pump (re-theming inside
    // the burst has left controls unresponsive). Re-applying the color mode
    // remaps the palette; menus repaint live, ambient colors are reassigned
    // under the new mapping, and the controls whose dark rendering is baked
    // at handle creation (buttons, the log and its scrollbar) get their
    // handles recreated, which preserves their state.

    private const int WM_SETTINGCHANGE = 0x001A;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    private readonly System.Windows.Forms.Timer _themeDebounce = new() { Interval = 250 };
    private bool _themeDebounceWired;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero &&
            "ImmersiveColorSet".Equals(Marshal.PtrToStringUni(m.LParam),
                                       StringComparison.OrdinalIgnoreCase))
        {
            if (!_themeDebounceWired)
            {
                _themeDebounceWired = true;
                _themeDebounce.Tick += (_, _) =>
                {
                    _themeDebounce.Stop();
                    RefreshTheme();
                };
            }
            _themeDebounce.Stop();
            _themeDebounce.Start();
        }
    }

    private void RefreshTheme()
    {
        Application.SetColorMode(SystemColorMode.System);

        BackColor = SystemColors.Control;
        ForeColor = SystemColors.ControlText;
        _detail.ForeColor = SystemColors.GrayText;

        _openButton.RefreshHandle();
        _clearButton.RefreshHandle();
        _log.RefreshHandle();

        // Assign the log's colors as resolved RGB values: reassigning the
        // same named SystemColor is a no-op (the setter compares by name),
        // which left the cached background brush -- visible in the empty
        // area below the text -- holding the previous theme's color.
        _log.BackColor = Color.FromArgb(SystemColors.Window.ToArgb());
        _log.ForeColor = Color.FromArgb(SystemColors.WindowText.ToArgb());

        int dark = Application.IsDarkModeEnabled ? 1 : 0;
        if (DwmSetWindowAttribute(Handle, 20, ref dark, 4) != 0)
        {
            _ = DwmSetWindowAttribute(Handle, 19, ref dark, 4);
        }

        Invalidate(true);
    }
}

// A Button that can recreate its native handle, re-baking the theme-
// dependent rendering chosen at handle creation.
internal sealed class ThemedButton : Button
{
    public void RefreshHandle() => RecreateHandle();
}

// Relays drag events to the shell's drag-image helper so the translucent
// file thumbnail Explorer draws while dragging stays visible over this
// window. Plain OLE drop targets (WinForms' default) never call the helper,
// which is why the image otherwise vanishes at the window edge. Every call
// degrades silently: without the helper the drop still works, just without
// the picture.
internal static class DragImage
{
    private static readonly IDropTargetHelper? Helper = Create();

    private static IDropTargetHelper? Create()
    {
        try
        {
            return (IDropTargetHelper)new DragDropHelper();
        }
        catch
        {
            return null;
        }
    }

    public static void Enter(Control target, DragEventArgs e)
    {
        if (Helper is null || e.Data is not ComIDataObject data)
        {
            return;
        }
        Point location = new(e.X, e.Y);
        try
        {
            Helper.DragEnter(target.Handle, data, ref location, (int)e.Effect);
        }
        catch
        {
        }
    }

    public static void Over(DragEventArgs e)
    {
        if (Helper is null)
        {
            return;
        }
        Point location = new(e.X, e.Y);
        try
        {
            Helper.DragOver(ref location, (int)e.Effect);
        }
        catch
        {
        }
    }

    public static void Leave()
    {
        try
        {
            Helper?.DragLeave();
        }
        catch
        {
        }
    }

    public static void Drop(DragEventArgs e)
    {
        if (Helper is null || e.Data is not ComIDataObject data)
        {
            return;
        }
        Point location = new(e.X, e.Y);
        try
        {
            Helper.Drop(data, ref location, (int)e.Effect);
        }
        catch
        {
        }
    }

    // shobjidl.h vtable order: DragEnter, DragLeave, DragOver, Drop, Show.
    [ComImport]
    [Guid("4657278B-411B-11d2-839A-00C04FD918D0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTargetHelper
    {
        void DragEnter(IntPtr hwndTarget, ComIDataObject dataObject, ref Point pt, int effect);
        void DragLeave();
        void DragOver(ref Point pt, int effect);
        void Drop(ComIDataObject dataObject, ref Point pt, int effect);
        void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    [ComImport]
    [Guid("4657278A-411B-11d2-839A-00C04FD918D0")]
    private class DragDropHelper
    {
    }
}

// A read-only log TextBox that never shows a caret. Suppressing the caret
// (HideCaret's hide count is cumulative, so calling it after every message
// keeps it hidden for good) means the control never looks focused, while
// mouse selection and Ctrl+C continue to work normally.
internal sealed class LogBox : TextBox
{
    public void RefreshHandle() => RecreateHandle();

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
        // Follow the system light/dark setting. Live changes are handled by
        // MainForm's debounced WM_SETTINGCHANGE refresh; the experimental
        // color mode (WFO5001) does not re-apply itself.
        Application.SetColorMode(SystemColorMode.System);
        Application.Run(new MainForm(args));
    }
}
