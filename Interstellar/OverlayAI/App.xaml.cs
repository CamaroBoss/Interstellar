using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace OverlayAI;

public partial class App : Application
{
    private static Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "Interstellar Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _single = new Mutex(true, "OverlayAI.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OverlayAI", "WebView2");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", dir);

        base.OnStartup(e);
    }
}
