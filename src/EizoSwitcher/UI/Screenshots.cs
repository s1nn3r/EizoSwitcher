using System.Drawing.Imaging;
using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

/// <summary>
/// Renders the settings tabs to PNG for the documentation:
///   EizoSwitcher.exe --screenshots docs\screenshots\en [--dark]   (language via EIZOSWITCHER_LANG)
/// Uses the real displays but demo rules and hides serial numbers.
/// </summary>
internal static class Screenshots
{
    public static int Run(string dir)
    {
        Ui.Demo = true;
        Directory.CreateDirectory(dir);
        using var displays = new DisplayService();
        displays.Refresh();
        Ui.DemoSerials.AddRange(displays.Displays.Select(d => d.Serial));

        using var form = new SettingsForm(DemoSettings(displays), displays, new GameDetector(), null)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000),
            ShowInTaskbar = false,
            Size = new Size(1180, 720),
        };
        form.Show();

        string[] names = ["1-programs", "2-games", "3-defaults", "4-displays", "5-general"];
        for (int i = 0; i < names.Length && i < form.TabCount; i++)
        {
            form.SelectedTab = i;
            Application.DoEvents();
            Thread.Sleep(150);
            Application.DoEvents();
            using var bmp = new Bitmap(form.Root.Width, form.Root.Height);
            form.Root.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
            bmp.Save(Path.Combine(dir, names[i] + ".png"), ImageFormat.Png);
        }
        form.Close();
        return 0;
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
            s.Displays.Add(new KnownDisplay { Id = d.Id, Name = d.Name, IsEizo = d.IsEizo, CanTurnOff = d.CanTurnOff, Modes = d.Eizo?.Modes.ToList() ?? [] });
        }
        return s;
    }
}
