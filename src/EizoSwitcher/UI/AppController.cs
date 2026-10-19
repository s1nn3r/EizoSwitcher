using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using EizoSwitcher.Services;

namespace EizoSwitcher.UI;

/// <summary>Owns the tray icon, the engine and all windows. Lives on the UI thread.</summary>
internal sealed class AppController : IBlackout
{
    private readonly Application app_;
    private readonly IClassicDesktopStyleApplicationLifetime desktop_;
    private readonly DisplayService displays_ = new();
    private readonly SwitchEngine engine_;
    private readonly TrayIcon tray_;
    private readonly Dictionary<string, OverlayWindow> overlays_ = [];
    private readonly DispatcherTimer updateTimer_;
    private readonly bool firstRun_;
    private AppSettings settings_;
    private SettingsWindow? settingsWindow_;
    private UpdateInfo? pendingUpdate_;
    private bool updating_;

    public AppController(Application app, IClassicDesktopStyleApplicationLifetime desktop, bool justUpdated)
    {
        app_ = app;
        desktop_ = desktop;
        firstRun_ = !File.Exists(AppSettings.FilePath);
        settings_ = AppSettings.Load();
        ApplyAppearance(settings_);
        Log.Write($"EizoSwitcher {Updater.Current} started");

        engine_ = new SwitchEngine(displays_, this, settings_);
        engine_.Switched += msg => Dispatcher.UIThread.Post(() => { if (settings_.ShowNotifications) ToastWindow.Show(msg); });
        engine_.StateChanged += () => Dispatcher.UIThread.Post(RebuildMenu);
        engine_.DisplaysRefreshed += () => Dispatcher.UIThread.Post(RememberDisplays);
        displays_.Changed += () => Dispatcher.UIThread.Post(RebuildMenu);

        tray_ = new TrayIcon { Icon = Ui.AppIcon(), ToolTipText = "EizoSwitcher", Menu = new NativeMenu(), IsVisible = true };
        tray_.Clicked += (_, _) => ShowSettings();
        TrayIcon.SetIcons(app_, [tray_]);
        RebuildMenu();

        Autostart.Apply(settings_.StartWithWindows);
        engine_.Start();
        ListenForShowRequests();

        updateTimer_ = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        updateTimer_.Tick += async (_, _) =>
        {
            updateTimer_.Interval = TimeSpan.FromHours(6);
            if (settings_.AutoCheckUpdates) await CheckForUpdatesAsync(interactive: false);
        };
        updateTimer_.Start();

        if (justUpdated) ToastWindow.Show(L.T("Aktualisiert auf Version {0}", "Updated to version {0}", Updater.Current));
    }

    // ---------------------------------------------------------------- tray menu

    private void RebuildMenu()
    {
        var menu = new NativeMenu();
        menu.Add(new NativeMenuItem($"EizoSwitcher {Updater.Current}") { IsEnabled = false });
        menu.Add(new NativeMenuItemSeparator());

        var tooltip = new List<string> { "EizoSwitcher" };
        foreach (var d in displays_.Displays)
        {
            string state = overlays_.ContainsKey(d.Id) ? DisplayAction.Text(DisplayAction.Black)
                : d.Eizo != null ? d.Eizo.CurrentModeName : "—";
            string reason = engine_.Reasons.GetValueOrDefault(d.Id) ?? "";
            var item = new NativeMenuItem($"{d.Name}:  {state}" + (reason.Length > 0 ? $"   ({reason})" : ""));
            tooltip.Add($"{d.Name}: {state}");

            var sub = new NativeMenu();
            if (d.Eizo != null)
            {
                for (int i = 0; i < d.Eizo.Modes.Count; i++)
                {
                    int index = i;
                    var mi = new NativeMenuItem(d.Eizo.Modes[i]) { ToggleType = MenuItemToggleType.Radio, IsChecked = i == d.Eizo.CurrentMode };
                    mi.Click += (_, _) => Task.Run(() => displays_.SetMode(d, index));
                    sub.Add(mi);
                }
                sub.Add(new NativeMenuItemSeparator());
            }
            var black = new NativeMenuItem(L.T("Schwarz schalten (Klick zum Aufheben)", "Blank (click to undo)"));
            black.Click += (_, _) => Show(d);
            sub.Add(black);
            item.Menu = sub;
            menu.Add(item);
        }
        if (displays_.Displays.Count == 0)
            menu.Add(new NativeMenuItem(L.T("Bildschirme werden gesucht …", "Looking for displays …")) { IsEnabled = false });

        if (engine_.ActiveGame != null)
            menu.Add(new NativeMenuItem(L.T("Spiel erkannt: ", "Game detected: ") + engine_.ActiveGame) { IsEnabled = false });

        menu.Add(new NativeMenuItemSeparator());
        var pause = new NativeMenuItem(L.T("Automatik pausieren", "Pause automatic switching")) { ToggleType = MenuItemToggleType.CheckBox, IsChecked = settings_.Paused };
        pause.Click += (_, _) => TogglePause();
        menu.Add(pause);

        var open = new NativeMenuItem(L.T("Einstellungen …", "Settings …"));
        open.Click += (_, _) => ShowSettings();
        menu.Add(open);

        var update = new NativeMenuItem(pendingUpdate_ != null
            ? L.T("Update auf {0} installieren", "Install update {0}", pendingUpdate_.Version)
            : L.T("Nach Updates suchen", "Check for updates"));
        update.Click += async (_, _) => { if (pendingUpdate_ != null) await InstallUpdateAsync(pendingUpdate_); else await CheckForUpdatesAsync(true); };
        menu.Add(update);

        menu.Add(new NativeMenuItemSeparator());
        var exit = new NativeMenuItem(L.T("Beenden", "Exit"));
        exit.Click += (_, _) => Exit();
        menu.Add(exit);

        tray_.Menu = menu;
        string tip = string.Join("\n", tooltip);
        tray_.ToolTipText = tip.Length > 120 ? tip[..120] : tip;
    }

    private void TogglePause()
    {
        var s = settings_.Clone();
        s.Paused = !s.Paused;
        ApplySettings(s);
        ToastWindow.Show(s.Paused ? L.T("Automatik pausiert", "Automatic switching paused") : L.T("Automatik aktiv", "Automatic switching active"));
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Language and light/dark theme; both can change at runtime.</summary>
    public static void ApplyAppearance(AppSettings s)
    {
        L.Set(s.Language);
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = s.Theme switch
            {
                "light" => Avalonia.Styling.ThemeVariant.Light,
                "dark" => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
    }

    private void ApplySettings(AppSettings s)
    {
        bool languageChanged = s.Language != settings_.Language;
        settings_ = s;
        ApplyAppearance(s);
        if (languageChanged) settingsWindow_?.Rebuild();
        try { s.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"Saving settings failed: {e.Message}"); }
        engine_.UpdateSettings(s);
        Autostart.Apply(s.StartWithWindows);
        RebuildMenu();
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
            if (k.Name != d.Name || k.IsEizo != d.IsEizo || k.SupportsPower != d.SupportsPower || !k.Modes.SequenceEqual(modes))
            {
                (k.Name, k.IsEizo, k.SupportsPower, k.Modes) = (d.Name, d.IsEizo, d.SupportsPower, modes);
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
        settingsWindow_?.DisplaysChanged();
        if (firstRun_ && !welcomed_) { welcomed_ = true; ShowSettings(); }
    }
    private bool welcomed_;

    private void ShowSettings()
    {
        if (settingsWindow_ != null) { settingsWindow_.Activate(); return; }
        settingsWindow_ = new SettingsWindow(settings_, displays_, engine_.Games, this);
        settingsWindow_.Saved += s =>
        {
            // keep what changed in the background meanwhile
            s.Displays = settings_.Displays;
            s.Paused = settings_.Paused;
            ApplySettings(s);
        };
        settingsWindow_.Closed += (_, _) => settingsWindow_ = null;
        settingsWindow_.Show();
        settingsWindow_.Activate();
    }

    private void ListenForShowRequests()
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSettingsEvent);
        var t = new Thread(() =>
        {
            while (ev.WaitOne()) Dispatcher.UIThread.Post(ShowSettings);
        }) { IsBackground = true, Name = "ShowSettingsListener" };
        t.Start();
    }

    public void RefreshDisplays() => engine_.RequestRefresh();

    // ---------------------------------------------------------------- updates

    public async Task CheckForUpdatesAsync(bool interactive)
    {
        try
        {
            var update = await Updater.CheckAsync();
            if (update == null)
            {
                if (interactive) ToastWindow.Show(L.T("EizoSwitcher {0} ist aktuell.", "EizoSwitcher {0} is up to date.", Updater.Current));
                return;
            }
            pendingUpdate_ = update;
            RebuildMenu();
            if (settings_.AutoInstallUpdates || interactive) await InstallUpdateAsync(update);
            else ToastWindow.Show(L.T("Version {0} ist verfügbar – Installation über das Tray-Menü.",
                                      "Version {0} is available – install it from the tray menu.", update.Version));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Write($"Update check failed: {e.Message}");
            if (interactive) ToastWindow.Show(L.T("Update-Prüfung fehlgeschlagen: ", "Update check failed: ") + e.Message);
        }
    }

    private async Task InstallUpdateAsync(UpdateInfo update)
    {
        if (updating_) return;
        updating_ = true;
        ToastWindow.Show(L.T("Update auf {0} wird installiert …", "Installing update {0} …", update.Version));
        try
        {
            await Task.Run(() => Updater.InstallAsync(update));
            Log.Write($"Update {update.Version} installed, restarting");
            Exit();
        }
        catch (Exception e)
        {
            Log.Write($"Update failed: {e}");
            ToastWindow.Show(L.T("Update fehlgeschlagen: ", "Update failed: ") + e.Message);
            if (e is UnauthorizedAccessException) Shell.Open(update.PageUrl); // e.g. installed under Program Files
        }
        finally { updating_ = false; }
    }

    // ---------------------------------------------------------------- IBlackout

    public void Show(DisplayInfo display) => Dispatcher.UIThread.Post(() =>
    {
        if (overlays_.ContainsKey(display.Id) || display.Width == 0) return;
        var w = new OverlayWindow(display);
        w.Closed += (_, _) => { overlays_.Remove(display.Id); RebuildMenu(); };
        overlays_[display.Id] = w;
        w.Show();
        RebuildMenu();
    });

    public void Hide(string displayId) => Dispatcher.UIThread.Post(() =>
    {
        if (overlays_.TryGetValue(displayId, out var w)) w.Close();
    });

    // ---------------------------------------------------------------- exit

    public void Exit()
    {
        updateTimer_.Stop();
        engine_.Dispose();
        foreach (var w in overlays_.Values.ToList()) w.Close();
        tray_.IsVisible = false;
        displays_.Dispose();
        Log.Write("EizoSwitcher stopped");
        desktop_.Shutdown();
    }
}
