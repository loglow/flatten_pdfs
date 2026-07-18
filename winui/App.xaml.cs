// Flatten PDFs for Windows -- WinUI 3 shell, application entry.
//
// The modern-Windows counterpart of the winforms/ target: identical behavior
// and the same shared core (shared/Core.cs), presented with WinUI 3 /
// Windows App SDK. See MainWindow.xaml/.cs for the interface. Deployment is
// unpackaged and framework-dependent: running requires the .NET Desktop
// Runtime and the Windows App Runtime; launch prompts with a download link
// when either is missing. WinUI does not support single-file publish, so
// the output is a folder that must stay together.

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace App;

public partial class MainApp : Application
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    private const uint MB_ICONERROR = 0x10;

    private MainWindow? _window;

    public MainApp()
    {
        InitializeComponent();

        // Startup failures in WinUI otherwise die silently; surface them.
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            ReportCrash(e.Exception?.ToString() ?? e.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportCrash(e.ExceptionObject.ToString() ?? "Unknown error");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Files dropped onto the exe (or passed on the command line).
        string[] files = [.. Environment.GetCommandLineArgs().Skip(1)];
        _window = new MainWindow(files);
        _window.Activate();
    }

    private static void ReportCrash(string details)
    {
        try
        {
            _ = MessageBoxW(IntPtr.Zero, details, Spec.Name, MB_ICONERROR);
        }
        catch
        {
            // Reporting must never crash the crash handler.
        }
    }
}
