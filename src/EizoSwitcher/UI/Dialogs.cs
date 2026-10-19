using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

/// <summary>Pick one or more running programs (with icon, description and window title).</summary>
internal sealed class ProcessPickerWindow : Window
{
    private static UIElement Dim(FrameworkElement e) { e.Opacity = 0.5; return e; }

    public sealed record Entry(string ExeName, string? Path, string Description, string WindowTitle);

    private readonly TextBox search_ = new() { Width = 320, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox windowsOnly_ = new() { Content = L.T("Nur Programme mit Fenster", "Only programs with a window"), IsChecked = true, Margin = new Thickness(16, 0, 0, 0) };
    private readonly System.Windows.Controls.ListView list_ = new() { SelectionMode = SelectionMode.Multiple };
    private List<Entry> all_ = [];

    public List<Entry> Selected { get; } = [];

    public ProcessPickerWindow()
    {
        Title = L.T("Laufendes Programm hinzufügen", "Add running program");
        Width = 820;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = Ui.AppIcon;

        list_.ItemTemplate = null;
        list_.MouseDoubleClick += (_, _) => Accept();

        var top = Ui.Row(10, Ui.Icon(Icons.Search), search_, windowsOnly_, Ui.Button(L.T("Aktualisieren", "Refresh"), Load, Icons.Refresh));
        top.Margin = new Thickness(0, 0, 0, 8);
        windowsOnly_.Margin = new Thickness(20, 0, 0, 0);
        top.Children[^1].SetValue(MarginProperty, new Thickness(16, 0, 0, 0));
        var hint = Ui.Caption(L.T("Mehrfachauswahl per Klick. Tipp: Starte das Programm vorher, damit es hier erscheint.",
                                  "Click to select several. Tip: start the program first so it shows up here."));
        hint.Margin = new Thickness(0, 0, 0, 8);
        var buttons = Ui.Row(0,
            Ui.Button(L.T("Abbrechen", "Cancel"), () => { DialogResult = false; }),
            Ui.Button(L.T("Hinzufügen", "Add"), Accept, Icons.Add, primary: true));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 12, 0, 0);

        var body = new DockPanel { Margin = new Thickness(20, 8, 20, 20) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(hint);
        body.Children.Add(buttons);
        body.Children.Add(list_);

        Content = body;

        search_.TextChanged += (_, _) => Filter();
        windowsOnly_.Checked += (_, _) => Filter();
        windowsOnly_.Unchecked += (_, _) => Filter();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
        Loaded += (_, _) => { Load(); search_.Focus(); };
    }

    private void Accept()
    {
        Selected.AddRange(list_.SelectedItems.Cast<System.Windows.Controls.ListViewItem>().Select(i => (Entry)i.Tag));
        DialogResult = true;
    }

    private void Load()
    {
        Cursor = Cursors.Wait;
        Task.Run(() =>
        {
            int own = Environment.ProcessId;
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
            return entries.Values.OrderBy(e => e.Description, StringComparer.CurrentCultureIgnoreCase).ToList();
        }).ContinueWith(t => Dispatcher.Invoke(() => { all_ = t.Result; Cursor = null; Filter(); }));
    }

    private void Filter()
    {
        string q = search_.Text.Trim();
        list_.Items.Clear();
        foreach (var e in all_.Where(e => (windowsOnly_.IsChecked != true || e.WindowTitle.Length > 0) &&
                                          (q.Length == 0 || e.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                         || e.ExeName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                         || e.WindowTitle.Contains(q, StringComparison.OrdinalIgnoreCase))))
        {
            var img = ExeIcons.For(e.Path);
            UIElement icon = img != null
                ? new System.Windows.Controls.Image { Source = img, Width = 28, Height = 28 }
                : Dim(Ui.Icon(Icons.Apps, 24));
            var text = new StackPanel();
            text.Children.Add(Ui.Body(e.Description, wrap: false));
            text.Children.Add(Ui.Caption(e.WindowTitle.Length > 0 ? $"{e.ExeName}  ·  {e.WindowTitle}" : e.ExeName, false));
            list_.Items.Add(new System.Windows.Controls.ListViewItem { Content = Ui.Row(14, icon, text), Tag = e, ToolTip = e.Path ?? e.ExeName, Padding = new Thickness(8, 6, 8, 6) });
        }
    }
}

/// <summary>Small yes/no question in the app's style (optional details, e.g. release notes, and a link button).</summary>
internal sealed class ConfirmWindow : Window
{
    internal ConfirmWindow(string title, string heading, string text, string? details, string yes, string no, (string Text, Action Click)? link)
    {
        Title = title;
        Icon = Ui.AppIcon;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true; // asked from the tray: no owner window to come up with

        var body = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        body.Children.Add(new TextBlock { Text = heading, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        if (!string.IsNullOrWhiteSpace(details))
        {
            var notes = Ui.Body(details, secondary: true);
            notes.Margin = new Thickness(12, 10, 12, 10);
            var card = Ui.Card(new ScrollViewer { Content = notes, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, new Thickness(0));
            card.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(card);
        }

        var buttons = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        var right = Ui.Row(8, Ui.Button(no, () => DialogResult = false), Ui.Button(yes, () => DialogResult = true, primary: true));
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        if (link is { } l) buttons.Children.Add(Ui.Button(l.Text, l.Click));
        body.Children.Add(buttons);

        Content = body;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    public static bool Ask(string title, string heading, string text, string? details, string yes, string no, (string, Action)? link = null) =>
        new ConfirmWindow(title, heading, text, details, yes, no, link).ShowDialog() == true;

    /// <summary>Markdown (release notes) as plain text.</summary>
    public static string PlainText(string markdown) =>
        string.Join("\n", markdown.Replace("\r", "").Split('\n')
            .Select(l => l.TrimStart('#', ' ').Replace("**", "").Replace("`", ""))
            .Select(l => l.StartsWith("- ") ? "•  " + l[2..] : l)).Trim();
}

/// <summary>Finds out how a display can be turned off: tries each method for a few seconds and asks.</summary>
internal sealed class OffWizardWindow : Window
{
    private readonly DisplayInfo display_;
    private readonly List<string> methods_;
    private int step_ = -1;
    private readonly TextBlock heading_ = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock text_ = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel buttons_ = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
    private readonly ProgressBar ring_ = new() { IsIndeterminate = true, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 16, 0, 0) };

    /// <summary>The method that worked (null = none / cancelled).</summary>
    public string? Method { get; private set; }

    public OffWizardWindow(DisplayInfo display)
    {
        display_ = display;
        // gentlest first; the primary display cannot be disconnected
        methods_ = [OffMethod.DdcStandby, OffMethod.DdcOff];
        if (display.CanTurnOff) methods_.Add(OffMethod.Disconnect);

        Title = L.T("Ausschalten einrichten – {0}", "Set up turning off – {0}", display.Name);
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = Ui.AppIcon;

        var body = new StackPanel { Margin = new Thickness(24, 8, 24, 24) };
        body.Children.Add(heading_);
        body.Children.Add(text_);
        body.Children.Add(ring_);
        body.Children.Add(buttons_);
        Content = body;

        Intro();
    }

    private void Buttons(params Button[] buttons)
    {
        buttons_.Children.Clear();
        foreach (var b in buttons) buttons_.Children.Add(b);
    }

    private void Intro()
    {
        heading_.Text = L.T("Wie lässt sich {0} ausschalten?", "How can {0} be turned off?", display_.Name);
        text_.Text = L.T("Monitore reagieren unterschiedlich. Der Assistent probiert nacheinander bis zu {0} Methoden, jeweils für 5 Sekunden. Schau dabei auf den Bildschirm und sag mir danach, ob er aus war.",
                         "Monitors react differently. The wizard tries up to {0} methods one after the other, 5 seconds each. Watch the display and tell me afterwards whether it was off.", methods_.Count);
        Buttons(Ui.Button(L.T("Abbrechen", "Cancel"), () => DialogResult = false),
                Ui.Button(L.T("Los geht's", "Start"), Next, Icons.Play, primary: true));
    }

    private async void Next()
    {
        step_++;
        if (step_ >= methods_.Count)
        {
            heading_.Text = L.T("Keine Methode hat funktioniert", "No method worked");
            text_.Text = display_.CanTurnOff
                ? L.T("Seltsam – „Vom Desktop trennen“ sollte immer wirken. Bitte melde das als Fehler.", "Odd – \"Disconnect from desktop\" should always work. Please report this as a bug.")
                : L.T("Dieser Bildschirm wird beim Ausschalten stattdessen schwarz abgedeckt.", "This display will be covered in black instead.");
            Buttons(Ui.Button(L.T("Schließen", "Close"), () => DialogResult = false, primary: true));
            return;
        }

        string method = methods_[step_];
        heading_.Text = L.T("Test {0} von {1}: {2}", "Test {0} of {1}: {2}", step_ + 1, methods_.Count, OffMethod.Text(method));
        text_.Text = method == OffMethod.Disconnect
            ? L.T("Der Bildschirm wird 5 Sekunden lang vom Desktop getrennt. Windows ordnet dabei kurz die Fenster neu an.",
                  "The display is disconnected from the desktop for 5 seconds. Windows briefly rearranges the windows.")
            : L.T("Der Bildschirm bekommt 5 Sekunden lang den DDC/CI-Befehl zum Ausschalten.", "The display receives the DDC/CI power-off command for 5 seconds.");
        Buttons();
        ring_.Visibility = Visibility.Visible;

        await Task.Run(async () =>
        {
            if (method == OffMethod.Disconnect)
            {
                DisplayConfig.Apply(new HashSet<string> { display_.DeviceName });
                await Task.Delay(5000);
                DisplayConfig.Restore();
            }
            else
            {
                Ddc.SetPower(display_.DeviceName, method == OffMethod.DdcOff ? 5u : 4u);
                await Task.Delay(5000);
                Ddc.SetPower(display_.DeviceName, 1);
            }
        });

        ring_.Visibility = Visibility.Collapsed;
        heading_.Text = L.T("War {0} gerade aus?", "Was {0} off just now?", display_.Name);
        text_.Text = OffMethod.Text(method);
        Buttons(Ui.Button(L.T("Nochmal", "Again"), () => { step_--; Next(); }, Icons.Repeat),
                Ui.Button(L.T("Nein", "No"), Next),
                Ui.Button(L.T("Ja, war aus", "Yes, it was off"), () => { Method = method; DialogResult = true; }, Icons.Check, primary: true));
    }
}
