using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EizoSwitcher.Services;

/// <summary>
/// "Turn off" for any monitor: the display is removed from the desktop (like "Disconnect this display" in the
/// Windows settings). Without a signal the monitor goes to standby by itself. The original layout is saved
/// before the first change and restored afterwards; a marker file makes sure displays come back after a crash.
/// DDC/CI power commands were tried first, but many monitors (e.g. EIZO ColorEdge, FlexScan) acknowledge and ignore them.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DisplayConfig
{
    private const uint QdcOnlyActivePaths = 0x2;
    private const uint SdcApply = 0x80, SdcUseSuppliedDisplayConfig = 0x20, SdcAllowChanges = 0x400, SdcTopologyExtend = 0x4;
    private const uint PathActive = 0x1, ModeIdxInvalid = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct RATIONAL { public uint Num, Den; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MODE_INFO { public uint infoType, id; public LUID adapterId; public fixed byte data[48]; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_DEVICE_NAME
    {
        public uint type, size;
        public LUID adapterId;
        public uint id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, [Out] MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint numPaths, [In] PATH_INFO[]? paths, uint numModes, [In] MODE_INFO[]? modes, uint flags);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME request);

    private static readonly object lock_ = new();
    private static PATH_INFO[]? originalPaths_;
    private static MODE_INFO[]? originalModes_;
    private static string[] originalSources_ = [];

    private static string Marker => Path.Combine(Log.Folder, "displays-disconnected");

    /// <summary>Displays (GDI names like \\.\DISPLAY3) that are currently disconnected by us.</summary>
    public static IReadOnlySet<string> Disconnected { get; private set; } = new HashSet<string>();

    /// <summary>Disconnects exactly these displays and reconnects all others that we disconnected before.</summary>
    public static bool Apply(IReadOnlySet<string> disconnect)
    {
        lock (lock_)
        {
            if (disconnect.SetEquals(Disconnected)) return true;
            if (disconnect.Count == 0) return Restore();

            if (originalPaths_ == null && !Capture()) return false;
            var paths = (PATH_INFO[])originalPaths_!.Clone();
            for (int i = 0; i < paths.Length; i++)
            {
                if ((paths[i].flags & PathActive) == 0 || !disconnect.Contains(originalSources_[i])) continue;
                paths[i].flags &= ~PathActive;
                paths[i].sourceInfo.modeInfoIdx = ModeIdxInvalid;
                paths[i].targetInfo.modeInfoIdx = ModeIdxInvalid;
            }
            int rc = SetDisplayConfig((uint)paths.Length, paths, (uint)originalModes_!.Length, originalModes_, SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges);
            if (rc != 0)
            {
                Log.Write($"Disconnecting {string.Join(", ", disconnect)} failed (error {rc})");
                return false;
            }
            Disconnected = new HashSet<string>(disconnect);
            try { Directory.CreateDirectory(Log.Folder); File.WriteAllText(Marker, string.Join(Environment.NewLine, disconnect)); } catch (IOException) { }
            Log.Write($"Disconnected {string.Join(", ", disconnect)}");
            return true;
        }
    }

    /// <summary>Brings back every display we disconnected (original layout).</summary>
    public static bool Restore()
    {
        lock (lock_)
        {
            if (Disconnected.Count == 0 && originalPaths_ == null) return true;
            int rc = originalPaths_ != null
                ? SetDisplayConfig((uint)originalPaths_.Length, originalPaths_, (uint)originalModes_!.Length, originalModes_, SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges)
                : -1;
            if (rc != 0) rc = SetDisplayConfig(0, null, 0, null, SdcApply | SdcTopologyExtend); // fallback: extend all
            Log.Write(rc == 0 ? "Displays reconnected" : $"Reconnecting displays failed (error {rc})");
            originalPaths_ = null;
            originalModes_ = null;
            Disconnected = new HashSet<string>();
            try { File.Delete(Marker); } catch (IOException) { }
            return rc == 0;
        }
    }

    /// <summary>After a crash: reconnect displays that were left disconnected.</summary>
    public static void RecoverAfterCrash()
    {
        if (!File.Exists(Marker)) return;
        int rc = SetDisplayConfig(0, null, 0, null, SdcApply | SdcTopologyExtend);
        Log.Write($"Reconnected displays left disconnected by a previous run (result {rc})");
        try { File.Delete(Marker); } catch (IOException) { }
    }

    private static bool Capture()
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint np, out uint nm) != 0) return false;
        var paths = new PATH_INFO[np];
        var modes = new MODE_INFO[nm];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
        Array.Resize(ref paths, (int)np);
        Array.Resize(ref modes, (int)nm);

        var sources = new string[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            var req = new SOURCE_DEVICE_NAME
            {
                type = 1, // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>(),
                adapterId = paths[i].sourceInfo.adapterId,
                id = paths[i].sourceInfo.id,
            };
            sources[i] = DisplayConfigGetDeviceInfo(ref req) == 0 ? req.viewGdiDeviceName : "";
        }
        originalPaths_ = paths;
        originalModes_ = modes;
        originalSources_ = sources;
        return true;
    }
}
