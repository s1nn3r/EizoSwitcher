using System.Runtime.Versioning;

namespace EizoSwitcher.Services;

/// <summary>Shows/hides the black overlay on a display (implemented by the UI layer).</summary>
public interface IBlackout
{
    void Show(DisplayInfo display);
    void Hide(string displayId);
}

/// <summary>
/// Decides per display what should be active and applies it. The program in the <b>foreground</b> counts:
/// its rule applies; if it is a game, the game settings apply; otherwise the defaults. With "keep until exit" the
/// color modes of the last program with its own rule (or game) stay while it runs in the background; black/off
/// only apply while it is in front. Clicking taskbar, desktop or start menu keeps the current state. Reacts immediately to foreground changes (WinEvent hook) and polls
/// as a fallback. Only switches when the desired action changes, so manual changes are kept until then.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SwitchEngine : IDisposable
{
    private readonly DisplayService displays_;
    private readonly IBlackout blackout_;
    private readonly ForegroundWatcher foreground_ = new();
    private readonly Dictionary<string, string> applied_ = [];          // display id -> action last applied
    private readonly HashSet<string> blackShown_ = [];
    private readonly Dictionary<string, (DisplayInfo Display, string Method)> off_ = [];
    private readonly List<string> pending_ = [];
    private readonly object lock_ = new();
    private System.Threading.Timer? timer_;
    private volatile AppSettings settings_;
    private volatile bool invalidate_, refreshRequested_ = true;
    private DateTime nextTopologyCheck_;
    private int pollFailures_;
    private ForegroundApp? app_;
    private ForegroundApp? owner_;   // last program with its own rule or a game – keeps its color modes while running
    private string? ownerGame_;

    public SwitchEngine(DisplayService displays, IBlackout blackout, AppSettings settings)
    {
        displays_ = displays;
        blackout_ = blackout;
        settings_ = settings;
        foreground_.Changed += () => timer_?.Change(40, Timeout.Infinite); // evaluate right away
    }

    public GameDetector Games { get; } = new();

    /// <summary>Display id -> why its current action is active (program, game, default).</summary>
    public IReadOnlyDictionary<string, string> Reasons { get; private set; } = new Dictionary<string, string>();
    public string? ActiveGame { get; private set; }
    /// <summary>Executable of <see cref="ActiveGame"/> ("Game.exe"), e.g. to exclude it.</summary>
    public string? ActiveGameProcess { get; private set; }
    /// <summary>The program that currently decides (foreground, shell surfaces ignored).</summary>
    public string? ActiveProgram { get; private set; }

    /// <summary>Human readable summary after switching.</summary>
    public event Action<string>? Switched;
    public event Action? StateChanged;
    /// <summary>Displays were re-enumerated (new monitor, different modes, ...).</summary>
    public event Action? DisplaysRefreshed;

    public void Start() => timer_ = new System.Threading.Timer(_ => Tick(), null, 0, Timeout.Infinite);

    public void UpdateSettings(AppSettings settings)
    {
        settings_ = settings;
        invalidate_ = true;
        timer_?.Change(50, Timeout.Infinite);
    }

    public void RequestRefresh()
    {
        refreshRequested_ = true;
        timer_?.Change(50, Timeout.Infinite);
    }

    private void Tick()
    {
        lock (lock_)
        {
            try
            {
                var now = DateTime.UtcNow;
                string? reason = refreshRequested_ ? "requested" : now >= nextTopologyCheck_ ? displays_.TopologyChange() : null;
                if (now >= nextTopologyCheck_) nextTopologyCheck_ = now.AddSeconds(10);
                if (reason != null)
                {
                    Log.Write($"Reading displays ({reason})");
                    refreshRequested_ = false;
                    displays_.Refresh();
                    invalidate_ = true;
                    DisplaysRefreshed?.Invoke();
                }

                // ColorNavigator talks to the monitor at the same time, so a single missed answer is normal
                if (displays_.PollEizo()) pollFailures_ = 0;
                else if (++pollFailures_ >= 5)
                {
                    pollFailures_ = 0;
                    refreshRequested_ = true;
                    Log.Write("EIZO monitor did not answer repeatedly – rescanning");
                }
                Evaluate();
            }
            catch (Exception e)
            {
                Log.Write($"Engine error: {e}");
            }
            finally
            {
                timer_?.Change(Math.Clamp(settings_.PollIntervalMs, 250, 60_000), Timeout.Infinite);
            }
        }
    }

    private void Evaluate()
    {
        var s = settings_;
        if (invalidate_) { invalidate_ = false; applied_.Clear(); }

        // shell surfaces (taskbar, desktop, start menu) are neutral: keep the program that was in front before
        var fg = ForegroundWatcher.Current();
        if (fg != null) app_ = fg;
        else if (app_ != null && !ForegroundWatcher.IsRunning(app_.Pid)) app_ = null;

        var rules = app_ == null ? [] : RulesFor(s, app_);
        string? game = app_ != null && s.Games.Enabled ? Games.Detect(app_, s.Games) : null;

        // a program with its own rule (or a game) takes over; it keeps its color modes until it exits
        if (rules.Count > 0 || game != null) (owner_, ownerGame_) = (app_, game);
        else if (owner_ != null && (!s.KeepModesUntilExit || !ForegroundWatcher.IsRunning(owner_.Pid))) (owner_, ownerGame_) = (null, null);
        bool background = owner_ != null && owner_.Pid != app_?.Pid;
        var ownerRules = background ? RulesFor(s, owner_!) : [];

        // displays we disconnected are no longer listed by Windows – keep evaluating them
        var all = displays_.Displays.ToList();
        foreach (var (d, _) in off_.Values) if (all.All(x => x.Id != d.Id)) all.Add(d);

        var reasons = new Dictionary<string, string>();
        bool offChanged = false;
        foreach (var d in all)
        {
            string action = "", reason = "";
            foreach (var r in rules)
                if (r.Actions.TryGetValue(d.Id, out var a) && a.Length > 0) { action = a; reason = r.Name; break; }
            if (action.Length == 0 && game != null && s.Games.Actions.TryGetValue(d.Id, out var g) && g.Length > 0)
                (action, reason) = (g, L.T("Spiel: ", "Game: ") + game);
            if (action.Length == 0 && background)
                (action, reason) = BackgroundMode(d, ownerRules, s);
            if (action.Length == 0 && s.Defaults.TryGetValue(d.Id, out var def) && def.Length > 0)
                (action, reason) = (def, L.T("Standard", "Default"));

            if (s.Paused) action = "";   // paused: no switching, black/off displays come back
            reasons[d.Id] = reason;

            if (applied_.TryGetValue(d.Id, out var prev) && prev == action) continue;
            if (Apply(d, action, reason, s, ref offChanged)) applied_[d.Id] = action;
        }

        if (offChanged) ApplyOff();

        if (pending_.Count > 0)
        {
            Switched?.Invoke(string.Join(Environment.NewLine, pending_));
            pending_.Clear();
        }

        bool changed = game != ActiveGame || app_?.Name != ActiveProgram || reasons.Count != Reasons.Count
                       || reasons.Any(kv => Reasons.GetValueOrDefault(kv.Key) != kv.Value);
        ActiveGame = game;
        ActiveGameProcess = game == null ? null : Path.GetFileName(app_?.Path) ?? app_?.Key;
        ActiveProgram = app_?.Name;
        Reasons = reasons;
        if (changed) StateChanged?.Invoke();
    }

    /// <summary>Color mode of the program in the background (black/off only apply in the foreground).</summary>
    private (string Action, string Reason) BackgroundMode(DisplayInfo d, List<ProcessRule> ownerRules, AppSettings s)
    {
        foreach (var r in ownerRules)
            if (r.Actions.TryGetValue(d.Id, out var a) && a.Length > 0)
                return DisplayAction.IsMode(a) ? (a, r.Name) : ("", "");
        if (ownerGame_ != null && s.Games.Actions.TryGetValue(d.Id, out var g) && DisplayAction.IsMode(g))
            return (g, L.T("Spiel: ", "Game: ") + ownerGame_);
        return ("", "");
    }

    /// <summary>
    /// Rules for a program, in priority order. A rule belongs to exactly the executable it was created for, so two
    /// versions of a program are two rules. By name only if the process path cannot be read (elevated processes),
    /// for rules without a path, and for rules whose program has moved (updates into a versioned folder, e.g. Store
    /// apps) – the latter only if no rule matches the path exactly.
    /// </summary>
    private static List<ProcessRule> RulesFor(AppSettings s, ForegroundApp app)
    {
        var byName = s.Rules.Where(r => r.Enabled && r.ProcessKey == app.Key).ToList();
        if (string.IsNullOrEmpty(app.Path)) return byName;
        var exact = byName.Where(r => string.Equals(r.ExePath, app.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact : [.. byName.Where(r => string.IsNullOrEmpty(r.ExePath) || !File.Exists(r.ExePath))];
    }

    /// <returns>false = temporary failure, retry on the next tick.</returns>
    private bool Apply(DisplayInfo d, string action, string reason, AppSettings s, ref bool offChanged)
    {
        // leave special states first
        bool wasSpecial = false;
        if (action != DisplayAction.Black && blackShown_.Remove(d.Id)) { blackout_.Hide(d.Id); wasSpecial = true; }
        if (action != DisplayAction.Off && off_.Remove(d.Id, out var off))
        {
            if (off.Method != OffMethod.Disconnect) Ddc.SetPower(off.Display.DeviceName, 1);
            else offChanged = true;
            wasSpecial = true;
        }

        switch (action)
        {
            case "":
                if (wasSpecial) Notify(d, L.T("normal", "normal"), reason);
                return true;

            case DisplayAction.Black:
                blackout_.Show(d);
                blackShown_.Add(d.Id);
                Notify(d, action, reason);
                return true;

            case DisplayAction.Off:
                if (off_.ContainsKey(d.Id)) return true; // already off (e.g. re-evaluated after the display vanished)
                string method = s.OffMethods.GetValueOrDefault(d.Id) ?? OffMethod.Disconnect;
                if (method == OffMethod.Disconnect && !d.CanTurnOff)
                {
                    // the primary display cannot be disconnected – blank it instead
                    blackout_.Show(d);
                    blackShown_.Add(d.Id);
                }
                else if (method == OffMethod.Disconnect)
                {
                    off_[d.Id] = (d, method);
                    offChanged = true;
                }
                else
                {
                    Ddc.SetPower(d.DeviceName, method == OffMethod.DdcOff ? 5u : 4u);
                    off_[d.Id] = (d, method);
                }
                Notify(d, action, reason);
                return true;

            default:
                if (d.Eizo == null) return true;
                int index = d.Eizo.IndexOf(action);
                if (index < 0)
                {
                    Log.Write($"{d.Name}: mode \"{action}\" does not exist (available: {string.Join(", ", d.Eizo.Modes)})");
                    return true;
                }
                if (d.Eizo.CurrentMode == index)
                {
                    displays_.ApplyProfile(d, index); // mode already right – make sure the profile is too
                    return true;
                }
                if (!displays_.SetMode(d, index))
                {
                    Log.Write($"{d.Name}: switching to \"{action}\" failed, retrying");
                    return false;
                }
                Notify(d, action, reason);
                return true;
        }
    }

    /// <summary>Disconnects exactly the displays that should be off via "disconnect".</summary>
    private void ApplyOff()
    {
        var names = off_.Values.Where(o => o.Method == OffMethod.Disconnect).Select(o => o.Display.DeviceName).ToHashSet();
        if (displays_.SetDisconnected(names)) return;

        // Windows refused: fall back to black overlays
        foreach (var (id, (d, method)) in off_.Where(o => o.Value.Method == OffMethod.Disconnect).ToList())
        {
            off_.Remove(id);
            blackout_.Show(d);
            blackShown_.Add(id);
        }
    }

    private void Notify(DisplayInfo d, string action, string reason)
    {
        string msg = $"{d.Name}: {DisplayAction.Text(action)}" + (reason.Length > 0 ? $"  ·  {reason}" : "");
        Log.Write(msg);
        pending_.Add(msg);
    }

    /// <summary>Restores black/off displays (on exit).</summary>
    public void RestoreAll()
    {
        lock (lock_)
        {
            foreach (var id in blackShown_) blackout_.Hide(id);
            blackShown_.Clear();
            foreach (var (d, method) in off_.Values.Where(o => o.Method != OffMethod.Disconnect)) Ddc.SetPower(d.DeviceName, 1);
            off_.Clear();
            DisplayConfig.Restore();
            applied_.Clear();
        }
    }

    public void Dispose()
    {
        timer_?.Dispose();
        foreground_.Dispose();
        RestoreAll();
    }
}
