using System.Runtime.InteropServices;
using System.Text;

namespace RemotePlayBlanker;

/// <summary>
/// Installs/removes the root-enumerated Virtual Display Driver device (what `devcon install X.inf Root\MttVDD`
/// does), using SetupAPI directly. Must run elevated. Used by the installer.
/// </summary>
public static class DriverInstaller
{
    public const string HardwareId = @"Root\MttVDD";

    /// <summary>0 = created a new device, 2 = device already existed (driver updated), 1 = failed.</summary>
    public static int Install(string infPath)
    {
        infPath = Path.GetFullPath(infPath);
        if (!File.Exists(infPath)) { Console.Error.WriteLine($"INF not found: {infPath}"); return 1; }

        bool existed = DeviceExists();
        if (!existed)
        {
            var className = new StringBuilder(256);
            if (!SetupDiGetINFClass(infPath, out var classGuid, className, className.Capacity, out _))
                return Fail("SetupDiGetINFClass");

            var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
            if (set == INVALID_HANDLE) return Fail("SetupDiCreateDeviceInfoList");
            try
            {
                var dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiCreateDeviceInfo(set, className.ToString(), ref classGuid, null, IntPtr.Zero, DICD_GENERATE_ID, ref dev))
                    return Fail("SetupDiCreateDeviceInfo");

                var hwid = Encoding.Unicode.GetBytes(HardwareId + "\0\0"); // REG_MULTI_SZ
                if (!SetupDiSetDeviceRegistryProperty(set, ref dev, SPDRP_HARDWAREID, hwid, (uint)hwid.Length))
                    return Fail("SetupDiSetDeviceRegistryProperty");

                if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref dev))
                    return Fail("SetupDiCallClassInstaller(DIF_REGISTERDEVICE)");
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, infPath, INSTALLFLAG_FORCE, out _))
            return Fail("UpdateDriverForPlugAndPlayDevices");

        Console.WriteLine(existed ? "Virtual display device already present; driver updated." : "Virtual display device installed.");
        return existed ? 2 : 0;
    }

    /// <summary>Removes every Root\MttVDD device. 0 = ok.</summary>
    public static int Uninstall()
    {
        int removed = 0;
        ForEachDevice((set, dev) =>
        {
            if (SetupDiCallClassInstaller(DIF_REMOVE, set, ref dev)) removed++;
            else Fail("DIF_REMOVE");
        });
        Console.WriteLine($"Removed {removed} virtual display device(s).");
        return 0;
    }

    public static bool DeviceExists()
    {
        bool found = false;
        ForEachDevice((_, _) => found = true);
        return found;
    }

    delegate void DeviceAction(IntPtr set, SP_DEVINFO_DATA dev);

    static void ForEachDevice(DeviceAction action)
    {
        var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE) return;
        try
        {
            var buf = new byte[2048];
            for (uint i = 0; ; i++)
            {
                var dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref dev)) break;
                if (!SetupDiGetDeviceRegistryProperty(set, ref dev, SPDRP_HARDWAREID, out _, buf, (uint)buf.Length, out var size))
                    continue;
                var ids = Encoding.Unicode.GetString(buf, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (ids.Any(id => id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) action(set, dev);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    static int Fail(string what)
    {
        Console.Error.WriteLine($"{what} failed: error {Marshal.GetLastWin32Error()}");
        return 1;
    }

    // ---------------- Win32 ----------------

    static readonly IntPtr INVALID_HANDLE = new(-1);
    const uint DICD_GENERATE_ID = 1, DIGCF_ALLCLASSES = 4;
    const uint SPDRP_HARDWAREID = 1, DIF_REGISTERDEVICE = 0x19, DIF_REMOVE = 0x05, INSTALLFLAG_FORCE = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetINFClass(string infName, out Guid classGuid, StringBuilder className, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiCreateDeviceInfo(IntPtr set, string deviceName, ref Guid classGuid, string? description,
        IntPtr hwndParent, uint flags, ref SP_DEVINFO_DATA dev);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA dev, uint property, byte[] buffer, uint size);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA dev, uint property,
        out uint regType, byte[] buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SP_DEVINFO_DATA dev);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA dev);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId, string fullInfPath,
        uint installFlags, out bool rebootRequired);
}
