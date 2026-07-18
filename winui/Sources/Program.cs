// Flatten PDFs for Windows -- WinUI 3 shell.
//
// The modern-Windows counterpart of the winforms/ target: identical behavior
// and the same shared core (see shared/Core.cs -- spec loader, PDFium
// interop, flattening engine), but presented with WinUI 3 / Windows App SDK:
// Fluent controls, a Mica window backdrop, fully themed dialogs, and the
// shell's native drag-and-drop visuals (no hand-rolled drag-image helper --
// WinUI preserves the drag thumbnail and caption automatically).
//
// The UI is built entirely in code -- no XAML files -- which keeps the target
// to this single source file. Deployment is unpackaged and framework-
// dependent: running requires the .NET Desktop Runtime and the Windows App
// Runtime; when either is missing, launch shows an install prompt with a
// download link. Unlike the winforms target, WinUI does not support
// single-file publish, so the output is a folder that must stay together.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callbackParams =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _ = new FlattenApp(args);
        });
    }
}

internal sealed partial class FlattenApp : Application
{
    private readonly string[] _args;
    private MainWindow? _window;

    public FlattenApp(string[] args)
    {
        _args = args;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Code-built apps must add the control styles that App.xaml normally
        // declares; without this every control renders unstyled.
        Resources.MergedDictionaries.Add(new XamlControlsResources());

        _window = new MainWindow(_args);
        _window.Activate();
    }
}

internal sealed partial class MainWindow : Window
{
    // Status markers in the log, written as Unicode escapes so the source
    // file's encoding never affects them: check mark, en dash, ballot X, and
    // an em-dash separator.
    private const string MarkOk = "\u2713";   // check mark
    private const string MarkSkip = "\u2013"; // en dash
    private const string MarkFail = "\u2717"; // ballot X
    private const string Dash = " \u2014 ";    // em-dash separator

    // Consolas renders visually smaller than the Mac's SF Mono at the same
    // nominal size; matched by eye against the Mac app. (WinUI font sizes and
    // layout units are effective pixels, so spec values are used directly --
    // no point conversion, and the system handles DPI for everything except
    // the physical-pixel window sizing below.)
    private const float ConsolasCorrection = 1.2f;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    private const uint MB_ICONASTERISK = 0x40;

    private readonly PdfFlattener _flattener = new();
    private readonly BlockingCollection<string[]> _queue = [];

    private readonly float _scale;
    private readonly Grid _root;
    private readonly Border _outline;
    private readonly MenuBar _menuBar;
    private readonly MenuFlyoutItem _openItem;
    private readonly Button _selectButton;
    private readonly TextBlock _log;
    private readonly ScrollViewer _logScroller;

    private int _activeBatches;
    private bool _dragHighlighted;
    private bool _closeConfirmed;

    private int Px(int value) => (int)MathF.Round(value * _scale);

    public MainWindow(string[] initialFiles)
    {
        Title = Spec.Name;
        _scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96f;

        // ------- Menu bar -------

        _openItem = new MenuFlyoutItem { Text = "Open..." };
        _openItem.KeyboardAccelerators.Add(new KeyboardAccelerator
        {
            Modifiers = VirtualKeyModifiers.Control,
            Key = VirtualKey.O
        });
        _openItem.Click += async (_, _) => await SelectFilesAsync();

        MenuFlyoutItem exitItem = new() { Text = "Exit" };
        exitItem.Click += (_, _) => Close();

        MenuBarItem fileMenu = new() { Title = "File" };
        fileMenu.Items.Add(_openItem);
        fileMenu.Items.Add(new MenuFlyoutSeparator());
        fileMenu.Items.Add(exitItem);

        MenuFlyoutItem aboutItem = new() { Text = "About " + Spec.Name };
        aboutItem.Click += async (_, _) => await ShowAboutAsync();
        MenuBarItem helpMenu = new() { Title = "Help" };
        helpMenu.Items.Add(aboutItem);

        _menuBar = new MenuBar();
        _menuBar.Items.Add(fileMenu);
        _menuBar.Items.Add(helpMenu);

        // ------- Content -------

        TextBlock title = new()
        {
            Text = Spec.Strings.DropTitle,
            FontSize = Spec.Layout.TitleFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        TextBlock detail = new()
        {
            Text = Spec.Strings.DropDetail,
            FontSize = Spec.Layout.DetailFontSize,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _log = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = Spec.Layout.LogFontSize * ConsolasCorrection,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        };
        _logScroller = new ScrollViewer
        {
            Content = _log,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8)
        };

        _selectButton = new Button { Content = Spec.Strings.SelectPdfsButton };
        _selectButton.Click += async (_, _) => await SelectFilesAsync();

        Button clearButton = new() { Content = Spec.Strings.ClearLogButton };
        clearButton.Click += (_, _) => _log.Text = "";

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Spec.Layout.ButtonGap,
            HorizontalAlignment = HorizontalAlignment.Center,
            // The gap between the buttons and the log is spacingAfterButtons;
            // the grid's RowSpacing already contributes `spacing` of it.
            Margin = new Thickness(0, 0, 0, Spec.Layout.SpacingAfterButtons - Spec.Layout.Spacing)
        };
        buttons.Children.Add(_selectButton);
        buttons.Children.Add(clearButton);

        Border logPanel = new()
        {
            Child = _logScroller,
            Background = ThemeBrush("LayerFillColorDefaultBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };

        Grid content = new()
        {
            Padding = new Thickness(Spec.Layout.Padding),
            RowSpacing = Spec.Layout.Spacing
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(title, 0);
        Grid.SetRow(detail, 1);
        Grid.SetRow(buttons, 2);
        Grid.SetRow(logPanel, 3);
        content.Children.Add(title);
        content.Children.Add(detail);
        content.Children.Add(buttons);
        content.Children.Add(logPanel);

        // The accent outline drawn while a valid drag is over the window.
        _outline = new Border
        {
            Child = content,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            BorderBrush = ThemeBrush("AccentFillColorDefaultBrush"),
            BorderThickness = new Thickness(0)
        };

        _root = new Grid { AllowDrop = true };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_menuBar, 0);
        Grid.SetRow(_outline, 1);
        _root.Children.Add(_menuBar);
        _root.Children.Add(_outline);

        _root.DragOver += OnDragOver;
        _root.DragLeave += (_, _) => SetHighlight(false);
        _root.Drop += OnDrop;

        Content = _root;

        // ------- Window chrome and sizing -------

        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else
        {
            _root.Background = ThemeBrush("ApplicationPageBackgroundThemeBrush");
        }

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.Closing += OnClosing;

        // Same starting and minimum sizes as the macOS app, plus the menu
        // bar's height (the Mac has no in-window menu bar). The menu height
        // is estimated first and corrected once it has a real layout size.
        ApplyWindowSize((int)(40 * _scale));
        _menuBar.Loaded += (_, _) => ApplyWindowSize((int)MathF.Round((float)(_menuBar.ActualHeight * _scale)));

        new Thread(WorkerLoop) { IsBackground = true, Name = "worker" }.Start();

        if (initialFiles.Length > 0)
        {
            EnqueueBatch(initialFiles);
        }
    }

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    private void ApplyWindowSize(int menuHeightPx)
    {
        AppWindow.Resize(new SizeInt32(
            Px(Spec.Layout.WindowWidth),
            Px(Spec.Layout.WindowHeight) + menuHeightPx));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Px(Spec.Layout.MinWindowWidth);
            presenter.PreferredMinimumHeight = Px(Spec.Layout.MinWindowHeight) + menuHeightPx;
        }
    }

    // ------- Drag and drop -------
    //
    // WinUI keeps the shell drag image and caption visible on its own; only
    // the accept decision and the accent outline are ours. PDFs are filtered
    // at drop time because DragOver cannot inspect file paths synchronously.

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool valid = e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = valid ? DataPackageOperation.Copy : DataPackageOperation.None;
        SetHighlight(valid);
    }

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
        _outline.BorderThickness = new Thickness(highlighted ? Spec.Layout.DropOutlineWidth : 0);
    }

    // ------- Menu / button actions -------

    private async Task SelectFilesAsync()
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

    private async Task ShowAboutAsync()
    {
        ContentDialog dialog = new()
        {
            Title = "About " + Spec.Name,
            Content = $"{Spec.Name} {Spec.Version}",
            CloseButtonText = "OK",
            XamlRoot = _root.XamlRoot
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
            XamlRoot = _root.XamlRoot
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
        _log.Text = _log.Text.Length == 0 ? line : _log.Text + "\n" + line;
        _logScroller.ChangeView(null, double.MaxValue, null, disableAnimation: true);
    }

    private void SetBusy(bool busy)
    {
        _selectButton.IsEnabled = !busy;
        _openItem.IsEnabled = !busy;
    }
}
