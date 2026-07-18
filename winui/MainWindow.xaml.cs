// Flatten PDFs for Windows -- WinUI 3 shell, main window.
//
// The XAML declares structure and theme brushes; every user-facing string,
// font size, and spacing value is assigned here from the shared spec. WinUI
// keeps the shell drag image and caption visible on its own -- unlike the
// winforms target, no drag-image helper is needed -- and dialogs, controls,
// and the Mica backdrop all follow the system theme automatically.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace App;

public sealed partial class MainWindow : Window
{
    // Status markers in the log, written as Unicode escapes so the source
    // file's encoding never affects them: check mark, en dash, ballot X, and
    // an em-dash separator.
    private const string MarkOk = "\u2713";   // check mark
    private const string MarkSkip = "\u2013"; // en dash
    private const string MarkFail = "\u2717"; // ballot X
    private const string Dash = " \u2014 ";    // em-dash separator

    // Consolas renders visually smaller than the Mac's SF Mono at the same
    // nominal size; matched by eye against the Mac app. (WinUI font sizes
    // and layout units are effective pixels, so spec values are used
    // directly; only the physical-pixel window sizing below needs the DPI.)
    private const float ConsolasCorrection = 1.2f;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    private const uint MB_ICONASTERISK = 0x40;

    private readonly PdfFlattener _flattener = new();
    private readonly BlockingCollection<string[]> _queue = [];
    private readonly IntPtr _hwnd;
    private readonly float _scale;

    private int _activeBatches;
    private bool _dragHighlighted;
    private bool _closeConfirmed;

    private int Px(int value) => (int)MathF.Round(value * _scale);

    public MainWindow(string[] initialFiles)
    {
        InitializeComponent();

        Title = Spec.Name;
        _hwnd = WindowNative.GetWindowHandle(this);
        _scale = GetDpiForWindow(_hwnd) / 96f;

        // The system title bar does not follow the app theme on its own.
        ApplyTitleBarTheme();
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();

        // Everything the spec owns: strings, fonts, spacing.
        AboutItem.Text = "About " + Spec.Name;
        TitleText.Text = Spec.Strings.DropTitle;
        TitleText.FontSize = Spec.Layout.TitleFontSize;
        DetailText.Text = Spec.Strings.DropDetail;
        DetailText.FontSize = Spec.Layout.DetailFontSize;
        SelectButton.Content = Spec.Strings.SelectPdfsButton;
        ClearButton.Content = Spec.Strings.ClearLogButton;
        LogText.FontSize = Spec.Layout.LogFontSize * ConsolasCorrection;
        ContentGrid.Padding = new Thickness(Spec.Layout.Padding);
        ContentGrid.RowSpacing = Spec.Layout.Spacing;
        Buttons.Spacing = Spec.Layout.ButtonGap;
        // The gap between the buttons and the log is spacingAfterButtons; the
        // grid's RowSpacing already contributes `spacing` of it.
        Buttons.Margin = new Thickness(
            0, 0, 0, Spec.Layout.SpacingAfterButtons - Spec.Layout.Spacing);

        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else
        {
            // No Mica (Windows 10): give the root an opaque themed background.
            try
            {
                Root.Background =
                    (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
            }
            catch
            {
                // Keep the default background if the brush lookup fails.
            }
        }

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.Closing += OnClosing;

        // Same starting and minimum sizes as the macOS app, plus the menu
        // bar's height (the Mac has no in-window menu bar). The menu height
        // is estimated first and corrected once it has a real layout size.
        ApplyWindowSize((int)(40 * _scale));
        Menu.Loaded += (_, _) =>
            ApplyWindowSize((int)MathF.Round((float)(Menu.ActualHeight * _scale)));

        new Thread(WorkerLoop) { IsBackground = true, Name = "worker" }.Start();

        if (initialFiles.Length > 0)
        {
            EnqueueBatch(initialFiles);
        }
    }

    private void ApplyWindowSize(int menuHeightPx)
    {
        // ResizeClient sizes the content area (like WinForms ClientSize);
        // Resize would include the title bar and borders, shrinking the
        // content relative to the other targets.
        AppWindow.ResizeClient(new SizeInt32(
            Px(Spec.Layout.WindowWidth),
            Px(Spec.Layout.WindowHeight) + menuHeightPx));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // The preferred minimums are outer-window sizes; add the
            // measured chrome so the minimum *content* matches the spec.
            int chromeWidth = AppWindow.Size.Width - AppWindow.ClientSize.Width;
            int chromeHeight = AppWindow.Size.Height - AppWindow.ClientSize.Height;
            presenter.PreferredMinimumWidth = Px(Spec.Layout.MinWindowWidth) + chromeWidth;
            presenter.PreferredMinimumHeight =
                Px(Spec.Layout.MinWindowHeight) + menuHeightPx + chromeHeight;
        }
    }

    private void ApplyTitleBarTheme()
    {
        // Attribute 20 on Windows 10 1903+/11; 19 on 1809.
        int dark = Root.ActualTheme == ElementTheme.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(_hwnd, 20, ref dark, 4) != 0)
        {
            _ = DwmSetWindowAttribute(_hwnd, 19, ref dark, 4);
        }
    }

    // ------- Drag and drop -------
    //
    // PDFs are filtered at drop time because DragOver cannot inspect file
    // paths synchronously.

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool valid = e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = valid ? DataPackageOperation.Copy : DataPackageOperation.None;
        SetHighlight(valid);
    }

    private void OnDragLeave(object sender, DragEventArgs e) => SetHighlight(false);

    private async void OnDrop(object sender, DragEventArgs e)
    {
        SetHighlight(false);
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            EnqueueBatch([.. items.Select(item => item.Path)]);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void SetHighlight(bool highlighted)
    {
        if (_dragHighlighted == highlighted)
        {
            return;
        }
        _dragHighlighted = highlighted;
        Outline.BorderThickness = new Thickness(highlighted ? Spec.Layout.DropOutlineWidth : 0);
    }

    // ------- Menu / button actions -------

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            EnqueueBatch([.. files.Select(file => file.Path)]);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => LogText.Text = "";

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private async void OnAboutClick(object sender, RoutedEventArgs e)
    {
        ContentDialog dialog = new()
        {
            Title = "About " + Spec.Name,
            Content = $"{Spec.Name} {Spec.Version}",
            CloseButtonText = "OK",
            XamlRoot = Root.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_activeBatches == 0 || _closeConfirmed)
        {
            _queue.CompleteAdding();
            return;
        }
        e.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        ContentDialog dialog = new()
        {
            Title = Spec.Name,
            Content = Spec.Strings.QuitConfirmMessage,
            PrimaryButtonText = "Quit",
            CloseButtonText = "Cancel",
            XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _closeConfirmed = true;
            Close();
        }
    }

    // ------- Work queue -------

    private void EnqueueBatch(IReadOnlyList<string> files)
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
                DispatcherQueue.TryEnqueue(() =>
                {
                    SetBusy(false);
                    MessageBeep(MB_ICONASTERISK);
                });
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

    private void PostLog(string line) => DispatcherQueue.TryEnqueue(() => AppendLog(line));

    private void AppendLog(string line)
    {
        LogText.Text = LogText.Text.Length == 0 ? line : LogText.Text + "\n" + line;
        LogScroller.ChangeView(null, double.MaxValue, null, disableAnimation: true);
    }

    private void SetBusy(bool busy)
    {
        SelectButton.IsEnabled = !busy;
        OpenItem.IsEnabled = !busy;
    }
}
