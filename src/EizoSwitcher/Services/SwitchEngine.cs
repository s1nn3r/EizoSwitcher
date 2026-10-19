using System.Runtime.Versioning;

namespace EizoSwitcher.Services;

/// <summary>Shows/hides the black overlay on a display (implemented by the UI layer).</summary>
public interface IBlackout
{
    void Show(DisplayInfo display);
    void Hide(string displayId);
}

/// <summary>
/// Decides per display what should be active and applies it:
/// first matching program rule (top to bottom) &gt; running game &gt; default.
/// Only switches when the desired action changes, so manual changes on the monitor are kept until then.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SwitchEngine : IDisposable
{
    private readonly DisplayService displays_;
    private readonly IBlackout blackout_;
    private readonly ProcessWatcher watcher_ = new();
    private readonly Dictionary<string, string> applied_ = [];          // display id -> action last applied
    private readonly HashSet<string> blackShown_ = [];
    private readonly Dictionary<string, DisplayInfo> poweredOff_ = [];
    private readonly object lock_ = new();
    private readonly List<string> pending_ = [];
    private System.Threading.Timer? timer_;
    private volatile AppSettings settings_;
    private volatile bool invalidate_, refreshRequested_ = true;
    private DateTime nextTopologyCheck_;

    public SwitchEngine(DisplayService displays, IBlackout blackout, AppSettings settings)
    {
        displays_ = displays;
        blackout_ = blackout;
        settings_ = settings;
    }

    public GameDetector Games { get; } = new();

    /// <summary>Display id -> why its current action is active (rule name, game, default).</summary>
    public IReadOnlyDictionary<string, string> Reasons { get; private set; } = new Dictionary<string, string>();
    public string? ActiveGame { get; private set; }

    /// <summary>Human readable "Display: action (reason)" after a switch.</summary>
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
                if (refreshRequested_ || (now >= nextTopologyCheck_ && displays_.TopologyChanged()))
                {
                    refreshRequested_ = false;
                    displays_.Refresh();
                    invalidate_ = true;
                    DisplaysRefreshed?.Invoke();
                }
                if (now >= nextTopologyCheck_) nextTopologyCheck_ = now.AddSeconds(10);
                if (!displays_.PollEizo()) refreshRequested_ = true;
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

        bool needPaths = s.Rules.Any(r => r.Enabled && r.MatchPath)
            || (s.Games.Enabled && (s.Games.UseWindowsGameList || s.Games.UseLauncherLibraries || s.Games.ExtraFolders.Count > 0));
        var snap = watcher_.Take(needPaths);
        var active = s.Rules.Where(r => r.Enabled && Matches(r, snap)).ToList();
        string? game = s.Games.Enabled ? Games.Detect(snap, s.Games) : null;

        var reasons = new Dictionary<string, string>();
        foreach (var d in displays_.Displays)
        {
            string action = "", reason = "";
            foreach (var r in active)
                if (r.Actions.TryGetValue(d.Id, out var a) && a.Length > 0) { action = a; reason = r.Name; break; }
            if (action.Length == 0 && game != null && s.Games.Actions.TryGetValue(d.Id, out var g) && g.Length > 0)
                (action, reason) = (g, L.T("Spiel: ", "Game: ") + game);
            if (action.Length == 0 && s.Defaults.TryGetValue(d.Id, out var def) && def.Length > 0)
                (action, reason) = (def, L.T("Standard", "Default"));

            if (s.Paused) action = "";   // paused: no switching, black/off displays come back
            reasons[d.Id] = reason;

            if (applied_.TryGetValue(d.Id, out var prev) && prev == action) continue;
            if (Apply(d, action, reason)) applied_[d.Id] = action;
        }

        if (pending_.Count > 0) { Switched?.Invoke(string.Join(Environment.NewLine, pending_)); pending_.Clear(); }

        bool changed = game != ActiveGame || reasons.Count != Reasons.Count || reasons.Any(kv => Reasons.GetValueOrDefault(kv.Key) != kv.Value);
        ActiveGame = game;
        Reasons = reasons;
        if (changed) StateChanged?.Invoke();
    }

    private static bool Matches(ProcessRule r, ProcessSnapshot snap) =>
        r.MatchPath && !string.IsNullOrEmpty(r.ExePath)
            ? snap.Processes.Any(p => string.Equals(p.Path, r.ExePath, StringComparison.OrdinalIgnoreCase))
            : snap.Keys.Contains(r.ProcessKey);

    /// <returns>false = temporary failure, retry on the next tick.</returns>
    private bool Apply(DisplayInfo d, string action, string reason)
    {
        // leave special states first
        bool wasSpecial = false;
        if (action != DisplayAction.Black && blackShown_.Remove(d.Id)) { blackout_.Hide(d.Id); wasSpecial = true; }
        if (action != DisplayAction.Off && poweredOff_.Remove(d.Id, out var off)) { displays_.SetPower(off, true); wasSpecial = true; }

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
                if (d.SupportsPower && displays_.SetPower(d, false)) poweredOff_[d.Id] = d;
                else { blackout_.Show(d); blackShown_.Add(d.Id); } // no DDC/CI: black instead
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
                if (d.Eizo.CurrentMode == index) return true;
                if (!displays_.SetMode(d, index))
                {
                    Log.Write($"{d.Name}: switching to \"{action}\" failed, retrying");
                    return false;
                }
                Notify(d, action, reason);
                return true;
        }
    }

    private void Notify(DisplayInfo d, string action, string reason)
    {
        string msg = $"{d.Name}: {DisplayAction.Text(action)}" + (reason.Length > 0 ? $"  ·  {reason}" : "");
        Log.Write(msg);
        pending_.Add(msg);
    }

    /// <summary>Restores black/off displays (on exit or pause).</summary>
    public void RestoreAll()
    {
        lock (lock_)
        {
            foreach (var id in blackShown_) blackout_.Hide(id);
            blackShown_.Clear();
            foreach (var d in poweredOff_.Values) displays_.SetPower(d, true);
            poweredOff_.Clear();
            applied_.Clear();
        }
    }

    public void Dispose()
    {
        timer_?.Dispose();
        RestoreAll();
    }
}
