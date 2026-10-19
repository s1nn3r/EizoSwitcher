using System.Text;

namespace EizoSwitcher;

/// <summary>
/// EIZO tunneled control protocol on HID usage page 0xFF30 (reconstructed from libmctrl_x64.dll):
///   PIN       = feature report 0x06 (u16), sent with every command
///   Get       = SetFeature 0x03 [A u16][B u16][PIN u16], then GetFeature 0x03 -> [A][B][PIN][data...]
///   Set       = SetFeature 0x02 [A u16][B u16][PIN u16][value...]
///   Identity  = feature report 0x08: ASCII serial (8) + model (16)
/// </summary>
public static class Eizo
{
    const byte RidSet = 0x02, RidGet = 0x03, RidPin = 0x06, RidIdentity = 0x08;
    const int ShortPayload = 38; // report 0x02/0x03 size without report ID

    public static ushort Pin(EizoHidDevice d)
    {
        var buf = new byte[d.FeatureReportLength];
        buf[0] = RidPin;
        if (!d.GetFeature(buf)) throw new IOException("PIN not readable");
        return BitConverter.ToUInt16(buf, 1);
    }

    public static (string Serial, string Model) Identity(EizoHidDevice d)
    {
        var buf = new byte[d.FeatureReportLength];
        buf[0] = RidIdentity;
        if (!d.GetFeature(buf)) return ("", "");
        return (Encoding.ASCII.GetString(buf, 1, 8).Trim(), Encoding.ASCII.GetString(buf, 9, 16).Trim());
    }


    static byte[] Packet(byte rid, int len, ushort a, ushort b, ushort pin, ReadOnlySpan<byte> data)
    {
        var p = new byte[len];
        p[0] = rid;
        BitConverter.TryWriteBytes(p.AsSpan(1), a);
        BitConverter.TryWriteBytes(p.AsSpan(3), b);
        BitConverter.TryWriteBytes(p.AsSpan(5), pin);
        data.CopyTo(p.AsSpan(7));
        return p;
    }

    /// <summary>Returns the data bytes after the 6-byte header, or null if no matching response.</summary>
    public static byte[]? Get(EizoHidDevice d, ushort a, ushort b)
    {
        ushort pin = Pin(d);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (!d.SetFeature(Packet(RidGet, d.FeatureReportLength, a, b, pin, default))) return null;
            var resp = new byte[d.FeatureReportLength];
            resp[0] = RidGet;
            if (!d.GetFeature(resp)) return null;
            if (BitConverter.ToUInt16(resp, 1) == a && BitConverter.ToUInt16(resp, 3) == b && BitConverter.ToUInt16(resp, 5) == pin)
                return resp.AsSpan(7, ShortPayload - 6).ToArray();
            Thread.Sleep(80 + Random.Shared.Next(32));
        }
        return null;
    }

    public static bool Set(EizoHidDevice d, ushort a, ushort b, ReadOnlySpan<byte> value)
        => d.SetFeature(Packet(RidSet, d.FeatureReportLength, a, b, Pin(d), value));

    // Usages reconstructed from libemc_x64.dll (EmCtrlGetMode / EmCtrlSetMode / EmCtrlGetModeName)
    const ushort PageMode = 0xFF00, UsageMode = 0x00F3;              // current color mode index
    const ushort PageName = 0xFF01, UsageNameSel = 0x00DB, UsageName = 0x00DC; // name selector / name bytes

    // one byte per mode: 1 = enabled (offered when cycling on the monitor), 0 = skipped ("mode skip")
    const ushort UsageModeEnabled = 0x00F4;

    /// <summary>Which modes are enabled on the monitor; null if not readable.</summary>
    public static bool[]? GetModesEnabled(EizoHidDevice d, int count)
    {
        var r = Get(d, PageMode, UsageModeEnabled);
        if (r == null || count == 0 || r.Take(count).All(b => b == 0)) return null; // all zero = not supported
        return [.. Enumerable.Range(0, count).Select(i => i >= r.Length || r[i] != 0)];
    }

    public static int? GetMode(EizoHidDevice d)
        => Get(d, PageMode, UsageMode) is { } r ? BitConverter.ToInt32(r, 0) : null;

    public static bool SetMode(EizoHidDevice d, int index)
        => Set(d, PageMode, UsageMode, BitConverter.GetBytes(index));

    public static string? GetModeName(EizoHidDevice d, int index)
    {
        if (!Set(d, PageName, UsageNameSel, BitConverter.GetBytes(index))) return null;
        var r = Get(d, PageName, UsageName);
        if (r == null) return null;
        // OSD charset: printable ASCII plus 0x01 = "®" (e.g. "Adobe®RGB")
        var sb = new StringBuilder();
        foreach (byte b in r)
        {
            if (b == 0) break;
            if (b == 0x01) sb.Append('®');
            else if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Loose comparison for user input: "AdobeRGB" matches "Adobe®RGB", case and spaces ignored.</summary>
    public static bool SameModeName(string a, string b)
    {
        static string Key(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || Key(a) == Key(b);
    }

    /// <summary>All mode names. The monitor clamps out-of-range indices, so the list ends at the first repeated name.</summary>
    public static List<string> GetModeNames(EizoHidDevice d)
    {
        var names = new List<string>();
        for (int i = 0; i < 32; i++)
        {
            var n = GetModeName(d, i);
            if (string.IsNullOrEmpty(n) || (names.Count > 0 && names[^1] == n)) break;
            names.Add(n);
        }
        return names;
    }
}
