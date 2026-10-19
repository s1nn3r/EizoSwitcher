using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Media.Imaging;
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
    private static readonly Dictionary<string, Bitmap?> cache_ = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? For(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        lock (cache_)
        {
            if (cache_.TryGetValue(path, out var bmp)) return bmp;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                using var bitmap = icon?.ToBitmap();
                if (bitmap != null)
                {
                    using var ms = new MemoryStream();
                    bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    bmp = new Bitmap(ms);
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or System.ComponentModel.Win32Exception) { }
            return cache_[path] = bmp;
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
