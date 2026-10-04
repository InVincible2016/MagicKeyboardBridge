using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace MagicKeyboardBridge
{
    // Adapter for the pinned HIDMaestro 1.10.0 IPC. Reconnect to the existing
    // keyboard after process exit/reboot; do not delete a live keyboard devnode.
    // IPC layout: upstream Internal/SharedMemoryIO.cs (MIT, notices included).
    public sealed class PersistentKeyboard : IDisposable
    {
        public static string InstanceId => BridgeSettings.Current.VirtualInstanceId;
        private static int Index => BridgeSettings.Current.ControllerIndex;
        private readonly Mutex owner;
        private bool locked;
        private IntPtr input, output, inputEvent;
        private uint sequence, lastOutput;
        private KeyboardIpc memory;
        [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] static extern uint CM_Locate_DevNode(out uint node,string id,uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_Status(out uint status,out uint problem,uint node,uint flags);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetEvent(IntPtr handle);

        public static void ValidateApi(){DriverPackage.VerifyDependency();}
        public PersistentKeyboard()
        {
            owner=new Mutex(false,@"Global\MagicKeyboardBridgeWriter_"+BridgeSettings.Current.InstallationId);
            try
            {
                try { locked=owner.WaitOne(0); } catch(AbandonedMutexException) { locked=true; }
                if(!locked)throw new InvalidOperationException("Another keyboard writer is active.");
                using(var device=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\"+InstanceId))
                {
                    var ids=device?.GetValue("HardwareID") as string[];
                    if(ids==null || !ids.Contains(@"root\VID_F055&PID_A164",StringComparer.OrdinalIgnoreCase)
                        || !ids.Contains(@"root\MagicKeyboardBridge",StringComparer.OrdinalIgnoreCase)
                        || !String.Equals(device.GetValue("Service") as string,"mshidumdf",StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The installed virtual keyboard identity does not match.");
                    using(var parameters=device.OpenSubKey("Device Parameters"))
                        if(!Equals(parameters?.GetValue("ControllerIndex"),Index))
                            throw new InvalidOperationException("Virtual keyboard controller index changed.");
                }
                using(var config=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro\Controller"+Index))
                {
                    if(config==null || !Equals(config.GetValue("MagicKeyboardBridgeOwner"),BridgeSettings.Current.InstallationId) || !Equals(config.GetValue("VendorId"),0xF055) || !Equals(config.GetValue("ProductId"),0xA164))
                        throw new InvalidOperationException("The controller configuration is missing or belongs to another device.");
                }
                VirtualDevice.WriteConfiguration(BridgeSettings.Current);
                bool ready=false;
                for(int i=0;i<150;i++)
                {
                    uint node,status,problem;
                    if(CM_Locate_DevNode(out node,InstanceId,0)==0 && CM_Get_DevNode_Status(out status,out problem,node,0)==0
                        && problem==0 && (status&8)!=0) { ready=true;break; }
                    Thread.Sleep(100);
                }
                if(!ready)throw new InvalidOperationException("The installed virtual keyboard did not become ready.");
                memory=new KeyboardIpc(Index);
                input=memory.Input;inputEvent=memory.InputEvent;output=memory.Output;
                if(input==IntPtr.Zero || inputEvent==IntPtr.Zero || output==IntPtr.Zero)
                    throw new InvalidOperationException("Virtual keyboard IPC is unavailable.");
                SubmitRawReport(new byte[8]);
            }
            catch { Dispose();throw; }
        }
        public void SubmitRawReport(byte[] report)
        {
            if(input==IntPtr.Zero)throw new ObjectDisposedException(nameof(PersistentKeyboard));
            if(report==null || report.Length!=8)throw new ArgumentException("Expected eight keyboard data bytes.");
            uint pending=unchecked(sequence+1);
            Marshal.WriteInt32(input,0,unchecked((int)pending));
            Thread.MemoryBarrier();
            Marshal.WriteInt32(input,4,8);
            Marshal.Copy(report,0,IntPtr.Add(input,8),8);
            Marshal.WriteInt32(input,278,0); // legacy keyboard payload; no extended report
            Thread.MemoryBarrier();
            sequence=unchecked(pending+1);
            Marshal.WriteInt32(input,0,unchecked((int)sequence));
            if(!SetEvent(inputEvent))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public int ReadLed()
        {
            if(output==IntPtr.Zero)return -1;
            uint head=unchecked((uint)Marshal.ReadInt32(output,0));
            if(head==0 || head==lastOutput)return -1;
            int slot=8+(int)((head-1)%64)*264;
            uint before=unchecked((uint)Marshal.ReadInt32(output,slot));
            byte reportId=Marshal.ReadByte(output,slot+5);
            ushort size=unchecked((ushort)Marshal.ReadInt16(output,slot+6));
            int led=Marshal.ReadByte(output,slot+8)&31;
            Thread.MemoryBarrier();
            if(before!=head || unchecked((uint)Marshal.ReadInt32(output,slot))!=head)return -1;
            lastOutput=head;
            return reportId==1 && size==1 ? led : -1;
        }
        public void Dispose()
        {
            if(input!=IntPtr.Zero) { try { SubmitRawReport(new byte[8]);Thread.Sleep(100); } catch { } }
            if(memory!=null) { memory.Dispose();memory=null; }
            input=output=inputEvent=IntPtr.Zero;
            if(locked) { owner.ReleaseMutex();locked=false; }
            owner.Dispose();
        }
    }
}
