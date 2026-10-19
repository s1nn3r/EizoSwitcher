using System.Windows;
using EizoSwitcher.Services;
using EizoSwitcher.UI;
using Wpf.Ui.Markup;

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
        L.Set(settings.Language);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary());
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        Theme.Apply(args.Contains("--dark") ? "dark" : args.Contains("--light") ? "light" : settings.Theme);

        if (shots >= 0)
        {
            app.Startup += (_, _) => Screenshots.Run(shots + 1 < args.Length && !args[shots + 1].StartsWith("--") ? args[shots + 1] : "screenshots");
            return app.Run();
        }

        app.Startup += (_, _) => _ = new TrayApp(app, settings, updated);
        return app.Run();
    }
}
