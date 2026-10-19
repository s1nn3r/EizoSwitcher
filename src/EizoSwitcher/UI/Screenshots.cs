using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

/// <summary>
/// Renders the settings pages to PNG for the documentation:
///   EizoSwitcher.exe --screenshots docs\screenshots\en [--dark|--light]   (language via EIZOSWITCHER_LANG,
///   window width via EIZOSWITCHER_SHOT_WIDTH, e.g. to check how the program table shrinks)
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
            Width = int.TryParse(Environment.GetEnvironmentVariable("EIZOSWITCHER_SHOT_WIDTH"), out int width) ? width : 1320,
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
                CaptureTrayMenu(displays, Path.Combine(dir, "6-tray-menu.png"));
                CaptureDialog(new ConfirmWindow(L.T("Update verfügbar", "Update available"),
                    L.T("EizoSwitcher {0} ist verfügbar", "EizoSwitcher {0} is available", "9.9.9"),
                    L.T("Installiert ist Version {0}. Jetzt aktualisieren? EizoSwitcher startet danach von selbst neu.",
                        "You have version {0}. Update now? EizoSwitcher restarts by itself afterwards.", Updater.Current),
                    ConfirmWindow.PlainText("### What's new\n\n**Something useful.** More text that explains it.\n- first point\n- second point"),
                    L.T("Jetzt aktualisieren", "Update now"), L.T("Später", "Later"), (L.T("Auf GitHub ansehen", "View on GitHub"), () => { })),
                    Path.Combine(dir, "7-update.png"), 300);
                CaptureDialog(new ProcessPickerWindow(), Path.Combine(dir, "8-picker.png"), 2500);
                displays.Dispose();
                Environment.Exit(0); // the opened context menu keeps the dispatcher alive otherwise
                return;
            }
            w.SelectedPage = page++;
        };
        timer.Start();
    }

    /// <summary>Opens the tray menu (with the first display's submenu) briefly and renders it.</summary>
    private static void CaptureTrayMenu(DisplayService displays, string path)
    {
        var menu = new ContextMenu();
        var reasons = displays.Displays.ToDictionary(d => d.Id, d => d.Primary ? "Adobe Photoshop" : L.T("Standard", "Default"));
        TrayMenu.Fill(menu,
            new TrayMenuState(displays.Displays, d => d.Eizo?.CurrentModeName ?? "—", reasons, null, "Adobe Photoshop", false, null),
            new TrayMenuActions((_, _) => { }, _ => { }, () => { }, () => { }, () => { }, () => { }, () => { }));
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = 40;
        menu.VerticalOffset = 40;
        menu.IsOpen = true;
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
        Capture(menu, path);

        // mode submenu of the first EIZO display (enabled modes, separator, disabled ones)
        if (menu.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(i => i.Items.Count > 2) is { } sub)
        {
            sub.IsSubmenuOpen = true;
            frame = new DispatcherFrame();
            t.Start();
            Dispatcher.PushFrame(frame);
            if (sub.Template.FindName("PART_Popup", sub) is System.Windows.Controls.Primitives.Popup { Child: FrameworkElement child })
                Capture(child, Path.ChangeExtension(path, null) + "-modes.png");
        }
        menu.IsOpen = false;
    }

    /// <summary>Shows a dialog off-screen (not modal), renders it and closes it.</summary>
    private static void CaptureDialog(Window w, string path, int waitMs)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = w.Top = -20000;
        w.ShowInTaskbar = false;
        w.Show();
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(waitMs) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
        ((Panel)w.Content).SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
        var root = (FrameworkElement)VisualTreeHelper.GetChild(w, 0); // whole client area incl. the content's margin
        root.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");
        root.UpdateLayout();
        Capture(root, path);
        w.Close();
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
            s.Displays.Add(new KnownDisplay { Id = d.Id, Name = d.Name, IsEizo = d.IsEizo, CanTurnOff = d.CanTurnOff, Modes = d.Eizo?.Modes.ToList() ?? [], ModesEnabled = d.Eizo?.Enabled.ToList() ?? [] });
        }
        return s;
    }
}
