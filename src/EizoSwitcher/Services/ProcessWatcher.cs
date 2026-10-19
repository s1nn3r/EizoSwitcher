using System.Diagnostics;

namespace EizoSwitcher.Services;

public static class ProcessWatcher
{
    /// <summary>Full executable path of a process (null if not accessible).</summary>
    public static string? PathOf(Process p)
    {
        if (OperatingSystem.IsWindows()) return Win32.ImagePath(p.Id);
        try { return p.MainModule?.FileName; } catch (Exception) { return null; }
    }
}
