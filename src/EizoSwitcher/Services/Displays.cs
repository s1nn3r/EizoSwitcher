using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace EizoSwitcher.Services;

/// <summary>One physical display as Windows sees it (EDID identity + desktop position).</summary>
public sealed record DisplayInfo(
    string Id, string Name, string Manufacturer, string Serial,
    string DeviceName, int X, int Y, int Width, int Height, bool Primary)
{
    /// <summary>Filled in by <see cref="DisplayService"/>: EIZO USB control available.</summary>
    public MonitorState? Eizo { get; init; }
    /// <summary>Filled in by <see cref="DisplayService"/>: monitor answers DDC/CI power commands.</summary>
    public bool SupportsPower { get; init; }
    /// <summary>Windows monitor device id ("MONITOR\ENC3041\{…}\0002"), used for color profiles.</summary>
    public string MonitorDeviceId { get; init; } = "";

    public bool IsEizo => Eizo != null;
}

/// <summary>Enumerates displays, links them to EIZO USB devices (EDID serial = USB serial) and drives DDC/CI power.</summary>
[SupportedOSPlatform("windows")]
public sealed class DisplayService : IDisposable
{
    private readonly object lock_ = new();
    private List<DisplayInfo> displays_ = [];

    public MonitorService Eizo { get; } = new();
    public event Action? Changed;

    public IReadOnlyList<DisplayInfo> Displays { get { lock (lock_) return displays_; } }

    /// <summary>Re-reads everything: Windows displays, EIZO USB devices (modes) and DDC/CI support.</summary>
    public void Refresh()
    {
        Eizo.Refresh();
        var eizo = Eizo.Monitors.ToDictionary(m => m.Serial);
        var list = new List<DisplayInfo>();
        foreach (var d in EnumerateDisplays())
            list.Add(d with { Eizo = eizo.GetValueOrDefault(d.Serial), SupportsPower = ProbePower(d.DeviceName) });

        // EIZO monitors connected via USB but not (yet) active on the desktop
        foreach (var m in eizo.Values.Where(m => list.All(d => d.Serial != m.Serial)))
            list.Add(new DisplayInfo(m.Serial, m.Model, "EIZO", m.Serial, "", 0, 0, 0, 0, false) { Eizo = m });

        lock (lock_) displays_ = [.. list.OrderBy(d => d.X).ThenBy(d => d.Y)];
        Log.Write("Displays: " + string.Join(", ", list.Select(d =>
            $"{d.Name} [{d.Id}]{(d.IsEizo ? " EIZO-USB" : "")}{(d.SupportsPower ? " DDC" : "")}")));
        Changed?.Invoke();
    }

    /// <summary>Cheap check whether the set of displays changed (for periodic polling).</summary>
    public bool TopologyChanged()
    {
        var now = EnumerateDisplays().Select(d => d.Id + d.DeviceName).Order();
        lock (lock_)
            return !now.SequenceEqual(displays_.Where(d => d.DeviceName != "").Select(d => d.Id + d.DeviceName).Order());
    }

    /// <summary>Updates the EIZO current-mode cache. Returns false if a device stopped answering.</summary>
    public bool PollEizo()
    {
        bool ok = Eizo.PollCurrentModes();
        var eizo = Eizo.Monitors.ToDictionary(m => m.Serial);
        lock (lock_)
            displays_ = [.. displays_.Select(d => d.IsEizo && eizo.TryGetValue(d.Serial, out var m) ? d with { Eizo = m } : d)];
        return ok;
    }

    /// <summary>Which Windows color profile belongs to a display's mode (null = leave unchanged). Set by the app.</summary>
    public Func<DisplayInfo, int, string?>? ProfileFor { get; set; }

    public bool SetMode(DisplayInfo d, int index)
    {
        if (d.Eizo == null || !Eizo.SetMode(d.Eizo.Serial, index)) return false;
        PollEizo();
        ApplyProfile(d, index);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Makes the color profile of the given mode the display's Windows default (if one is configured/found).</summary>
    public void ApplyProfile(DisplayInfo d, int index)
    {
        if (ProfileFor?.Invoke(d, index) is { } profile && ColorProfiles.SetDefault(d.MonitorDeviceId, profile, out bool changed) && changed)
            Log.Write($"{d.Name}: color profile {profile}");
    }

    /// <summary>DDC/CI VCP 0xD6 (power mode): 1 = on, 4 = off (DPM off, wakes up via DDC).</summary>
    public bool SetPower(DisplayInfo d, bool on) =>
        WithPhysicalMonitor(d.DeviceName, h => Win32.SetVCPFeature(h, 0xD6, on ? 1u : 4u));

    private static bool ProbePower(string deviceName) =>
        WithPhysicalMonitor(deviceName, h => Win32.GetVCPFeatureAndVCPFeatureReply(h, 0xD6, IntPtr.Zero, out _, out _));

    private static bool WithPhysicalMonitor(string deviceName, Func<IntPtr, bool> action)
    {
        if (deviceName.Length == 0) return false;
        IntPtr hmon = IntPtr.Zero;
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (Win32.GetMonitorInfo(h, ref mi) && mi.szDevice == deviceName) hmon = h;
            return true;
        }, IntPtr.Zero);
        if (hmon == IntPtr.Zero || !Win32.GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint n) || n == 0) return false;

        var phys = new Win32.PHYSICAL_MONITOR[n];
        if (!Win32.GetPhysicalMonitorsFromHMONITOR(hmon, n, phys)) return false;
        try { return phys.Any(p => action(p.hPhysicalMonitor)); }
        finally { Win32.DestroyPhysicalMonitors(n, phys); }
    }

    /// <summary>Active desktop displays with EDID name/serial from the registry.</summary>
    internal static List<DisplayInfo> EnumerateDisplays()
    {
        var monitors = new List<(string Device, Win32.RECT Rect, bool Primary)>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (Win32.GetMonitorInfo(h, ref mi)) monitors.Add((mi.szDevice, mi.rcMonitor, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);

        var result = new List<DisplayInfo>();
        foreach (var (device, r, primary) in monitors)
        {
            var dd = new Win32.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
            Edid? edid = null;
            string instance = device, monitorId = "";
            // EDD_GET_DEVICE_INTERFACE_NAME: DeviceID = \\?\DISPLAY#ENC3041#5&...&UID33027#{guid}
            for (uint i = 0; Win32.EnumDisplayDevices(device, i, ref dd, 1); i++)
            {
                if ((dd.StateFlags & 1) == 0) continue; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
                var parts = dd.DeviceID.Split('#');
                if (parts.Length < 3) continue;
                instance = $@"DISPLAY\{parts[1]}\{parts[2]}";
                edid = Edid.FromRegistry(instance);
                // same monitor without the flag: DeviceID = MONITOR\ENC3041\{class guid}\0002 (color management id)
                var plain = new Win32.DISPLAY_DEVICE { cb = dd.cb };
                if (Win32.EnumDisplayDevices(device, i, ref plain, 0)) monitorId = plain.DeviceID;
                break;
            }

            string name = edid?.Name ?? device.TrimStart('\\', '.');
            string serial = edid?.SerialText ?? "";
            string id = serial.Length > 0 ? serial : edid != null ? $"{edid.Manufacturer}{edid.ProductCode:X4}-{edid.SerialNumber}" : instance;
            result.Add(new DisplayInfo(id, name, Vendor(edid?.Manufacturer), serial, device, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, primary)
                { MonitorDeviceId = monitorId });
        }
        return result;
    }

    /// <summary>EDID PnP manufacturer ids of common monitor brands.</summary>
    private static string Vendor(string? pnp) => pnp switch
    {
        null => "",
        "ENC" => "EIZO",
        "DEL" => "Dell",
        "SAM" or "SEC" => "Samsung",
        "GSM" => "LG",
        "AUS" or "ACI" => "ASUS",
        "BNQ" => "BenQ",
        "HWP" or "HPN" => "HP",
        "LEN" => "Lenovo",
        "PHL" => "Philips",
        "VSC" => "ViewSonic",
        "AOC" => "AOC",
        "ACR" => "Acer",
        "MSI" => "MSI",
        "NEC" => "NEC",
        "SNY" => "Sony",
        "APP" => "Apple",
        "GBT" => "Gigabyte",
        "IVM" => "iiyama",
        "FUS" => "Fujitsu",
        "HKC" => "HKC",
        _ => pnp,
    };

    public void Dispose() => Eizo.Dispose();
}

/// <summary>The few EDID fields we need.</summary>
internal sealed record Edid(string Manufacturer, ushort ProductCode, uint SerialNumber, string? Name, string? Serial)
{
    /// <summary>
    /// Serial as printed on the monitor: the text descriptor if present, else the numeric field
    /// (EIZO ColorEdge only fills the latter; 0x01010101 is a common placeholder).
    /// </summary>
    public string? SerialText => !string.IsNullOrEmpty(Serial) ? Serial
        : SerialNumber is not (0 or 0x01010101 or 0xFFFFFFFF) ? SerialNumber.ToString() : null;

    [SupportedOSPlatform("windows")]
    public static Edid? FromRegistry(string instancePath)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instancePath}\Device Parameters");
        return key?.GetValue("EDID") is byte[] data ? Parse(data) : null;
    }

    public static Edid? Parse(byte[] e)
    {
        if (e.Length < 128) return null;
        int m = (e[8] << 8) | e[9];
        string mfg = new([(char)('@' + ((m >> 10) & 31)), (char)('@' + ((m >> 5) & 31)), (char)('@' + (m & 31))]);
        string? name = null, serial = null;
        for (int off = 54; off <= 108; off += 18)
        {
            if (e[off] != 0 || e[off + 1] != 0) continue; // detailed timing, not a descriptor
            string text = Encoding.ASCII.GetString(e, off + 5, 13).Split('\n')[0].Trim();
            if (e[off + 3] == 0xFC) name = text;
            else if (e[off + 3] == 0xFF) serial = text;
        }
        return new Edid(mfg, BitConverter.ToUInt16(e, 10), BitConverter.ToUInt32(e, 12), name, serial);
    }
}
