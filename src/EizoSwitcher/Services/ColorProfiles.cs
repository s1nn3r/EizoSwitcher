using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace EizoSwitcher.Services;

/// <summary>
/// Windows display color profiles (ICC). ColorNavigator creates one profile per monitor and mode, named
/// "{Model}({Serial}){mode index + 1:00}{mode name}.icc" (e.g. "CG279X(12345678)10CAL.icc") and makes it the
/// current user's default for that display. EizoSwitcher does the same after switching a mode.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ColorProfiles
{
    /// <summary>Profile mapping value: do not touch the Windows profile for this mode.</summary>
    public const string Keep = "@keep";

    private const int ScopeCurrentUser = 1, CptIcc = 0, CpstNone = 4;
    private const uint ClassMonitor = 0x6D6E7472; // 'mntr'

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsAssociateColorProfileWithDevice(int scope, string profile, string device);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsSetDefaultColorProfile(int scope, string device, int cpt, int cpst, uint id, string profile);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsGetDefaultColorProfileSize(int scope, string device, int cpt, int cpst, uint id, out uint size);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsGetDefaultColorProfile(int scope, string device, int cpt, int cpst, uint id, uint size, StringBuilder name);
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WcsSetUsePerUserProfiles(string device, uint deviceClass, bool usePerUser);

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\drivers\color");

    /// <summary>Installed display profiles (file names).</summary>
    public static List<string> Installed()
    {
        try
        {
            return [.. Directory.EnumerateFiles(Folder)
                .Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileName(f)!)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The profile ColorNavigator created for this monitor and mode, if any.</summary>
    public static string? FromColorNavigator(string model, string serial, int modeIndex)
    {
        if (model.Length == 0 || serial.Length == 0 || modeIndex < 0) return null;
        string prefix = $"{model}({serial}){modeIndex + 1:00}";
        return Installed().FirstOrDefault(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Which profile to use: explicit mapping, else ColorNavigator's; null = leave unchanged.</summary>
    public static string? For(AppSettings s, DisplayInfo d, int modeIndex)
    {
        if (!s.SwitchColorProfiles || d.Eizo == null || modeIndex < 0 || modeIndex >= d.Eizo.Modes.Count) return null;
        string mode = d.Eizo.Modes[modeIndex];
        if (s.ColorProfiles.TryGetValue(d.Id, out var map) && map.TryGetValue(mode, out var chosen) && chosen.Length > 0)
            return chosen == Keep ? null : chosen;
        return FromColorNavigator(d.Eizo.Model, d.Eizo.Serial, modeIndex);
    }

    /// <summary>Current user's default profile of a display (null if none / unknown).</summary>
    public static string? GetDefault(string monitorDeviceId)
    {
        if (monitorDeviceId.Length == 0 || !WcsGetDefaultColorProfileSize(ScopeCurrentUser, monitorDeviceId, CptIcc, CpstNone, 0, out uint size)) return null;
        var sb = new StringBuilder((int)size / 2 + 1);
        return WcsGetDefaultColorProfile(ScopeCurrentUser, monitorDeviceId, CptIcc, CpstNone, 0, size, sb) ? sb.ToString() : null;
    }

    /// <summary>Makes <paramref name="profile"/> the current user's default profile of the display.</summary>
    public static bool SetDefault(string monitorDeviceId, string profile, out bool changed)
    {
        changed = false;
        if (monitorDeviceId.Length == 0) return false;
        if (string.Equals(GetDefault(monitorDeviceId), profile, StringComparison.OrdinalIgnoreCase)) return true;
        changed = true;
        bool ok = WcsAssociateColorProfileWithDevice(ScopeCurrentUser, profile, monitorDeviceId)
               && WcsSetUsePerUserProfiles(monitorDeviceId, ClassMonitor, true)
               && WcsSetDefaultColorProfile(ScopeCurrentUser, monitorDeviceId, CptIcc, CpstNone, 0, profile);
        if (!ok) Log.Write($"Setting color profile \"{profile}\" failed (error {Marshal.GetLastWin32Error()})");
        return ok;
    }
}
