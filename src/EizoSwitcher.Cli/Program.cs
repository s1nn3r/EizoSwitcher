using System.Text;
using System.Text.Json;
using EizoSwitcher;

Console.OutputEncoding = Encoding.UTF8;
var options = args.Where(a => a.StartsWith("--")).Select(a => a.ToLowerInvariant()).ToHashSet();
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
bool json = options.Contains("--json");
string cmd = positional.Length > 0 ? positional[0].ToLowerInvariant() : "list";

if (cmd is "help" or "-h" or "/?" || options.Contains("--help")) { Help(); return 0; }
if (options.Contains("--version")) { Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3)); return 0; }

using var service = new MonitorService();
service.Refresh();
var monitors = service.Monitors;

if (monitors.Count == 0 && cmd != "dump")
{
    Out.Error(L.T("Kein EIZO-Monitor gefunden. Ist das USB-Kabel zwischen PC und Monitor angeschlossen?",
                  "No EIZO monitor found. Is the USB cable between PC and monitor connected?"));
    return 1;
}

switch (cmd)
{
    case "list":
        if (json) { Out.Json(monitors.Select(m => new MonitorJson(m.Model, m.Serial, m.CurrentMode, m.CurrentModeName, null))); return 0; }
        Out.Line();
        Out.Table(
            [L.T("Modell", "Model"), L.T("Seriennummer", "Serial"), L.T("Aktueller Modus", "Current mode")],
            monitors.Select(m => new[] { m.Model, m.Serial, m.CurrentModeName }).ToList());
        Out.Line();
        return 0;

    case "modes":
    {
        var selected = positional.Length > 1 ? Select(positional[1]) : monitors;
        if (selected == null) return 1;
        if (json)
        {
            Out.Json(selected.Select(m => new MonitorJson(m.Model, m.Serial, m.CurrentMode, m.CurrentModeName, m.Modes)
                { Enabled = [.. Enumerable.Range(0, m.Modes.Count).Select(m.IsEnabled)] }));
            return 0;
        }
        foreach (var m in selected)
        {
            Out.Line();
            Out.Heading(m.Model, m.Serial);
            for (int i = 0; i < m.Modes.Count; i++)
            {
                bool current = i == m.CurrentMode, enabled = m.IsEnabled(i);
                ConsoleColor? color = current ? ConsoleColor.Green : enabled ? null : ConsoleColor.DarkGray;
                Out.Write(current ? "  ● " : "    ", color);
                Out.Write($"{i,2}  ", ConsoleColor.DarkGray);
                Out.Write(m.Modes[i].PadRight(16), color);
                if (current) Out.Write(L.T("aktuell", "current") + " ", ConsoleColor.DarkGray);
                if (!enabled) Out.Write(L.T("(am Monitor deaktiviert)", "(disabled on the monitor)"), ConsoleColor.DarkGray);
                Out.Line();
            }
        }
        Out.Line();
        return 0;
    }

    case "get":
    {
        var m = SelectOne(positional.ElementAtOrDefault(1));
        if (m == null) return 1;
        if (json) { Out.Json(new MonitorJson(m.Model, m.Serial, m.CurrentMode, m.CurrentModeName, null)); return 0; }
        Console.WriteLine(m.CurrentModeName);
        return 0;
    }

    case "set":
    {
        // set <mode>            (only one monitor connected)
        // set <monitor> <mode>
        string? target = positional.Length > 2 ? positional[1] : null;
        string? modeArg = positional.Length > 2 ? positional[2] : positional.ElementAtOrDefault(1);
        if (modeArg == null) { Help(); return 2; }
        var m = SelectOne(target);
        if (m == null) return 1;

        int index = int.TryParse(modeArg, out int n) && n >= 0 && n < m.Modes.Count ? n : m.IndexOf(modeArg);
        if (index < 0)
        {
            Out.Error(L.T("Modus \"{0}\" gibt es auf {1} nicht. Verfügbar: {2}",
                          "Mode \"{0}\" does not exist on {1}. Available: {2}", modeArg, m.Model, string.Join(", ", m.Modes)));
            return 1;
        }

        string before = m.CurrentModeName;
        bool ok = service.SetMode(m.Serial, index);
        var after = service.Monitors.First(x => x.Serial == m.Serial);
        if (json) { Out.Json(new MonitorJson(after.Model, after.Serial, after.CurrentMode, after.CurrentModeName, null)); return ok ? 0 : 1; }

        Out.Line();
        Out.Write($"  {m.Model}  ", ConsoleColor.White);
        Out.Write(before, ConsoleColor.DarkGray);
        Out.Write("  →  ");
        Out.Write(after.CurrentModeName, ok ? ConsoleColor.Green : ConsoleColor.Red);
        Out.Write(ok ? "  ✓" : "  ✗ " + L.T("fehlgeschlagen", "failed"), ok ? ConsoleColor.Green : ConsoleColor.Red);
        Out.Line();
        Out.Line();
        return ok ? 0 : 1;
    }

    case "raw":
    {
        // Diagnostics: raw <monitor> <page hex> <usage hex>  -> tunneled read
        var m = SelectOne(positional.ElementAtOrDefault(1));
        if (m == null || positional.Length < 4) return 2;
        ushort page = Convert.ToUInt16(positional[2], 16), usage = Convert.ToUInt16(positional[3], 16);
        foreach (var d in EizoHidDevice.Enumerate())
        {
            if (Eizo.Identity(d).Serial == m.Serial)
            {
                var r = Eizo.Get(d, page, usage);
                Console.WriteLine(r == null ? "<no response>" : $"{BitConverter.ToInt32(r, 0),8}   {Convert.ToHexString(r, 0, 16)}");
            }
            d.Dispose();
        }
        return 0;
    }

    case "dump":
    {
        // Diagnostics: raw HID feature reports (read only)
        var devices = EizoHidDevice.Enumerate();
        foreach (var d in devices)
        {
            var (serial, model) = Eizo.Identity(d);
            Out.Line();
            Out.Heading(model, $"{serial}  ·  PID 0x{d.ProductId:X4}");
            foreach (var c in d.FeatureValues)
            {
                string val = !d.TryGetValue(c, out uint v, out byte[] raw) ? "<read failed>"
                    : c.ReportCount > 1 ? Convert.ToHexString(raw, 0, Math.Min(raw.Length, 1 + c.ReportCount)).TrimEnd('0')
                    : v.ToString();
                Out.Write($"    0x{c.ReportId:X2}  ", ConsoleColor.DarkGray);
                Out.Write($"{c.UsagePage:X4}:{c.Usage:X4}  {c.BitSize,2}x{c.ReportCount,-4} ");
                Out.Write(val.Length > 64 ? val[..64] + "…" : val, ConsoleColor.Cyan);
                Out.Line();
            }
            d.Dispose();
        }
        Out.Line();
        return 0;
    }

    default:
        Out.Error(L.T("Unbekannter Befehl \"{0}\".", "Unknown command \"{0}\".", cmd));
        Help();
        return 2;
}

IReadOnlyList<MonitorState>? Select(string spec)
{
    var hits = monitors.Where(m => m.Model.Equals(spec, StringComparison.OrdinalIgnoreCase) || m.Serial == spec).ToList();
    if (hits.Count > 0) return hits;
    Out.Error(L.T("Monitor \"{0}\" nicht gefunden. Verbunden: {1}",
                  "Monitor \"{0}\" not found. Connected: {1}", spec, string.Join(", ", monitors.Select(m => m.Model))));
    return null;
}

MonitorState? SelectOne(string? spec)
{
    if (spec == null)
    {
        if (monitors.Count == 1) return monitors[0];
        Out.Error(L.T("Mehrere Monitore verbunden – bitte Modell oder Seriennummer angeben ({0}).",
                      "Several monitors connected – please specify model or serial ({0}).", string.Join(", ", monitors.Select(m => m.Model))));
        return null;
    }
    var hits = Select(spec);
    if (hits is { Count: > 1 })
    {
        Out.Error(L.T("Mehrere Monitore dieses Modells – bitte die Seriennummer angeben.", "Several monitors of this model – please specify the serial number."));
        return null;
    }
    return hits?[0];
}

static void Help()
{
    Out.Line();
    Out.Write("  eizoswitch", ConsoleColor.White);
    Out.Write(L.T("  –  Farbmodus von EIZO-Monitoren per USB umschalten", "  –  switch the color mode of EIZO monitors over USB"), ConsoleColor.DarkGray);
    Out.Line();
    Out.Line();
    (string, string)[] rows =
    [
        ("list", L.T("Monitore und aktueller Modus", "Monitors and current mode")),
        ("modes [monitor]", L.T("Alle Farbmodi anzeigen", "Show all color modes")),
        ("get [monitor]", L.T("Nur den aktuellen Modus ausgeben", "Print the current mode only")),
        ("set [monitor] <mode>", L.T("Modus setzen (Name oder Nummer)", "Switch mode (name or number)")),
        ("dump", L.T("Diagnose: rohe HID-Reports", "Diagnostics: raw HID reports")),
    ];
    foreach (var (c, d) in rows)
    {
        Out.Write("    " + c.PadRight(24), ConsoleColor.Cyan);
        Out.Write(d);
        Out.Line();
    }
    Out.Line();
    Out.Write(L.T("  [monitor] = Modell oder Seriennummer, entfällt bei nur einem Monitor.  --json für Skripte.",
                  "  [monitor] = model or serial, optional with a single monitor.  --json for scripts."), ConsoleColor.DarkGray);
    Out.Line();
    Out.Write(L.T("  Beispiel:  eizoswitch set CG279X BT.709", "  Example:  eizoswitch set CG279X BT.709"), ConsoleColor.DarkGray);
    Out.Line();
    Out.Line();
}

internal sealed record MonitorJson(string Model, string Serial, int ModeIndex, string Mode, IReadOnlyList<string>? Modes)
{
    public IReadOnlyList<bool>? Enabled { get; init; }
}

internal static class Out
{
    private static readonly bool color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null;

    public static void Write(string text, ConsoleColor? fg = null)
    {
        if (color && fg != null) Console.ForegroundColor = fg.Value;
        Console.Write(text);
        if (color && fg != null) Console.ResetColor();
    }

    public static void Line() => Console.WriteLine();

    public static void Heading(string title, string sub)
    {
        Write("  " + title, ConsoleColor.White);
        Write("  ·  " + sub, ConsoleColor.DarkGray);
        Line();
    }

    public static void Error(string message)
    {
        if (color) Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine();
        Console.Error.WriteLine("  " + message);
        Console.Error.WriteLine();
        if (color) Console.ResetColor();
    }

    public static void Table(string[] header, List<string[]> rows)
    {
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        for (int i = 0; i < header.Length; i++) Write("  " + header[i].PadRight(widths[i] + 2), ConsoleColor.DarkGray);
        Line();
        for (int i = 0; i < header.Length; i++) Write("  " + new string('─', widths[i]) + "  ", ConsoleColor.DarkGray);
        Line();
        foreach (var r in rows)
        {
            for (int i = 0; i < r.Length; i++) Write("  " + r[i].PadRight(widths[i] + 2), i == 0 ? ConsoleColor.White : i == r.Length - 1 ? ConsoleColor.Green : null);
            Line();
        }
    }

    public static void Json<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, typeof(T), CliJson.Default));
}
