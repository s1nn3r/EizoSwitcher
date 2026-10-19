using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EizoSwitcher.Services;

/// <summary>How a display is turned off. Chosen per display by the setup wizard.</summary>
public static class OffMethod
{
    /// <summary>Disconnect from the desktop – works with every monitor (default).</summary>
    public const string Disconnect = "disconnect";
    /// <summary>DDC/CI power mode 4 (standby).</summary>
    public const string DdcStandby = "ddc-standby";
    /// <summary>DDC/CI power mode 5 (off).</summary>
    public const string DdcOff = "ddc-off";

    public static readonly string[] All = [DdcStandby, DdcOff, Disconnect];

    public static string Text(string method) => method switch
    {
        DdcStandby => L.T("DDC/CI: Standby", "DDC/CI: standby"),
        DdcOff => L.T("DDC/CI: Ausschalten", "DDC/CI: power off"),
        _ => L.T("Vom Desktop trennen", "Disconnect from desktop"),
    };
}

/// <summary>DDC/CI power control (VCP 0xD6). Many monitors acknowledge but ignore it – hence the wizard.</summary>
[SupportedOSPlatform("windows")]
public static class Ddc
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

    /// <summary>Sets the power mode of the display shown as <paramref name="deviceName"/> (\\.\DISPLAYn).</summary>
    public static bool SetPower(string deviceName, uint mode)
    {
        if (deviceName.Length == 0) return false;
        IntPtr hmon = IntPtr.Zero;
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (Win32.GetMonitorInfo(h, ref mi) && mi.szDevice == deviceName) hmon = h;
            return true;
        }, IntPtr.Zero);
        if (hmon == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint n) || n == 0) return false;

        var phys = new PHYSICAL_MONITOR[n];
        if (!GetPhysicalMonitorsFromHMONITOR(hmon, n, phys)) return false;
        try { return phys.Aggregate(false, (ok, p) => SetVCPFeature(p.Handle, 0xD6, mode) || ok); }
        finally { DestroyPhysicalMonitors(n, phys); }
    }
}
