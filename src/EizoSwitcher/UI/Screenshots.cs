using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

/// <summary>
/// Renders the settings window tabs to PNG for the documentation:
///   EizoSwitcher.exe --screenshots docs/screenshots/de   (language via EIZOSWITCHER_LANG)
/// Uses the real displays but demo rules and hides serial numbers.
/// </summary>
internal static class Screenshots
{
    public static void Run(IClassicDesktopStyleApplicationLifetime desktop, string dir)
    {
        Ui.Demo = true;
        if (App.Args.Contains("--dark")) Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Directory.CreateDirectory(dir);
        var displays = new DisplayService();
        displays.Refresh();

        var window = new SettingsWindow(DemoSettings(displays), displays, new GameDetector(), null!)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-20000, -20000),
            ShowInTaskbar = false,
            Width = 1180,
            Height = 700,
        };
        window.Show();

        string[] names = ["1-programs", "2-games", "3-defaults", "4-displays", "5-general"];
        int tab = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            if (tab > 0) Capture(window, Path.Combine(dir, names[tab - 1] + ".png"));
            if (tab == names.Length)
            {
                timer.Stop();
                window.Close();
                displays.Dispose();
                desktop.Shutdown();
                return;
            }
            window.Tabs.SelectedIndex = tab++;
        };
        timer.Start();
    }

    private static void Capture(Window w, string path)
    {
        double scale = w.RenderScaling;
        var size = new PixelSize((int)(w.ClientSize.Width * scale), (int)(w.ClientSize.Height * scale));
        using var bmp = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bmp.Render(w);
        bmp.Save(path);
    }

    private static AppSettings DemoSettings(DisplayService displays)
    {
        var s = new AppSettings();
        string? Pick(DisplayInfo d, params string[] prefs) =>
            d.Eizo == null ? null : prefs.FirstOrDefault(p => d.Eizo.IndexOf(p) >= 0) ?? d.Eizo.Modes.FirstOrDefault();

        ProcessRule Rule(string name, string exe, Func<DisplayInfo, string?> action)
        {
            var r = new ProcessRule { Name = name, ExeName = exe };
            foreach (var d in displays.Displays)
                if (action(d) is { } a) r.Actions[d.Id] = a;
            return r;
        }

        s.Rules.Add(Rule("DaVinci Resolve", "Resolve.exe", d => Pick(d, "BT.709", "sRGB")));
        s.Rules.Add(Rule("Adobe Premiere Pro", "Adobe Premiere Pro.exe", d => Pick(d, "BT.709", "sRGB")));
        s.Rules.Add(Rule("Adobe Photoshop", "Photoshop.exe", d => Pick(d, "CAL", "CAL1")));
        s.Rules.Add(Rule("Adobe Lightroom Classic", "Lightroom.exe", d => d.IsEizo ? Pick(d, "CAL", "CAL1") : null));
        s.Rules.Add(Rule(L.T("Altes Werkzeug", "Old tool"), "PrintTool.exe", d => d.IsEizo ? "Paper" : null));
        s.Games.Actions = displays.Displays.ToDictionary(d => d.Id, d => d.Primary ? Pick(d, "sRGB") ?? "" : DisplayAction.Black);
        foreach (var d in displays.Displays)
        {
            if (d.Eizo != null) s.Defaults[d.Id] = d.Eizo.CurrentModeName;
            s.Displays.Add(new KnownDisplay { Id = d.Id, Name = d.Name, IsEizo = d.IsEizo, SupportsPower = d.SupportsPower, Modes = d.Eizo?.Modes.ToList() ?? [] });
        }
        return s;
    }
}
