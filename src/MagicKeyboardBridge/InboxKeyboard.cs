using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

// Select only a compatible driver from Windows' own input.inf. No new driver
// package, certificate or kernel binary is required for emergency recovery.
public static class InboxKeyboard
{
    [StructLayout(LayoutKind.Sequential)]
    struct Device { public int Size; public Guid Class; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct InstallParameters {
        public int Size; public uint Flags, FlagsEx;
        public IntPtr Window, Callback, Context, Queue, ClassReserved;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string DriverPath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct Driver {
        public int Size; public uint Type; public UIntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Provider;
        public System.Runtime.InteropServices.ComTypes.FILETIME Date;
        public ulong Version;
    }
    [DllImport("setupapi.dll", SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr cls,IntPtr hwnd);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiOpenDeviceInfo(IntPtr set,string id,IntPtr hwnd,uint flags,ref Device device);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDeviceInstallParams(IntPtr set,ref Device device,ref InstallParameters parameters);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiSetDeviceInstallParams(IntPtr set,ref Device device,ref InstallParameters parameters);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiBuildDriverInfoList(IntPtr set,ref Device device,uint type);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiEnumDriverInfo(IntPtr set,ref Device device,uint type,uint index,ref Driver driver);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDriverInfoList(IntPtr set,ref Device device,uint type);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev.dll", SetLastError=true)] static extern bool DiInstallDevice(IntPtr hwnd,IntPtr set,ref Device device,ref Driver driver,uint flags,[MarshalAs(UnmanagedType.Bool)] out bool reboot);

    public static string Select(string instance, bool install)
    {
        if (!instance.StartsWith("USB\\VID_05AC&PID_0267&MI_01\\",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the A1644 USB keyboard interface is supported.");
        IntPtr set=SetupDiCreateDeviceInfoList(IntPtr.Zero,IntPtr.Zero);
        if(set==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
        var device=new Device {Size=Marshal.SizeOf(typeof(Device))};
        bool list=false;
        try {
            if(!SetupDiOpenDeviceInfo(set,instance,IntPtr.Zero,0,ref device))throw new Win32Exception(Marshal.GetLastWin32Error());
            var parameters=new InstallParameters {Size=Marshal.SizeOf(typeof(InstallParameters))};
            if(!SetupDiGetDeviceInstallParams(set,ref device,ref parameters))throw new Win32Exception(Marshal.GetLastWin32Error());
            parameters.Flags |= 0x10000 | 0x800000; // DI_ENUMSINGLEINF | DI_QUIETINSTALL
            parameters.DriverPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"INF","input.inf");
            if(!SetupDiSetDeviceInstallParams(set,ref device,ref parameters))throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!SetupDiBuildDriverInfoList(set,ref device,2))throw new Win32Exception(Marshal.GetLastWin32Error());
            list=true;
            var driver=new Driver {Size=Marshal.SizeOf(typeof(Driver))};
            if(!SetupDiEnumDriverInfo(set,ref device,2,0,ref driver))throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!String.Equals(driver.Provider,"Microsoft",StringComparison.OrdinalIgnoreCase))
                throw new Exception("Recovery driver provider is not Microsoft.");
            bool reboot=false;
            if(install && !DiInstallDevice(IntPtr.Zero,set,ref device,ref driver,0,out reboot))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return driver.Description+" | "+driver.Provider+" | input.inf | RebootRequired="+reboot;
        }
        finally {
            if(list)SetupDiDestroyDriverInfoList(set,ref device,2);
            SetupDiDestroyDeviceInfoList(set);
        }
    }
}
