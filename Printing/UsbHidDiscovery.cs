using System.Runtime.InteropServices;

namespace Etikra.Printing;

public static class UsbHidDiscovery
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;

    public static IReadOnlyList<PrinterDevice> FindSupvanPrinters()
    {
        var devices = new List<PrinterDevice>();
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1))
        {
            return devices;
        }

        try
        {
            var index = 0u;
            while (true)
            {
                var data = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index++, ref data))
                {
                    if (Marshal.GetLastWin32Error() == 259) // ERROR_NO_MORE_ITEMS
                    {
                        break;
                    }

                    continue;
                }

                _ = SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0)
                {
                    continue;
                }

                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (string.IsNullOrWhiteSpace(path) || !TryParseId(path, "vid_", out var vid) || vid != PrinterProfiles.SupvanVendorId)
                    {
                        continue;
                    }

                    _ = TryParseId(path, "pid_", out var pid);
                    var profile = PrinterProfiles.Find(pid);
                    var name = profile is null ? $"SUPVAN device (PID {pid:X4}, unsupported)" : $"SUPVAN / KATASYMBOL {profile.Name}";
                    devices.Add(new PrinterDevice(path, name, profile, path));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return devices;
    }

    private static bool TryParseId(string path, string marker, out ushort value)
    {
        value = 0;
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && path.Length >= index + marker.Length + 4 &&
               ushort.TryParse(path.AsSpan(index + marker.Length, 4), System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public UIntPtr Reserved;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid interfaceGuid, uint index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr deviceInfo);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
