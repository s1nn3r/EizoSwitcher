using EizoSwitcher.Services;
using EizoSwitcher.UI;

namespace EizoSwitcher;

internal static class Program
{
    public const string ShowSettingsEvent = @"Local\EizoSwitcher.ShowSettings";

    [STAThread]
    public static int Main(string[] args)
    {
        int shots = Array.IndexOf(args, "--screenshots");
        bool updated = args.Contains("--updated");

        using var mutex = new Mutex(false, @"Local\EizoSwitcher.Tray");
        if (shots < 0)
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
            DisplayConfig.RecoverAfterCrash();
        }

        var settings = AppSettings.Load();
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(ColorMode(args.Contains("--dark") ? "dark" : settings.Theme));
        L.Set(settings.Language);

        if (shots >= 0) return Screenshots.Run(shots + 1 < args.Length ? args[shots + 1] : "screenshots");

        Application.Run(new TrayApp(settings, updated));
        return 0;
    }

    public static SystemColorMode ColorMode(string theme) => theme switch
    {
        "light" => SystemColorMode.Classic,
        "dark" => SystemColorMode.Dark,
        _ => SystemColorMode.System,
    };
}
