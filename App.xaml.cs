using System.Windows;
using Hooking.Services;

namespace Hooking;

public partial class App : Application
{
    private readonly SingleInstance _instance = new();
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        // A development entry point: renders the window through the WPF
        // compositor and prints the measured layout, so the interface can be
        // checked without a desktop screenshot.
        if (e.Args.Length >= 2 && e.Args[0] == "--probe")
        {
            var code = LayoutProbe.Run(e.Args[1]);
            Shutdown(code);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--settings-probe")
        {
            var code = SettingsProbe.Run(e.Args[1]);
            Shutdown(code);
            return;
        }

        // A second instance would spawn a second engine and overwrite the first
        // instance's settings, so it is refused. The running copy is usually hidden in
        // the tray, so the second launch asks it to show its window instead.
        if (!_instance.TryAcquire())
        {
            SingleInstance.RequestShow();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        // The window is created here rather than through StartupUri, so the
        // diagnostics above never build an interface as a side effect.
        _window = new MainWindow();
        MainWindow = _window;

        // Windows shutting down or signing out must be able to close the window, even
        // though closing it normally only hides it in the tray.
        SessionEnding += (_, _) => _window?.PrepareForExit();

        _instance.ListenForShowRequests(() => Dispatcher.BeginInvoke(() => _window?.ShowFromTray()));

        // Started by the sign-in task with --tray: the engine starts and the window
        // stays in the notification area. Started by hand, the window opens as usual.
        _window.Start(inTray: e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance.Dispose();
        base.OnExit(e);
    }
}
