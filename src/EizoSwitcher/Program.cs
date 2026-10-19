using Avalonia;
using EizoSwitcher.Services;

namespace EizoSwitcher;

internal static class Program
{
    public const string ShowSettingsEvent = @"Local\EizoSwitcher.ShowSettings";

    [STAThread]
    public static int Main(string[] args)
    {
        bool screenshots = args.Contains("--screenshots");
        bool updated = args.Contains("--updated");

        using var mutex = new Mutex(false, @"Local\EizoSwitcher.Tray");
        if (!screenshots)
        {
            bool owned;
            try { owned = mutex.WaitOne(updated ? TimeSpan.FromSeconds(20) : TimeSpan.Zero); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                // already running: bring up its settings window instead of starting twice
                try { using var ev = EventWaitHandle.OpenExisting(ShowSettingsEvent); ev.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
                return 0;
            }
            Task.Run(Updater.CleanupOldFiles);
        }

        App.Args = args;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // used by the Avalonia designer as well
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
