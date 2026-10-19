using System.Diagnostics;

namespace EizoSwitcher.Services;

public sealed record ProcessInfo(int Id, string Key, string Name, string? Path);

public sealed class ProcessSnapshot(IReadOnlyList<ProcessInfo> processes)
{
    public IReadOnlyList<ProcessInfo> Processes { get; } = processes;
    public HashSet<string> Keys { get; } = processes.Select(p => p.Key).ToHashSet();
}

/// <summary>Polls the process list; executable paths are cached per PID because they are comparatively expensive.</summary>
public sealed class ProcessWatcher
{
    private Dictionary<int, string?> paths_ = [];

    public ProcessSnapshot Take(bool withPaths)
    {
        var procs = Process.GetProcesses();
        var list = new List<ProcessInfo>(procs.Length);
        var paths = new Dictionary<int, string?>();
        foreach (var p in procs)
        {
            using (p)
            {
                string? path = null;
                if (withPaths && p.Id > 4)
                {
                    if (!paths_.TryGetValue(p.Id, out path)) path = PathOf(p);
                    paths[p.Id] = path;
                }
                list.Add(new ProcessInfo(p.Id, ProcessNames.Normalize(p.ProcessName), p.ProcessName, path));
            }
        }
        if (withPaths) paths_ = paths;
        return new ProcessSnapshot(list);
    }

    public static string? PathOf(Process p)
    {
        if (OperatingSystem.IsWindows()) return Win32.ImagePath(p.Id);
        try { return p.MainModule?.FileName; } catch (Exception) { return null; }
    }
}
