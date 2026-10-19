using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EizoSwitcher;

/// <summary>Minimal Win32 HID access (SetupAPI + hid.dll), feature reports only.</summary>
internal static class Native
{
    public const ushort EizoVid = 0x056D;
    public const int HidP_Feature = 2;
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pd);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pd);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool HidD_GetProductString(SafeFileHandle h, char[] buf, int len);
    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, char[] buf, int len);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pd, out HIDP_CAPS caps);
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int type, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pd);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int type, ushort page, ushort coll, ushort usage, out uint value, IntPtr pd, byte[] report, int len);
    [DllImport("hid.dll")] public static extern int HidP_SetUsageValue(int type, ushort page, ushort coll, ushort usage, uint value, IntPtr pd, byte[] report, int len);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValueArray(int type, ushort page, ushort coll, ushort usage, byte[] buf, ushort bufLen, IntPtr pd, byte[] report, int len);

    [DllImport("setupapi.dll", SetLastError = true)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    [StructLayout(LayoutKind.Sequential)] public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
    [StructLayout(LayoutKind.Sequential)] public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(8)] public ushort LinkUsage;
        [FieldOffset(10)] public ushort LinkUsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(13)] public byte IsStringRange;
        [FieldOffset(14)] public byte IsDesignatorRange;
        [FieldOffset(15)] public byte IsAbsolute;
        [FieldOffset(16)] public byte HasNull;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(32)] public uint UnitsExp;
        [FieldOffset(36)] public uint Units;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(48)] public int PhysicalMin;
        [FieldOffset(52)] public int PhysicalMax;
        // Range.UsageMin / NotRange.Usage share offset 56; Range.UsageMax at 58
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(58)] public ushort UsageMax;
    }
}

public sealed record HidValueCap(byte ReportId, ushort UsagePage, ushort Usage, ushort LinkCollection, ushort BitSize, ushort ReportCount, int LogicalMin, int LogicalMax);

/// <summary>One EIZO HID device (one monitor).</summary>
public sealed class EizoHidDevice : IDisposable
{
    public string Path { get; }
    public ushort ProductId { get; }
    public string Product { get; }
    public string Serial { get; }
    public IReadOnlyList<HidValueCap> FeatureValues { get; }
    public int FeatureReportLength { get; }

    private readonly SafeFileHandle handle_;
    private readonly IntPtr preparsed_;

    private EizoHidDevice(string path, SafeFileHandle h, ushort pid)
    {
        Path = path;
        handle_ = h;
        ProductId = pid;
        Product = ReadString(Native.HidD_GetProductString);
        Serial = ReadString(Native.HidD_GetSerialNumberString);
        if (!Native.HidD_GetPreparsedData(h, out preparsed_)) throw new Win32Exception();
        Native.HidP_GetCaps(preparsed_, out var caps);
        FeatureReportLength = caps.FeatureReportByteLength;

        var list = new List<HidValueCap>();
        ushort n = caps.NumberFeatureValueCaps;
        if (n > 0)
        {
            var vc = new Native.HIDP_VALUE_CAPS[n];
            Native.HidP_GetValueCaps(Native.HidP_Feature, vc, ref n, preparsed_);
            foreach (var c in vc.Take(n))
            {
                int last = c.IsRange != 0 ? c.UsageMax : c.UsageMin;
                for (int u = c.UsageMin; u <= last; u++)
                    list.Add(new HidValueCap(c.ReportID, c.UsagePage, (ushort)u, c.LinkCollection, c.BitSize, c.ReportCount, c.LogicalMin, c.LogicalMax));
            }
        }
        FeatureValues = list;
    }

    private delegate bool StrFn(SafeFileHandle h, char[] buf, int len);
    private string ReadString(StrFn fn)
    {
        var buf = new char[128];
        return fn(handle_, buf, buf.Length * 2) ? new string(buf).TrimEnd('\0') : "";
    }

    public static List<EizoHidDevice> Enumerate()
    {
        var result = new List<EizoHidDevice>();
        Native.HidD_GetHidGuid(out var guid);
        IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12 /* PRESENT|DEVICEINTERFACE */);
        try
        {
            var did = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did); i++)
            {
                Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out int req, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(detail, 8); // cbSize for x64
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref did, detail, req, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4)!;
                    if (!path.Contains("vid_056d", StringComparison.OrdinalIgnoreCase)) continue;

                    var h = Native.CreateFile(path, 0xC0000000 /* R|W */, 3 /* share R|W */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
                    if (h.IsInvalid) continue;
                    var attr = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
                    Native.HidD_GetAttributes(h, ref attr);
                    result.Add(new EizoHidDevice(path, h, attr.ProductID));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    /// <summary>Reads the feature report containing <paramref name="cap"/> and extracts its value.</summary>
    public bool TryGetValue(HidValueCap cap, out uint value, out byte[] raw)
    {
        value = 0;
        raw = new byte[FeatureReportLength];
        raw[0] = cap.ReportId;
        if (!Native.HidD_GetFeature(handle_, raw, raw.Length)) return false;
        if (cap.ReportCount > 1) return true; // array: caller inspects raw
        return Native.HidP_GetUsageValue(Native.HidP_Feature, cap.UsagePage, cap.LinkCollection, cap.Usage, out value, preparsed_, raw, raw.Length) == Native.HIDP_STATUS_SUCCESS;
    }

    /// <summary>Writes a single-value usage via SetFeature.</summary>
    public void SetValue(HidValueCap cap, uint value)
    {
        var report = new byte[FeatureReportLength];
        report[0] = cap.ReportId;
        int st = Native.HidP_SetUsageValue(Native.HidP_Feature, cap.UsagePage, cap.LinkCollection, cap.Usage, value, preparsed_, report, report.Length);
        if (st != Native.HIDP_STATUS_SUCCESS) throw new InvalidOperationException($"HidP_SetUsageValue failed: 0x{st:X8}");
        if (!Native.HidD_SetFeature(handle_, report, report.Length)) throw new Win32Exception();
    }

    /// <summary>Raw feature report access (report[0] = report ID).</summary>
    public bool GetFeature(byte[] report) => Native.HidD_GetFeature(handle_, report, report.Length);
    public bool SetFeature(byte[] report) => Native.HidD_SetFeature(handle_, report, report.Length);

    public void Dispose()
    {
        if (preparsed_ != IntPtr.Zero) Native.HidD_FreePreparsedData(preparsed_);
        handle_.Dispose();
    }
}
