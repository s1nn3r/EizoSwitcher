using System.Text.Json;
using System.Text.Json.Serialization;

namespace EizoSwitcher.Services;

/// <summary>
/// Per-display action: a color mode name (EIZO), <see cref="Black"/>, <see cref="Off"/>, or empty = leave unchanged.
/// </summary>
public static class DisplayAction
{
    public const string Black = "@black";
    public const string Off = "@off";

    public static bool IsMode(string? action) => !string.IsNullOrEmpty(action) && action[0] != '@';

    public static string Text(string? action) => action switch
    {
        null or "" => "—",
        Black => L.T("Schwarz", "Black"),
        Off => L.T("Ausschalten", "Turn off"),
        _ => action,
    };
}

public sealed class ProcessRule
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    /// <summary>Executable file name, e.g. "Photoshop.exe".</summary>
    public string ExeName { get; set; } = "";
    /// <summary>Full path; if known, only this executable matches (two versions of a program = two rules).</summary>
    public string? ExePath { get; set; }
    /// <summary>Display id -> action (see <see cref="DisplayAction"/>).</summary>
    public Dictionary<string, string> Actions { get; set; } = [];

    [JsonIgnore] public string ProcessKey => ProcessNames.Normalize(ExeName);
}

public sealed class GameSettings
{
    public bool Enabled { get; set; } = true;
    public bool DetectExclusiveFullscreen { get; set; } = true;
    public bool DetectBorderlessFullscreen { get; set; } = true;
    public bool UseWindowsGameList { get; set; } = true;
    public bool UseLauncherLibraries { get; set; } = true;
    public Dictionary<string, string> Actions { get; set; } = [];
    public List<string> ExtraFolders { get; set; } = [];
    public List<string> ExcludedProcesses { get; set; } = [.. DefaultExclusions];

    /// <summary>Launchers, helpers and fullscreen apps that are not games.</summary>
    public static readonly string[] DefaultExclusions =
    [
        "explorer", "ApplicationFrameHost", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "LockApp",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "GalaxyClient", "GalaxyClient Helper",
        "Battle.net", "EADesktop", "EALauncher", "upc", "UbisoftConnect", "RockstarLauncher", "XboxPcApp",
        "UnityCrashHandler64", "UnityCrashHandler32", "CrashReportClient", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService",
        "wallpaper32", "wallpaper64", "webwallpaper32", "vrmonitor", "vrserver",
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "vlc", "mpc-hc64", "mpc-be64", "PotPlayerMini64", "mpv",
        "POWERPNT", "Teams", "ms-teams", "Zoom", "obs64", "Resolve", "Adobe Premiere Pro", "EizoSwitcher",
    ];
}

/// <summary>Last known state of a display, so rules stay editable while it is disconnected.</summary>
public sealed class KnownDisplay
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsEizo { get; set; }
    public bool CanTurnOff { get; set; }
    public List<string> Modes { get; set; } = [];
    /// <summary>Per mode: enabled on the monitor (empty = unknown).</summary>
    public List<bool> ModesEnabled { get; set; } = [];
    public DateTime LastSeen { get; set; }
}

public sealed class AppSettings
{
    public int PollIntervalMs { get; set; } = 1500;
    public bool StartWithWindows { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    /// <summary>Keep a program's color modes after switching to another window, until it exits.</summary>
    public bool KeepModesUntilExit { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; }
    public bool Paused { get; set; }
    /// <summary>UI language code ("de", "fr", …); empty = system language.</summary>
    public string Language { get; set; } = "";
    /// <summary>"light", "dark" or empty = follow Windows.</summary>
    public string Theme { get; set; } = "";
    /// <summary>Display id -> how "turn off" works for it (see <see cref="OffMethod"/>); missing = disconnect.</summary>
    public Dictionary<string, string> OffMethods { get; set; } = [];
    /// <summary>Also switch the Windows color profile (ICC) of the display together with the mode.</summary>
    public bool SwitchColorProfiles { get; set; } = true;
    /// <summary>Display id -> mode name -> profile file name ("@keep" = leave unchanged); missing = ColorNavigator's profile.</summary>
    public Dictionary<string, Dictionary<string, string>> ColorProfiles { get; set; } = [];
    /// <summary>Display id -> mode used when no rule and no game is active.</summary>
    public Dictionary<string, string> Defaults { get; set; } = [];
    public List<ProcessRule> Rules { get; set; } = [];
    public GameSettings Games { get; set; } = new();
    public List<KnownDisplay> Displays { get; set; } = [];

    public static string FilePath => Path.Combine(Log.Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Log.Write($"settings.json unreadable, starting with defaults: {e.Message}");
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch (IOException) { }
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Log.Folder);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        File.Move(tmp, FilePath, true);
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings), SettingsJson.Default.AppSettings)!;
}

public static class Links
{
    public const string Project = "https://github.com/s1nn3r/EizoSwitcher";
    public const string Donate = "https://www.paypal.com/donate/?business=marc.breuer%40giant-online.de&currency_code=EUR&item_name=EizoSwitcher";
}

public static class ProcessNames
{
    /// <summary>"C:\x\Photoshop.EXE" / "Photoshop" -> "photoshop"</summary>
    public static string Normalize(string name)
    {
        name = Path.GetFileName(name.Trim());
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
