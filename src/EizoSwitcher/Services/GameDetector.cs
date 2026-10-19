using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EizoSwitcher.Services;

/// <summary>
/// Detects running games. There is no single reliable signal, so several are combined:
///  1. foreground app in exclusive Direct3D fullscreen (SHQueryUserNotificationState)
///  2. foreground window covering a whole monitor (borderless fullscreen)
///  3. executables Windows itself classified as games (Game Bar: HKCU\System\GameConfigStore)
///  4. executables inside launcher libraries (Steam, Epic, GOG, Ubisoft, EA, Xbox) that have a window
/// A game detected via 1/2 stays "active" until its process exits, so alt-tabbing does not flip the mode.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class GameDetector
{
    private DateTime nextScan_ = DateTime.MinValue;
    private List<string> libraries_ = [];
    private HashSet<string> windowsGames_ = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> sticky_ = [];

    public IReadOnlyList<string> Libraries { get { Rescan(); return libraries_; } }

    /// <summary>Returns the name of a running game or null.</summary>
    public string? Detect(ProcessSnapshot snap, GameSettings s)
    {
        Rescan();
        var excluded = s.ExcludedProcesses.Select(ProcessNames.Normalize).ToHashSet();
        var byId = snap.Processes.ToDictionary(p => p.Id);

        foreach (var pid in sticky_.Keys.Where(id => !byId.ContainsKey(id)).ToList()) sticky_.Remove(pid);

        if (s.DetectExclusiveFullscreen || s.DetectBorderlessFullscreen)
        {
            var hwnd = Win32.GetForegroundWindow();
            Win32.GetWindowThreadProcessId(hwnd, out int pid);
            if (byId.TryGetValue(pid, out var fg) && pid != Environment.ProcessId && !excluded.Contains(fg.Key)
                && ((s.DetectExclusiveFullscreen && Win32.IsExclusiveFullscreen()) || (s.DetectBorderlessFullscreen && Win32.IsFullscreenWindow(hwnd))))
                sticky_[pid] = fg.Name;
        }
        foreach (var (pid, name) in sticky_)
            if (byId.TryGetValue(pid, out var p) && !excluded.Contains(p.Key)) return name;

        var roots = (s.UseLauncherLibraries ? libraries_ : []).Concat(s.ExtraFolders)
            .Select(r => r.TrimEnd('\\') + "\\").ToList();
        foreach (var p in snap.Processes)
        {
            if (p.Path == null || excluded.Contains(p.Key)) continue;
            if (s.UseWindowsGameList && windowsGames_.Contains(p.Path)) return p.Name;
            if (roots.Any(r => p.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) && HasWindow(p.Id)) return p.Name;
        }
        return null;
    }

    private static bool HasWindow(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.MainWindowHandle != IntPtr.Zero; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void Rescan()
    {
        if (DateTime.UtcNow < nextScan_) return;
        nextScan_ = DateTime.UtcNow.AddMinutes(5);
        try
        {
            libraries_ = [.. FindLibraries().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)];
            windowsGames_ = ReadWindowsGameList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"Game library scan failed: {e.Message}");
        }
    }

    [GeneratedRegex("\"path\"\\s+\"(.+?)\"")]
    private static partial Regex SteamPath();

    private static IEnumerable<string> FindLibraries()
    {
        // Steam: main install + all library folders
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam)
        {
            var roots = new List<string> { steam };
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                roots.AddRange(SteamPath().Matches(File.ReadAllText(vdf)).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
            foreach (var r in roots) yield return Path.Combine(r.Replace('/', '\\'), "steamapps", "common");
        }

        // Epic: one manifest per installed game
        string epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (Directory.Exists(epic))
        {
            foreach (var file in Directory.EnumerateFiles(epic, "*.item"))
            {
                string? location = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    location = doc.RootElement.GetProperty("InstallLocation").GetString();
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or IOException) { }
                if (!string.IsNullOrEmpty(location)) yield return location;
            }
        }

        // GOG
        using (var gog = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
        {
            if (gog != null)
                foreach (var name in gog.GetSubKeyNames())
                {
                    using var k = gog.OpenSubKey(name);
                    if (k?.GetValue("path") is string p) yield return p;
                }
        }

        // Ubisoft
        if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir", null) is string ubi)
            yield return Path.Combine(ubi, "games");

        // Common default folders on every fixed drive (Xbox app, EA, ...)
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            foreach (var rel in new[] { "XboxGames", @"Program Files\EA Games", @"Program Files (x86)\Origin Games",
                                        @"Program Files\Epic Games", @"Program Files (x86)\GOG Galaxy\Games", "GOG Games" })
                yield return Path.Combine(drive.RootDirectory.FullName, rel);
    }

    private static HashSet<string> ReadWindowsGameList()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
        if (key == null) return set;
        foreach (var name in key.GetSubKeyNames())
        {
            using var k = key.OpenSubKey(name);
            if (k?.GetValue("MatchedExeFullPath") is string p && p.Length > 0) set.Add(p);
        }
        return set;
    }
}
