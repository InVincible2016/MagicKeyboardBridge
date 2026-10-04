using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

// Read-only queries. This UMDF keyboard's child is HID\HIDCLASS\...;
// WMI's Keyboard list and a HID\VID_* name do not reliably identify it.
public static class BridgeHealth
{
    public static string VirtualRoot => MagicKeyboardBridge.BridgeSettings.Current.VirtualInstanceId;
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] static extern uint CM_Locate_DevNode(out uint node,string id,uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Child(out uint child,uint parent,uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Sibling(out uint sibling,uint node,uint flags);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] static extern uint CM_Get_Device_ID(uint node,StringBuilder id,int length,uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_Status(out uint status,out uint problem,uint node,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int processId);
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder image,ref int size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(IntPtr process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool ProcessIdToSessionId(int processId,out uint session);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public sealed class WorkerInfo { public string Image; public DateTime StartedUtc; public uint SessionId; public bool Running; }
    public sealed class NodeInfo { public string InstanceId; public uint Status; public uint Problem; public bool Started; }
    public static WorkerInfo ReadWorker(int processId)
    {
        IntPtr handle=OpenProcess(0x1000,false,processId);
        if(handle==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot query worker process.");
        try {
            var image=new StringBuilder(32768);int length=image.Capacity;uint code,session;long created,exited,kernel,user;
            if(!QueryFullProcessImageName(handle,0,image,ref length) || !GetExitCodeProcess(handle,out code)
                || !GetProcessTimes(handle,out created,out exited,out kernel,out user) || !ProcessIdToSessionId(processId,out session))
                throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot read worker identity.");
            return new WorkerInfo {Image=image.ToString(),StartedUtc=DateTime.FromFileTimeUtc(created),SessionId=session,Running=code==259};
        } finally {CloseHandle(handle);}
    }
    static NodeInfo ReadNode(uint node)
    {
        var id=new StringBuilder(1024);uint status,problem;
        uint result=CM_Get_Device_ID(node,id,id.Capacity,0);
        if(result!=0)throw new InvalidOperationException("Device identity query failed: "+result);
        result=CM_Get_DevNode_Status(out status,out problem,node,0);
        if(result!=0)throw new InvalidOperationException("Device status query failed: "+result);
        return new NodeInfo {InstanceId=id.ToString(),Status=status,Problem=problem,Started=(status&0x400)==0 && (status&8)!=0};
    }
    public static NodeInfo ReadDevice(string id)
    {
        uint node;uint result=CM_Locate_DevNode(out node,id,0);
        if(result!=0)throw new InvalidOperationException("Device not present: "+id+" ("+result+")");
        return ReadNode(node);
    }
    public static NodeInfo[] ReadVirtualKeyboard()
    {
        using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\"+VirtualRoot)) {
            var ids=key==null?null:key.GetValue("HardwareID") as string[];
            if(ids==null || !Array.Exists(ids,x=>String.Equals(x,@"root\VID_F055&PID_A164",StringComparison.OrdinalIgnoreCase))
                || !Array.Exists(ids,x=>String.Equals(x,@"root\MagicKeyboardBridge",StringComparison.OrdinalIgnoreCase))
                || !String.Equals(key.GetValue("Service") as string,"mshidumdf",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Virtual root ownership check failed.");
        }
        uint root;uint result=CM_Locate_DevNode(out root,VirtualRoot,0);
        if(result!=0)throw new InvalidOperationException("Virtual root not present: "+result);
        var nodes=new List<NodeInfo>();nodes.Add(ReadNode(root));
        uint child;
        if(CM_Get_Child(out child,root,0)==0) {
            do {
                if(nodes.Count>=32)throw new InvalidOperationException("Unexpected virtual device tree size.");
                nodes.Add(ReadNode(child));
            } while(CM_Get_Sibling(out child,child,0)==0);
        }
        return nodes.ToArray();
    }
}
