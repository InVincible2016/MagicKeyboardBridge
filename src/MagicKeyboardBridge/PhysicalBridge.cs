using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MagicKeyboardBridge
{
    public static class DeviceBinding
    {
        [DllImport("newdev.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr parent, string hardwareId, string inf, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
        public static bool Select(string inf)
        {
            bool reboot;
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, "USB\\VID_05AC&PID_0267&MI_01", Path.GetFullPath(inf), 1, out reboot))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return reboot;
        }
    }

    public sealed class BridgeResult
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public DateTimeOffset Time { get; set; }
        public int ProcessId { get; set; }
        public int SessionId { get; set; }
        public bool TestSigningActive { get; set; }
        public long KeyboardReports { get; set; }
        public int FnPresses { get; set; }
        public int ControlPresses { get; set; }
        public int OtherReports { get; set; }
        public int LedUpdates { get; set; }
        public int SecondsRemaining { get; set; }
    }

    public static class PhysicalBridge
    {
        private static readonly Guid InterfaceGuid = new Guid("B7F3DB12-302E-4C46-A8F1-016D438F2CA1");
        [StructLayout(LayoutKind.Sequential)] struct DeviceInterface { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential, Pack=1)] struct UsbInterface { public byte Length, DescriptorType, Number, Alternate, Endpoints, Class, Subclass, Protocol, String; }
        [StructLayout(LayoutKind.Sequential)] struct Pipe { public int Type; public byte Id; public ushort MaxPacket; public byte Interval; }
        [StructLayout(LayoutKind.Sequential, Pack=1)] struct SetupPacket { public byte RequestType, Request; public ushort Value, Index, Length; }
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr window, uint flags);
        [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref DeviceInterface data);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterface data, IntPtr detail, uint size, out uint required, IntPtr info);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint create, uint flags, IntPtr template);
        [DllImport("winusb.dll", SetLastError=true)] static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr handle);
        [DllImport("winusb.dll")] static extern bool WinUsb_Free(IntPtr handle);
        [DllImport("winusb.dll", SetLastError=true)] static extern bool WinUsb_GetDescriptor(IntPtr handle, byte type, byte index, ushort language, byte[] buffer, uint length, out uint read);
        [DllImport("winusb.dll", SetLastError=true)] static extern bool WinUsb_QueryInterfaceSettings(IntPtr handle, byte alternate, out UsbInterface settings);
        [DllImport("winusb.dll", SetLastError=true)] static extern bool WinUsb_QueryPipe(IntPtr handle, byte alternate, byte index, out Pipe pipe);
        [DllImport("winusb.dll", SetLastError=true)] static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, uint length, ref uint value);
        [DllImport("winusb.dll", SetLastError=true)] static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipe, byte[] buffer, uint length, out uint read, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError=true)] static extern bool WinUsb_ControlTransfer(IntPtr handle, SetupPacket setup, byte[] bytes, uint length, out uint count, IntPtr overlapped);

        private static string FindDevice()
        {
            Guid guid = InterfaceGuid;
            IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var found = new List<string>();
            try
            {
                for (uint i=0;;i++)
                {
                    var data = new DeviceInterface { Size=Marshal.SizeOf<DeviceInterface>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 259) throw new Win32Exception(error);
                        break;
                    }
                    uint size;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out size, IntPtr.Zero);
                    if (size < 8 || size > 65536) throw new Exception("Invalid interface path length.");
                    IntPtr detail = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out size, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (path.IndexOf("vid_05ac&pid_0267&mi_01", StringComparison.OrdinalIgnoreCase) >= 0) found.Add(path);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            if (found.Count == 0) return null;
            if (found.Count != 1) throw new Exception("Expected one A1644 WinUSB interface, found " + found.Count);
            return found[0];
        }
        private static void Save(string path, BridgeResult result)
        {
            result.Time = DateTimeOffset.Now;
            BridgeSettings.AtomicWrite(path,result);
        }
        public static BridgeResult Run(string stopFile, string statusFile, int seconds)
        {
            if (seconds < 0 || seconds > 90) throw new ArgumentOutOfRangeException(nameof(seconds));
            var result = new BridgeResult { Status="Opening", ProcessId=Environment.ProcessId, SessionId=Process.GetCurrentProcess().SessionId, TestSigningActive=VirtualKeyboardGate.TestMode() };
            Save(statusFile, result);
            // No event hook, SendInput call, text logging, or access to other physical keyboards.
            string devicePath=null;
            for(int attempt=0;attempt<20;attempt++)
            {
                if(File.Exists(stopFile)) throw new OperationCanceledException("Test stopped before device opened.");
                try { devicePath=FindDevice(); if(devicePath==null)throw new IOException("Keyboard is disconnected."); break; }
                catch { if(attempt==19)throw;Thread.Sleep(500); }
            }
            using (var file = CreateFile(devicePath, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero))
            {
                if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr usb;
                if (!WinUsb_Initialize(file, out usb)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    uint read;
                    byte[] descriptor = new byte[18];
                    if (!WinUsb_GetDescriptor(usb, 1, 0, 0, descriptor, 18, out read) || read != 18
                        || BitConverter.ToUInt16(descriptor,8) != 0x05AC || BitConverter.ToUInt16(descriptor,10) != 0x0267)
                        throw new Exception("USB identity is not Apple A1644.");
                    UsbInterface settings;
                    if (!WinUsb_QueryInterfaceSettings(usb,0,out settings)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (settings.Number != 1 || settings.Class != 3) throw new Exception("Unexpected USB interface; refusing input capture.");
                    var pipes = new List<Pipe>();
                    for (byte i=0;i<settings.Endpoints;i++)
                    {
                        Pipe candidate;
                        if (!WinUsb_QueryPipe(usb,0,i,out candidate)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        if (candidate.Type == 3 && (candidate.Id & 0x80) != 0) pipes.Add(candidate);
                    }
                    if (pipes.Count != 1) throw new Exception("Expected exactly one interrupt input pipe.");
                    Pipe input = pipes[0];
                    if (input.MaxPacket < 10 || input.MaxPacket > 1024) throw new Exception("Unexpected interrupt packet size.");
                    uint timeout = 100;
                    if (!WinUsb_SetPipePolicy(usb,input.Id,3,4,ref timeout)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    // Report protocol. Some revisions reject an unchanged protocol request;
                    // packet parsing below still requires the exact known report format.
                    WinUsb_ControlTransfer(usb, new SetupPacket {RequestType=0x21,Request=0x0B,Value=1,Index=settings.Number,Length=0}, Array.Empty<byte>(),0,out read,IntPtr.Zero);
                    {
                        using (var keyboard = new PersistentKeyboard())
                        {
                            try
                            {
                                keyboard.SubmitRawReport(new byte[8]);
                                result.Status="Running"; result.Message="Physical USB bridge active.";
                                result.SecondsRemaining=seconds; Save(statusFile,result);
                                var timer=Stopwatch.StartNew(); long lastSave=0;
                                bool previousFn=false, previousControl=false;
                                byte[] buffer=new byte[input.MaxPacket];
                                while ((seconds == 0 || timer.Elapsed.TotalSeconds < seconds) && !File.Exists(stopFile))
                                {
                                    string probeFile=Path.Combine(Path.GetDirectoryName(statusFile),"probe.request");
                                    if(File.Exists(probeFile))
                                    {
                                        File.Delete(probeFile);
                                        keyboard.SubmitRawReport(new byte[] {0,0,0x73,0,0,0,0,0});
                                        Thread.Sleep(80);
                                        keyboard.SubmitRawReport(new byte[8]);
                                    }
                                    int led=keyboard.ReadLed();
                                    if (led >= 0)
                                    {
                                        byte[] output={1,(byte)led};
                                        if (WinUsb_ControlTransfer(usb,new SetupPacket {RequestType=0x21,Request=9,Value=0x0201,Index=settings.Number,Length=2},output,2,out read,IntPtr.Zero)) result.LedUpdates++;
                                    }
                                    if (WinUsb_ReadPipe(usb,input.Id,buffer,(uint)buffer.Length,out read,IntPtr.Zero))
                                    {
                                        if(read>0 && buffer[0]==1 && read!=10) throw new Exception("Unexpected keyboard report length; stopping safely.");
                                        if (read == 10 && buffer[0] == 1)
                                        {
                                            var report=new byte[10]; Array.Copy(buffer,report,10);
                                            bool fn=(report[9]&2)!=0, control=(report[1]&1)!=0;
                                            if(fn&&!previousFn)result.FnPresses++;
                                            if(control&&!previousControl)result.ControlPresses++;
                                            previousFn=fn;previousControl=control;
                                            keyboard.SubmitRawReport(A1644Mapper.Translate(report, BridgeSettings.Current.SwapOptionCommand));
                                            Array.Clear(report,0,report.Length);
                                            result.KeyboardReports++;
                                        }
                                        else { result.OtherReports++; }
                                        Array.Clear(buffer,0,buffer.Length);
                                    }
                                    else
                                    {
                                        int error=Marshal.GetLastWin32Error();
                                        if(error!=121) throw new Win32Exception(error,"Keyboard USB read failed");
                                        // A held key produces no new report. Timeout alone must not release it.
                                    }
                                    if(timer.ElapsedMilliseconds-lastSave>=500)
                                    {
                                        result.SecondsRemaining=Math.Max(0,seconds-(int)timer.Elapsed.TotalSeconds);
                                        Save(statusFile,result);lastSave=timer.ElapsedMilliseconds;
                                    }
                                }
                            }
                            finally
                            {
                                keyboard.SubmitRawReport(new byte[8]);
                                Thread.Sleep(100); // Let HIDClass consume the all-keys-up report before removal.
                            }
                        }
                    }
                    result.Status="Finished";result.SecondsRemaining=0;
                    result.Message="Bridge stopped and virtual keys released.";
                    Save(statusFile,result);
                }
                finally { WinUsb_Free(usb); }
            }
            return result;
        }
        public static void RunContinuous(string stopFile,string statusFile)
        {
            int reconnectFailures=0;
            while(!File.Exists(stopFile))
            {
                if(FindDevice()==null)
                {
                    Save(statusFile,new BridgeResult {Status="WaitingForKeyboard",ProcessId=Environment.ProcessId,SessionId=Process.GetCurrentProcess().SessionId,TestSigningActive=VirtualKeyboardGate.TestMode()});
                    Thread.Sleep(500);
                    continue;
                }
                var attempt=Stopwatch.StartNew();
                try { Run(stopFile,statusFile,0);reconnectFailures=0; }
                catch(OperationCanceledException) when(File.Exists(stopFile)) { return; }
                catch(Win32Exception error) when(error.NativeErrorCode is 6 or 31 or 995 or 1167 || FindDevice()==null)
                {
                    reconnectFailures=attempt.Elapsed.TotalSeconds>10?1:reconnectFailures+1;
                    if(reconnectFailures>=5 && FindDevice()!=null)throw;
                    // A fast unplug/replug may already enumerate the replacement interface.
                    Save(statusFile,new BridgeResult {Status="WaitingForKeyboard",Message="Reconnecting USB keyboard.",ProcessId=Environment.ProcessId,SessionId=Process.GetCurrentProcess().SessionId,TestSigningActive=VirtualKeyboardGate.TestMode()});
                    Thread.Sleep(500);
                }
            }
        }
    }
}
