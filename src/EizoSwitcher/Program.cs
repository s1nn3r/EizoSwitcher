using System.Runtime.InteropServices;
using EizoSwitcher;

// WinExe: no window when started from Autostart; attach to the terminal when launched from one.
AttachConsole(-1);
Console.OutputEncoding = System.Text.Encoding.UTF8;

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
string baseDir = AppContext.BaseDirectory;

if (cmd == "run")
{
    string configPath = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(baseDir, "config.json");
    string logPath = Path.Combine(baseDir, "EizoSwitcher.log");
    using var single = new Mutex(true, @"Local\EizoSwitcher", out bool first);
    if (!first) { Console.Error.WriteLine("Läuft bereits."); return 1; }

    void Log(string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}";
        Console.WriteLine(line);
        try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    Log($"Gestartet, Konfiguration: {configPath}");
    new Switcher(configPath, Log).Run(cts.Token);
    return 0;
}

var devices = EizoHidDevice.Enumerate();
try
{
    switch (cmd)
    {
        case "list":
            foreach (var d in devices)
                Console.WriteLine($"{Eizo.Model(d),-24} PID 0x{d.ProductId:X4}  Modus {Eizo.GetMode(d)}");
            break;

        case "modes":
            foreach (var d in devices)
            {
                int? cur = Eizo.GetMode(d);
                Console.WriteLine($"# {Eizo.Model(d)}");
                string? prev = null;
                for (int i = 0; i < 32; i++)
                {
                    var name = Eizo.GetModeName(d, i);
                    if (string.IsNullOrEmpty(name) || name == prev) break; // monitor clamps past the last mode
                    Console.WriteLine($"  {i,2}  {name}{(i == cur ? "   <== aktiv" : "")}");
                    prev = name;
                }
            }
            break;

        case "set":
            // set <model|serial> <index|name>
            {
                if (args.Length < 3) goto default;
                var d = devices.FirstOrDefault(x => Eizo.Identity(x) is var (s, m) && (m.Equals(args[1], StringComparison.OrdinalIgnoreCase) || s == args[1]));
                if (d == null) { Console.Error.WriteLine("Monitor nicht gefunden."); return 1; }
                int idx;
                if (!int.TryParse(args[2], out idx))
                {
                    idx = -1;
                    for (int i = 0; i < 32 && idx < 0; i++)
                        if (string.Equals(Eizo.GetModeName(d, i), args[2], StringComparison.OrdinalIgnoreCase)) idx = i;
                    if (idx < 0) { Console.Error.WriteLine($"Modus \"{args[2]}\" nicht gefunden."); return 1; }
                }
                bool ok = Eizo.SetMode(d, idx);
                Thread.Sleep(1000);
                Console.WriteLine($"{Eizo.Model(d)} -> {idx}: {(ok ? "OK" : "FEHLER")}, aktuell {Eizo.GetMode(d)}");
            }
            break;

        case "dump":
            // Diagnose: alle Feature-Reports roh auslesen (nur lesend)
            foreach (var d in devices)
            {
                Console.WriteLine($"# PID 0x{d.ProductId:X4} {Eizo.Model(d)}");
                foreach (var c in d.FeatureValues)
                {
                    string val = !d.TryGetValue(c, out uint v, out byte[] raw) ? "<read failed>"
                        : c.ReportCount > 1 ? Convert.ToHexString(raw, 0, Math.Min(raw.Length, 1 + c.ReportCount)).TrimEnd('0')
                        : v.ToString();
                    Console.WriteLine($"  rid=0x{c.ReportId:X2} usage=0x{c.UsagePage:X4}:{c.Usage:X4} {c.BitSize}x{c.ReportCount} = {val}");
                }
            }
            break;

        default:
            Console.WriteLine("""
                EizoSwitcher – Farbmodus von EIZO-Monitoren per USB umschalten (ohne ColorNavigator)

                  list                         angeschlossene Monitore + aktueller Modus
                  modes                        alle Farbmodi je Monitor
                  set <modell|serial> <modus>  Modus setzen (Name oder Index)
                  run [config.json]            Hintergrundbetrieb: Modus nach laufenden Prozessen
                  dump                         Diagnose (rohe HID-Reports)
                """);
            return cmd == "help" ? 0 : 2;
    }
}
finally { foreach (var d in devices) d.Dispose(); }
return 0;

[DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
