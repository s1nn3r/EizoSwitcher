using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EizoSwitcher.Services;

/// <summary>
/// Decides whether the program in the foreground is a game. There is no single reliable signal, so several are combined:
///  1. exclusive Direct3D fullscreen (SHQueryUserNotificationState)
///  2. window covering a whole monitor (borderless fullscreen)
///  3. executables Windows itself classified as games (Game Bar: HKCU\System\GameConfigStore)
///  4. executables inside launcher libraries (Steam, Epic, GOG, Ubisoft, EA, Xbox) or extra folders
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class GameDetector
{
    private DateTime nextScan_ = DateTime.MinValue;
    private List<string> libraries_ = [];
    private HashSet<string> windowsGames_ = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Libraries { get { Rescan(); return libraries_; } }

    /// <summary>Returns the game's name if the foreground program is a game, else null.</summary>
    public string? Detect(ForegroundApp fg, GameSettings s)
    {
        Rescan();
        if (s.ExcludedProcesses.Any(x => ProcessNames.Normalize(x) == fg.Key)) return null;
        if (s.DetectExclusiveFullscreen && Win32.IsExclusiveFullscreen()) return fg.Name;
        if (s.DetectBorderlessFullscreen && Win32.IsFullscreenWindow(fg.Window)) return fg.Name;
        if (fg.Path == null) return null;
        if (s.UseWindowsGameList && windowsGames_.Contains(fg.Path)) return fg.Name;
        var roots = (s.UseLauncherLibraries ? libraries_ : []).Concat(s.ExtraFolders).Select(r => r.TrimEnd('\\') + "\\");
        return roots.Any(r => fg.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) ? fg.Name : null;
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
