using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace EizoSwitcher.Services;

/// <summary>The program in the foreground (the one you are working in).</summary>
public sealed record ForegroundApp(int Pid, string Name, string Key, string? Path, IntPtr Window);

/// <summary>
/// Reports foreground changes immediately (SetWinEventHook on its own message-loop thread) and resolves the
/// foreground window to its process. Shell surfaces (taskbar, desktop, start menu, Alt+Tab, …) are "neutral":
/// clicking them does not count as switching to another program.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003, EventSystemMinimizeEnd = 0x0017, WmQuit = 0x0012;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    private static readonly HashSet<string> NeutralClasses =
    [
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame", "ForegroundStaging", "#32768", "TaskListThumbnailWnd",
    ];
    private static readonly HashSet<string> NeutralProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp", "lockapp", "textinputhost", "eizoswitcher",
    };

    private readonly Thread thread_;
    private WinEventProc? proc_; // kept alive while the hook exists
    private uint threadId_;

    /// <summary>Raised on the watcher thread whenever another window comes to the foreground.</summary>
    public event Action? Changed;

    public ForegroundWatcher()
    {
        thread_ = new Thread(Run) { IsBackground = true, Name = "ForegroundWatcher" };
        thread_.Start();
    }

    private void Run()
    {
        threadId_ = GetCurrentThreadId();
        proc_ = (_, _, _, _, _, _, _) => Changed?.Invoke();
        var h1 = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, proc_, 0, 0, 0);
        var h2 = SetWinEventHook(EventSystemMinimizeEnd, EventSystemMinimizeEnd, IntPtr.Zero, proc_, 0, 0, 0);
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWinEvent(h1);
        UnhookWinEvent(h2);
    }

    /// <summary>The foreground program, or null if a neutral shell surface (or nothing) is in front.</summary>
    public static ForegroundApp? Current()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (NeutralClasses.Contains(cls.ToString())) return null;

        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid <= 4 || pid == Environment.ProcessId) return null;
        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }

        string key = ProcessNames.Normalize(name);
        if (NeutralProcesses.Contains(key)) return null;
        return new ForegroundApp(pid, name, key, Win32.ImagePath(pid), hwnd);
    }

    public static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        if (threadId_ != 0) PostThreadMessage(threadId_, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }
}
