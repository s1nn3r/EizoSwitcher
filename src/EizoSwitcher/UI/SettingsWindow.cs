using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EizoSwitcher.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using WpfUi = Wpf.Ui.Controls;

namespace EizoSwitcher.UI;

/// <summary>Settings in the Windows 11 style. Every change is applied right away (no save button).</summary>
internal sealed class SettingsWindow : FluentWindow
{
    /// <summary>One display column: live data if connected, otherwise what we remembered.</summary>
    private sealed record Col(string Id, string Name, bool Connected, bool IsEizo, bool CanTurnOff, IReadOnlyList<string> Modes, string? Current);

    private sealed class RuleRow
    {
        public required ProcessRule Rule;
        public required Border View;
        public required CheckBox Enabled;
        public required System.Windows.Controls.TextBox Name;
        public required CheckBox MatchPath;
        public required Dictionary<string, ComboBox> Actions;
    }

    private const double NameWidth = 230, PathWidth = 46, ColWidth = 158;

    private readonly DisplayService displays_;
    private readonly GameDetector games_;
    private readonly TrayApp? tray_;
    private readonly AppSettings work_;
    private readonly DispatcherTimer saveTimer_ = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private List<Col> cols_ = [];
    private string colsSignature_ = "";
    private bool building_;

    private readonly ListBox nav_ = new();
    private readonly ContentControl page_ = new();
    private readonly List<(string Title, SymbolRegular Icon, Func<UIElement> Build)> pages_ = [];

    private readonly List<RuleRow> rows_ = [];
    private readonly StackPanel ruleTable_ = new();
    private readonly Dictionary<string, ComboBox> defaultBoxes_ = [], gameBoxes_ = [];
    private readonly Dictionary<(string Id, string Mode), ComboBox> profileBoxes_ = [];

    public event Action<AppSettings>? Saved;

    public int SelectedPage { get => nav_.SelectedIndex; set => nav_.SelectedIndex = Math.Clamp(value, 0, pages_.Count - 1); }
    public int PageCount => pages_.Count;
    public FrameworkElement Root { get; private set; } = null!;

    public SettingsWindow(AppSettings settings, DisplayService displays, GameDetector games, TrayApp? tray)
    {
        displays_ = displays;
        games_ = games;
        tray_ = tray;
        work_ = settings.Clone();

        Width = 1280;
        Height = 800;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        Icon = Ui.AppIcon;

        saveTimer_.Tick += (_, _) => { saveTimer_.Stop(); Collect(); Saved?.Invoke(work_.Clone()); };
        Closing += (_, _) => { if (saveTimer_.IsEnabled) { saveTimer_.Stop(); Collect(); Saved?.Invoke(work_.Clone()); } };
        nav_.SelectionChanged += (_, _) => ShowPage();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        Rebuild();
        Theme.Attach(this);
    }

    /// <summary>Marks the settings as changed; they are applied shortly after the last change.</summary>
    private void Changed()
    {
        if (building_) return;
        saveTimer_.Stop();
        saveTimer_.Start();
    }

    /// <summary>Builds all texts and pages (again), e.g. after the language changed.</summary>
    public void Rebuild()
    {
        building_ = true;
        int selected = Math.Max(0, nav_.SelectedIndex);
        Title = "EizoSwitcher – " + L.T("Einstellungen", "Settings");
        cols_ = BuildColumns();
        colsSignature_ = Signature(cols_);

        pages_.Clear();
        pages_.Add((L.T("Programme", "Programs"), SymbolRegular.Apps24, ProgramsPage));
        pages_.Add((L.T("Spiele", "Games"), SymbolRegular.Games24, GamesPage));
        pages_.Add((L.T("Standard", "Defaults"), SymbolRegular.Home24, DefaultsPage));
        pages_.Add((L.T("Bildschirme", "Displays"), SymbolRegular.Desktop24, DisplaysPage));
        pages_.Add((L.T("Allgemein", "General"), SymbolRegular.Settings24, GeneralPage));
        pageCache_.Clear();

        nav_.Items.Clear();
        foreach (var (title, icon, _) in pages_)
        {
            var item = Ui.Row(12, new SymbolIcon { Symbol = icon, FontSize = 18 }, Ui.Body(title, wrap: false));
            item.Margin = new Thickness(4, 6, 4, 6);
            nav_.Items.Add(new ListBoxItem { Content = item });
        }

        var titleBar = new TitleBar { Title = "EizoSwitcher", Icon = new ImageIcon { Source = Ui.AppIcon } };
        var navPanel = new DockPanel { Width = 240, Margin = new Thickness(8, 8, 0, 8) };
        var version = Ui.Caption($"EizoSwitcher {Updater.Current}");
        version.Margin = new Thickness(14, 0, 0, 6);
        DockPanel.SetDock(version, Dock.Bottom);
        navPanel.Children.Add(version);
        navPanel.Children.Add(nav_);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (navPanel.Parent is Panel old1) old1.Children.Remove(navPanel);
        if (page_.Parent is Panel old2) old2.Children.Remove(page_);
        body.Children.Add(navPanel);
        Grid.SetColumn(page_, 1);
        body.Children.Add(page_);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(titleBar);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Root = root;
        Content = null;
        Content = root;

        nav_.SelectedIndex = Math.Min(selected, pages_.Count - 1);
        ShowPage();
        building_ = false;
    }

    private readonly Dictionary<int, UIElement> pageCache_ = [];

    private void ShowPage()
    {
        int i = nav_.SelectedIndex;
        if (i < 0 || i >= pages_.Count) return;
        bool was = building_;
        building_ = true;
        if (!pageCache_.TryGetValue(i, out var content)) pageCache_[i] = content = pages_[i].Build();
        page_.Content = content;
        building_ = was;
    }

    /// <summary>Called when displays were re-read; only rebuilds if something visible changed.</summary>
    public void DisplaysChanged()
    {
        var cols = BuildColumns();
        if (Signature(cols) == colsSignature_) return;
        Collect();
        Rebuild();
    }

    private static string Signature(List<Col> cols) =>
        string.Join("|", cols.Select(c => $"{c.Id}:{c.Name}:{c.Connected}:{c.CanTurnOff}:{string.Join(",", c.Modes)}"));

    private List<Col> BuildColumns()
    {
        var cols = displays_.Displays.Select(d => new Col(d.Id, d.Name, true, d.IsEizo, d.CanTurnOff, d.Eizo?.Modes ?? [], d.Eizo?.CurrentModeName)).ToList();
        foreach (var k in work_.Displays.Where(k => cols.All(c => c.Id != k.Id)))
            cols.Add(new Col(k.Id, k.Name, false, k.IsEizo, k.CanTurnOff, k.Modes, null));
        return [.. cols.Select(c => cols.Count(x => x.Name == c.Name) > 1 ? c with { Name = $"{c.Name} ({Ui.Serial(c.Id)[^4..]})" } : c)];
    }

    private static List<Opt> Options(Col c, bool special)
    {
        var list = new List<Opt> { new("", "—") };
        list.AddRange(c.Modes.Select(m => new Opt(m, m)));
        if (special)
        {
            list.Add(new Opt(DisplayAction.Black, "■  " + DisplayAction.Text(DisplayAction.Black)));
            list.Add(new Opt(DisplayAction.Off, "⏻  " + DisplayAction.Text(DisplayAction.Off)));
        }
        return list;
    }

    private static string ColSubtitle(Col c) =>
        !c.Connected ? L.T("nicht verbunden", "not connected")
        : c.IsEizo ? L.T("EIZO · Farbmodi", "EIZO · color modes")
        : L.T("Schwarz / Aus", "Black / off");

    // ================================================================= Programs

    private UIElement ProgramsPage()
    {
        var header = new Grid { Margin = new Thickness(8, 0, 8, 6) };
        DefineColumns(header);
        void H(int col, UIElement e) { Grid.SetColumn(e, col); header.Children.Add(e); }
        H(2, Ui.Caption(L.T("Name", "Name"), false));
        var pathHead = Ui.Caption(L.T("Pfad", "Path"), false);
        pathHead.ToolTip = L.T("Nur auslösen, wenn das Programm aus genau diesem Ordner gestartet wurde", "Only trigger when the program runs from exactly this folder");
        H(3, pathHead);
        for (int i = 0; i < cols_.Count; i++)
        {
            var head = new StackPanel { Opacity = cols_[i].Connected ? 1 : 0.5 };
            head.Children.Add(new WpfUi.TextBlock { Text = cols_[i].Name, FontTypography = FontTypography.BodyStrong });
            head.Children.Add(Ui.Caption(ColSubtitle(cols_[i]), false));
            H(4 + i, head);
        }

        rows_.Clear();
        ruleTable_.Children.Clear();
        foreach (var r in work_.Rules) AddRow(r);
        UpdateEmptyState();

        var table = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        table.Children.Add(header);
        table.Children.Add(ruleTable_);
        var card = new Card { Content = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, Padding = new Thickness(8, 12, 8, 8) };

        var intro = Ui.Body(L.T("Sobald eines dieser Programme im Vordergrund ist, werden die Bildschirme wie angegeben umgeschaltet. Wechselst du zu einem anderen Programm oder wird es beendet, gilt wieder der Standard. " +
                                "„—“ lässt einen Bildschirm unverändert. Graue Zeilen verweisen auf einen Farbmodus oder Bildschirm, den es nicht (mehr) gibt.",
                                "As soon as one of these programs is in the foreground, the displays switch as configured. When you switch to another program or it exits, the defaults apply again. " +
                                "\"—\" leaves a display unchanged. Grey rows refer to a color mode or display that does not exist (anymore)."), secondary: true);
        intro.Margin = new Thickness(0, 0, 0, 14);

        var buttons = Ui.Row(0,
            Ui.Button(L.T("Laufendes Programm …", "Running program …"), AddFromProcesses, SymbolRegular.Add24, primary: true),
            Ui.Button(L.T("Programmdatei auswählen …", "Choose executable …"), AddFromFile, SymbolRegular.FolderOpen24));
        buttons.Margin = new Thickness(0, 0, 0, 14);

        var page = Ui.Page(Ui.Title(L.T("Programme", "Programs")), intro, buttons, card);
        ((StackPanel)page.Content).MaxWidth = double.PositiveInfinity;
        return page;
    }

    private void DefineColumns(Grid g)
    {
        foreach (var w in new[] { 40.0, 36, NameWidth, PathWidth })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        foreach (var _ in cols_) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColWidth + 10) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
    }

    private void AddRow(ProcessRule rule)
    {
        var grid = new Grid();
        DefineColumns(grid);
        void Put(int col, UIElement e) { Grid.SetColumn(e, col); grid.Children.Add(e); }

        var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = L.T("Regel aktiv", "Rule enabled"), MinWidth = 0, Padding = new Thickness(0) };
        Put(0, enabled);
        var img = ExeIcons.For(rule.ExePath);
        Put(1, img != null
            ? new System.Windows.Controls.Image { Source = img, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center }
            : new SymbolIcon { Symbol = SymbolRegular.Apps24, FontSize = 20, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });

        var name = new System.Windows.Controls.TextBox { Text = rule.Name, VerticalAlignment = VerticalAlignment.Center };
        var exe = Ui.Caption(rule.ExeName, false);
        exe.Margin = new Thickness(2, 2, 0, 0);
        var nameCell = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = rule.ExePath ?? rule.ExeName };
        nameCell.Children.Add(name);
        nameCell.Children.Add(exe);
        Put(2, nameCell);
        var matchPath = new CheckBox { IsChecked = rule.MatchPath, IsEnabled = !string.IsNullOrEmpty(rule.ExePath), VerticalAlignment = VerticalAlignment.Center, ToolTip = rule.ExePath, MinWidth = 0, Padding = new Thickness(0) };
        Put(3, matchPath);

        var actions = new Dictionary<string, ComboBox>();
        var row = new RuleRow { Rule = rule, View = null!, Enabled = enabled, Name = name, MatchPath = matchPath, Actions = actions };
        for (int i = 0; i < cols_.Count; i++)
        {
            var box = Ui.Combo(Options(cols_[i], special: true), rule.Actions.GetValueOrDefault(cols_[i].Id) ?? "", ColWidth, () => { UpdateRowState(row); Changed(); });
            box.Margin = new Thickness(0, 0, 10, 0);
            actions[cols_[i].Id] = box;
            Put(4 + i, box);
        }
        Put(4 + cols_.Count, Ui.Row(0,
            Ui.IconButton(SymbolRegular.ArrowUp24, L.T("Nach oben (höhere Priorität)", "Move up (higher priority)"), () => Move(row, -1)),
            Ui.IconButton(SymbolRegular.ArrowDown24, L.T("Nach unten", "Move down"), () => Move(row, 1)),
            Ui.IconButton(SymbolRegular.Delete24, L.T("Entfernen", "Remove"), () => Remove(row))));

        row.View = new Border { Child = grid, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
        row.View.MouseEnter += (_, _) => row.View.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        row.View.MouseLeave += (_, _) => row.View.Background = Brushes.Transparent;
        enabled.Checked += (_, _) => { UpdateRowState(row); Changed(); };
        enabled.Unchecked += (_, _) => { UpdateRowState(row); Changed(); };
        matchPath.Checked += (_, _) => Changed();
        matchPath.Unchecked += (_, _) => Changed();
        name.TextChanged += (_, _) => Changed();
        rows_.Add(row);
        ruleTable_.Children.Add(row.View);
        UpdateRowState(row);
    }

    private void UpdateRowState(RuleRow row)
    {
        var reasons = new List<string>();
        foreach (var c in cols_)
        {
            string t = Ui.Token(row.Actions[c.Id]);
            if (t.Length == 0) continue;
            if (!c.Connected) reasons.Add(L.T("{0} ist nicht verbunden", "{0} is not connected", c.Name));
            else if (DisplayAction.IsMode(t) && !c.Modes.Contains(t, StringComparer.OrdinalIgnoreCase))
                reasons.Add(L.T("Den Farbmodus „{0}“ gibt es auf {1} nicht (mehr)", "Color mode \"{0}\" does not exist on {1} (anymore)", t, c.Name));
        }
        bool enabled = row.Enabled.IsChecked == true;
        row.View.Opacity = reasons.Count > 0 ? 0.45 : enabled ? 1 : 0.6;
        row.View.ToolTip = reasons.Count > 0 ? string.Join("\n", reasons) : null;
        row.Name.FontStyle = enabled ? FontStyles.Normal : FontStyles.Italic;
    }

    private void Move(RuleRow row, int delta)
    {
        int i = rows_.IndexOf(row), j = i + delta;
        if (j < 0 || j >= rows_.Count) return;
        rows_.RemoveAt(i);
        rows_.Insert(j, row);
        ruleTable_.Children.Remove(row.View);
        ruleTable_.Children.Insert(j, row.View);
        Changed();
    }

    private void Remove(RuleRow row)
    {
        rows_.Remove(row);
        ruleTable_.Children.Remove(row.View);
        UpdateEmptyState();
        Changed();
    }

    private void UpdateEmptyState()
    {
        var hint = ruleTable_.Children.OfType<WpfUi.TextBlock>().FirstOrDefault();
        if (rows_.Count == 0 && hint == null)
        {
            var t = Ui.Body(L.T("Noch keine Programme. Füge oben ein laufendes Programm oder eine Programmdatei hinzu.",
                                "No programs yet. Add a running program or an executable above."), secondary: true);
            t.Margin = new Thickness(10, 14, 10, 14);
            ruleTable_.Children.Add(t);
        }
        else if (rows_.Count > 0 && hint != null) ruleTable_.Children.Remove(hint);
    }

    private void AddProgram(string exeName, string? path, string? description)
    {
        exeName = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe";
        var existing = rows_.FirstOrDefault(r => ProcessNames.Normalize(r.Rule.ExeName) == ProcessNames.Normalize(exeName));
        if (existing != null) { existing.Name.Focus(); return; }
        AddRow(new ProcessRule { Name = description ?? Path.GetFileNameWithoutExtension(exeName), ExeName = exeName, ExePath = path });
        UpdateEmptyState();
        Changed();
    }

    private void AddFromProcesses()
    {
        var picker = new ProcessPickerWindow { Owner = this };
        if (picker.ShowDialog() == true)
            foreach (var e in picker.Selected) AddProgram(e.ExeName, e.Path, e.Description);
    }

    private void AddFromFile()
    {
        var dlg = new OpenFileDialog { Title = L.T("Programm auswählen", "Choose program"), Filter = L.T("Programme", "Programs") + " (*.exe)|*.exe", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var path in dlg.FileNames)
            AddProgram(Path.GetFileName(path), path, Shell.Describe(path, Path.GetFileNameWithoutExtension(path)));
    }

    // ================================================================= Games

    private ToggleSwitch gamesEnabled_ = null!, gExclusive_ = null!, gBorderless_ = null!, gWinList_ = null!, gLibraries_ = null!;
    private ListBox extraFolders_ = null!;
    private System.Windows.Controls.TextBox exclusions_ = null!;

    private UIElement GamesPage()
    {
        var g = work_.Games;
        gamesEnabled_ = Ui.Toggle(g.Enabled, Changed);
        gExclusive_ = Ui.Toggle(g.DetectExclusiveFullscreen, Changed);
        gBorderless_ = Ui.Toggle(g.DetectBorderlessFullscreen, Changed);
        gWinList_ = Ui.Toggle(g.UseWindowsGameList, Changed);
        gLibraries_ = Ui.Toggle(g.UseLauncherLibraries, Changed);

        var items = new List<UIElement>
        {
            Ui.Title(L.T("Spiele", "Games")),
            Ui.Setting(SymbolRegular.Games24, L.T("Spiele automatisch erkennen", "Detect games automatically"),
                       L.T("Gilt, solange das Spiel im Vordergrund ist. Programmregeln haben Vorrang.", "Applies while the game is in the foreground. Program rules take precedence."), gamesEnabled_),
            Ui.Section(L.T("Während ein Spiel läuft", "While a game is running")),
        };
        gameBoxes_.Clear();
        foreach (var c in cols_)
        {
            var box = Ui.Combo(Options(c, special: true), g.Actions.GetValueOrDefault(c.Id) ?? "", 240, Changed);
            gameBoxes_[c.Id] = box;
            items.Add(Ui.Setting(SymbolRegular.Desktop24, c.Name, ColSubtitle(c), box));
        }
        items.Add(Ui.Caption(L.T("Tipp: Nebenbildschirme auf „Schwarz“ oder „Ausschalten“ stellen, damit sie nicht ablenken.",
                                 "Tip: set secondary displays to \"Black\" or \"Turn off\" so they don't distract.")));

        items.Add(Ui.Section(L.T("So werden Spiele erkannt", "How games are detected")));
        items.Add(Ui.Setting(SymbolRegular.FullScreenMaximize24, L.T("Exklusiver Vollbildmodus (DirectX)", "Exclusive fullscreen (DirectX)"),
            L.T("Zuverlässig bei klassischem Vollbild.", "Reliable for classic fullscreen."), gExclusive_));
        items.Add(Ui.Setting(SymbolRegular.Window24, L.T("Randloses Vollbildfenster im Vordergrund", "Borderless fullscreen window in the foreground"),
            L.T("Für moderne Spiele im „Vollbild-Fenster“. Browser und Videoplayer sind unten ausgenommen.", "For modern games in \"borderless\" mode. Browsers and video players are excluded below."), gBorderless_));
        items.Add(Ui.Setting(SymbolRegular.XboxController24, L.T("Von Windows als Spiel erkannte Programme (Game Bar)", "Programs Windows recognizes as games (Game Bar)"),
            L.T("Nutzt die Liste, die Windows für den Spielmodus führt.", "Uses the list Windows keeps for Game Mode."), gWinList_));
        items.Add(Ui.Setting(SymbolRegular.Library24, L.T("Programme aus Spielebibliotheken (Steam, Epic, GOG, Ubisoft, EA, Xbox)", "Programs from game libraries (Steam, Epic, GOG, Ubisoft, EA, Xbox)"),
            L.T("Erkennt Spiele auch im Fenstermodus.", "Detects games even in windowed mode."), gLibraries_));

        var libraries = new ListBox { ItemsSource = games_.Libraries, MaxHeight = 160, IsHitTestVisible = false };
        extraFolders_ = new ListBox { ItemsSource = new List<string>(g.ExtraFolders), MinHeight = 40, MaxHeight = 160 };
        var folders = new StackPanel();
        folders.Children.Add(Ui.Caption(L.T("Automatisch gefunden:", "Found automatically:")));
        folders.Children.Add(libraries);
        folders.Children.Add(Ui.Caption(L.T("Zusätzliche Ordner:", "Additional folders:")));
        folders.Children.Add(extraFolders_);
        var folderButtons = Ui.Row(0,
            Ui.Button(L.T("Ordner hinzufügen …", "Add folder …"), AddFolder, SymbolRegular.FolderAdd24),
            Ui.Button(L.T("Entfernen", "Remove"), () =>
            {
                if (extraFolders_.SelectedItem is string s) { extraFolders_.ItemsSource = ((List<string>)extraFolders_.ItemsSource).Where(x => x != s).ToList(); Changed(); }
            }, SymbolRegular.Delete24));
        folderButtons.Margin = new Thickness(0, 8, 0, 0);
        folders.Children.Add(folderButtons);
        items.Add(new CardExpander { Icon = new SymbolIcon { Symbol = SymbolRegular.Folder24 }, Header = L.T("Spielebibliotheken", "Game libraries"), Content = folders, Margin = new Thickness(0, 0, 0, 4) });

        exclusions_ = new System.Windows.Controls.TextBox { Text = string.Join(Environment.NewLine, g.ExcludedProcesses), AcceptsReturn = true, Height = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        exclusions_.TextChanged += (_, _) => Changed();
        var excl = new StackPanel();
        excl.Children.Add(Ui.Caption(L.T("Prozessnamen, einer pro Zeile (ohne .exe).", "Process names, one per line (without .exe).")));
        excl.Children.Add(exclusions_);
        var reset = Ui.Button(L.T("Liste zurücksetzen", "Reset list"), () => exclusions_.Text = string.Join(Environment.NewLine, GameSettings.DefaultExclusions), SymbolRegular.ArrowReset24);
        reset.Margin = new Thickness(0, 8, 0, 0);
        excl.Children.Add(reset);
        items.Add(new CardExpander { Icon = new SymbolIcon { Symbol = SymbolRegular.Prohibited24 }, Header = L.T("Nie als Spiel behandeln", "Never treat as a game"), Content = excl });

        return Ui.Page([.. items]);
    }

    private void AddFolder()
    {
        var dlg = new OpenFolderDialog { Title = L.T("Spieleordner auswählen", "Choose games folder") };
        if (dlg.ShowDialog(this) != true) return;
        var list = (List<string>)extraFolders_.ItemsSource;
        if (!list.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) extraFolders_.ItemsSource = list.Append(dlg.FolderName).ToList();
        Changed();
    }

    // ================================================================= Defaults

    private UIElement DefaultsPage()
    {
        var items = new List<UIElement>
        {
            Ui.Title(L.T("Standard", "Defaults")),
            Ui.Body(L.T("Dieser Zustand gilt, solange kein konfiguriertes Programm und kein Spiel im Vordergrund ist. Beim ersten Start übernimmt EizoSwitcher den aktuell eingestellten Modus.",
                        "This applies while no configured program and no game is in the foreground. On first start EizoSwitcher takes over the currently selected mode."), secondary: true),
            new Border { Height = 12 },
        };
        defaultBoxes_.Clear();
        foreach (var c in cols_)
        {
            if (c.IsEizo)
            {
                var box = Ui.Combo(Options(c, special: false), work_.Defaults.GetValueOrDefault(c.Id) ?? "", 240, Changed);
                defaultBoxes_[c.Id] = box;
                items.Add(Ui.Setting(SymbolRegular.Desktop24, c.Name, c.Current != null ? L.T("aktuell: {0}", "current: {0}", c.Current) : ColSubtitle(c), box));
            }
            else
            {
                items.Add(Ui.Setting(SymbolRegular.Desktop24, c.Name, ColSubtitle(c), Ui.Caption(L.T("immer normal (an, nicht abgedunkelt)", "always normal (on, not blanked)"), false)));
            }
        }
        if (cols_.Count == 0) items.Add(Ui.Body(L.T("Keine Bildschirme gefunden.", "No displays found."), secondary: true));
        return Ui.Page([.. items]);
    }

    // ================================================================= Displays

    private UIElement DisplaysPage()
    {
        profileBoxes_.Clear();
        var refresh = Ui.Button(L.T("Neu einlesen", "Rescan"), () => tray_?.RefreshDisplays(), SymbolRegular.ArrowSync24);
        refresh.Margin = new Thickness(16, 0, 0, 4);
        var head = Ui.Row(0, Ui.Title(L.T("Bildschirme", "Displays")), refresh);
        var items = new List<UIElement>
        {
            head,
            Ui.Body(L.T("EIZO-Monitore mit angeschlossenem USB-Kabel lassen sich direkt umschalten. Alle Bildschirme können abgedunkelt oder ausgeschaltet werden.",
                        "EIZO monitors with the USB cable connected can be switched directly. Every display can be blanked or turned off."), secondary: true),
            new Border { Height = 12 },
        };

        string Yes(bool b) => b ? L.T("ja", "yes") : L.T("nein", "no");
        foreach (var d in displays_.Displays)
        {
            var content = new StackPanel();
            var facts = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void Fact(string k, string v)
            {
                int r = facts.RowDefinitions.Count;
                facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var a = Ui.Caption(k, false);
                var b = Ui.Body(v, wrap: false);
                a.Margin = b.Margin = new Thickness(0, 2, 0, 2);
                Grid.SetRow(a, r); Grid.SetRow(b, r); Grid.SetColumn(b, 1);
                facts.Children.Add(a); facts.Children.Add(b);
            }
            Fact(L.T("Hersteller", "Manufacturer"), d.Manufacturer);
            Fact(L.T("Seriennummer", "Serial number"), Ui.Serial(d.Serial));
            if (d.Width > 0) Fact(L.T("Auflösung / Position", "Resolution / position"), $"{d.Width} × {d.Height}  @ {d.X}, {d.Y}{(d.Primary ? L.T("  (Hauptbildschirm)", "  (primary)") : "")}");
            Fact(L.T("EIZO-USB-Steuerung", "EIZO USB control"), Yes(d.IsEizo));
            if (d.MonitorDeviceId.Length > 0) Fact(L.T("Windows-Farbprofil", "Windows color profile"), Ui.Mask(ColorProfiles.GetDefault(d.MonitorDeviceId) ?? "—", d.Serial));
            content.Children.Add(facts);

            if (d.Eizo != null)
            {
                var box = Ui.Combo(d.Eizo.Modes.Select(m => new Opt(m, m)), d.Eizo.CurrentModeName, 220);
                content.Children.Add(Ui.Setting(SymbolRegular.Color24, L.T("Farbmodus jetzt umschalten", "Switch color mode now"), null,
                    Ui.Row(8, box, Ui.Button(L.T("Aktivieren", "Activate"), () =>
                    {
                        int i = box.SelectedIndex;
                        Task.Run(() => displays_.SetMode(d, i));
                    }, primary: true))));
            }

            string method = work_.OffMethods.GetValueOrDefault(d.Id) ?? OffMethod.Disconnect;
            var offText = Ui.Caption(d.CanTurnOff || method != OffMethod.Disconnect ? OffMethod.Text(method)
                : L.T("Hauptbildschirm – wird stattdessen schwarz", "Primary display – blanked instead"), false);
            offText.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(Ui.Setting(SymbolRegular.Power24, L.T("Ausschalten", "Turn off"),
                L.T("Der Assistent testet, welche Methode bei diesem Bildschirm wirkt.", "The wizard tests which method works for this display."),
                Ui.Row(12, offText, Ui.Button(L.T("Einrichten …", "Set up …"), () => RunOffWizard(d), SymbolRegular.Wand24))));
            if (tray_ != null && d.Width > 0)
            {
                content.Children.Add(Ui.Setting(SymbolRegular.WeatherMoon24, L.T("Schwarz testen", "Test black"), L.T("Deckt den Bildschirm 3 Sekunden schwarz ab.", "Covers the display in black for 3 seconds."),
                    Ui.Button(L.T("3 s schwarz", "Black for 3 s"), () => { tray_.Show(d); Task.Delay(3000).ContinueWith(_ => tray_.Hide(d.Id)); })));
            }

            if (d.Eizo != null && d.MonitorDeviceId.Length > 0)
            {
                var profiles = new StackPanel();
                profiles.Children.Add(Ui.Caption(L.T("Farbprofil je Modus – wird beim Umschalten als Windows-Standardprofil dieses Bildschirms gesetzt:",
                                                     "Color profile per mode – set as this display's Windows default profile when switching:")));
                foreach (var (mode, box) in ProfileLines(d))
                {
                    var line = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var label = Ui.Body(mode, wrap: false);
                    label.VerticalAlignment = VerticalAlignment.Center;
                    line.Children.Add(label);
                    Grid.SetColumn(box, 1);
                    line.Children.Add(box);
                    profiles.Children.Add(line);
                }
                content.Children.Add(new CardExpander { Icon = new SymbolIcon { Symbol = SymbolRegular.DocumentBulletList24 }, Header = L.T("Farbprofile", "Color profiles"), Content = profiles, Margin = new Thickness(0, 4, 0, 0) });
            }

            var sub = d.Eizo != null ? $"{L.T("EIZO · USB", "EIZO · USB")} · {d.Eizo.CurrentModeName}" : d.Manufacturer;
            var header = new StackPanel();
            header.Children.Add(new WpfUi.TextBlock { Text = $"{d.Name}   ·   {d.DeviceName.TrimStart('\\', '.')}", FontTypography = FontTypography.BodyStrong });
            header.Children.Add(Ui.Caption(sub, false));
            items.Add(new CardExpander { Icon = new SymbolIcon { Symbol = SymbolRegular.Desktop24 }, Header = header, Content = content, IsExpanded = true, Margin = new Thickness(0, 0, 0, 6) });
        }

        foreach (var k in work_.Displays.Where(k => displays_.Displays.All(d => d.Id != k.Id)))
        {
            items.Add(Ui.Setting(SymbolRegular.DesktopOff24, k.Name, L.T("Nicht verbunden · zuletzt gesehen {0:g}", "Not connected · last seen {0:g}", k.LastSeen),
                Ui.Button(L.T("Vergessen", "Forget"), () => { Collect(); work_.Displays.RemoveAll(x => x.Id == k.Id); Changed(); Rebuild(); }, SymbolRegular.Delete24)));
        }
        return Ui.Page([.. items]);
    }

    private List<(string Mode, ComboBox Box)> ProfileLines(DisplayInfo d)
    {
        var installed = ColorProfiles.Installed();
        var map = work_.ColorProfiles.GetValueOrDefault(d.Id);
        var lines = new List<(string, ComboBox)>();
        for (int i = 0; i < d.Eizo!.Modes.Count; i++)
        {
            string mode = d.Eizo.Modes[i];
            string prefix = $"{d.Eizo.Model}({d.Eizo.Serial}){i + 1:00}";
            string? cn = installed.FirstOrDefault(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            var opts = new List<Opt>
            {
                new("", cn != null ? L.T("Automatisch: {0}", "Automatic: {0}", Ui.Mask(cn, d.Serial))
                                   : L.T("Automatisch (kein ColorNavigator-Profil gefunden)", "Automatic (no ColorNavigator profile found)")),
                new(ColorProfiles.Keep, L.T("Nicht ändern", "Leave unchanged")),
            };
            opts.AddRange(installed.Select(f => new Opt(f, Ui.Mask(f, d.Serial))));
            var box = Ui.Combo(opts, map?.GetValueOrDefault(mode) ?? "", 440, Changed);
            profileBoxes_[(d.Id, mode)] = box;
            lines.Add((mode, box));
        }
        return lines;
    }

    private void RunOffWizard(DisplayInfo d)
    {
        var wizard = new OffWizardWindow(d) { Owner = this };
        if (wizard.ShowDialog() == true && wizard.Method != null)
        {
            Collect();
            work_.OffMethods[d.Id] = wizard.Method;
            Saved?.Invoke(work_.Clone());
            int page = SelectedPage;
            Rebuild();
            SelectedPage = page;
        }
    }

    // ================================================================= General

    private ComboBox language_ = null!, theme_ = null!;
    private ToggleSwitch autostart_ = null!, notify_ = null!, profiles_ = null!, autoCheck_ = null!, autoInstall_ = null!;
    private NumberBox poll_ = null!;

    private UIElement GeneralPage()
    {
        var languages = new List<Opt> { new("", L.T("Systemsprache", "System language")) };
        languages.AddRange(L.Languages.Select(l => new Opt(l.Code, l.Name)));
        language_ = Ui.Combo(languages, work_.Language, 220, Changed);
        theme_ = Ui.Combo([new("", L.T("Wie Windows", "Same as Windows")), new("light", L.T("Hell", "Light")), new("dark", L.T("Dunkel", "Dark"))], work_.Theme, 220, Changed);
        autostart_ = Ui.Toggle(work_.StartWithWindows, Changed);
        notify_ = Ui.Toggle(work_.ShowNotifications, Changed);
        profiles_ = Ui.Toggle(work_.SwitchColorProfiles, Changed);
        poll_ = new NumberBox { Value = work_.PollIntervalMs, Minimum = 250, Maximum = 10000, SmallChange = 250, LargeChange = 1000, Width = 160, MaxDecimalPlaces = 0 };
        poll_.ValueChanged += (_, _) => Changed();
        autoCheck_ = Ui.Toggle(work_.AutoCheckUpdates, Changed);
        autoInstall_ = Ui.Toggle(work_.AutoInstallUpdates, Changed);

        var coffee = Ui.Button(L.T("Spendier mir einen Kaffee", "Buy me a coffee"), () => Shell.Open(Links.Donate), SymbolRegular.DrinkCoffee24, primary: true);

        return Ui.Page(
            Ui.Title(L.T("Allgemein", "General")),
            Ui.Section(L.T("Darstellung", "Appearance")),
            Ui.Setting(SymbolRegular.LocalLanguage24, L.T("Sprache", "Language"), null, language_),
            Ui.Setting(SymbolRegular.DarkTheme24, L.T("Farbschema", "Theme"), null, theme_),
            Ui.Section(L.T("Start und Verhalten", "Startup and behavior")),
            Ui.Setting(SymbolRegular.Power24, L.T("Mit Windows starten", "Start with Windows"), null, autostart_),
            Ui.Setting(SymbolRegular.Alert24, L.T("Beim Umschalten kurz benachrichtigen", "Show a short notification when switching"), null, notify_),
            Ui.Setting(SymbolRegular.Color24, L.T("Windows-Farbprofil mitschalten", "Also switch the Windows color profile"),
                L.T("Setzt beim Umschalten das passende ICC-Profil als Windows-Standard des Bildschirms, so wie ColorNavigator.",
                    "Sets the matching ICC profile as the display's Windows default when switching, like ColorNavigator does."), profiles_),
            Ui.Setting(SymbolRegular.Timer24, L.T("Prüfintervall (ms)", "Check interval (ms)"),
                L.T("Programmwechsel werden sofort erkannt; das Intervall betrifft nur Hintergrundprüfungen.",
                    "Program switches are detected immediately; the interval only affects background checks."), poll_),
            Ui.Section(L.T("Updates", "Updates")),
            Ui.Setting(SymbolRegular.ArrowDownload24, L.T("Automatisch nach Updates suchen", "Check for updates automatically"),
                L.T("Installierte Version: {0}", "Installed version: {0}", Updater.Current), autoCheck_),
            Ui.Setting(SymbolRegular.ArrowSync24, L.T("Updates ohne Nachfrage installieren", "Install updates without asking"),
                L.T("Updates kommen aus den GitHub-Releases; die Prüfsumme wird kontrolliert.", "Updates come from GitHub releases; the checksum is verified."), autoInstall_),
            Ui.Setting(SymbolRegular.CloudArrowDown24, L.T("Jetzt nach Updates suchen", "Check now"), null,
                Ui.Row(0,
                    Ui.Button(L.T("Prüfen", "Check"), () => { if (tray_ != null) _ = tray_.CheckForUpdatesAsync(true); }),
                    Ui.Button(L.T("Releases auf GitHub", "Releases on GitHub"), () => Shell.Open(Updater.ReleasesUrl)))),
            Ui.Section(L.T("Gefällt dir EizoSwitcher?", "Do you like EizoSwitcher?")),
            Ui.Setting(SymbolRegular.Heart24, L.T("Gefällt dir EizoSwitcher?", "Do you like EizoSwitcher?"),
                L.T("EizoSwitcher ist kostenlos. Wenn es dir den Alltag erleichtert, freue ich mich über einen Kaffee.",
                    "EizoSwitcher is free. If it makes your day a little easier, I'd be happy about a coffee."), coffee),
            Ui.Section(L.T("Info", "About")),
            Ui.Setting(SymbolRegular.Info24, "EizoSwitcher " + Updater.Current,
                L.T("EIZO, ColorEdge und ColorNavigator sind Marken der EIZO Corporation. EizoSwitcher ist ein unabhängiges Projekt und nicht mit EIZO verbunden.",
                    "EIZO, ColorEdge and ColorNavigator are trademarks of EIZO Corporation. EizoSwitcher is an independent project, not affiliated with EIZO."),
                Ui.Row(0,
                    Ui.Button(L.T("Projektseite", "Project page"), () => Shell.Open(Links.Project)),
                    Ui.Button(L.T("Protokoll anzeigen", "Show log"), () => Shell.Open(Log.FilePath)))),
            Ui.Setting(SymbolRegular.FolderOpen24, L.T("Einstellungsordner öffnen", "Open settings folder"), Log.Folder,
                Ui.Button(L.T("Öffnen", "Open"), () => { Directory.CreateDirectory(Log.Folder); Shell.Open(Log.Folder); })));
    }

    // ================================================================= collect

    private void Collect()
    {
        if (pageCache_.ContainsKey(0))
        {
            work_.Rules = [.. rows_.Select(row =>
            {
                var r = row.Rule;
                r.Enabled = row.Enabled.IsChecked == true;
                r.Name = row.Name.Text.Trim();
                r.MatchPath = row.MatchPath.IsChecked == true;
                foreach (var (id, box) in row.Actions)
                {
                    string t = Ui.Token(box);
                    if (t.Length == 0) r.Actions.Remove(id); else r.Actions[id] = t;
                }
                return r;
            })];
        }

        if (pageCache_.ContainsKey(1))
        {
            var g = work_.Games;
            g.Enabled = gamesEnabled_.IsChecked == true;
            g.DetectExclusiveFullscreen = gExclusive_.IsChecked == true;
            g.DetectBorderlessFullscreen = gBorderless_.IsChecked == true;
            g.UseWindowsGameList = gWinList_.IsChecked == true;
            g.UseLauncherLibraries = gLibraries_.IsChecked == true;
            foreach (var (id, box) in gameBoxes_)
            {
                string t = Ui.Token(box);
                if (t.Length == 0) g.Actions.Remove(id); else g.Actions[id] = t;
            }
            g.ExtraFolders = [.. (List<string>)extraFolders_.ItemsSource];
            g.ExcludedProcesses = [.. exclusions_.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        if (pageCache_.ContainsKey(2))
            foreach (var (id, box) in defaultBoxes_)
            {
                string t = Ui.Token(box);
                if (t.Length == 0) work_.Defaults.Remove(id); else work_.Defaults[id] = t;
            }

        if (pageCache_.ContainsKey(3))
            foreach (var ((id, mode), box) in profileBoxes_)
            {
                string t = Ui.Token(box);
                if (!work_.ColorProfiles.TryGetValue(id, out var map)) work_.ColorProfiles[id] = map = [];
                if (t.Length == 0) map.Remove(mode); else map[mode] = t;
                if (map.Count == 0) work_.ColorProfiles.Remove(id);
            }

        if (pageCache_.ContainsKey(4))
        {
            work_.Language = Ui.Token(language_);
            work_.Theme = Ui.Token(theme_);
            work_.StartWithWindows = autostart_.IsChecked == true;
            work_.ShowNotifications = notify_.IsChecked == true;
            work_.SwitchColorProfiles = profiles_.IsChecked == true;
            work_.PollIntervalMs = (int)(poll_.Value ?? 1500);
            work_.AutoCheckUpdates = autoCheck_.IsChecked == true;
            work_.AutoInstallUpdates = autoInstall_.IsChecked == true;
        }
    }
}
