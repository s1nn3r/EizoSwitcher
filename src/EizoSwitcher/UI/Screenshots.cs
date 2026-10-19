using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

/// <summary>
/// Renders the settings pages to PNG for the documentation:
///   EizoSwitcher.exe --screenshots docs\screenshots\en [--dark|--light]   (language via EIZOSWITCHER_LANG)
/// Uses the real displays but demo rules and hides serial numbers.
/// </summary>
internal static class Screenshots
{
    public static void Run(string dir)
    {
        Ui.Demo = true;
        Directory.CreateDirectory(dir);
        var displays = new DisplayService();
        displays.Refresh();
        Ui.DemoSerials.AddRange(displays.Displays.Select(d => d.Serial));

        var w = new SettingsWindow(DemoSettings(displays), displays, new GameDetector(), null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            Width = 1320,
            Height = 780,
        };
        w.Show();
        // the Mica backdrop is not part of a rendered bitmap – paint the theme background instead
        ((Panel)w.Root).SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");

        string[] names = ["1-programs", "2-games", "3-defaults", "4-displays", "5-general"];
        int page = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            if (page > 0) Capture(w.Root, Path.Combine(dir, names[page - 1] + ".png"));
            if (page == names.Length || page >= w.PageCount)
            {
                timer.Stop();
                w.Close();
                displays.Dispose();
                Application.Current.Shutdown();
                return;
            }
            w.SelectedPage = page++;
        };
        timer.Start();
    }

    private static void Capture(FrameworkElement root, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(root);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        png.Save(fs);
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

        s.Rules.Add(Rule("DaVinci Resolve", "Resolve.exe", d => Pick(d, "BT.709", "sRGB") ?? (d.Primary ? null : DisplayAction.Black)));
        s.Rules.Add(Rule("Adobe Premiere Pro", "Adobe Premiere Pro.exe", d => Pick(d, "BT.709", "sRGB")));
        s.Rules.Add(Rule("Adobe Photoshop", "Photoshop.exe", d => Pick(d, "CAL", "CAL1")));
        s.Rules.Add(Rule("Adobe Lightroom Classic", "Lightroom.exe", d => d.IsEizo ? Pick(d, "CAL", "CAL1") : null));
        s.Rules.Add(Rule(L.T("Altes Werkzeug", "Old tool"), "PrintTool.exe", d => d.IsEizo ? "Paper" : null));
        s.Games.Actions = displays.Displays.ToDictionary(d => d.Id, d => d.Primary ? Pick(d, "sRGB") ?? "" : DisplayAction.Off);
        foreach (var d in displays.Displays)
        {
            if (d.Eizo != null) s.Defaults[d.Id] = d.Eizo.CurrentModeName;
            s.Displays.Add(new KnownDisplay { Id = d.Id, Name = d.Name, IsEizo = d.IsEizo, CanTurnOff = d.CanTurnOff, Modes = d.Eizo?.Modes.ToList() ?? [] });
        }
        return s;
    }
}
