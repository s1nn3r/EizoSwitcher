using System.Windows;
using System.Windows.Controls;
using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

/// <summary>What the tray menu shows (snapshot).</summary>
internal sealed record TrayMenuState(
    IReadOnlyList<DisplayInfo> Displays, Func<DisplayInfo, string> StateOf, IReadOnlyDictionary<string, string> Reasons,
    string? ActiveGame, string? ActiveProgram, bool Paused, UpdateInfo? PendingUpdate, string? ActiveGameProcess = null);

/// <summary>What the tray menu can do.</summary>
internal sealed record TrayMenuActions(
    Action<DisplayInfo, int> SetMode, Action<DisplayInfo> Blank, Action TogglePause, Action ShowSettings,
    Action CheckForUpdates, Action InstallUpdate, Action Exit, Action<string>? ExcludeGame = null);

/// <summary>Builds the tray context menu (separate from TrayApp so the screenshot mode can render it too).</summary>
internal static class TrayMenu
{
    public static void Fill(ContextMenu menu, TrayMenuState s, TrayMenuActions a)
    {
        menu.Items.Clear();
        menu.FontSize = 12;

        MenuItem Item(string header, Action? click = null, string? icon = null, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (icon != null) mi.Icon = Ui.Icon(icon, 14);
            if (click != null) mi.Click += (_, _) => click();
            return mi;
        }

        menu.Items.Add(Item($"EizoSwitcher {Updater.Current}", enabled: false, icon: Icons.Info));
        menu.Items.Add(new Separator());
        foreach (var d in s.Displays)
        {
            string reason = s.Reasons.GetValueOrDefault(d.Id) ?? "";
            var item = Item("", icon: Icons.Display);
            // room before the submenu chevron
            item.Header = new TextBlock { Text = $"{d.Name}:  {s.StateOf(d)}" + (reason.Length > 0 ? $"   ({reason})" : ""), Margin = new Thickness(0, 0, 18, 0) };
            if (d.Eizo != null)
            {
                // modes enabled on the monitor first, the disabled ones below a separator
                foreach (bool enabledGroup in new[] { true, false })
                {
                    var indices = Enumerable.Range(0, d.Eizo.Modes.Count).Where(i => d.Eizo.IsEnabled(i) == enabledGroup).ToList();
                    if (indices.Count == 0) continue;
                    if (!enabledGroup) item.Items.Add(new Separator());
                    foreach (int index in indices)
                    {
                        var mi = Item(d.Eizo.Modes[index], () => a.SetMode(d, index));
                        mi.IsCheckable = true;
                        mi.IsChecked = index == d.Eizo.CurrentMode;
                        if (!enabledGroup) mi.Opacity = 0.6;
                        item.Items.Add(mi);
                    }
                }
                item.Items.Add(new Separator());
            }
            item.Items.Add(Item(L.T("Schwarz schalten (Klick zum Aufheben)", "Blank (click to undo)"), () => a.Blank(d), Icons.Moon));
            menu.Items.Add(item);
        }
        if (s.Displays.Count == 0)
            menu.Items.Add(Item(L.T("Bildschirme werden gesucht …", "Looking for displays …"), enabled: false, icon: Icons.Display));
        if (s.ActiveGame != null)
        {
            menu.Items.Add(Item(L.T("Spiel erkannt: ", "Game detected: ") + s.ActiveGame, enabled: false, icon: Icons.Game));
            // one click for false alarms (a fullscreen app that is no game)
            if (s.ActiveGameProcess is { } process && a.ExcludeGame != null)
                menu.Items.Add(Item(L.T("„{0}“ nie als Spiel behandeln", "Never treat \"{0}\" as a game", s.ActiveGame), () => a.ExcludeGame(process), Icons.Blocked));
        }
        else if (s.ActiveProgram != null)
            menu.Items.Add(Item(L.T("Im Vordergrund: ", "In the foreground: ") + s.ActiveProgram, enabled: false, icon: Icons.Apps));

        menu.Items.Add(new Separator());
        // no checkable items in the main menu: their check column would indent all texts (Fluent template)
        menu.Items.Add(s.Paused
            ? Item(L.T("Automatik fortsetzen", "Resume automatic switching"), a.TogglePause, Icons.Play)
            : Item(L.T("Automatik pausieren", "Pause automatic switching"), a.TogglePause, Icons.Pause));
        var open = Item(L.T("Einstellungen …", "Settings …"), a.ShowSettings, Icons.Settings);
        open.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(open);
        menu.Items.Add(s.PendingUpdate != null
            ? Item(L.T("Update auf {0} installieren", "Install update {0}", s.PendingUpdate.Version), a.InstallUpdate, Icons.Download)
            : Item(L.T("Nach Updates suchen", "Check for updates"), a.CheckForUpdates, Icons.Refresh));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Beenden", "Exit"), a.Exit, Icons.Close));
    }
}
