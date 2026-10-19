using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EizoSwitcher.Services;
using Forms = System.Windows.Forms;
using MenuItem = System.Windows.Controls.MenuItem;

namespace EizoSwitcher.UI;

/// <summary>Owns the tray icon, the engine and all windows. Lives on the UI thread.</summary>
internal sealed class TrayApp : IBlackout
{
    private readonly Application app_;
    private readonly Dispatcher ui_;
    private readonly Forms.NotifyIcon tray_;
    private readonly ContextMenu menu_ = new();
    private readonly Window menuHost_;
    private readonly DisplayService displays_ = new();
    private readonly SwitchEngine engine_;
    private readonly Dictionary<string, OverlayWindow> overlays_ = [];
    private readonly DispatcherTimer updateTimer_ = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly bool firstRun_;
    private AppSettings settings_;
    private SettingsWindow? settingsWindow_;
    private UpdateInfo? pendingUpdate_;
    private bool updating_, welcomed_;

    public TrayApp(Application app, AppSettings settings, bool justUpdated)
    {
        app_ = app;
        ui_ = app.Dispatcher;
        firstRun_ = !File.Exists(AppSettings.FilePath);
        settings_ = settings;
        Log.Write($"EizoSwitcher {Updater.Current} started");

        displays_.ProfileFor = (d, index) => ColorProfiles.For(settings_, d, index);
        engine_ = new SwitchEngine(displays_, this, settings_);
        engine_.Switched += msg => Ui(() => { if (settings_.ShowNotifications) Notify(msg); });
        engine_.StateChanged += () => Ui(UpdateTooltip);
        engine_.DisplaysRefreshed += () => Ui(RememberDisplays);

        // invisible helper window: the context menu needs a foreground window to close when clicking elsewhere
        menuHost_ = new Window { Width = 1, Height = 1, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, AllowsTransparency = true, Background = Brushes.Transparent };
        // tool window: otherwise Task Manager lists EizoSwitcher under "Apps" instead of background processes
        menuHost_.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(menuHost_).Handle;
            OverlayWindow.SetWindowLong(hwnd, -20, OverlayWindow.GetWindowLong(hwnd, -20) | 0x00000080 /* WS_EX_TOOLWINDOW */);
        };
        menuHost_.Show();

        tray_ = new Forms.NotifyIcon { Icon = UI.Ui.TrayIcon(Forms.SystemInformation.SmallIconSize), Text = "EizoSwitcher", Visible = true };
        tray_.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right) ShowMenu();
            else if (e.Button == Forms.MouseButtons.Left) ShowSettings();
        };
        tray_.BalloonTipClicked += async (_, _) => { if (pendingUpdate_ != null && ConfirmUpdate(pendingUpdate_)) await InstallUpdateAsync(pendingUpdate_); };

        Autostart.Apply(settings_.StartWithWindows);
        engine_.Start();
        ListenForShowRequests();

        updateTimer_.Tick += async (_, _) =>
        {
            updateTimer_.Interval = TimeSpan.FromHours(6);
            if (settings_.AutoCheckUpdates) await CheckForUpdatesAsync(interactive: false);
        };
        updateTimer_.Start();

        if (justUpdated) Notify(L.T("Aktualisiert auf Version {0}", "Updated to version {0}", Updater.Current));
    }

    private void Ui(Action a)
    {
        if (ui_.HasShutdownStarted) return;
        if (ui_.CheckAccess()) a(); else ui_.BeginInvoke(a);
    }

    private void Notify(string message) => tray_.ShowBalloonTip(3000, "EizoSwitcher", message, Forms.ToolTipIcon.None);

    // ---------------------------------------------------------------- tray menu

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private void ShowMenu()
    {
        BuildMenu();
        SetForegroundWindow(new WindowInteropHelper(menuHost_).Handle);
        menu_.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu_.IsOpen = true;
    }

    private void BuildMenu() => TrayMenu.Fill(menu_,
        new TrayMenuState(displays_.Displays, State, engine_.Reasons, engine_.ActiveGame, engine_.ActiveProgram, settings_.Paused, pendingUpdate_),
        new TrayMenuActions(
            SetMode: (d, index) => Task.Run(() => displays_.SetMode(d, index)).ContinueWith(_ => Ui(UpdateTooltip)),
            Blank: Show,
            TogglePause: TogglePause,
            ShowSettings: ShowSettings,
            CheckForUpdates: () => _ = CheckForUpdatesAsync(true),
            InstallUpdate: () => { if (pendingUpdate_ != null) _ = InstallUpdateAsync(pendingUpdate_); },
            Exit: ExitApp));

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
        bool languageChanged = s.Language != settings_.Language;
        bool themeChanged = s.Theme != settings_.Theme;
        settings_ = s;
        try { s.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"Saving settings failed: {e.Message}"); }
        engine_.UpdateSettings(s);
        Autostart.Apply(s.StartWithWindows);
        UpdateTooltip();

        if (themeChanged) Theme.Apply(s.Theme);
        if (languageChanged)
        {
            L.Set(s.Language);
            settingsWindow_?.Rebuild();
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
            var enabled = d.Eizo?.Enabled.ToList() ?? [];
            if (k.Name != d.Name || k.IsEizo != d.IsEizo || k.CanTurnOff != d.CanTurnOff || !k.Modes.SequenceEqual(modes) || !k.ModesEnabled.SequenceEqual(enabled))
            {
                (k.Name, k.IsEizo, k.CanTurnOff, k.Modes, k.ModesEnabled) = (d.Name, d.IsEizo, d.CanTurnOff, modes, enabled);
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
        settingsWindow_?.DisplaysChanged();
        if (firstRun_ && !welcomed_) { welcomed_ = true; ShowSettings(); }
    }

    private void ShowSettings()
    {
        if (settingsWindow_ != null)
        {
            if (settingsWindow_.WindowState == WindowState.Minimized) settingsWindow_.WindowState = WindowState.Normal;
            settingsWindow_.Activate();
            return;
        }
        var w = new SettingsWindow(settings_, displays_, engine_.Games, this);
        w.Saved += s =>
        {
            // keep what changed in the background meanwhile
            s.Displays = settings_.Displays;
            s.Paused = settings_.Paused;
            ApplySettings(s);
        };
        w.Closed += (_, _) => { if (settingsWindow_ == w) settingsWindow_ = null; };
        settingsWindow_ = w;
        w.Show();
        w.Activate();
    }

    private void ListenForShowRequests()
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSettingsEvent);
        new Thread(() => { while (ev.WaitOne()) Ui(ShowSettings); }) { IsBackground = true, Name = "ShowSettingsListener" }.Start();
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
                if (interactive) Notify(L.T("EizoSwitcher {0} ist aktuell.", "EizoSwitcher {0} is up to date.", Updater.Current));
                return;
            }
            pendingUpdate_ = update;
            if (interactive)
            {
                if (ConfirmUpdate(update)) await InstallUpdateAsync(update);
            }
            else if (settings_.AutoInstallUpdates) await InstallUpdateAsync(update);
            else Notify(L.T("Version {0} ist verfügbar – zum Installieren hier oder im Tray-Menü klicken.",
                            "Version {0} is available – click here or use the tray menu to install it.", update.Version));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Write($"Update check failed: {e.Message}");
            if (interactive) Notify(L.T("Update-Prüfung fehlgeschlagen: ", "Update check failed: ") + e.Message);
        }
    }

    private static bool ConfirmUpdate(UpdateInfo update) => ConfirmWindow.Ask(
        L.T("Update verfügbar", "Update available"),
        L.T("EizoSwitcher {0} ist verfügbar", "EizoSwitcher {0} is available", update.Version),
        L.T("Installiert ist Version {0}. Jetzt aktualisieren? EizoSwitcher startet danach von selbst neu.",
            "You have version {0}. Update now? EizoSwitcher restarts by itself afterwards.", Updater.Current),
        ConfirmWindow.PlainText(update.Notes),
        L.T("Jetzt aktualisieren", "Update now"), L.T("Später", "Later"),
        (L.T("Auf GitHub ansehen", "View on GitHub"), () => Shell.Open(update.PageUrl)));

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
        var w = new OverlayWindow(display);
        w.Closed += (_, _) => { overlays_.Remove(display.Id); UpdateTooltip(); };
        overlays_[display.Id] = w;
        w.Show();
        UpdateTooltip();
    });

    public void Hide(string displayId) => Ui(() =>
    {
        if (overlays_.TryGetValue(displayId, out var w)) w.Close();
    });

    // ---------------------------------------------------------------- exit

    private void ExitApp()
    {
        updateTimer_.Stop();
        engine_.Dispose();
        foreach (var w in overlays_.Values.ToList()) w.Close();
        tray_.Visible = false;
        tray_.Dispose();
        displays_.Dispose();
        Log.Write("EizoSwitcher stopped");
        app_.Shutdown();
    }
}

/// <summary>Black borderless window that blanks one display (works with every monitor). Click hides it.</summary>
internal sealed class OverlayWindow : Window
{
    private readonly DisplayInfo display_;

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] internal static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public OverlayWindow(DisplayInfo d)
    {
        display_ = d;
        Title = "EizoSwitcher";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Cursor = System.Windows.Input.Cursors.Hand;

        var hint = new System.Windows.Controls.TextBlock
        {
            Text = "EizoSwitcher\n" + L.T("Bildschirm abgedunkelt · Klicken zum Einblenden", "Display blanked · click to show"),
            Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 90)), FontSize = 16, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Content = new Grid { Background = Brushes.Black, Children = { hint } };
        MouseDown += (_, _) => Close();
        Loaded += (_, _) => Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => hint.Text = ""));

        // position in physical pixels – independent of the per-monitor DPI scaling
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */);
            SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, display_.X, display_.Y, display_.Width, display_.Height, 0x0010 /* SWP_NOACTIVATE */);
        };
    }
}
