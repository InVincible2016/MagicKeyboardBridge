using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
namespace MagicKeyboardBridge;
public static class VirtualDevice
{
 const string HardwareId=@"root\MagicKeyboardBridge";
 [StructLayout(LayoutKind.Sequential)] struct Device{public int Size;public Guid Class;public uint DevInst;public IntPtr Reserved;}
 [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid cls,IntPtr hwnd);
 [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfo(IntPtr set,string id,ref Guid cls,string description,IntPtr hwnd,uint flags,ref Device device);
 [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set,ref Device device,uint property,byte[] data,int length);
 [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiCallClassInstaller(uint function,IntPtr set,ref Device device);
 [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
 [DllImport("newdev.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd,string hardware,string inf,uint flags,[MarshalAs(UnmanagedType.Bool)]out bool reboot);
 public static BridgeSettings Allocate(bool swapOptionCommand)
 {
  using var mutex=new Mutex(false,@"Global\MagicKeyboardBridgeSetup");
  if(!mutex.WaitOne(TimeSpan.FromSeconds(5)))throw new IOException("Another bridge setup is running.");
  try {
   var settings=new BridgeSettings{SwapOptionCommand=swapOptionCommand};
   for(int attempt=0;attempt<256;attempt++) {
    int index=RandomNumberGenerator.GetInt32(1024,65536);
    using var existing=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro\Controller"+index);
    if(existing!=null)continue;
    settings.ControllerIndex=index;
    WriteConfiguration(settings);
    return settings;
   }
   throw new IOException("Cannot allocate an unused virtual controller index.");
  }finally{mutex.ReleaseMutex();}
 }
 public static void WriteConfiguration(BridgeSettings settings)
 {
  settings.Validate();
  string path=@"SOFTWARE\HIDMaestro\Controller"+settings.ControllerIndex;
  using(var existing=Registry.LocalMachine.OpenSubKey(path))
   if(existing!=null && !Equals(existing.GetValue("MagicKeyboardBridgeOwner"),settings.InstallationId))throw new IOException("Controller index belongs to another application.");
  using var config=Registry.LocalMachine.CreateSubKey(path);
  config.SetValue("MagicKeyboardBridgeOwner",settings.InstallationId);
  config.SetValue("DeviceInstanceId",settings.VirtualInstanceId);
  config.SetValue("FunctionMode",0,RegistryValueKind.DWord);
  config.SetValue("VendorId",0xF055,RegistryValueKind.DWord);
  config.SetValue("ProductId",0xA164,RegistryValueKind.DWord);
  config.SetValue("VersionNumber",0x0100,RegistryValueKind.DWord);
  config.SetValue("Bluetooth",0,RegistryValueKind.DWord);
  config.SetValue("Ds3Sixaxis",0,RegistryValueKind.DWord);
  config.SetValue("ReportDescriptor",VirtualKeyboardGate.Descriptor,RegistryValueKind.Binary);
  config.SetValue("InputReportByteLength",9,RegistryValueKind.DWord);
  config.SetValue("ProductString","Magic Keyboard Bridge");
  config.SetValue("DeviceDescription","Magic Keyboard Bridge");
 }
 public static void Create(string inf)
 {
  var settings=BridgeSettings.Current;
  using(var old=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\"+settings.VirtualInstanceId)) {
   if(old!=null) {
    var ids=old.GetValue("HardwareID")as string[];
    if(ids==null || !ids.Contains(HardwareId,StringComparer.OrdinalIgnoreCase))throw new IOException("The virtual device identity belongs to another application.");
    using var parameters=old.OpenSubKey("Device Parameters");
    if(!Equals(parameters?.GetValue("ControllerIndex"),settings.ControllerIndex))throw new IOException("Virtual device index mismatch.");
    WriteConfiguration(settings);
    return; // Never remove and recreate a live keyboard during restart.
   }
  }
  WriteConfiguration(settings);
  Guid cls=new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
  IntPtr set=SetupDiCreateDeviceInfoList(ref cls,IntPtr.Zero);
  if(set==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
  try {
   var device=new Device{Size=Marshal.SizeOf<Device>()};
   if(!SetupDiCreateDeviceInfo(set,settings.VirtualInstanceId,ref cls,"Magic Keyboard Bridge",IntPtr.Zero,0,ref device))throw new Win32Exception(Marshal.GetLastWin32Error());
   byte[] ids=Encoding.Unicode.GetBytes(HardwareId+"\0root\\VID_F055&PID_A164\0\0");
   if(!SetupDiSetDeviceRegistryProperty(set,ref device,1,ids,ids.Length))throw new Win32Exception(Marshal.GetLastWin32Error());
   using(var key=Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Enum\"+settings.VirtualInstanceId)) {
    key.SetValue("ParentIdPrefix","1&"+settings.InstallationId[..8].ToLowerInvariant()+"&0");
    using var parameters=key.CreateSubKey("Device Parameters");
    parameters.SetValue("ControllerIndex",settings.ControllerIndex,RegistryValueKind.DWord);
   }
   if(!SetupDiCallClassInstaller(0x19,set,ref device))throw new Win32Exception(Marshal.GetLastWin32Error());
   if(!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero,HardwareId,Path.GetFullPath(inf),1,out bool reboot))throw new Win32Exception(Marshal.GetLastWin32Error());
   if(reboot)throw new IOException("Windows requires a restart to activate the virtual device. Physical keyboard was not changed.");
  }finally{SetupDiDestroyDeviceInfoList(set);}
 }
}
