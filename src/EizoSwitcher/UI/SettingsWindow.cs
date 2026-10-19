using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EizoSwitcher.Services;
using Microsoft.Win32;

namespace EizoSwitcher.UI;

/// <summary>Settings in the Windows 11 style. Every change is applied right away (no save button).</summary>
internal sealed class SettingsWindow : Window
{
    /// <summary>One display column: live data if connected, otherwise what we remembered.</summary>
    private sealed record Col(string Id, string Name, bool Connected, bool IsEizo, bool CanTurnOff, IReadOnlyList<string> Modes, IReadOnlyList<bool> Enabled, string? Current);

    private sealed class RuleRow
    {
        public required ProcessRule Rule;
        public required Border View;
        public required CheckBox Enabled;
        public required System.Windows.Controls.TextBox Name;
        public required Dictionary<string, ComboBox> Actions;
    }

    // name and display columns shrink down to their minimum before the table scrolls sideways
    private const double NameWidth = 230, NameMin = 140, ColWidth = 158, ColMin = 112, ColGap = 10;

    private readonly DisplayService displays_;
    private readonly GameDetector games_;
    private readonly TrayApp? tray_;
    private readonly AppSettings work_;
    private readonly DispatcherTimer saveTimer_ = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private List<Col> cols_ = [];
    private string colsSignature_ = "";
    private bool building_;

    private readonly ListBox nav_ = new() { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly ContentControl page_ = new();
    private readonly List<(string Title, string Icon, Func<UIElement> Build)> pages_ = [];

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
        Icon = Ui.AppIcon;

        saveTimer_.Tick += (_, _) => { saveTimer_.Stop(); Collect(); Saved?.Invoke(work_.Clone()); };
        Closing += (_, _) => { if (saveTimer_.IsEnabled) { saveTimer_.Stop(); Collect(); Saved?.Invoke(work_.Clone()); } };
        nav_.SelectionChanged += (_, _) => ShowPage();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        Rebuild();
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
        pages_.Add((L.T("Programme", "Programs"), Icons.Apps, ProgramsPage));
        pages_.Add((L.T("Spiele", "Games"), Icons.Game, GamesPage));
        pages_.Add((L.T("Standard", "Defaults"), Icons.Home, DefaultsPage));
        pages_.Add((L.T("Bildschirme", "Displays"), Icons.Display, DisplaysPage));
        pages_.Add((L.T("Allgemein", "General"), Icons.Settings, GeneralPage));
        pageCache_.Clear();

        nav_.Items.Clear();
        foreach (var (title, icon, _) in pages_)
        {
            var item = Ui.Row(12, Ui.Icon(icon, 18), Ui.Body(title, wrap: false));
            item.Margin = new Thickness(4, 6, 4, 6);
            nav_.Items.Add(new ListBoxItem { Content = item });
        }

        var navPanel = new DockPanel { Width = 240, Margin = new Thickness(8, 8, 0, 8) };
        var version = Ui.Caption($"EizoSwitcher {Updater.Current}");
        version.Margin = new Thickness(14, 0, 0, 6);
        DockPanel.SetDock(version, Dock.Bottom);
        navPanel.Children.Add(version);
        if (nav_.Parent is Panel oldNav) oldNav.Children.Remove(nav_);
        navPanel.Children.Add(nav_);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (navPanel.Parent is Panel old1) old1.Children.Remove(navPanel);
        if (page_.Parent is Panel old2) old2.Children.Remove(page_);
        body.Children.Add(navPanel);
        Grid.SetColumn(page_, 1);
        body.Children.Add(page_);

        var root = body;
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
        string.Join("|", cols.Select(c => $"{c.Id}:{c.Name}:{c.Connected}:{c.CanTurnOff}:{string.Join(",", c.Modes)}:{string.Join("", c.Enabled.Select(e => e ? 1 : 0))}"));

    private List<Col> BuildColumns()
    {
        var cols = displays_.Displays.Select(d => new Col(d.Id, d.Name, true, d.IsEizo, d.CanTurnOff, d.Eizo?.Modes ?? [], d.Eizo?.Enabled ?? [], d.Eizo?.CurrentModeName)).ToList();
        foreach (var k in work_.Displays.Where(k => cols.All(c => c.Id != k.Id)))
            cols.Add(new Col(k.Id, k.Name, false, k.IsEizo, k.CanTurnOff, k.Modes, k.ModesEnabled, null));
        return [.. cols.Select(c => cols.Count(x => x.Name == c.Name) > 1 ? c with { Name = $"{c.Name} ({Ui.Serial(c.Id)[^4..]})" } : c)];
    }

    private static List<Opt> Options(Col c, bool special)
    {
        var list = new List<Opt> { new("", "—") };
        list.AddRange(Ui.ModeOptions(c.Modes, c.Enabled));
        if (special)
        {
            if (c.Modes.Count > 0) list.Add(Opt.Separator);
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
        for (int i = 0; i < cols_.Count; i++)
        {
            var head = new StackPanel { Opacity = cols_[i].Connected ? 1 : 0.5 };
            head.Children.Add(Ui.Strong(cols_[i].Name));
            head.Children.Add(Ui.Caption(ColSubtitle(cols_[i]), false));
            H(3 + i, head);
        }

        rows_.Clear();
        ruleTable_.Children.Clear();
        foreach (var r in work_.Rules) AddRow(r);
        UpdateEmptyState();

        var table = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        table.Children.Add(header);
        if (ruleTable_.Parent is Panel previous) previous.Children.Remove(ruleTable_); // page is rebuilt when displays change
        table.Children.Add(ruleTable_);
        var scroll = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        table.HorizontalAlignment = HorizontalAlignment.Left;
        double fixedWidth = 16 + 40 + 36 + 110;
        scroll.SizeChanged += (_, e) => table.Width = Math.Clamp(e.NewSize.Width,
            fixedWidth + NameMin + cols_.Count * (ColMin + ColGap), fixedWidth + NameWidth + cols_.Count * (ColWidth + ColGap));
        var card = Ui.Card(scroll, new Thickness(8, 12, 8, 8));

        var intro = Ui.Body(L.T("Sobald eines dieser Programme im Vordergrund ist, werden die Bildschirme wie angegeben umgeschaltet. Der Farbmodus bleibt, bis das Programm beendet wird oder ein anderes eingetragenes Programm oder Spiel nach vorne kommt (einstellbar unter Allgemein); Schwarz und Aus gelten nur, solange es im Vordergrund ist. " +
                                "„—“ lässt einen Bildschirm unverändert. Graue Zeilen verweisen auf einen Farbmodus oder Bildschirm, den es nicht (mehr) gibt.",
                                "As soon as one of these programs is in the foreground, the displays switch as configured. The color mode stays until the program exits or another listed program or game comes to the front (configurable under General); black and off only apply while it is in the foreground. " +
                                "\"—\" leaves a display unchanged. Grey rows refer to a color mode or display that does not exist (anymore)."), secondary: true);
        intro.Margin = new Thickness(0, 0, 0, 14);

        var buttons = Ui.Row(0,
            Ui.Button(L.T("Laufendes Programm …", "Running program …"), AddFromProcesses, Icons.Add, primary: true),
            Ui.Button(L.T("Programmdatei auswählen …", "Choose executable …"), AddFromFile, Icons.Folder));
        buttons.Margin = new Thickness(0, 0, 0, 14);

        var page = Ui.Page(Ui.Title(L.T("Programme", "Programs")), intro, buttons, card);
        ((StackPanel)page.Content).MaxWidth = double.PositiveInfinity;
        return page;
    }

    private static UIElement Dim(FrameworkElement e) { e.Opacity = 0.5; return e; }

    private void DefineColumns(Grid g)
    {
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameWidth, GridUnitType.Star), MinWidth = NameMin, MaxWidth = NameWidth });
        foreach (var _ in cols_)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColWidth + ColGap, GridUnitType.Star), MinWidth = ColMin + ColGap, MaxWidth = ColWidth + ColGap });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
    }

    private void AddRow(ProcessRule rule)
    {
        var grid = new Grid();
        DefineColumns(grid);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // exe and folder under the name box
        void Put(int col, UIElement e) { Grid.SetColumn(e, col); grid.Children.Add(e); }

        var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = L.T("Regel aktiv", "Rule enabled"), MinWidth = 0, Padding = new Thickness(0) };
        Put(0, enabled);
        var img = ExeIcons.For(rule.ExePath);
        Put(1, img != null
            ? new System.Windows.Controls.Image { Source = img, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center }
            : Dim(Ui.Icon(Icons.Apps, 20)));

        var name = new System.Windows.Controls.TextBox { Text = rule.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), ToolTip = rule.ExePath ?? rule.ExeName };
        // the folder tells two versions of a program apart (each has its own rule)
        string? folder = Path.GetDirectoryName(rule.ExePath);
        // (the last folder is what differs, e.g. "Adobe Photoshop 2025"; full path in the tooltip)
        var exe = Ui.Caption(string.IsNullOrEmpty(folder) ? rule.ExeName : $"…\\{Path.GetFileName(folder)}\\{rule.ExeName}", false);
        exe.Margin = new Thickness(2, 2, 12, 0);
        exe.TextTrimming = TextTrimming.CharacterEllipsis;
        exe.ToolTip = rule.ExePath ?? rule.ExeName;
        Put(2, name);
        Grid.SetRow(exe, 1);
        Put(2, exe);

        var actions = new Dictionary<string, ComboBox>();
        var row = new RuleRow { Rule = rule, View = null!, Enabled = enabled, Name = name, Actions = actions };
        for (int i = 0; i < cols_.Count; i++)
        {
            var box = Ui.Combo(Options(cols_[i], special: true), rule.Actions.GetValueOrDefault(cols_[i].Id) ?? "", ColWidth, () => { UpdateRowState(row); Changed(); });
            box.Width = double.NaN; // follows the column width
            box.Margin = new Thickness(0, 0, ColGap, 0);
            actions[cols_[i].Id] = box;
            Put(3 + i, box);
        }
        Put(3 + cols_.Count, Ui.Row(0,
            Ui.IconButton(Icons.Up, L.T("Nach oben (höhere Priorität)", "Move up (higher priority)"), () => Move(row, -1)),
            Ui.IconButton(Icons.Down, L.T("Nach unten", "Move down"), () => Move(row, 1)),
            Ui.IconButton(Icons.Delete, L.T("Entfernen", "Remove"), () => Remove(row))));

        row.View = new Border { Child = grid, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
        row.View.MouseEnter += (_, _) => row.View.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        row.View.MouseLeave += (_, _) => row.View.Background = Brushes.Transparent;
        enabled.Checked += (_, _) => { UpdateRowState(row); Changed(); };
        enabled.Unchecked += (_, _) => { UpdateRowState(row); Changed(); };
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
        var hint = ruleTable_.Children.OfType<TextBlock>().FirstOrDefault();
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
        // same executable = same rule; another version of the program (other folder) gets its own
        var existing = rows_.FirstOrDefault(r => string.IsNullOrEmpty(r.Rule.ExePath) || string.IsNullOrEmpty(path)
            ? ProcessNames.Normalize(r.Rule.ExeName) == ProcessNames.Normalize(exeName)
            : string.Equals(r.Rule.ExePath, path, StringComparison.OrdinalIgnoreCase));
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

    private CheckBox gamesEnabled_ = null!, gExclusive_ = null!, gBorderless_ = null!, gWinList_ = null!, gLibraries_ = null!;
    private ListBox extraFolders_ = null!;
    private ListBox exclusions_ = null!;

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
            Ui.Setting(Icons.Game, L.T("Spiele automatisch erkennen", "Detect games automatically"),
                       L.T("Gilt, solange das Spiel im Vordergrund ist. Programmregeln haben Vorrang.", "Applies while the game is in the foreground. Program rules take precedence."), gamesEnabled_),
            Ui.Section(L.T("Während ein Spiel läuft", "While a game is running")),
        };
        gameBoxes_.Clear();
        foreach (var c in cols_)
        {
            var box = Ui.Combo(Options(c, special: true), g.Actions.GetValueOrDefault(c.Id) ?? "", 240, Changed);
            gameBoxes_[c.Id] = box;
            items.Add(Ui.Setting(Icons.Display, c.Name, ColSubtitle(c), box));
        }
        items.Add(Ui.Caption(L.T("Tipp: Nebenbildschirme auf „Schwarz“ oder „Ausschalten“ stellen, damit sie nicht ablenken.",
                                 "Tip: set secondary displays to \"Black\" or \"Turn off\" so they don't distract.")));

        items.Add(Ui.Section(L.T("So werden Spiele erkannt", "How games are detected")));
        items.Add(Ui.Setting(Icons.FullScreen, L.T("Exklusiver Vollbildmodus (DirectX)", "Exclusive fullscreen (DirectX)"),
            L.T("Zuverlässig bei klassischem Vollbild.", "Reliable for classic fullscreen."), gExclusive_));
        items.Add(Ui.Setting(Icons.Window, L.T("Randloses Vollbildfenster im Vordergrund", "Borderless fullscreen window in the foreground"),
            L.T("Für moderne Spiele im „Vollbild-Fenster“. Browser und Videoplayer sind unten ausgenommen.", "For modern games in \"borderless\" mode. Browsers and video players are excluded below."), gBorderless_));
        items.Add(Ui.Setting(Icons.Game, L.T("Von Windows als Spiel erkannte Programme (Game Bar)", "Programs Windows recognizes as games (Game Bar)"),
            L.T("Nutzt die Liste, die Windows für den Spielmodus führt.", "Uses the list Windows keeps for Game Mode."), gWinList_));
        items.Add(Ui.Setting(Icons.Library, L.T("Programme aus Spielebibliotheken (Steam, Epic, GOG, Ubisoft, EA, Xbox)", "Programs from game libraries (Steam, Epic, GOG, Ubisoft, EA, Xbox)"),
            L.T("Erkennt Spiele auch im Fenstermodus.", "Detects games even in windowed mode."), gLibraries_));

        var libraries = new ListBox { ItemsSource = games_.Libraries, MaxHeight = 160, IsHitTestVisible = false };
        extraFolders_ = new ListBox { ItemsSource = new List<string>(g.ExtraFolders), MinHeight = 40, MaxHeight = 160 };
        var folders = new StackPanel();
        folders.Children.Add(Ui.Caption(L.T("Automatisch gefunden:", "Found automatically:")));
        folders.Children.Add(libraries);
        folders.Children.Add(Ui.Caption(L.T("Zusätzliche Ordner:", "Additional folders:")));
        folders.Children.Add(extraFolders_);
        var folderButtons = Ui.Row(0,
            Ui.Button(L.T("Ordner hinzufügen …", "Add folder …"), AddFolder, Icons.Folder),
            Ui.Button(L.T("Entfernen", "Remove"), () =>
            {
                if (extraFolders_.SelectedItem is string s) { extraFolders_.ItemsSource = ((List<string>)extraFolders_.ItemsSource).Where(x => x != s).ToList(); Changed(); }
            }, Icons.Delete));
        folderButtons.Margin = new Thickness(0, 8, 0, 0);
        folders.Children.Add(folderButtons);
        items.Add(Ui.Expander(Icons.Folder, L.T("Spielebibliotheken", "Game libraries"), folders));

        exclusions_ = new ListBox { SelectionMode = SelectionMode.Extended, MinHeight = 40, MaxHeight = 240 };
        // compact rows (the Fluent default is made for touch)
        var compact = new Style(typeof(ListBoxItem), TryFindResource(typeof(ListBoxItem)) as Style);
        compact.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 3, 12, 3)));
        compact.Setters.Add(new Setter(MinHeightProperty, 26.0));
        exclusions_.ItemContainerStyle = compact;
        SetExclusions(g.ExcludedProcesses);
        exclusions_.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Delete) RemoveExclusions(); };
        var excl = new StackPanel();
        excl.Children.Add(Ui.Caption(L.T("Diese Programme lösen nie die Spiel-Einstellungen aus, auch im Vollbild nicht. Ein Fehlalarm lässt sich auch direkt im Tray-Menü ausnehmen.",
                                         "These programs never trigger the game settings, not even in fullscreen. A false alarm can also be excluded right from the tray menu.")));
        excl.Children.Add(exclusions_);
        var exclButtons = Ui.Row(8,
            Ui.Button(L.T("Laufendes Programm …", "Running program …"), AddExclusionFromProcesses, Icons.Add),
            Ui.Button(L.T("Programmdatei auswählen …", "Choose executable …"), AddExclusionFromFile, Icons.Folder),
            Ui.Button(L.T("Entfernen", "Remove"), RemoveExclusions, Icons.Delete),
            Ui.Button(L.T("Liste zurücksetzen", "Reset list"), () => { SetExclusions(GameSettings.DefaultExclusions); Changed(); }, Icons.Reset));
        exclButtons.Margin = new Thickness(0, 8, 0, 0);
        excl.Children.Add(exclButtons);
        items.Add(Ui.Expander(Icons.Blocked, L.T("Nie als Spiel behandeln", "Never treat as a game"), excl));

        return Ui.Page([.. items]);
    }

    private List<string> Exclusions => (List<string>)exclusions_.ItemsSource;

    private void SetExclusions(IEnumerable<string> names) =>
        exclusions_.ItemsSource = names.Select(ProcessNames.Base).Where(n => n.Length > 0)
            .DistinctBy(ProcessNames.Normalize).Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Adds a program to "never treat as a game" (also used by the tray menu while this window is open).</summary>
    public void ExcludeGame(string process)
    {
        if (exclusions_ != null && pageCache_.ContainsKey(1))
        {
            SetExclusions(Exclusions.Append(process));
            exclusions_.SelectedItem = Exclusions.FirstOrDefault(x => ProcessNames.Normalize(x) == ProcessNames.Normalize(process));
            exclusions_.ScrollIntoView(exclusions_.SelectedItem);
        }
        else
            work_.Games.ExcludedProcesses = [.. work_.Games.ExcludedProcesses.Append(ProcessNames.Base(process)).DistinctBy(ProcessNames.Normalize)];
        Changed();
    }

    private void AddExclusionFromProcesses()
    {
        var picker = new ProcessPickerWindow { Owner = this };
        if (picker.ShowDialog() == true)
            foreach (var e in picker.Selected) ExcludeGame(e.ExeName);
    }

    private void AddExclusionFromFile()
    {
        var dlg = new OpenFileDialog { Title = L.T("Programm auswählen", "Choose program"), Filter = L.T("Programme", "Programs") + " (*.exe)|*.exe", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var path in dlg.FileNames) ExcludeGame(path);
    }

    private void RemoveExclusions()
    {
        var selected = exclusions_.SelectedItems.Cast<string>().ToHashSet();
        if (selected.Count == 0) return;
        SetExclusions(Exclusions.Where(x => !selected.Contains(x)));
        Changed();
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
                items.Add(Ui.Setting(Icons.Display, c.Name, c.Current != null ? L.T("aktuell: {0}", "current: {0}", c.Current) : ColSubtitle(c), box));
            }
            else
            {
                items.Add(Ui.Setting(Icons.Display, c.Name, ColSubtitle(c), Ui.Caption(L.T("immer normal (an, nicht abgedunkelt)", "always normal (on, not blanked)"), false)));
            }
        }
        if (cols_.Count == 0) items.Add(Ui.Body(L.T("Keine Bildschirme gefunden.", "No displays found."), secondary: true));
        return Ui.Page([.. items]);
    }

    // ================================================================= Displays

    private UIElement DisplaysPage()
    {
        profileBoxes_.Clear();
        var refresh = Ui.Button(L.T("Neu einlesen", "Rescan"), () => tray_?.RefreshDisplays(), Icons.Refresh);
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
                var box = Ui.Combo(Ui.ModeOptions(d.Eizo.Modes, d.Eizo.Enabled), d.Eizo.CurrentModeName, 220);
                content.Children.Add(Ui.Setting(Icons.Color, L.T("Farbmodus jetzt umschalten", "Switch color mode now"), null,
                    Ui.Row(8, box, Ui.Button(L.T("Aktivieren", "Activate"), () =>
                    {
                        int i = d.Eizo.IndexOf(Ui.Token(box));
                        if (i >= 0) Task.Run(() => displays_.SetMode(d, i));
                    }, primary: true))));
            }

            string method = work_.OffMethods.GetValueOrDefault(d.Id) ?? OffMethod.Disconnect;
            var offText = Ui.Caption(d.CanTurnOff || method != OffMethod.Disconnect ? OffMethod.Text(method)
                : L.T("Hauptbildschirm – wird stattdessen schwarz", "Primary display – blanked instead"), false);
            offText.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(Ui.Setting(Icons.Power, L.T("Ausschalten", "Turn off"),
                L.T("Der Assistent testet, welche Methode bei diesem Bildschirm wirkt.", "The wizard tests which method works for this display."),
                Ui.Row(12, offText, Ui.Button(L.T("Einrichten …", "Set up …"), () => RunOffWizard(d), Icons.Wand))));
            if (tray_ != null && d.Width > 0)
            {
                content.Children.Add(Ui.Setting(Icons.Moon, L.T("Schwarz testen", "Test black"), L.T("Deckt den Bildschirm 3 Sekunden schwarz ab.", "Covers the display in black for 3 seconds."),
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
                content.Children.Add(Ui.Expander(Icons.Document, L.T("Farbprofile", "Color profiles"), profiles));
            }

            var sub = d.Eizo != null ? $"{L.T("EIZO · USB", "EIZO · USB")} · {d.Eizo.CurrentModeName}" : d.Manufacturer;
            var header = new StackPanel();
            header.Children.Add(Ui.Strong($"{d.Name}   ·   {d.DeviceName.TrimStart('\\', '.')}"));
            header.Children.Add(Ui.Caption(sub, false));
            items.Add(Ui.Expander(Icons.Display, header, content, expanded: true));
        }

        foreach (var k in work_.Displays.Where(k => displays_.Displays.All(d => d.Id != k.Id)))
        {
            items.Add(Ui.Setting(Icons.Display, k.Name, L.T("Nicht verbunden · zuletzt gesehen {0:g}", "Not connected · last seen {0:g}", k.LastSeen),
                Ui.Button(L.T("Vergessen", "Forget"), () => { Collect(); work_.Displays.RemoveAll(x => x.Id == k.Id); Changed(); Rebuild(); }, Icons.Delete)));
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
    private CheckBox autostart_ = null!, notify_ = null!, keep_ = null!, profiles_ = null!, autoCheck_ = null!, autoInstall_ = null!;
    private ComboBox poll_ = null!;

    private UIElement GeneralPage()
    {
        var languages = new List<Opt> { new("", L.T("Systemsprache", "System language")) };
        languages.AddRange(L.Languages.Select(l => new Opt(l.Code, l.Name)));
        language_ = Ui.Combo(languages, work_.Language, 220, Changed);
        theme_ = Ui.Combo([new("", L.T("Wie Windows", "Same as Windows")), new("light", L.T("Hell", "Light")), new("dark", L.T("Dunkel", "Dark"))], work_.Theme, 220, Changed);
        autostart_ = Ui.Toggle(work_.StartWithWindows, Changed);
        notify_ = Ui.Toggle(work_.ShowNotifications, Changed);
        keep_ = Ui.Toggle(work_.KeepModesUntilExit, Changed);
        profiles_ = Ui.Toggle(work_.SwitchColorProfiles, Changed);
        poll_ = Ui.Combo(new[] { 500, 1000, 1500, 2000, 3000, 5000 }.Select(v => new Opt(v.ToString(), $"{v} ms")), work_.PollIntervalMs.ToString(), 160, Changed);
        autoCheck_ = Ui.Toggle(work_.AutoCheckUpdates, Changed);
        autoInstall_ = Ui.Toggle(work_.AutoInstallUpdates, Changed);

        var coffee = Ui.Button(L.T("Spendier mir einen Kaffee", "Buy me a coffee"), () => Shell.Open(Links.Donate), Icons.Heart, primary: true);

        return Ui.Page(
            Ui.Title(L.T("Allgemein", "General")),
            Ui.Section(L.T("Darstellung", "Appearance")),
            Ui.Setting(Icons.Globe, L.T("Sprache", "Language"), null, language_),
            Ui.Setting(Icons.Theme, L.T("Farbschema", "Theme"), null, theme_),
            Ui.Section(L.T("Start und Verhalten", "Startup and behavior")),
            Ui.Setting(Icons.Power, L.T("Mit Windows starten", "Start with Windows"), null, autostart_),
            Ui.Setting(Icons.Bell, L.T("Beim Umschalten kurz benachrichtigen", "Show a short notification when switching"), null, notify_),
            Ui.Setting(Icons.Apps, L.T("Farbmodus behalten, bis das Programm beendet wird", "Keep the color mode until the program exits"),
                L.T("Ausgeschaltet gilt sofort wieder der Standard, sobald ein anderes Fenster aktiv wird. Schwarz und Aus gelten immer nur im Vordergrund.",
                    "When off, the defaults apply as soon as another window becomes active. Black and off always apply only in the foreground."), keep_),
            Ui.Setting(Icons.Color, L.T("Windows-Farbprofil mitschalten", "Also switch the Windows color profile"),
                L.T("Setzt beim Umschalten das passende ICC-Profil als Windows-Standard des Bildschirms, so wie ColorNavigator.",
                    "Sets the matching ICC profile as the display's Windows default when switching, like ColorNavigator does."), profiles_),
            Ui.Setting(Icons.Timer, L.T("Prüfintervall (ms)", "Check interval (ms)"),
                L.T("Programmwechsel werden sofort erkannt; das Intervall betrifft nur Hintergrundprüfungen.",
                    "Program switches are detected immediately; the interval only affects background checks."), poll_),
            Ui.Section(L.T("Updates", "Updates")),
            Ui.Setting(Icons.Download, L.T("Automatisch nach Updates suchen", "Check for updates automatically"),
                L.T("Installierte Version: {0}", "Installed version: {0}", Updater.Current), autoCheck_),
            Ui.Setting(Icons.Refresh, L.T("Updates ohne Nachfrage installieren", "Install updates without asking"),
                L.T("Updates kommen aus den GitHub-Releases; die Prüfsumme wird kontrolliert.", "Updates come from GitHub releases; the checksum is verified."), autoInstall_),
            Ui.Setting(Icons.Download, L.T("Jetzt nach Updates suchen", "Check now"), null,
                Ui.Row(0,
                    Ui.Button(L.T("Prüfen", "Check"), () => { if (tray_ != null) _ = tray_.CheckForUpdatesAsync(true); }),
                    Ui.Button(L.T("Releases auf GitHub", "Releases on GitHub"), () => Shell.Open(Updater.ReleasesUrl)))),
            Ui.Section(L.T("Gefällt dir EizoSwitcher?", "Do you like EizoSwitcher?")),
            Ui.Setting(Icons.Heart, L.T("Gefällt dir EizoSwitcher?", "Do you like EizoSwitcher?"),
                L.T("EizoSwitcher ist kostenlos. Wenn es dir den Alltag erleichtert, freue ich mich über einen Kaffee.",
                    "EizoSwitcher is free. If it makes your day a little easier, I'd be happy about a coffee."), coffee),
            Ui.Section(L.T("Info", "About")),
            Ui.Setting(Icons.Info, "EizoSwitcher " + Updater.Current,
                L.T("EIZO, ColorEdge und ColorNavigator sind Marken der EIZO Corporation. EizoSwitcher ist ein unabhängiges Projekt und nicht mit EIZO verbunden.",
                    "EIZO, ColorEdge and ColorNavigator are trademarks of EIZO Corporation. EizoSwitcher is an independent project, not affiliated with EIZO."),
                Ui.Row(0,
                    Ui.Button(L.T("Projektseite", "Project page"), () => Shell.Open(Links.Project)),
                    Ui.Button(L.T("Protokoll anzeigen", "Show log"), () => Shell.Open(Log.FilePath)))),
            Ui.Setting(Icons.Folder, L.T("Einstellungsordner öffnen", "Open settings folder"), Log.Folder,
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
            g.ExcludedProcesses = [.. Exclusions];
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
            work_.KeepModesUntilExit = keep_.IsChecked == true;
            work_.SwitchColorProfiles = profiles_.IsChecked == true;
            work_.PollIntervalMs = int.TryParse(Ui.Token(poll_), out int ms) ? ms : 1500;
            work_.AutoCheckUpdates = autoCheck_.IsChecked == true;
            work_.AutoInstallUpdates = autoInstall_.IsChecked == true;
        }
    }
}
