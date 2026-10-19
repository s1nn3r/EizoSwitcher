using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

internal sealed class SettingsWindow : Window
{
    /// <summary>One display column: live data if connected, otherwise what we remembered.</summary>
    private sealed record Col(string Id, string Name, bool Connected, bool IsEizo, bool SupportsPower, IReadOnlyList<string> Modes, string? Current);

    /// <summary>ComboBox entry for a display action.</summary>
    private sealed record Opt(string Token, string Label, bool Missing = false)
    {
        public override string ToString() => Label;
    }

    private sealed class RuleRow
    {
        public required ProcessRule Rule;
        public required Border View;
        public required CheckBox Enabled;
        public required TextBox Name;
        public required CheckBox MatchPath;
        public required Dictionary<string, ComboBox> Actions;
    }

    private const double NameWidth = 210, ExeWidth = 170, PathWidth = 56, ColWidth = 160;

    private readonly DisplayService displays_;
    private readonly GameDetector games_;
    private readonly AppController controller_;
    private readonly AppSettings work_;
    private List<Col> cols_ = [];

    private readonly TabControl tabs_ = new();
    private readonly List<RuleRow> rows_ = [];
    private readonly StackPanel ruleTable_ = new() { Spacing = 2 };
    private readonly Dictionary<string, ComboBox> defaultBoxes_ = [];
    private readonly Dictionary<string, ComboBox> gameBoxes_ = [];
    private ToggleSwitch gamesEnabled_ = null!;
    private CheckBox gExclusive_ = null!, gBorderless_ = null!, gWinList_ = null!, gLibraries_ = null!;
    private ListBox extraFolders_ = null!;
    private TextBox exclusions_ = null!;
    private CheckBox autostart_ = null!, notify_ = null!, autoCheck_ = null!, autoInstall_ = null!;
    private NumericUpDown poll_ = null!;
    private ComboBox language_ = null!, theme_ = null!;

    public event Action<AppSettings>? Saved;

    public TabControl Tabs => tabs_;

    public SettingsWindow(AppSettings settings, DisplayService displays, GameDetector games, AppController controller)
    {
        displays_ = displays;
        games_ = games;
        controller_ = controller;
        work_ = settings.Clone();

        Icon = Ui.AppIcon();
        Width = 1120;
        Height = 720;
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        tabs_.Padding = new Thickness(8, 4, 8, 0);
        Rebuild();

        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    /// <summary>Builds all texts and tabs again (e.g. after the language changed).</summary>
    public void Rebuild()
    {
        Title = "EizoSwitcher – " + L.T("Einstellungen", "Settings");
        var save = Ui.Button(L.T("Speichern", "Save"), () => { Collect(); Saved?.Invoke(work_.Clone()); Close(); }, accent: true);
        var apply = Ui.Button(L.T("Übernehmen", "Apply"), () => { Collect(); Saved?.Invoke(work_.Clone()); });
        var cancel = Ui.Button(L.T("Abbrechen", "Cancel"), Close);
        var bar = Ui.Row(8, apply, cancel, save);
        bar.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = new Border { Padding = new Thickness(16, 10), BorderBrush = Ui.SubtleBorder, BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
        DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);

        Content = null; // detach tabs_ from the previous layout
        BuildTabs();
        Content = new DockPanel { Children = { footer, tabs_ } };
    }

    private void BuildTabs()
    {
        int selected = Math.Max(0, tabs_.SelectedIndex);
        cols_ = BuildColumns();
        tabs_.Items.Clear();
        tabs_.Items.Add(new TabItem { Header = L.T("Programme", "Programs"), Content = ProgramsTab() });
        tabs_.Items.Add(new TabItem { Header = L.T("Spiele", "Games"), Content = GamesTab() });
        tabs_.Items.Add(new TabItem { Header = L.T("Standard", "Defaults"), Content = DefaultsTab() });
        tabs_.Items.Add(new TabItem { Header = L.T("Bildschirme", "Displays"), Content = DisplaysTab() });
        tabs_.Items.Add(new TabItem { Header = L.T("Allgemein", "General"), Content = GeneralTab() });
        tabs_.SelectedIndex = selected;
    }

    /// <summary>Called when displays were re-enumerated while the window is open.</summary>
    public void DisplaysChanged()
    {
        Collect();
        BuildTabs();
    }

    private List<Col> BuildColumns()
    {
        var cols = displays_.Displays.Select(d => new Col(d.Id, d.Name, true, d.IsEizo, d.SupportsPower,
            d.Eizo?.Modes ?? [], d.Eizo?.CurrentModeName)).ToList();
        foreach (var k in work_.Displays.Where(k => cols.All(c => c.Id != k.Id)))
            cols.Add(new Col(k.Id, k.Name, false, k.IsEizo, k.SupportsPower, k.Modes, null));
        // same model twice: append the serial so columns can be told apart
        return [.. cols.Select(c => cols.Count(x => x.Name == c.Name) > 1 ? c with { Name = $"{c.Name} ({Ui.Serial(c.Id)[^4..]})" } : c)];
    }

    private static List<Opt> Options(Col c, bool special, bool noneMeansDefault = false)
    {
        var list = new List<Opt> { new("", noneMeansDefault ? L.T("— (Standard)", "— (default)") : "—") };
        list.AddRange(c.Modes.Select(m => new Opt(m, m)));
        if (special)
        {
            list.Add(new Opt(DisplayAction.Black, "■ " + DisplayAction.Text(DisplayAction.Black)));
            if (c.SupportsPower) list.Add(new Opt(DisplayAction.Off, "⏻ " + DisplayAction.Text(DisplayAction.Off)));
        }
        return list;
    }

    private static ComboBox ActionBox(Col c, string? value, bool special, double width, bool noneMeansDefault = false)
    {
        var opts = Options(c, special, noneMeansDefault);
        value ??= "";
        var sel = opts.FirstOrDefault(o => o.Token.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (sel == null)
        {
            sel = new Opt(value, value + "  ⚠", Missing: true);
            opts.Add(sel);
        }
        return new ComboBox { ItemsSource = opts, SelectedItem = sel, Width = width, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private static string Token(ComboBox box) => (box.SelectedItem as Opt)?.Token ?? "";

    private static string ColSubtitle(Col c) =>
        !c.Connected ? L.T("nicht verbunden", "not connected")
        : c.IsEizo ? L.T("EIZO · Farbmodi", "EIZO · color modes")
        : c.SupportsPower ? L.T("Schwarz / Aus", "Black / off")
        : L.T("Schwarz", "Black");

    // ================================================================= Programs

    private Control ProgramsTab()
    {
        var add = Ui.Button(L.T("＋  Laufendes Programm …", "＋  Running program …"), async () => await AddFromProcessesAsync(), accent: true);
        var browse = Ui.Button(L.T("＋  Programmdatei auswählen …", "＋  Choose executable …"), async () => await AddFromFileAsync());

        var header = new Grid { ColumnDefinitions = RowColumns(), Margin = new Thickness(6, 0, 6, 4) };
        void H(int col, Control c) { Grid.SetColumn(c, col); header.Children.Add(c); }
        H(2, Ui.Text(L.T("Name", "Name"), 12, FontWeight.SemiBold, opacity: 0.8));
        H(3, Ui.Text(L.T("Programm", "Program"), 12, FontWeight.SemiBold, opacity: 0.8));
        var pathHead = Ui.Text(L.T("Pfad", "Path"), 12, FontWeight.SemiBold, opacity: 0.8);
        ToolTip.SetTip(pathHead, L.T("Nur auslösen, wenn das Programm aus genau diesem Ordner gestartet wurde", "Only trigger when the program runs from exactly this folder"));
        H(4, pathHead);
        for (int i = 0; i < cols_.Count; i++)
        {
            var c = cols_[i];
            var head = Ui.Column(0, Ui.Text(c.Name, 12.5, FontWeight.SemiBold), Ui.Text(ColSubtitle(c), 11, opacity: 0.65));
            head.Opacity = c.Connected ? 1 : 0.5;
            H(5 + i, head);
        }

        rows_.Clear();
        ruleTable_.Children.Clear();
        foreach (var r in work_.Rules) AddRow(r);
        UpdateEmptyState();

        var table = Ui.Column(4, header, ruleTable_);
        return new DockPanel
        {
            Margin = new Thickness(24, 18, 24, 12),
            Children =
            {
                Docked(Ui.Column(10,
                    Ui.Heading(L.T("Programme", "Programs")),
                    Ui.Hint(L.T("Sobald eines dieser Programme läuft, werden die Bildschirme wie angegeben umgeschaltet. Wird es beendet, gilt wieder der Standard. " +
                                "Laufen mehrere, gewinnt die oberste Zeile. „—“ lässt einen Bildschirm unverändert. Graue Zeilen verweisen auf einen Farbmodus oder Bildschirm, den es nicht (mehr) gibt.",
                                "As soon as one of these programs runs, the displays switch as configured. When it exits, the defaults apply again. " +
                                "If several are running, the top row wins. \"—\" leaves a display unchanged. Grey rows refer to a color mode or display that does not exist (anymore).")),
                    Ui.Row(8, add, browse)), Avalonia.Controls.Dock.Top, new Thickness(0, 0, 0, 14)),
                new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = table },
            },
        };
    }

    private static Control Docked(Control c, Avalonia.Controls.Dock dock, Thickness margin)
    {
        DockPanel.SetDock(c, dock);
        c.Margin = margin;
        return c;
    }

    private ColumnDefinitions RowColumns()
    {
        var defs = new ColumnDefinitions
        {
            new(32, GridUnitType.Pixel), new(30, GridUnitType.Pixel),
            new(NameWidth, GridUnitType.Pixel), new(ExeWidth, GridUnitType.Pixel), new(PathWidth, GridUnitType.Pixel),
        };
        foreach (var _ in cols_) defs.Add(new ColumnDefinition(ColWidth + 8, GridUnitType.Pixel));
        defs.Add(new ColumnDefinition(100, GridUnitType.Pixel));
        return defs;
    }

    private void AddRow(ProcessRule rule)
    {
        var grid = new Grid { ColumnDefinitions = RowColumns() };
        void Put(int col, Control c) { Grid.SetColumn(c, col); grid.Children.Add(c); }

        var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(enabled, L.T("Regel aktiv", "Rule enabled"));
        Put(0, enabled);

        var bmp = OperatingSystem.IsWindows() ? ExeIcons.For(rule.ExePath) : null;
        Put(1, bmp != null
            ? new Image { Source = bmp, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center }
            : Ui.Text("▣", 16, opacity: 0.4));

        var name = new TextBox { Text = rule.Name, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Put(2, name);

        var exe = Ui.Text(rule.ExeName, 12.5, opacity: 0.85);
        exe.Margin = new Thickness(0, 0, 8, 0);
        ToolTip.SetTip(exe, rule.ExePath ?? rule.ExeName);
        Put(3, exe);

        var matchPath = new CheckBox { IsChecked = rule.MatchPath, IsEnabled = !string.IsNullOrEmpty(rule.ExePath), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(matchPath, rule.ExePath ?? "");
        Put(4, matchPath);

        var actions = new Dictionary<string, ComboBox>();
        for (int i = 0; i < cols_.Count; i++)
        {
            var box = ActionBox(cols_[i], rule.Actions.GetValueOrDefault(cols_[i].Id), special: true, ColWidth);
            box.Margin = new Thickness(0, 0, 8, 0);
            actions[cols_[i].Id] = box;
            Put(5 + i, box);
        }

        var row = new RuleRow { Rule = rule, View = null!, Enabled = enabled, Name = name, MatchPath = matchPath, Actions = actions };
        var buttons = Ui.Row(0,
            Ui.IconButton("↑", L.T("Nach oben (höhere Priorität)", "Move up (higher priority)"), () => Move(row, -1)),
            Ui.IconButton("↓", L.T("Nach unten", "Move down"), () => Move(row, 1)),
            Ui.IconButton("✕", L.T("Entfernen", "Remove"), () => Remove(row)));
        Put(5 + cols_.Count, buttons);

        row.View = new Border { Child = grid, Padding = new Thickness(6, 4), CornerRadius = new CornerRadius(6) };
        foreach (var box in actions.Values) box.SelectionChanged += (_, _) => UpdateRowState(row);
        enabled.IsCheckedChanged += (_, _) => UpdateRowState(row);
        rows_.Add(row);
        ruleTable_.Children.Add(row.View);
        UpdateRowState(row);
    }

    private void UpdateRowState(RuleRow row)
    {
        var reasons = new List<string>();
        foreach (var c in cols_)
        {
            string t = Token(row.Actions[c.Id]);
            if (t.Length == 0) continue;
            if (!c.Connected) reasons.Add(L.T("{0} ist nicht verbunden", "{0} is not connected", c.Name));
            else if (DisplayAction.IsMode(t) && !c.Modes.Contains(t, StringComparer.OrdinalIgnoreCase))
                reasons.Add(L.T("Den Farbmodus „{0}“ gibt es auf {1} nicht (mehr)", "Color mode \"{0}\" does not exist on {1} (anymore)", t, c.Name));
        }
        bool enabled = row.Enabled.IsChecked == true;
        row.View.Opacity = reasons.Count > 0 ? 0.45 : enabled ? 1 : 0.6;
        row.View.Background = reasons.Count > 0 ? Ui.Subtle : null;
        ToolTip.SetTip(row.View, reasons.Count > 0 ? string.Join("\n", reasons) : null);
        row.Name.FontStyle = enabled ? FontStyle.Normal : FontStyle.Italic;
    }

    private void Move(RuleRow row, int delta)
    {
        int i = rows_.IndexOf(row), j = i + delta;
        if (j < 0 || j >= rows_.Count) return;
        rows_.RemoveAt(i);
        rows_.Insert(j, row);
        ruleTable_.Children.RemoveAt(i);
        ruleTable_.Children.Insert(j, row.View);
    }

    private void Remove(RuleRow row)
    {
        rows_.Remove(row);
        ruleTable_.Children.Remove(row.View);
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (rows_.Count == 0 && ruleTable_.Children.Count == 0)
        {
            var hint = Ui.Hint(L.T("Noch keine Programme. Füge oben ein laufendes Programm oder eine Programmdatei hinzu.",
                                   "No programs yet. Add a running program or an executable above."));
            hint.Margin = new Thickness(6, 12);
            ruleTable_.Children.Add(hint);
        }
        else if (rows_.Count > 0 && ruleTable_.Children.Count > rows_.Count)
        {
            ruleTable_.Children.RemoveAt(0);
        }
    }

    private void AddProgram(string exeName, string? path, string? description)
    {
        var existing = rows_.FirstOrDefault(r => ProcessNames.Normalize(r.Rule.ExeName) == ProcessNames.Normalize(exeName));
        if (existing != null) { existing.Name.Focus(); return; }
        AddRow(new ProcessRule
        {
            Name = description ?? Path.GetFileNameWithoutExtension(exeName),
            ExeName = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe",
            ExePath = path,
        });
        UpdateEmptyState();
    }

    private async Task AddFromProcessesAsync()
    {
        var picked = await new ProcessPickerWindow().ShowDialog<List<ProcessPickerWindow.Entry>?>(this);
        foreach (var e in picked ?? []) AddProgram(e.ExeName, e.Path, e.Description);
    }

    private async Task AddFromFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Programm auswählen", "Choose program"),
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(L.T("Programme", "Programs")) { Patterns = ["*.exe"] }],
        });
        foreach (var f in files)
        {
            string? path = f.TryGetLocalPath();
            if (path != null) AddProgram(Path.GetFileName(path), path, Shell.Describe(path, Path.GetFileNameWithoutExtension(path)));
        }
    }

    // ================================================================= Games

    private Control GamesTab()
    {
        var g = work_.Games;
        gamesEnabled_ = new ToggleSwitch { IsChecked = g.Enabled, OnContent = L.T("Spiele automatisch erkennen", "Detect games automatically"), OffContent = L.T("Spiele automatisch erkennen", "Detect games automatically") };

        gExclusive_ = Ui.Check(L.T("Exklusiver Vollbildmodus (DirectX)", "Exclusive fullscreen (DirectX)"), g.DetectExclusiveFullscreen);
        gBorderless_ = Ui.Check(L.T("Randloses Vollbildfenster im Vordergrund", "Borderless fullscreen window in the foreground"), g.DetectBorderlessFullscreen);
        gWinList_ = Ui.Check(L.T("Von Windows als Spiel erkannte Programme (Game Bar)", "Programs Windows recognizes as games (Game Bar)"), g.UseWindowsGameList);
        gLibraries_ = Ui.Check(L.T("Programme aus Spielebibliotheken (Steam, Epic, GOG, Ubisoft, EA, Xbox)", "Programs from game libraries (Steam, Epic, GOG, Ubisoft, EA, Xbox)"), g.UseLauncherLibraries);

        var detection = Ui.Card(
            Ui.Text(L.T("So werden Spiele erkannt", "How games are detected"), 14, FontWeight.SemiBold),
            gExclusive_, Ui.Hint(L.T("   Zuverlässig bei klassischem Vollbild. Wird auch bei Alt+Tab gehalten, bis das Spiel beendet ist.",
                                      "   Reliable for classic fullscreen. Kept on Alt+Tab until the game exits.")),
            gBorderless_, Ui.Hint(L.T("   Für moderne Spiele im „Vollbild-Fenster“. Browser und Videoplayer sind unten ausgenommen.",
                                       "   For modern games in \"borderless\" mode. Browsers and video players are excluded below.")),
            gWinList_, Ui.Hint(L.T("   Nutzt die Liste, die Windows für den Spielmodus führt.", "   Uses the list Windows keeps for Game Mode.")),
            gLibraries_, Ui.Hint(L.T("   Erkennt Spiele schon beim Start, auch im Fenstermodus.", "   Detects games right at start, even in windowed mode.")));

        gameBoxes_.Clear();
        var modes = Ui.Card(
            Ui.Text(L.T("Während ein Spiel läuft", "While a game is running"), 14, FontWeight.SemiBold),
            Ui.Hint(L.T("Programmregeln haben Vorrang. Tipp: Nebenbildschirme auf „Schwarz“ stellen, damit sie nicht ablenken.",
                        "Program rules take precedence. Tip: set secondary displays to \"Black\" so they don't distract.")));
        foreach (var c in cols_)
        {
            var box = ActionBox(c, g.Actions.GetValueOrDefault(c.Id), special: true, 220);
            gameBoxes_[c.Id] = box;
            ((StackPanel)modes.Child!).Children.Add(DisplayLine(c, box));
        }

        extraFolders_ = new ListBox { ItemsSource = new List<string>(g.ExtraFolders), MinHeight = 60, MaxHeight = 160 };
        var libraries = new ListBox { ItemsSource = OperatingSystem.IsWindows() ? games_.Libraries : [], MaxHeight = 180, IsEnabled = true };
        var folders = Ui.Card(
            Ui.Text(L.T("Spielebibliotheken", "Game libraries"), 14, FontWeight.SemiBold),
            Ui.Hint(L.T("Automatisch gefunden:", "Found automatically:")), libraries,
            Ui.Hint(L.T("Zusätzliche Ordner:", "Additional folders:")), extraFolders_,
            Ui.Row(8,
                Ui.Button(L.T("Ordner hinzufügen …", "Add folder …"), async () => await AddFolderAsync()),
                Ui.Button(L.T("Entfernen", "Remove"), () =>
                {
                    if (extraFolders_.SelectedItem is string s)
                        extraFolders_.ItemsSource = ((List<string>)extraFolders_.ItemsSource!).Where(x => x != s).ToList();
                })));

        exclusions_ = new TextBox { Text = string.Join(Environment.NewLine, g.ExcludedProcesses), AcceptsReturn = true, Height = 160, TextWrapping = TextWrapping.NoWrap };
        var excluded = Ui.Card(
            Ui.Text(L.T("Nie als Spiel behandeln", "Never treat as a game"), 14, FontWeight.SemiBold),
            Ui.Hint(L.T("Prozessnamen, einer pro Zeile (ohne .exe).", "Process names, one per line (without .exe).")),
            exclusions_,
            Ui.Button(L.T("Liste zurücksetzen", "Reset list"), () => exclusions_.Text = string.Join(Environment.NewLine, GameSettings.DefaultExclusions)));

        return Ui.Page(Ui.Heading(L.T("Spiele", "Games")), gamesEnabled_, modes, detection, folders, excluded);
    }

    private async Task AddFolderAsync()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("Spieleordner auswählen", "Choose games folder") });
        var list = (List<string>)extraFolders_.ItemsSource!;
        foreach (var f in picked)
            if (f.TryGetLocalPath() is { } p && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list = [.. list, p];
        extraFolders_.ItemsSource = list;
    }

    private static Grid DisplayLine(Col c, Control editor, string? extra = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,240,*"), Margin = new Thickness(0, 2) };
        var label = Ui.Column(0, Ui.Text(c.Name, 13, FontWeight.SemiBold), Ui.Text(ColSubtitle(c), 11, opacity: 0.65));
        label.Opacity = c.Connected ? 1 : 0.55;
        grid.Children.Add(label);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        if (extra != null)
        {
            var t = Ui.Text(extra, 12, opacity: 0.7);
            t.Margin = new Thickness(14, 0, 0, 0);
            Grid.SetColumn(t, 2);
            grid.Children.Add(t);
        }
        return grid;
    }

    // ================================================================= Defaults

    private Control DefaultsTab()
    {
        defaultBoxes_.Clear();
        var card = Ui.Card();
        var panel = (StackPanel)card.Child!;
        foreach (var c in cols_)
        {
            if (c.IsEizo)
            {
                var box = ActionBox(c, work_.Defaults.GetValueOrDefault(c.Id), special: false, 220);
                defaultBoxes_[c.Id] = box;
                panel.Children.Add(DisplayLine(c, box, c.Current != null ? L.T("aktuell: {0}", "current: {0}", c.Current) : null));
            }
            else
            {
                panel.Children.Add(DisplayLine(c, Ui.Text(L.T("immer normal (an, nicht abgedunkelt)", "always normal (on, not blanked)"), 12.5, opacity: 0.7)));
            }
        }
        if (cols_.Count == 0) panel.Children.Add(Ui.Hint(L.T("Keine Bildschirme gefunden.", "No displays found.")));

        return Ui.Page(
            Ui.Heading(L.T("Standard", "Defaults")),
            Ui.Hint(L.T("Dieser Zustand gilt, solange kein konfiguriertes Programm und kein Spiel läuft. Beim ersten Start übernimmt EizoSwitcher den aktuell eingestellten Modus.",
                        "This applies while no configured program and no game is running. On first start EizoSwitcher takes over the currently selected mode.")),
            card);
    }

    // ================================================================= Displays

    private Control DisplaysTab()
    {
        var list = Ui.Column(10);
        foreach (var d in displays_.Displays) list.Children.Add(DisplayCard(d));
        foreach (var k in work_.Displays.Where(k => displays_.Displays.All(d => d.Id != k.Id)))
        {
            var card = Ui.Card(Ui.Text(k.Name, 15, FontWeight.SemiBold),
                Ui.Hint(L.T("Nicht verbunden · zuletzt gesehen {0:g}", "Not connected · last seen {0:g}", k.LastSeen)),
                Ui.Button(L.T("Vergessen", "Forget"), () => { work_.Displays.RemoveAll(x => x.Id == k.Id); BuildTabs(); }));
            card.Opacity = 0.6;
            list.Children.Add(card);
        }

        var refresh = Ui.Button(L.T("Neu einlesen", "Rescan"), () => controller_.RefreshDisplays());
        return Ui.Page(
            Ui.Row(16, Ui.Heading(L.T("Bildschirme", "Displays")), refresh),
            Ui.Hint(L.T("EIZO-Monitore mit angeschlossenem USB-Kabel lassen sich direkt umschalten. Alle anderen Bildschirme können abgedunkelt und – falls sie DDC/CI unterstützen – ausgeschaltet werden.",
                        "EIZO monitors with the USB cable connected can be switched directly. All other displays can be blanked and – if they support DDC/CI – turned off.")),
            list);
    }

    private Control DisplayCard(DisplayInfo d)
    {
        string Yes(bool b) => b ? L.T("ja", "yes") : L.T("nein", "no");
        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), RowSpacing = 3 };
        void Fact(string k, string v)
        {
            int r = facts.RowDefinitions.Count;
            facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var a = Ui.Text(k, 12.5, opacity: 0.7);
            var b = Ui.Text(v, 12.5);
            Grid.SetRow(a, r); Grid.SetRow(b, r); Grid.SetColumn(b, 1);
            facts.Children.Add(a); facts.Children.Add(b);
        }
        Fact(L.T("Hersteller", "Manufacturer"), d.Manufacturer);
        Fact(L.T("Seriennummer", "Serial number"), Ui.Serial(d.Serial));
        if (d.Width > 0) Fact(L.T("Auflösung / Position", "Resolution / position"), $"{d.Width} × {d.Height}  @ {d.X}, {d.Y}{(d.Primary ? L.T("  (Hauptbildschirm)", "  (primary)") : "")}");
        Fact(L.T("EIZO-USB-Steuerung", "EIZO USB control"), Yes(d.IsEizo));
        Fact(L.T("DDC/CI ausschalten", "DDC/CI power off"), Yes(d.SupportsPower));
        if (d.Eizo != null) Fact(L.T("Aktueller Farbmodus", "Current color mode"), d.Eizo.CurrentModeName);

        var actions = Ui.Row(8);
        if (d.Eizo != null)
        {
            var box = new ComboBox { ItemsSource = d.Eizo.Modes, SelectedIndex = d.Eizo.CurrentMode, Width = 200 };
            actions.Children.Add(box);
            actions.Children.Add(Ui.Button(L.T("Aktivieren", "Activate"), () =>
            {
                int i = box.SelectedIndex;
                Task.Run(() => displays_.SetMode(d, i)).ContinueWith(_ => Dispatcher.UIThread.Post(BuildTabs));
            }));
        }
        if (d.Width > 0)
        {
            actions.Children.Add(Ui.Button(L.T("3 s schwarz testen", "Test black (3 s)"), () =>
            {
                controller_.Show(d);
                DispatcherTimer.RunOnce(() => controller_.Hide(d.Id), TimeSpan.FromSeconds(3));
            }));
        }
        if (d.SupportsPower)
        {
            actions.Children.Add(Ui.Button(L.T("5 s ausschalten testen", "Test power off (5 s)"), () =>
            {
                Task.Run(() => { displays_.SetPower(d, false); Thread.Sleep(5000); displays_.SetPower(d, true); });
            }));
        }

        return Ui.Card(
            Ui.Row(10, Ui.Text(d.Name, 15, FontWeight.SemiBold), Ui.Text(d.DeviceName.TrimStart('\\', '.'), 12, opacity: 0.6)),
            facts, actions);
    }

    // ================================================================= General

    private Control GeneralTab()
    {
        autostart_ = Ui.Check(L.T("Mit Windows starten", "Start with Windows"), work_.StartWithWindows);
        notify_ = Ui.Check(L.T("Beim Umschalten kurz benachrichtigen", "Show a short notification when switching"), work_.ShowNotifications);
        poll_ = new NumericUpDown { Value = work_.PollIntervalMs, Minimum = 250, Maximum = 10000, Increment = 250, Width = 160, FormatString = "0" };
        var start = Ui.Card(
            Ui.Text(L.T("Start und Verhalten", "Startup and behavior"), 14, FontWeight.SemiBold),
            autostart_, notify_,
            Ui.Row(10, Ui.Text(L.T("Prozesse prüfen alle", "Check processes every"), 13), poll_, Ui.Text("ms", 13)),
            Ui.Hint(L.T("Kleinere Werte schalten schneller um, kosten aber minimal mehr CPU. 1500 ms sind ein guter Wert.",
                        "Smaller values switch faster at a tiny CPU cost. 1500 ms is a good value.")));

        autoCheck_ = Ui.Check(L.T("Automatisch nach Updates suchen", "Check for updates automatically"), work_.AutoCheckUpdates);
        autoInstall_ = Ui.Check(L.T("Updates ohne Nachfrage installieren", "Install updates without asking"), work_.AutoInstallUpdates);
        var updates = Ui.Card(
            Ui.Text(L.T("Updates", "Updates"), 14, FontWeight.SemiBold),
            Ui.Text(L.T("Installierte Version: {0}", "Installed version: {0}", Updater.Current), 13),
            autoCheck_, autoInstall_,
            Ui.Hint(L.T("Updates kommen aus den GitHub-Releases. Die Prüfsumme wird kontrolliert; ist EizoSwitcher signiert, muss es auch das Update sein.",
                        "Updates come from GitHub releases. The checksum is verified; if EizoSwitcher is signed, the update must be signed by the same publisher.")),
            Ui.Row(8,
                Ui.Button(L.T("Jetzt nach Updates suchen", "Check now"), async () => await controller_.CheckForUpdatesAsync(true)),
                Ui.Button(L.T("Releases auf GitHub", "Releases on GitHub"), () => Shell.Open(Updater.ReleasesUrl))));

        var languages = new List<Opt> { new("", L.T("Systemsprache", "System language")) };
        languages.AddRange(L.Languages.Select(l => new Opt(l.Code, l.Name)));
        language_ = new ComboBox { ItemsSource = languages, SelectedItem = languages.FirstOrDefault(o => o.Token == work_.Language) ?? languages[0], Width = 220 };
        var themes = new List<Opt> { new("", L.T("Wie Windows", "Same as Windows")), new("light", L.T("Hell", "Light")), new("dark", L.T("Dunkel", "Dark")) };
        theme_ = new ComboBox { ItemsSource = themes, SelectedItem = themes.FirstOrDefault(o => o.Token == work_.Theme) ?? themes[0], Width = 220 };
        var appearance = Ui.Card(
            Ui.Text(L.T("Darstellung", "Appearance"), 14, FontWeight.SemiBold),
            LabeledLine(L.T("Sprache", "Language"), language_),
            LabeledLine(L.T("Farbschema", "Theme"), theme_));

        var coffee = Ui.Card(
            Ui.Text(L.T("Gefällt dir EizoSwitcher?", "Do you like EizoSwitcher?"), 14, FontWeight.SemiBold),
            Ui.Hint(L.T("EizoSwitcher ist kostenlos. Wenn es dir den Alltag erleichtert, freue ich mich über einen Kaffee.",
                        "EizoSwitcher is free. If it makes your day a little easier, I'd be happy about a coffee.")),
            Ui.Button(L.T("☕  Spendier mir einen Kaffee", "☕  Buy me a coffee"), () => Shell.Open(Links.Donate), accent: true));

        var info = Ui.Card(
            Ui.Text(L.T("Info", "About"), 14, FontWeight.SemiBold),
            Ui.Row(8,
                Ui.Button(L.T("Projektseite", "Project page"), () => Shell.Open(Links.Project)),
                Ui.Button(L.T("Einstellungsordner öffnen", "Open settings folder"), () => { Directory.CreateDirectory(Log.Folder); Shell.Open(Log.Folder); }),
                Ui.Button(L.T("Protokoll anzeigen", "Show log"), () => Shell.Open(Log.FilePath))),
            Ui.Hint(L.T("EIZO, ColorEdge und ColorNavigator sind Marken der EIZO Corporation. EizoSwitcher ist ein unabhängiges Projekt und nicht mit EIZO verbunden.",
                        "EIZO, ColorEdge and ColorNavigator are trademarks of EIZO Corporation. EizoSwitcher is an independent project, not affiliated with EIZO.")));

        return Ui.Page(Ui.Heading(L.T("Allgemein", "General")), appearance, start, updates, coffee, info);
    }

    private static Grid LabeledLine(string label, Control editor)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*") };
        grid.Children.Add(Ui.Text(label, 13));
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    // ================================================================= collect

    private void Collect()
    {
        if (gamesEnabled_ == null) return; // tabs not built yet

        work_.Rules = [.. rows_.Select(row =>
        {
            var r = row.Rule;
            r.Enabled = row.Enabled.IsChecked == true;
            r.Name = row.Name.Text?.Trim() ?? "";
            r.MatchPath = row.MatchPath.IsChecked == true;
            foreach (var (id, box) in row.Actions)
            {
                string t = Token(box);
                if (t.Length == 0) r.Actions.Remove(id); else r.Actions[id] = t;
            }
            return r;
        })];

        foreach (var (id, box) in defaultBoxes_)
        {
            string t = Token(box);
            if (t.Length == 0) work_.Defaults.Remove(id); else work_.Defaults[id] = t;
        }

        var g = work_.Games;
        g.Enabled = gamesEnabled_.IsChecked == true;
        g.DetectExclusiveFullscreen = gExclusive_.IsChecked == true;
        g.DetectBorderlessFullscreen = gBorderless_.IsChecked == true;
        g.UseWindowsGameList = gWinList_.IsChecked == true;
        g.UseLauncherLibraries = gLibraries_.IsChecked == true;
        foreach (var (id, box) in gameBoxes_)
        {
            string t = Token(box);
            if (t.Length == 0) g.Actions.Remove(id); else g.Actions[id] = t;
        }
        g.ExtraFolders = [.. (List<string>)extraFolders_.ItemsSource!];
        g.ExcludedProcesses = [.. (exclusions_.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

        work_.StartWithWindows = autostart_.IsChecked == true;
        work_.ShowNotifications = notify_.IsChecked == true;
        work_.PollIntervalMs = (int)(poll_.Value ?? 1500);
        work_.AutoCheckUpdates = autoCheck_.IsChecked == true;
        work_.AutoInstallUpdates = autoInstall_.IsChecked == true;
        work_.Language = Token(language_);
        work_.Theme = Token(theme_);
    }
}
