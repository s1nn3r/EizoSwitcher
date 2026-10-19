namespace EizoSwitcher;

/// <summary>Snapshot of one connected EIZO monitor.</summary>
public sealed record MonitorState(string Serial, string Model, IReadOnlyList<string> Modes, int CurrentMode)
{
    public string CurrentModeName => CurrentMode >= 0 && CurrentMode < Modes.Count ? Modes[CurrentMode] : $"#{CurrentMode}";

    /// <summary>Per mode: enabled on the monitor (offered when cycling through modes). Empty = unknown, all enabled.</summary>
    public IReadOnlyList<bool> Enabled { get; init; } = [];

    public bool IsEnabled(int index) => index >= Enabled.Count || Enabled[index];

    public int IndexOf(string mode)
    {
        for (int i = 0; i < Modes.Count; i++)
            if (Modes[i].Equals(mode, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < Modes.Count; i++)
            if (Eizo.SameModeName(Modes[i], mode)) return i;
        return -1;
    }
}

/// <summary>Thread-safe access to all connected EIZO monitors; HID handles stay open between calls.</summary>
public sealed class MonitorService : IDisposable
{
    private readonly object lock_ = new();
    private readonly List<(EizoHidDevice Device, string Serial)> devices_ = [];
    private Dictionary<string, MonitorState> states_ = [];

    /// <summary>Raised (on the calling thread) after monitors or their current modes changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<MonitorState> Monitors
    {
        get { lock (lock_) return [.. states_.Values.OrderBy(m => m.Model).ThenBy(m => m.Serial)]; }
    }

    /// <summary>Re-enumerates the USB devices and reads model, serial, mode names and current mode.</summary>
    public void Refresh()
    {
        lock (lock_)
        {
            CloseDevices();
            var states = new Dictionary<string, MonitorState>();
            foreach (var d in EizoHidDevice.Enumerate())
            {
                try
                {
                    var (serial, model) = Eizo.Identity(d);
                    if (serial.Length == 0 || states.ContainsKey(serial)) { d.Dispose(); continue; }
                    var modes = Eizo.GetModeNames(d);
                    states[serial] = new MonitorState(serial, model, modes, Eizo.GetMode(d) ?? -1)
                    {
                        Enabled = Eizo.GetModesEnabled(d, modes.Count) ?? [],
                    };
                    devices_.Add((d, serial));
                }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
                {
                    Log.Write($"Monitor not readable: {e.Message}");
                    d.Dispose();
                }
            }
            states_ = states;
        }
        Changed?.Invoke();
    }

    private int polls_;

    /// <summary>Re-reads the current mode of every monitor (and now and then which modes are enabled).
    /// Returns false if a device stopped answering.</summary>
    public bool PollCurrentModes()
    {
        bool changed = false, ok = true;
        lock (lock_)
        {
            bool readEnabled = ++polls_ % 10 == 0; // enabling modes in the monitor menu is rare
            foreach (var (d, serial) in devices_)
            {
                int? cur = TryGetMode(d);
                if (cur == null) { ok = false; continue; }
                var state = states_[serial];
                if (state.CurrentMode != cur)
                {
                    state = state with { CurrentMode = cur.Value };
                    changed = true;
                }
                if (readEnabled)
                {
                    bool[]? enabled = null;
                    try { enabled = Eizo.GetModesEnabled(d, state.Modes.Count); } catch (IOException) { }
                    if (enabled != null && !enabled.SequenceEqual(state.Enabled))
                    {
                        state = state with { Enabled = enabled };
                        changed = true;
                    }
                }
                states_[serial] = state;
            }
        }
        if (changed) Changed?.Invoke();
        return ok;
    }

    /// <summary>Switches the color mode and waits until the monitor reports it.</summary>
    public bool SetMode(string serial, int index)
    {
        lock (lock_)
        {
            var device = devices_.FirstOrDefault(x => x.Serial == serial).Device;
            if (device == null) return false;
            try
            {
                if (!Eizo.SetMode(device, index)) return false;
            }
            catch (IOException) { return false; }

            int? cur = null;
            for (int i = 0; i < 15 && cur != index; i++)
            {
                Thread.Sleep(150);
                cur = TryGetMode(device);
            }
            states_[serial] = states_[serial] with { CurrentMode = cur ?? index };
        }
        Changed?.Invoke();
        return true;
    }

    private static int? TryGetMode(EizoHidDevice d)
    {
        try { return Eizo.GetMode(d); }
        catch (IOException) { return null; }
    }

    private void CloseDevices()
    {
        foreach (var (d, _) in devices_) d.Dispose();
        devices_.Clear();
    }

    public void Dispose()
    {
        lock (lock_) CloseDevices();
    }
}
