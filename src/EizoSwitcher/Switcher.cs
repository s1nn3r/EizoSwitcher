using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EizoSwitcher;

internal sealed class Rule
{
    public string Name { get; set; } = "";
    /// <summary>Process names without ".exe", case-insensitive.</summary>
    public List<string> Processes { get; set; } = [];
    /// <summary>Wildcard patterns on the full executable path, e.g. "*\steamapps\common\*".</summary>
    public List<string> Paths { get; set; } = [];
    /// <summary>Monitor (model, serial or "*") -> mode name or index.</summary>
    public Dictionary<string, string> Modes { get; set; } = [];
}

internal sealed class Config
{
    public double PollSeconds { get; set; } = 2;
    public Dictionary<string, string> Default { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];

    public static Config Load(string path) =>
        JsonSerializer.Deserialize<Config>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
        ?? throw new InvalidDataException("Leere Konfiguration");
}

internal sealed class Switcher(string configPath, Action<string> log)
{
    private readonly Dictionary<string, string> applied_ = []; // serial -> mode spec last applied by us
    private readonly Dictionary<string, List<string>> modeNames_ = []; // serial -> names by index
    private DateTime configStamp_;
    private Config config_ = new();

    public void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception e) { log($"Fehler: {e.Message}"); }
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(0.5, config_.PollSeconds)));
        }
    }

    private void Tick()
    {
        var stamp = File.GetLastWriteTimeUtc(configPath);
        if (stamp != configStamp_)
        {
            config_ = Config.Load(configPath);
            configStamp_ = stamp;
            applied_.Clear(); // re-evaluate everything after config edits
            log($"Konfiguration geladen ({config_.Rules.Count} Regeln)");
        }

        var active = ActiveRules();
        using var devices = new DeviceList(EizoHidDevice.Enumerate());
        foreach (var d in devices.Items)
        {
            var (serial, model) = Eizo.Identity(d);
            if (serial == "") continue;

            var (spec, source) = Resolve(active, model, serial);
            if (spec == null || (applied_.TryGetValue(serial, out var prev) && prev == spec)) continue;

            int? index = ResolveIndex(d, serial, spec);
            if (index == null) { log($"{model}: Modus \"{spec}\" unbekannt (verfügbar: {string.Join(", ", Names(d, serial))})"); applied_[serial] = spec; continue; }

            if (Eizo.GetMode(d) != index && !Eizo.SetMode(d, index.Value))
            {
                log($"{model}: Umschalten auf \"{spec}\" fehlgeschlagen, neuer Versuch im nächsten Zyklus");
                continue;
            }
            applied_[serial] = spec;
            log($"{model} ({serial}) -> {spec} [{index}]  ({source})");
        }
    }

    private (string? Spec, string Source) Resolve(List<Rule> active, string model, string serial)
    {
        foreach (var r in active)
            if (Lookup(r.Modes, model, serial) is { } s) return (s, $"Regel \"{r.Name}\"");
        return (Lookup(config_.Default, model, serial), "Standard");
    }

    private static string? Lookup(Dictionary<string, string> map, string model, string serial)
    {
        foreach (var key in new[] { serial, model, "*" })
            foreach (var (k, v) in map)
                if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    private int? ResolveIndex(EizoHidDevice d, string serial, string spec)
    {
        if (int.TryParse(spec, out int i)) return i;
        var names = Names(d, serial);
        int idx = names.FindIndex(n => n.Equals(spec, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx : null;
    }

    private List<string> Names(EizoHidDevice d, string serial)
    {
        if (modeNames_.TryGetValue(serial, out var cached)) return cached;
        var names = new List<string>();
        // The monitor clamps out-of-range indices, so the list ends at the first repeated name.
        for (int i = 0; i < 32; i++)
        {
            var n = Eizo.GetModeName(d, i);
            if (string.IsNullOrEmpty(n) || (names.Count > 0 && names[^1] == n)) break;
            names.Add(n);
        }
        if (names.Count > 0) modeNames_[serial] = names;
        return names;
    }

    private List<Rule> ActiveRules()
    {
        var procs = Process.GetProcesses();
        var names = new HashSet<string>(procs.Select(p => p.ProcessName), StringComparer.OrdinalIgnoreCase);
        List<string>? paths = null;
        if (config_.Rules.Any(r => r.Paths.Count > 0))
            paths = procs.Select(p => ImagePath(p.Id)).OfType<string>().ToList();
        foreach (var p in procs) p.Dispose();

        return config_.Rules.Where(r =>
            r.Processes.Any(names.Contains) ||
            (paths != null && r.Paths.Any(pat => paths.Any(path => FileSystemName.MatchesSimpleExpression(pat, path)))))
            .ToList();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private sealed class DeviceList(List<EizoHidDevice> items) : IDisposable
    {
        public List<EizoHidDevice> Items { get; } = items;
        public void Dispose() { foreach (var d in Items) d.Dispose(); }
    }
}
