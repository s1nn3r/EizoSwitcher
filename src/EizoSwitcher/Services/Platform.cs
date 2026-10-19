using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace EizoSwitcher.Services;

[SupportedOSPlatform("windows")]
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "EizoSwitcher";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath != null) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(Name, false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Log.Write($"Autostart: {e.Message}");
        }
    }
}

/// <summary>Program icons for the rule list (cached).</summary>
[SupportedOSPlatform("windows")]
public static class ExeIcons
{
    private static readonly Dictionary<string, Image?> cache_ = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Program icon scaled to <paramref name="size"/> pixels, or null.</summary>
    public static Image? For(string? path, int size)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        string key = $"{size}|{path}";
        lock (cache_)
        {
            if (cache_.TryGetValue(key, out var img)) return img;
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    using var large = icon.ToBitmap();
                    img = new Bitmap(large, size, size);
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or System.ComponentModel.Win32Exception) { }
            return cache_[key] = img;
        }
    }
}

public static class Shell
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Write($"Open {target}: {e.Message}"); }
    }

    /// <summary>File description from the version resource ("Adobe Photoshop 2026"), else the file name.</summary>
    public static string Describe(string? path, string fallback)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return fallback;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            string? d = string.IsNullOrWhiteSpace(v.FileDescription) ? v.ProductName : v.FileDescription;
            return string.IsNullOrWhiteSpace(d) ? fallback : d.Trim();
        }
        catch (FileNotFoundException) { return fallback; }
    }
}
