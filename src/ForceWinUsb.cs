// Forces Microsoft's inbox "WinUsb Device" driver (C:\Windows\INF\winusb.inf) onto a device,
// the same as Device Manager > Update driver > Let me pick > WinUsb Device. Run as Administrator.
// Usage: force-winusb.exe "USB\VID_1A86&PID_5397\<serial>"

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

static class ForceWinUsb
{
    const int DI_ENUMSINGLEINF = 0x00010000;
    const int DI_FLAGSEX_ALLOWEXCLUDEDDRVS = 0x00000800;
    const int SPDIT_CLASSDRIVER = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SP_DEVINSTALL_PARAMS
    {
        public int cbSize;
        public int Flags;
        public int FlagsEx;
        public IntPtr hwndParent;
        public IntPtr InstallMsgHandler;
        public IntPtr InstallMsgHandlerContext;
        public IntPtr FileQueue;
        public UIntPtr ClassInstallReserved;
        public int Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DriverPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SP_DRVINFO_DATA
    {
        public int cbSize;
        public int DriverType;
        public UIntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string MfgName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProviderName;
        public long DriverDate;
        public long DriverVersion;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwnd);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiOpenDeviceInfo(IntPtr set, string instanceId, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInstallParams(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DEVINSTALL_PARAMS p);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiSetDeviceInstallParams(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DEVINSTALL_PARAMS p);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref SP_DEVINFO_DATA data, int type);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiEnumDriverInfo(IntPtr set, ref SP_DEVINFO_DATA data, int type, int index, ref SP_DRVINFO_DATA drv);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiSetSelectedDriver(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DRVINFO_DATA drv);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool DiInstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DRVINFO_DATA drv, int flags, out bool reboot);

    static void Check(bool ok, string what)
    {
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), what + " failed");
    }

    static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("Usage: force-winusb.exe <device instance id>");
            return 2;
        }
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        try
        {
            SP_DEVINFO_DATA dev = new SP_DEVINFO_DATA();
            dev.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            Check(SetupDiOpenDeviceInfo(set, args[0], IntPtr.Zero, 0, ref dev), "SetupDiOpenDeviceInfo");

            SP_DEVINSTALL_PARAMS p = new SP_DEVINSTALL_PARAMS();
            p.cbSize = Marshal.SizeOf(typeof(SP_DEVINSTALL_PARAMS));
            Check(SetupDiGetDeviceInstallParams(set, ref dev, ref p), "SetupDiGetDeviceInstallParams");
            p.Flags |= DI_ENUMSINGLEINF;
            p.FlagsEx |= DI_FLAGSEX_ALLOWEXCLUDEDDRVS;
            p.DriverPath = Environment.ExpandEnvironmentVariables(@"%WINDIR%\INF\winusb.inf");
            Check(SetupDiSetDeviceInstallParams(set, ref dev, ref p), "SetupDiSetDeviceInstallParams");
            Check(SetupDiBuildDriverInfoList(set, ref dev, SPDIT_CLASSDRIVER), "SetupDiBuildDriverInfoList");

            for (int i = 0; ; i++)
            {
                SP_DRVINFO_DATA drv = new SP_DRVINFO_DATA();
                drv.cbSize = Marshal.SizeOf(typeof(SP_DRVINFO_DATA));
                if (!SetupDiEnumDriverInfo(set, ref dev, SPDIT_CLASSDRIVER, i, ref drv))
                {
                    Console.WriteLine("No 'WinUsb Device' driver found in winusb.inf");
                    return 1;
                }
                Console.WriteLine("Found driver: {0} ({1})", drv.Description, drv.ProviderName);
                if (drv.Description != "WinUsb Device") continue;

                Check(SetupDiSetSelectedDriver(set, ref dev, ref drv), "SetupDiSetSelectedDriver");
                bool reboot;
                Check(DiInstallDevice(IntPtr.Zero, set, ref dev, ref drv, 0, out reboot), "DiInstallDevice");
                Console.WriteLine("Installed WinUsb Device driver. Reboot needed: {0}", reboot);
                return 0;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("ERROR: " + e.Message);
            return 1;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
