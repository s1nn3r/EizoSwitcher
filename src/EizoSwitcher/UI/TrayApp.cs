using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

/// <summary>Owns the tray icon, the engine and all windows. Lives on the UI thread.</summary>
internal sealed class TrayApp : ApplicationContext, IBlackout
{
    private readonly Control invoker_ = new();
    private readonly NotifyIcon tray_;
    private readonly DisplayService displays_ = new();
    private readonly SwitchEngine engine_;
    private readonly Dictionary<string, OverlayForm> overlays_ = [];
    private readonly System.Windows.Forms.Timer updateTimer_ = new() { Interval = 20_000 };
    private readonly bool firstRun_;
    private AppSettings settings_;
    private SettingsForm? settingsForm_;
    private UpdateInfo? pendingUpdate_;
    private bool updating_, welcomed_;

    public TrayApp(AppSettings settings, bool justUpdated)
    {
        _ = invoker_.Handle; // marshalling target for background threads
        firstRun_ = !File.Exists(AppSettings.FilePath);
        settings_ = settings;
        Log.Write($"EizoSwitcher {Updater.Current} started");

        displays_.ProfileFor = (d, index) => ColorProfiles.For(settings_, d, index);
        engine_ = new SwitchEngine(displays_, this, settings_);
        engine_.Switched += msg => Ui(() => { if (settings_.ShowNotifications) Notify(msg); });
        engine_.StateChanged += () => Ui(UpdateTooltip);
        engine_.DisplaysRefreshed += () => Ui(RememberDisplays);

        tray_ = new NotifyIcon { Icon = UI.Ui.AppIcon(SystemInformation.SmallIconSize), Text = "EizoSwitcher", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        tray_.ContextMenuStrip.Opening += (_, e) => { BuildMenu(tray_.ContextMenuStrip); e.Cancel = false; };
        tray_.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        tray_.BalloonTipClicked += async (_, _) => { if (pendingUpdate_ != null) await InstallUpdateAsync(pendingUpdate_); };

        Autostart.Apply(settings_.StartWithWindows);
        engine_.Start();
        ListenForShowRequests();

        updateTimer_.Tick += async (_, _) =>
        {
            updateTimer_.Interval = 6 * 3600 * 1000;
            if (settings_.AutoCheckUpdates) await CheckForUpdatesAsync(interactive: false);
        };
        updateTimer_.Start();

        if (justUpdated) Notify(L.T("Aktualisiert auf Version {0}", "Updated to version {0}", Updater.Current));
    }

    private void Ui(Action a)
    {
        if (invoker_.IsDisposed) return;
        if (invoker_.InvokeRequired) invoker_.BeginInvoke(a); else a();
    }

    private void Notify(string message) => tray_.ShowBalloonTip(3000, "EizoSwitcher", message, ToolTipIcon.None);

    // ---------------------------------------------------------------- tray menu

    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem($"EizoSwitcher {Updater.Current}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        foreach (var d in displays_.Displays)
        {
            string reason = engine_.Reasons.GetValueOrDefault(d.Id) ?? "";
            var item = new ToolStripMenuItem($"{d.Name}:  {State(d)}" + (reason.Length > 0 ? $"   ({reason})" : ""));
            if (d.Eizo != null)
            {
                for (int i = 0; i < d.Eizo.Modes.Count; i++)
                {
                    int index = i;
                    var mi = new ToolStripMenuItem(d.Eizo.Modes[i]) { Checked = i == d.Eizo.CurrentMode };
                    mi.Click += (_, _) => Task.Run(() => displays_.SetMode(d, index)).ContinueWith(_ => Ui(UpdateTooltip));
                    item.DropDownItems.Add(mi);
                }
                item.DropDownItems.Add(new ToolStripSeparator());
            }
            item.DropDownItems.Add(L.T("Schwarz schalten (Klick zum Aufheben)", "Blank (click to undo)"), null, (_, _) => Show(d));
            menu.Items.Add(item);
        }
        if (displays_.Displays.Count == 0)
            menu.Items.Add(new ToolStripMenuItem(L.T("Bildschirme werden gesucht …", "Looking for displays …")) { Enabled = false });
        if (engine_.ActiveGame != null)
            menu.Items.Add(new ToolStripMenuItem(L.T("Spiel erkannt: ", "Game detected: ") + engine_.ActiveGame) { Enabled = false });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L.T("Automatik pausieren", "Pause automatic switching"), null, (_, _) => TogglePause()) { Checked = settings_.Paused });
        var open = new ToolStripMenuItem(L.T("Einstellungen …", "Settings …"), null, (_, _) => ShowSettings());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(pendingUpdate_ != null
            ? new ToolStripMenuItem(L.T("Update auf {0} installieren", "Install update {0}", pendingUpdate_.Version), null, async (_, _) => await InstallUpdateAsync(pendingUpdate_))
            : new ToolStripMenuItem(L.T("Nach Updates suchen", "Check for updates"), null, async (_, _) => await CheckForUpdatesAsync(true)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("Beenden", "Exit"), null, (_, _) => ExitApp());
    }

    private string State(DisplayInfo d) =>
        overlays_.ContainsKey(d.Id) ? DisplayAction.Text(DisplayAction.Black) : d.Eizo != null ? d.Eizo.CurrentModeName : "—";

    private void UpdateTooltip()
    {
        string tip = string.Join("\n", displays_.Displays.Select(d => $"{d.Name}: {State(d)}").Prepend("EizoSwitcher"));
        tray_.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private void TogglePause()
    {
        var s = settings_.Clone();
        s.Paused = !s.Paused;
        ApplySettings(s);
        Notify(s.Paused ? L.T("Automatik pausiert", "Automatic switching paused") : L.T("Automatik aktiv", "Automatic switching active"));
    }

    // ---------------------------------------------------------------- settings

    private void ApplySettings(AppSettings s)
    {
        bool lookChanged = s.Language != settings_.Language || s.Theme != settings_.Theme;
        settings_ = s;
        try { s.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"Saving settings failed: {e.Message}"); }
        engine_.UpdateSettings(s);
        Autostart.Apply(s.StartWithWindows);
        UpdateTooltip();

        if (lookChanged)
        {
            L.Set(s.Language);
            Application.SetColorMode(Program.ColorMode(s.Theme));
            if (settingsForm_ is { IsDisposed: false } open)
            {
                // reopen so texts and colors are rebuilt
                int tab = open.SelectedTab;
                open.Close();
                ShowSettings(tab);
            }
        }
    }

    /// <summary>Stores what we know about the connected displays and seeds defaults with the current mode.</summary>
    private void RememberDisplays()
    {
        var s = settings_.Clone();
        bool changed = false;
        foreach (var d in displays_.Displays)
        {
            var k = s.Displays.FirstOrDefault(x => x.Id == d.Id);
            if (k == null) { k = new KnownDisplay { Id = d.Id }; s.Displays.Add(k); changed = true; }
            var modes = d.Eizo?.Modes.ToList() ?? [];
            if (k.Name != d.Name || k.IsEizo != d.IsEizo || k.CanTurnOff != d.CanTurnOff || !k.Modes.SequenceEqual(modes))
            {
                (k.Name, k.IsEizo, k.CanTurnOff, k.Modes) = (d.Name, d.IsEizo, d.CanTurnOff, modes);
                changed = true;
            }
            k.LastSeen = DateTime.Now;
            if (d.Eizo is { CurrentMode: >= 0 } && !s.Defaults.ContainsKey(d.Id))
            {
                s.Defaults[d.Id] = d.Eizo.CurrentModeName;
                Log.Write($"{d.Name}: default mode = {d.Eizo.CurrentModeName}");
                changed = true;
            }
        }
        if (changed) ApplySettings(s);
        UpdateTooltip();
        settingsForm_?.DisplaysChanged();
        if (firstRun_ && !welcomed_) { welcomed_ = true; ShowSettings(); }
    }

    private void ShowSettings() => ShowSettings(0);

    private void ShowSettings(int tab)
    {
        if (settingsForm_ is { IsDisposed: false })
        {
            if (settingsForm_.WindowState == FormWindowState.Minimized) settingsForm_.WindowState = FormWindowState.Normal;
            settingsForm_.Activate();
            return;
        }
        var form = new SettingsForm(settings_, displays_, engine_.Games, this) { SelectedTab = tab };
        form.Saved += s =>
        {
            // keep what changed in the background meanwhile
            s.Displays = settings_.Displays;
            s.Paused = settings_.Paused;
            ApplySettings(s);
        };
        form.FormClosed += (_, _) => { if (settingsForm_ == form) settingsForm_ = null; };
        settingsForm_ = form;
        form.Show();
        form.Activate();
    }

    private void ListenForShowRequests()
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSettingsEvent);
        new Thread(() => { while (ev.WaitOne()) Ui(ShowSettings); }) { IsBackground = true, Name = "ShowSettingsListener" }.Start();
    }

    public void RefreshDisplays() => engine_.RequestRefresh();

    public DisplayService Displays => displays_;

    // ---------------------------------------------------------------- updates

    public async Task CheckForUpdatesAsync(bool interactive)
    {
        try
        {
            var update = await Updater.CheckAsync();
            if (update == null)
            {
                if (interactive) Notify(L.T("EizoSwitcher {0} ist aktuell.", "EizoSwitcher {0} is up to date.", Updater.Current));
                return;
            }
            pendingUpdate_ = update;
            if (settings_.AutoInstallUpdates || interactive) await InstallUpdateAsync(update);
            else Notify(L.T("Version {0} ist verfügbar – Installation über das Tray-Menü.",
                            "Version {0} is available – install it from the tray menu.", update.Version));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Write($"Update check failed: {e.Message}");
            if (interactive) Notify(L.T("Update-Prüfung fehlgeschlagen: ", "Update check failed: ") + e.Message);
        }
    }

    private async Task InstallUpdateAsync(UpdateInfo update)
    {
        if (updating_) return;
        updating_ = true;
        Notify(L.T("Update auf {0} wird installiert …", "Installing update {0} …", update.Version));
        try
        {
            await Task.Run(() => Updater.InstallAsync(update));
            Log.Write($"Update {update.Version} installed, restarting");
            ExitApp();
        }
        catch (Exception e)
        {
            Log.Write($"Update failed: {e}");
            Notify(L.T("Update fehlgeschlagen: ", "Update failed: ") + e.Message);
            if (e is UnauthorizedAccessException) Shell.Open(update.PageUrl); // e.g. installed under Program Files
        }
        finally { updating_ = false; }
    }

    // ---------------------------------------------------------------- IBlackout

    public void Show(DisplayInfo display) => Ui(() =>
    {
        if (overlays_.ContainsKey(display.Id) || display.Width == 0) return;
        var f = new OverlayForm(display);
        f.FormClosed += (_, _) => { overlays_.Remove(display.Id); UpdateTooltip(); };
        overlays_[display.Id] = f;
        f.Show();
        UpdateTooltip();
    });

    public void Hide(string displayId) => Ui(() =>
    {
        if (overlays_.TryGetValue(displayId, out var f)) f.Close();
    });

    // ---------------------------------------------------------------- exit

    private void ExitApp()
    {
        updateTimer_.Stop();
        engine_.Dispose();
        foreach (var f in overlays_.Values.ToList()) f.Close();
        tray_.Visible = false;
        tray_.Dispose();
        displays_.Dispose();
        Log.Write("EizoSwitcher stopped");
        ExitThread();
    }
}

/// <summary>Black borderless window that blanks one display (works with every monitor). Click hides it.</summary>
internal sealed class OverlayForm : Form
{
    public OverlayForm(DisplayInfo d)
    {
        Text = "EizoSwitcher";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(d.X, d.Y, d.Width, d.Height);
        BackColor = Color.Black;
        TopMost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Hand;

        var hint = new Label
        {
            Text = "EizoSwitcher\n" + L.T("Bildschirm abgedunkelt · Klicken zum Einblenden", "Display blanked · click to show"),
            ForeColor = Color.FromArgb(90, 90, 90), BackColor = Color.Black, AutoSize = false, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12f),
        };
        hint.Click += (_, _) => Close();
        Controls.Add(hint);
        Click += (_, _) => Close();

        var fade = new System.Windows.Forms.Timer { Interval = 3000 };
        fade.Tick += (_, _) => { hint.Text = ""; fade.Dispose(); };
        Shown += (_, _) => fade.Start();
    }

    // do not steal the focus from the game / program in front
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }
}
