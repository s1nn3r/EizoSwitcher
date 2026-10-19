using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

/// <summary>Pick one or more running programs (with icon, description and window title).</summary>
internal sealed class ProcessPickerWindow : Window
{
    public sealed record Entry(string ExeName, string? Path, string Description, string WindowTitle);

    private readonly TextBox search_ = new() { PlaceholderText = L.T("Suchen …", "Search …"), MinWidth = 280 };
    private readonly CheckBox windowsOnly_ = Ui.Check(L.T("Nur Programme mit Fenster", "Only programs with a window"), true);
    private readonly ListBox list_ = new() { SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle };
    private List<Entry> all_ = [];

    public ProcessPickerWindow()
    {
        Title = L.T("Laufendes Programm hinzufügen", "Add running program");
        Icon = Ui.AppIcon();
        Width = 760;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        list_.ItemTemplate = new FuncDataTemplate<Entry>((e, _) =>
        {
            if (e == null) return new TextBlock();
            var bmp = OperatingSystem.IsWindows() ? ExeIcons.For(e.Path) : null;
            Control icon = bmp != null ? new Image { Source = bmp, Width = 24, Height = 24 } : Ui.Text("▣", 18, opacity: 0.4);
            var title = Ui.Text(e.Description, 13.5, FontWeight.SemiBold);
            var sub = Ui.Text(e.WindowTitle.Length > 0 ? $"{e.ExeName}  ·  {e.WindowTitle}" : e.ExeName, 12, opacity: 0.65);
            var row = Ui.Row(12, icon, Ui.Column(1, title, sub));
            ToolTip.SetTip(row, e.Path ?? e.ExeName);
            return row;
        });

        var add = Ui.Button(L.T("Hinzufügen", "Add"), () => Close(list_.SelectedItems?.Cast<Entry>().ToList()), accent: true);
        var cancel = Ui.Button(L.T("Abbrechen", "Cancel"), () => Close(null));
        var bottom = Ui.Row(8, cancel, add);
        bottom.HorizontalAlignment = HorizontalAlignment.Right;
        bottom.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);

        var top = Ui.Row(12, search_, windowsOnly_, Ui.Button(L.T("Aktualisieren", "Refresh"), Load));
        top.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);

        var hint = Ui.Hint(L.T("Mehrfachauswahl per Klick. Tipp: Starte das Programm vorher, damit es hier erscheint.",
                               "Click to select several. Tip: start the program first so it shows up here."));
        hint.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(hint, Avalonia.Controls.Dock.Top);

        Content = new DockPanel { Margin = new Thickness(16), Children = { bottom, top, hint, list_ } };

        search_.TextChanged += (_, _) => Filter();
        windowsOnly_.IsCheckedChanged += (_, _) => Filter();
        list_.DoubleTapped += (_, _) => Close(list_.SelectedItems?.Cast<Entry>().ToList());
        Opened += (_, _) => { Load(); search_.Focus(); };
    }

    private void Load()
    {
        Task.Run(() =>
        {
            var own = Environment.ProcessId;
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id <= 4 || p.Id == own) continue;
                    string? path = ProcessWatcher.PathOf(p);
                    string exe = System.IO.Path.GetFileName(path) ?? p.ProcessName + ".exe";
                    string title = "";
                    try { title = p.MainWindowTitle; } catch (InvalidOperationException) { }
                    string key = path ?? exe;
                    if (entries.TryGetValue(key, out var known) && known.WindowTitle.Length >= title.Length) continue;
                    entries[key] = new Entry(exe, path, Shell.Describe(path, System.IO.Path.GetFileNameWithoutExtension(exe)), title);
                }
            }
            var sorted = entries.Values.OrderBy(e => e.Description, StringComparer.CurrentCultureIgnoreCase).ToList();
            Dispatcher.UIThread.Post(() => { all_ = sorted; Filter(); });
        });
    }

    private void Filter()
    {
        string q = search_.Text?.Trim() ?? "";
        list_.ItemsSource = all_.Where(e =>
            (windowsOnly_.IsChecked != true || e.WindowTitle.Length > 0) &&
            (q.Length == 0 || e.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || e.ExeName.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || e.WindowTitle.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
