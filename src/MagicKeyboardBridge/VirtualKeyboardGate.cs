using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MagicKeyboardBridge
{
    // Gate 1 only: can the UMDF framework expose a working keyboard on this PC?
    // Does not open or replace the physical Apple keyboard or alter boot configuration.
    public sealed class GateResult
    {
        public bool TestSigningActive;
        public int MappingChecks;
        public int KeyDown;
        public int KeyUp;
        public bool Passed;
        public string Note;
    }
    public static class VirtualKeyboardGate
    {
        private const ushort Vid = 0xF055, Pid = 0xA164; // Local experimental identity, not a physical product.
        public static readonly byte[] Descriptor = {
            0x05,0x01, 0x09,0x06, 0xA1,0x01, 0x85,0x01,
            0x05,0x07, 0x19,0xE0, 0x29,0xE7, 0x15,0x00, 0x25,0x01,
            0x75,0x01, 0x95,0x08, 0x81,0x02,
            0x75,0x08, 0x95,0x01, 0x81,0x01,
            0x05,0x08, 0x19,0x01, 0x29,0x05, 0x75,0x01, 0x95,0x05, 0x91,0x02,
            0x75,0x03, 0x95,0x01, 0x91,0x01,
            0x05,0x07, 0x19,0x00, 0x29,0x73, 0x15,0x00, 0x25,0x73,
            0x75,0x08, 0x95,0x06, 0x81,0x00, 0xC0
        };
        [StructLayout(LayoutKind.Sequential)] struct CodeIntegrity { public uint Length, Options; }
        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, ref CodeIntegrity info, int size, out int length);
        public static bool TestMode()
        {
            var ci = new CodeIntegrity { Length = 8 }; int length;
            int status = NtQuerySystemInformation(103, ref ci, 8, out length);
            if (status < 0) throw new Exception("Cannot determine code integrity state: " + status.ToString("X8"));
            return (ci.Options & 2) != 0;
        }
        delegate IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WindowClass { public uint Style; public WindowProc Proc; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string Menu, ClassName; }
        [StructLayout(LayoutKind.Sequential)] struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Position; public uint Private; }
        [StructLayout(LayoutKind.Sequential)] struct HidAttributes { public int Size; public ushort Vendor, Product, Version; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClass(ref WindowClass wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string cls, IntPtr instance);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
        [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint header);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
        [DllImport("user32.dll")] static extern bool PeekMessage(out Message msg, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Message msg);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint create, uint flags, IntPtr template);
        [DllImport("hid.dll")][return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
        static readonly WindowProc procedure = WindowMessage;
        static readonly Dictionary<IntPtr, bool> identities = new Dictionary<IntPtr, bool>();
        static int downs, ups;
        static bool IsOurDevice(IntPtr device)
        {
            bool match; if (identities.TryGetValue(device, out match)) return match;
            uint size = 0; GetRawInputDeviceInfo(device, 0x20000007, null, ref size);
            if (size == 0 || size > 16384) return false;
            var name = new StringBuilder((int)size + 1);
            if (GetRawInputDeviceInfo(device, 0x20000007, name, ref size) == uint.MaxValue) return false;
            using (var handle = CreateFile(name.ToString(), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                var attributes = new HidAttributes { Size = Marshal.SizeOf(typeof(HidAttributes)) };
                match = !handle.IsInvalid && HidD_GetAttributes(handle, ref attributes) && attributes.Vendor == Vid && attributes.Product == Pid;
            }
            match = match && name.ToString().IndexOf("1&"+BridgeSettings.Current.InstallationId.Substring(0,8)+"&0",StringComparison.OrdinalIgnoreCase)>=0;
            identities[device] = match; return match;
        }
        static IntPtr WindowMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
        {
            if (msg == 0x00FF)
            {
                uint header = (uint)(8 + 2 * IntPtr.Size), size = 0;
                GetRawInputData(lp, 0x10000003, IntPtr.Zero, ref size, header);
                if (size >= header + 16 && size < 4096)
                {
                    IntPtr data = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (GetRawInputData(lp, 0x10000003, data, ref size, header) == size
                            && Marshal.ReadInt32(data) == 1 && IsOurDevice(Marshal.ReadIntPtr(data, 8)))
                        {
                            int offset = (int)header;
                            // Ignore and never store any normal key or any other keyboard's input.
                            if (Marshal.ReadInt16(data, offset + 6) == 0x87)
                            {
                                if ((Marshal.ReadInt16(data, offset + 2) & 1) != 0) ups++; else downs++;
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(data); }
                }
            }
            return DefWindowProc(hwnd, msg, wp, lp);
        }
        static void Pump(int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            do
            {
                Message message;
                while (PeekMessage(out message, IntPtr.Zero, 0, 0, 1)) DispatchMessage(ref message);
                Thread.Sleep(5);
            } while (clock.ElapsedMilliseconds < milliseconds);
        }
        public static GateResult TestPersistent()
        {
            var result=new GateResult {TestSigningActive=TestMode(),MappingChecks=A1644Mapper.SelfTest()};
            if(result.TestSigningActive)throw new InvalidOperationException("Normal-mode test requires test signing OFF.");
            string name="MagicKeyboardPersistentProbe_"+Guid.NewGuid().ToString("N");
            IntPtr instance=GetModuleHandle(null),window=IntPtr.Zero;
            var wc=new WindowClass {Proc=procedure,Instance=instance,ClassName=name};
            if(RegisterClass(ref wc)==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                window=CreateWindowEx(0,name,"",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,instance,IntPtr.Zero);
                if(window==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
                if(!RegisterRawInputDevices(new RawDevice[] {new RawDevice {Page=1,Usage=6,Flags=0x100,Target=window}},1,(uint)Marshal.SizeOf(typeof(RawDevice))))throw new Win32Exception(Marshal.GetLastWin32Error());
                using(var keyboard=new PersistentKeyboard())
                {
                    Pump(1000);downs=ups=0;identities.Clear();
                    keyboard.SubmitRawReport(new byte[] {0,0,0x73,0,0,0,0,0});Pump(100);
                    keyboard.SubmitRawReport(new byte[8]);Pump(1000);
                    result.KeyDown=downs;result.KeyUp=ups;result.Passed=downs==1 && ups==1;
                    result.Note="Existing virtual keyboard tested with test signing OFF; physical keyboard unchanged.";
                }
                return result;
            }
            finally
            {
                RegisterRawInputDevices(new RawDevice[] {new RawDevice {Page=1,Usage=6,Flags=1,Target=IntPtr.Zero}},1,(uint)Marshal.SizeOf(typeof(RawDevice)));
                if(window!=IntPtr.Zero)DestroyWindow(window);
                UnregisterClass(name,instance);
            }
        }
        // Observe only our virtual keyboard. The SYSTEM worker emits the probe;
        // observing it in the interactive session verifies delivery across sessions.
        public static GateResult ObserveWorker(string requestPath)
        {
            var result=new GateResult {TestSigningActive=TestMode(),MappingChecks=A1644Mapper.SelfTest()};
            string name="MagicKeyboardObserver_"+Guid.NewGuid().ToString("N");
            IntPtr instance=GetModuleHandle(null), window=IntPtr.Zero;
            var wc=new WindowClass {Proc=procedure,Instance=instance,ClassName=name};
            if(RegisterClass(ref wc)==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                window=CreateWindowEx(0,name,"",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,instance,IntPtr.Zero);
                if(window==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
                var raw=new RawDevice[] {new RawDevice {Page=1,Usage=6,Flags=0x100,Target=window}};
                if(!RegisterRawInputDevices(raw,1,(uint)Marshal.SizeOf(typeof(RawDevice))))throw new Win32Exception(Marshal.GetLastWin32Error());
                downs=ups=0;identities.Clear();
                System.IO.File.WriteAllText(requestPath,"F24 probe");
                Pump(4000);
                result.KeyDown=downs;result.KeyUp=ups;result.Passed=downs==1 && ups==1;
                result.Note="Observed SYSTEM worker input in the interactive session. Normal boot still requires verification.";
                return result;
            }
            finally
            {
                if(System.IO.File.Exists(requestPath))System.IO.File.Delete(requestPath);
                RegisterRawInputDevices(new RawDevice[] {new RawDevice {Page=1,Usage=6,Flags=1,Target=IntPtr.Zero}},1,(uint)Marshal.SizeOf(typeof(RawDevice)));
                if(window!=IntPtr.Zero)DestroyWindow(window);
                UnregisterClass(name,instance);
            }
        }
    }
}
