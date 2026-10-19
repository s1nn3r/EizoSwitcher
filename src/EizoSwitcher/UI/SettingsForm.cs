using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

internal sealed class SettingsForm : Form
{
    /// <summary>One display column: live data if connected, otherwise what we remembered.</summary>
    private sealed record Col(string Id, string Name, bool Connected, bool IsEizo, bool SupportsPower, IReadOnlyList<string> Modes, string? Current);

    private readonly DisplayService displays_;
    private readonly GameDetector games_;
    private readonly TrayApp? tray_;
    private readonly AppSettings work_;
    private List<Col> cols_ = [];

    private readonly Panel root_ = new() { Dock = DockStyle.Fill };
    private readonly TabControl tabs_ = new() { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
    private DataGridView grid_ = new();
    private readonly Dictionary<string, ComboBox> defaultBoxes_ = [];
    private readonly Dictionary<string, ComboBox> gameBoxes_ = [];
    private CheckBox gamesEnabled_ = null!, gExclusive_ = null!, gBorderless_ = null!, gWinList_ = null!, gLibraries_ = null!;
    private ListBox extraFolders_ = null!;
    private TextBox exclusions_ = null!;
    private CheckBox autostart_ = null!, notify_ = null!, autoCheck_ = null!, autoInstall_ = null!;
    private NumericUpDown poll_ = null!;
    private ComboBox language_ = null!, theme_ = null!;
    private bool built_;

    public event Action<AppSettings>? Saved;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedTab { get => tabs_.SelectedIndex; set => tabs_.SelectedIndex = Math.Clamp(value, 0, Math.Max(0, tabs_.TabCount - 1)); }
    public int TabCount => tabs_.TabCount;

    /// <summary>Client area without window frame (for screenshots).</summary>
    public Control Root => root_;

    public SettingsForm(AppSettings settings, DisplayService displays, GameDetector games, TrayApp? tray)
    {
        displays_ = displays;
        games_ = games;
        tray_ = tray;
        work_ = settings.Clone();

        Text = "EizoSwitcher – " + L.T("Einstellungen", "Settings");
        Icon = Ui.AppIcon(SystemInformation.IconSize);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(1180, 760);
        MinimumSize = new Size(860, 540);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(12, 8, 12, 10) };
        footer.Controls.Add(Ui.Button(L.T("Übernehmen", "Apply"), () => { Collect(); Saved?.Invoke(work_.Clone()); }));
        footer.Controls.Add(Ui.Button(L.T("Abbrechen", "Cancel"), Close));
        var save = Ui.Button(L.T("Speichern", "Save"), () => { Collect(); Saved?.Invoke(work_.Clone()); Close(); }, accent: true);
        footer.Controls.Add(save);
        AcceptButton = null; // Enter must work inside the grid

        root_.Controls.Add(tabs_);
        root_.Controls.Add(footer);
        Controls.Add(root_);
        BuildTabs();
        Shown += (_, _) => grid_.ClearSelection();
    }

    private void BuildTabs()
    {
        int selected = Math.Max(0, tabs_.SelectedIndex);
        cols_ = BuildColumns();
        tabs_.SuspendLayout();
        foreach (TabPage p in tabs_.TabPages) p.Dispose();
        tabs_.TabPages.Clear();
        tabs_.TabPages.Add(Page(L.T("Programme", "Programs"), ProgramsTab()));
        tabs_.TabPages.Add(Page(L.T("Spiele", "Games"), GamesTab()));
        tabs_.TabPages.Add(Page(L.T("Standard", "Defaults"), DefaultsTab()));
        tabs_.TabPages.Add(Page(L.T("Bildschirme", "Displays"), DisplaysTab()));
        tabs_.TabPages.Add(Page(L.T("Allgemein", "General"), GeneralTab()));
        tabs_.SelectedIndex = Math.Min(selected, tabs_.TabCount - 1);
        tabs_.ResumeLayout();
        built_ = true;
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true };
        page.Controls.Add(content);
        return page;
    }

    /// <summary>Called when displays were re-enumerated while the window is open.</summary>
    public void DisplaysChanged()
    {
        Collect();
        BuildTabs();
    }

    private List<Col> BuildColumns()
    {
        var cols = displays_.Displays.Select(d => new Col(d.Id, d.Name, true, d.IsEizo, d.SupportsPower, d.Eizo?.Modes ?? [], d.Eizo?.CurrentModeName)).ToList();
        foreach (var k in work_.Displays.Where(k => cols.All(c => c.Id != k.Id)))
            cols.Add(new Col(k.Id, k.Name, false, k.IsEizo, k.SupportsPower, k.Modes, null));
        // same model twice: append the end of the serial so the columns can be told apart
        return [.. cols.Select(c => cols.Count(x => x.Name == c.Name) > 1 ? c with { Name = $"{c.Name} ({Ui.Serial(c.Id)[^4..]})" } : c)];
    }

    private static List<Opt> Options(Col c, bool special)
    {
        var list = new List<Opt> { new("", "—") };
        list.AddRange(c.Modes.Select(m => new Opt(m, m)));
        if (special)
        {
            list.Add(new Opt(DisplayAction.Black, "■ " + DisplayAction.Text(DisplayAction.Black)));
            if (c.SupportsPower) list.Add(new Opt(DisplayAction.Off, "⏻ " + DisplayAction.Text(DisplayAction.Off)));
        }
        return list;
    }

    private static string ColSubtitle(Col c) =>
        !c.Connected ? L.T("nicht verbunden", "not connected")
        : c.IsEizo ? L.T("EIZO · Farbmodi", "EIZO · color modes")
        : c.SupportsPower ? L.T("Schwarz / Aus", "Black / off")
        : L.T("Schwarz", "Black");

    // ================================================================= Programs

    private Control ProgramsTab()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 12, 20, 8) };

        var intro = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(1080, 0), ForeColor = SystemColors.GrayText, Font = Ui.HintFont,
            Padding = new Padding(0, 0, 0, 8),
            Text = L.T("Sobald eines dieser Programme läuft, werden die Bildschirme wie angegeben umgeschaltet. Wird es beendet, gilt wieder der Standard. " +
                       "Laufen mehrere, gewinnt die oberste Zeile. „—“ lässt einen Bildschirm unverändert. Graue Zeilen verweisen auf einen Farbmodus oder Bildschirm, den es nicht (mehr) gibt.",
                       "As soon as one of these programs runs, the displays switch as configured. When it exits, the defaults apply again. " +
                       "If several are running, the top row wins. \"—\" leaves a display unchanged. Grey rows refer to a color mode or display that does not exist (anymore)."),
        };
        var heading = new Label { Dock = DockStyle.Top, AutoSize = true, Text = L.T("Programme", "Programs"), Font = Ui.HeadingFont, Padding = new Padding(0, 0, 0, 4) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
        bar.Controls.Add(Ui.Button(L.T("＋  Laufendes Programm …", "＋  Running program …"), AddFromProcesses, accent: true));
        bar.Controls.Add(Ui.Button(L.T("＋  Programmdatei auswählen …", "＋  Choose executable …"), AddFromFile));
        bar.Controls.Add(Ui.Button(L.T("Entfernen", "Remove"), RemoveSelected));
        bar.Controls.Add(Ui.Button("↑", () => MoveSelected(-1)));
        bar.Controls.Add(Ui.Button("↓", () => MoveSelected(1)));

        SetupGrid();
        foreach (var r in work_.Rules) AddRow(r);

        // Fill first, then the top-docked controls (last added docks first)
        panel.Controls.Add(grid_);
        panel.Controls.Add(bar);
        panel.Controls.Add(intro);
        panel.Controls.Add(heading);
        return panel;
    }

    private void SetupGrid()
    {
        grid_ = new DataGridView();
        grid_.Dock = DockStyle.Fill;
        grid_.AllowUserToAddRows = false;
        grid_.AllowUserToResizeRows = false;
        grid_.RowHeadersVisible = false;
        grid_.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid_.MultiSelect = true;
        grid_.EditMode = DataGridViewEditMode.EditOnEnter;
        grid_.BorderStyle = BorderStyle.None;
        grid_.BackgroundColor = SystemColors.Window;
        grid_.GridColor = SystemColors.ControlLight;
        grid_.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid_.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid_.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid_.EnableHeadersVisualStyles = false;
        grid_.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Control;
        grid_.ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
        grid_.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
        grid_.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid_.DefaultCellStyle.BackColor = SystemColors.Window;
        grid_.DefaultCellStyle.ForeColor = SystemColors.WindowText;
        grid_.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
        grid_.DefaultCellStyle.SelectionForeColor = SystemColors.HighlightText;
        grid_.RowTemplate.Height = 32;
        grid_.ShowCellToolTips = true;

        grid_.Columns.Add(new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = "", Width = 34, ToolTipText = L.T("Regel aktiv", "Rule enabled") });
        grid_.Columns.Add(new DataGridViewImageColumn { Name = "icon", HeaderText = "", Width = 30, ImageLayout = DataGridViewImageCellLayout.Normal, ReadOnly = true, DefaultCellStyle = { NullValue = null } });
        grid_.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = L.T("Name", "Name"), Width = 200 });
        grid_.Columns.Add(new DataGridViewTextBoxColumn { Name = "exe", HeaderText = L.T("Programm", "Program"), Width = 170, ReadOnly = true, DefaultCellStyle = { ForeColor = SystemColors.GrayText } });
        grid_.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "path", HeaderText = L.T("Pfad", "Path"), Width = 52,
            ToolTipText = L.T("Nur auslösen, wenn das Programm aus genau diesem Ordner gestartet wurde", "Only trigger when the program runs from exactly this folder"),
        });
        foreach (var c in cols_)
        {
            grid_.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "mon:" + c.Id,
                HeaderText = $"{c.Name}\n{ColSubtitle(c)}",
                Width = 160,
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
                DisplayMember = nameof(Opt.Label),
                ValueMember = nameof(Opt.Token),
                ToolTipText = c.Connected ? c.Name : L.T("{0} ist nicht verbunden", "{0} is not connected", c.Name),
            });
        }
        grid_.Columns.Add(new DataGridViewTextBoxColumn { Name = "fill", HeaderText = "", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });

        grid_.CurrentCellDirtyStateChanged += (_, _) => { if (grid_.IsCurrentCellDirty) grid_.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid_.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) UpdateRowState(grid_.Rows[e.RowIndex]); };
        grid_.DataError += (_, e) => e.ThrowException = false;
    }

    private void AddRow(ProcessRule rule)
    {
        int i = grid_.Rows.Add();
        var row = grid_.Rows[i];
        row.Tag = rule;
        row.Cells["enabled"].Value = rule.Enabled;
        row.Cells["icon"].Value = ExeIcons.For(rule.ExePath, 20);
        row.Cells["name"].Value = rule.Name;
        row.Cells["exe"].Value = rule.ExeName;
        row.Cells["exe"].ToolTipText = rule.ExePath ?? rule.ExeName;
        row.Cells["path"].Value = rule.MatchPath;
        row.Cells["path"].ReadOnly = string.IsNullOrEmpty(rule.ExePath);
        foreach (var c in cols_)
        {
            var cell = (DataGridViewComboBoxCell)row.Cells["mon:" + c.Id];
            var opts = Options(c, special: true);
            string value = rule.Actions.GetValueOrDefault(c.Id) ?? "";
            if (opts.All(o => !o.Token.Equals(value, StringComparison.OrdinalIgnoreCase))) opts.Add(new Opt(value, value + "  ⚠"));
            else value = opts.First(o => o.Token.Equals(value, StringComparison.OrdinalIgnoreCase)).Token;
            cell.DataSource = opts;
            cell.DisplayMember = nameof(Opt.Label);
            cell.ValueMember = nameof(Opt.Token);
            cell.Value = value;
        }
        UpdateRowState(row);
    }

    private void UpdateRowState(DataGridViewRow row)
    {
        var reasons = new List<string>();
        foreach (var c in cols_)
        {
            string t = row.Cells["mon:" + c.Id].Value as string ?? "";
            if (t.Length == 0) continue;
            if (!c.Connected) reasons.Add(L.T("{0} ist nicht verbunden", "{0} is not connected", c.Name));
            else if (DisplayAction.IsMode(t) && !c.Modes.Contains(t, StringComparer.OrdinalIgnoreCase))
                reasons.Add(L.T("Den Farbmodus „{0}“ gibt es auf {1} nicht (mehr)", "Color mode \"{0}\" does not exist on {1} (anymore)", t, c.Name));
        }
        bool enabled = row.Cells["enabled"].Value is true;
        bool missing = reasons.Count > 0;
        row.DefaultCellStyle.ForeColor = missing || !enabled ? SystemColors.GrayText : Color.Empty;
        row.DefaultCellStyle.BackColor = missing ? SystemColors.Control : Color.Empty;
        row.DefaultCellStyle.Font = enabled ? null : new Font(Font, FontStyle.Italic);
        string tip = string.Join("\n", reasons);
        foreach (DataGridViewCell cell in row.Cells)
            if (cell.OwningColumn?.Name != "exe") cell.ToolTipText = tip;
    }

    private void RemoveSelected()
    {
        foreach (DataGridViewRow row in grid_.SelectedRows) grid_.Rows.Remove(row);
    }

    private void MoveSelected(int delta)
    {
        if (grid_.CurrentRow is not { } row) return;
        int i = row.Index, j = i + delta;
        if (j < 0 || j >= grid_.Rows.Count) return;
        grid_.EndEdit();
        grid_.Rows.RemoveAt(i);
        grid_.Rows.Insert(j, row);
        grid_.ClearSelection();
        row.Selected = true;
        grid_.CurrentCell = row.Cells["name"];
    }

    private void AddProgram(string exeName, string? path, string? description)
    {
        exeName = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe";
        foreach (DataGridViewRow r in grid_.Rows)
        {
            if (ProcessNames.Normalize(((ProcessRule)r.Tag!).ExeName) == ProcessNames.Normalize(exeName))
            {
                grid_.ClearSelection();
                r.Selected = true;
                return;
            }
        }
        AddRow(new ProcessRule { Name = description ?? Path.GetFileNameWithoutExtension(exeName), ExeName = exeName, ExePath = path });
        var last = grid_.Rows[^1];
        grid_.ClearSelection();
        last.Selected = true;
        grid_.FirstDisplayedScrollingRowIndex = last.Index;
    }

    private void AddFromProcesses()
    {
        using var picker = new ProcessPickerForm();
        if (picker.ShowDialog(this) == DialogResult.OK)
            foreach (var e in picker.Selected) AddProgram(e.ExeName, e.Path, e.Description);
    }

    private void AddFromFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = L.T("Programm auswählen", "Choose program"),
            Filter = L.T("Programme", "Programs") + " (*.exe)|*.exe",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in dlg.FileNames)
            AddProgram(Path.GetFileName(path), path, Shell.Describe(path, Path.GetFileNameWithoutExtension(path)));
    }

    // ================================================================= Games

    private Control GamesTab()
    {
        var g = work_.Games;
        var p = new PageBuilder().Heading(L.T("Spiele", "Games"));
        gamesEnabled_ = Ui.Check(L.T("Spiele automatisch erkennen", "Detect games automatically"), g.Enabled);
        gamesEnabled_.Font = new Font(Font, FontStyle.Bold);
        p.Span(gamesEnabled_);

        p.Section(L.T("Während ein Spiel läuft", "While a game is running"))
         .Hint(L.T("Programmregeln haben Vorrang. Tipp: Nebenbildschirme auf „Schwarz“ stellen, damit sie nicht ablenken.",
                   "Program rules take precedence. Tip: set secondary displays to \"Black\" so they don't distract."));
        gameBoxes_.Clear();
        foreach (var c in cols_)
        {
            var box = Ui.Combo(Options(c, special: true), g.Actions.GetValueOrDefault(c.Id) ?? "");
            gameBoxes_[c.Id] = box;
            p.Line(c.Name, box, ColSubtitle(c));
        }

        gExclusive_ = Ui.Check(L.T("Exklusiver Vollbildmodus (DirectX)", "Exclusive fullscreen (DirectX)"), g.DetectExclusiveFullscreen);
        gBorderless_ = Ui.Check(L.T("Randloses Vollbildfenster im Vordergrund", "Borderless fullscreen window in the foreground"), g.DetectBorderlessFullscreen);
        gWinList_ = Ui.Check(L.T("Von Windows als Spiel erkannte Programme (Game Bar)", "Programs Windows recognizes as games (Game Bar)"), g.UseWindowsGameList);
        gLibraries_ = Ui.Check(L.T("Programme aus Spielebibliotheken (Steam, Epic, GOG, Ubisoft, EA, Xbox)", "Programs from game libraries (Steam, Epic, GOG, Ubisoft, EA, Xbox)"), g.UseLauncherLibraries);
        p.Section(L.T("So werden Spiele erkannt", "How games are detected"))
         .Span(gExclusive_).Hint(L.T("   Zuverlässig bei klassischem Vollbild. Wird auch bei Alt+Tab gehalten, bis das Spiel beendet ist.",
                                     "   Reliable for classic fullscreen. Kept on Alt+Tab until the game exits."), 20)
         .Span(gBorderless_).Hint(L.T("   Für moderne Spiele im „Vollbild-Fenster“. Browser und Videoplayer sind unten ausgenommen.",
                                      "   For modern games in \"borderless\" mode. Browsers and video players are excluded below."), 20)
         .Span(gWinList_).Hint(L.T("   Nutzt die Liste, die Windows für den Spielmodus führt.", "   Uses the list Windows keeps for Game Mode."), 20)
         .Span(gLibraries_).Hint(L.T("   Erkennt Spiele schon beim Start, auch im Fenstermodus.", "   Detects games right at start, even in windowed mode."), 20);

        var libraries = new ListBox { Width = 640, Height = 110, IntegralHeight = false, SelectionMode = SelectionMode.None };
        libraries.Items.AddRange([.. games_.Libraries]);
        extraFolders_ = new ListBox { Width = 640, Height = 80, IntegralHeight = false };
        extraFolders_.Items.AddRange([.. g.ExtraFolders]);
        p.Section(L.T("Spielebibliotheken", "Game libraries"))
         .Hint(L.T("Automatisch gefunden:", "Found automatically:")).Span(libraries)
         .Hint(L.T("Zusätzliche Ordner:", "Additional folders:")).Span(extraFolders_)
         .Span(Ui.Flow(
             Ui.Button(L.T("Ordner hinzufügen …", "Add folder …"), AddFolder),
             Ui.Button(L.T("Entfernen", "Remove"), () => { if (extraFolders_.SelectedIndex >= 0) extraFolders_.Items.RemoveAt(extraFolders_.SelectedIndex); })));

        exclusions_ = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Width = 640, Height = 150, Text = string.Join(Environment.NewLine, g.ExcludedProcesses) };
        p.Section(L.T("Nie als Spiel behandeln", "Never treat as a game"))
         .Hint(L.T("Prozessnamen, einer pro Zeile (ohne .exe).", "Process names, one per line (without .exe)."))
         .Span(exclusions_)
         .Span(Ui.Button(L.T("Liste zurücksetzen", "Reset list"), () => exclusions_.Text = string.Join(Environment.NewLine, GameSettings.DefaultExclusions)));
        return p.Build();
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = L.T("Spieleordner auswählen", "Choose games folder"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) == DialogResult.OK && !extraFolders_.Items.Cast<string>().Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
            extraFolders_.Items.Add(dlg.SelectedPath);
    }

    // ================================================================= Defaults

    private Control DefaultsTab()
    {
        var p = new PageBuilder().Heading(L.T("Standard", "Defaults"))
            .Hint(L.T("Dieser Zustand gilt, solange kein konfiguriertes Programm und kein Spiel läuft. Beim ersten Start übernimmt EizoSwitcher den aktuell eingestellten Modus.",
                      "This applies while no configured program and no game is running. On first start EizoSwitcher takes over the currently selected mode."));
        defaultBoxes_.Clear();
        foreach (var c in cols_)
        {
            if (c.IsEizo)
            {
                var box = Ui.Combo(Options(c, special: false), work_.Defaults.GetValueOrDefault(c.Id) ?? "");
                defaultBoxes_[c.Id] = box;
                var line = Ui.Flow(box);
                if (c.Current != null) line.Controls.Add(Ui.Text(L.T("aktuell: {0}", "current: {0}", c.Current), dim: true));
                p.Line(c.Name, line, ColSubtitle(c));
            }
            else
            {
                p.Line(c.Name, Ui.Text(L.T("immer normal (an, nicht abgedunkelt)", "always normal (on, not blanked)"), dim: true), ColSubtitle(c));
            }
        }
        if (cols_.Count == 0) p.Hint(L.T("Keine Bildschirme gefunden.", "No displays found."));
        return p.Build();
    }

    // ================================================================= Displays

    private Control DisplaysTab()
    {
        var p = new PageBuilder();
        p.Span(Ui.Flow(new Label { Text = L.T("Bildschirme", "Displays"), Font = Ui.HeadingFont, AutoSize = true, Margin = new Padding(0, 0, 16, 6) },
                       Ui.Button(L.T("Neu einlesen", "Rescan"), () => tray_?.RefreshDisplays())))
         .Hint(L.T("EIZO-Monitore mit angeschlossenem USB-Kabel lassen sich direkt umschalten. Alle anderen Bildschirme können abgedunkelt und – falls sie DDC/CI unterstützen – ausgeschaltet werden.",
                   "EIZO monitors with the USB cable connected can be switched directly. All other displays can be blanked and – if they support DDC/CI – turned off."));

        string Yes(bool b) => b ? L.T("ja", "yes") : L.T("nein", "no");
        foreach (var d in displays_.Displays)
        {
            p.Section($"{d.Name}   ·   {d.DeviceName.TrimStart('\\', '.')}");
            p.Line(L.T("Hersteller", "Manufacturer"), Ui.Text(d.Manufacturer));
            p.Line(L.T("Seriennummer", "Serial number"), Ui.Text(Ui.Serial(d.Serial)));
            if (d.Width > 0)
                p.Line(L.T("Auflösung / Position", "Resolution / position"),
                       Ui.Text($"{d.Width} × {d.Height}  @ {d.X}, {d.Y}{(d.Primary ? L.T("  (Hauptbildschirm)", "  (primary)") : "")}"));
            p.Line(L.T("EIZO-USB-Steuerung", "EIZO USB control"), Ui.Text(Yes(d.IsEizo)));
            p.Line(L.T("DDC/CI ausschalten", "DDC/CI power off"), Ui.Text(Yes(d.SupportsPower)));

            var actions = Ui.Flow();
            if (d.Eizo != null)
            {
                p.Line(L.T("Aktueller Farbmodus", "Current color mode"), Ui.Text(d.Eizo.CurrentModeName));
                var box = Ui.Combo(d.Eizo.Modes.Select(m => new Opt(m, m)), d.Eizo.CurrentModeName, 200);
                actions.Controls.Add(box);
                actions.Controls.Add(Ui.Button(L.T("Aktivieren", "Activate"), () =>
                {
                    int i = box.SelectedIndex;
                    Task.Run(() => displays_.SetMode(d, i)).ContinueWith(_ => BeginInvoke(() => { Collect(); BuildTabs(); }));
                }));
            }
            if (d.Width > 0 && tray_ != null)
            {
                actions.Controls.Add(Ui.Button(L.T("3 s schwarz testen", "Test black (3 s)"), () =>
                {
                    tray_.Show(d);
                    Task.Delay(3000).ContinueWith(_ => tray_.Hide(d.Id));
                }));
            }
            if (d.SupportsPower)
            {
                actions.Controls.Add(Ui.Button(L.T("5 s ausschalten testen", "Test power off (5 s)"), () =>
                    Task.Run(() => { displays_.SetPower(d, false); Thread.Sleep(5000); displays_.SetPower(d, true); })));
            }
            p.Span(actions);
        }

        foreach (var k in work_.Displays.Where(k => displays_.Displays.All(d => d.Id != k.Id)))
        {
            p.Section(k.Name)
             .Hint(L.T("Nicht verbunden · zuletzt gesehen {0:g}", "Not connected · last seen {0:g}", k.LastSeen))
             .Span(Ui.Button(L.T("Vergessen", "Forget"), () => { Collect(); work_.Displays.RemoveAll(x => x.Id == k.Id); BuildTabs(); }));
        }
        return p.Build();
    }

    // ================================================================= General

    private Control GeneralTab()
    {
        var p = new PageBuilder().Heading(L.T("Allgemein", "General"));

        var languages = new List<Opt> { new("", L.T("Systemsprache", "System language")) };
        languages.AddRange(L.Languages.Select(l => new Opt(l.Code, l.Name)));
        language_ = Ui.Combo(languages, work_.Language);
        theme_ = Ui.Combo([new("", L.T("Wie Windows", "Same as Windows")), new("light", L.T("Hell", "Light")), new("dark", L.T("Dunkel", "Dark"))], work_.Theme);
        p.Section(L.T("Darstellung", "Appearance"))
         .Line(L.T("Sprache", "Language"), language_)
         .Line(L.T("Farbschema", "Theme"), theme_);

        autostart_ = Ui.Check(L.T("Mit Windows starten", "Start with Windows"), work_.StartWithWindows);
        notify_ = Ui.Check(L.T("Beim Umschalten kurz benachrichtigen", "Show a short notification when switching"), work_.ShowNotifications);
        poll_ = new NumericUpDown { Minimum = 250, Maximum = 10000, Increment = 250, Value = Math.Clamp(work_.PollIntervalMs, 250, 10000), Width = 90, Margin = new Padding(0, 3, 6, 3) };
        p.Section(L.T("Start und Verhalten", "Startup and behavior"))
         .Span(autostart_).Span(notify_)
         .Line(L.T("Prozesse prüfen alle", "Check processes every"), Ui.Flow(poll_, Ui.Text("ms")))
         .Hint(L.T("Kleinere Werte schalten schneller um, kosten aber minimal mehr CPU. 1500 ms sind ein guter Wert.",
                   "Smaller values switch faster at a tiny CPU cost. 1500 ms is a good value."));

        autoCheck_ = Ui.Check(L.T("Automatisch nach Updates suchen", "Check for updates automatically"), work_.AutoCheckUpdates);
        autoInstall_ = Ui.Check(L.T("Updates ohne Nachfrage installieren", "Install updates without asking"), work_.AutoInstallUpdates);
        p.Section(L.T("Updates", "Updates"))
         .Span(Ui.Text(L.T("Installierte Version: {0}", "Installed version: {0}", Updater.Current)))
         .Span(autoCheck_).Span(autoInstall_)
         .Hint(L.T("Updates kommen aus den GitHub-Releases. Die Prüfsumme wird kontrolliert; ist EizoSwitcher signiert, muss es auch das Update sein.",
                   "Updates come from GitHub releases. The checksum is verified; if EizoSwitcher is signed, the update must be signed by the same publisher."))
         .Span(Ui.Flow(
             Ui.Button(L.T("Jetzt nach Updates suchen", "Check now"), async () => { if (tray_ != null) await tray_.CheckForUpdatesAsync(true); }),
             Ui.Button(L.T("Releases auf GitHub", "Releases on GitHub"), () => Shell.Open(Updater.ReleasesUrl))));

        p.Section(L.T("Gefällt dir EizoSwitcher?", "Do you like EizoSwitcher?"))
         .Hint(L.T("EizoSwitcher ist kostenlos. Wenn es dir den Alltag erleichtert, freue ich mich über einen Kaffee.",
                   "EizoSwitcher is free. If it makes your day a little easier, I'd be happy about a coffee."))
         .Span(Ui.Button(L.T("Spendier mir einen Kaffee", "Buy me a coffee"), () => Shell.Open(Links.Donate), accent: true));

        p.Section(L.T("Info", "About"))
         .Span(Ui.Flow(
             Ui.Button(L.T("Projektseite", "Project page"), () => Shell.Open(Links.Project)),
             Ui.Button(L.T("Einstellungsordner öffnen", "Open settings folder"), () => { Directory.CreateDirectory(Log.Folder); Shell.Open(Log.Folder); }),
             Ui.Button(L.T("Protokoll anzeigen", "Show log"), () => Shell.Open(Log.FilePath))))
         .Hint(L.T("EIZO, ColorEdge und ColorNavigator sind Marken der EIZO Corporation. EizoSwitcher ist ein unabhängiges Projekt und nicht mit EIZO verbunden.",
                   "EIZO, ColorEdge and ColorNavigator are trademarks of EIZO Corporation. EizoSwitcher is an independent project, not affiliated with EIZO."));
        return p.Build();
    }

    // ================================================================= collect

    private void Collect()
    {
        if (!built_) return;
        grid_.EndEdit();

        var rules = new List<ProcessRule>();
        foreach (DataGridViewRow row in grid_.Rows)
        {
            var r = (ProcessRule)row.Tag!;
            r.Enabled = row.Cells["enabled"].Value is true;
            r.Name = (row.Cells["name"].Value as string)?.Trim() ?? "";
            r.MatchPath = row.Cells["path"].Value is true;
            foreach (var c in cols_)
            {
                string t = row.Cells["mon:" + c.Id].Value as string ?? "";
                if (t.Length == 0) r.Actions.Remove(c.Id); else r.Actions[c.Id] = t;
            }
            rules.Add(r);
        }
        work_.Rules = rules;

        foreach (var (id, box) in defaultBoxes_)
        {
            string t = Ui.Token(box);
            if (t.Length == 0) work_.Defaults.Remove(id); else work_.Defaults[id] = t;
        }

        var g = work_.Games;
        g.Enabled = gamesEnabled_.Checked;
        g.DetectExclusiveFullscreen = gExclusive_.Checked;
        g.DetectBorderlessFullscreen = gBorderless_.Checked;
        g.UseWindowsGameList = gWinList_.Checked;
        g.UseLauncherLibraries = gLibraries_.Checked;
        foreach (var (id, box) in gameBoxes_)
        {
            string t = Ui.Token(box);
            if (t.Length == 0) g.Actions.Remove(id); else g.Actions[id] = t;
        }
        g.ExtraFolders = [.. extraFolders_.Items.Cast<string>()];
        g.ExcludedProcesses = [.. exclusions_.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

        work_.StartWithWindows = autostart_.Checked;
        work_.ShowNotifications = notify_.Checked;
        work_.PollIntervalMs = (int)poll_.Value;
        work_.AutoCheckUpdates = autoCheck_.Checked;
        work_.AutoInstallUpdates = autoInstall_.Checked;
        work_.Language = Ui.Token(language_);
        work_.Theme = Ui.Token(theme_);
    }
}
