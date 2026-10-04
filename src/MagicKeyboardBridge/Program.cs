using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
namespace MagicKeyboardBridge;
public static class Program
{
 static void Print(object value)=>Console.WriteLine(JsonSerializer.Serialize(value,BridgeSettings.Json));
 static string RequireStage(string[] args){int at=Array.IndexOf(args,"--stage");if(at<0||at+1>=args.Length)throw new ArgumentException("--stage is required.");return Path.GetFullPath(args[at+1]);}
 static void RequireAdmin(){if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("Run Install.cmd and accept Windows administrator authorization.");}
 public static int Main(string[] args)
 {
  try {
   if(args.Length==0){Console.WriteLine("MagicKeyboardBridge: self-test | preflight | status | initialize | prepare-drivers | create-virtual | gate | worker | health | observe | bind | restore-device");return 0;}
   string command=args[0];
   if(command=="self-test"){Print(new{Status="Passed",MappingChecks=A1644Mapper.SelfTest()});return 0;}
   if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,19041) || RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64))throw new PlatformNotSupportedException("Windows 10 build 19041 or newer, x64 or ARM64, is required.");
   if(RuntimeInformation.OSArchitecture==Architecture.Arm64 && !OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))throw new PlatformNotSupportedException("ARM64 requires Windows 11 for the catalog tool x64 emulation.");
   if(command=="preflight") {
    if(VirtualKeyboardGate.TestMode())throw new InvalidOperationException("Windows test signing is enabled. This installer does not change boot settings.");
    var devices=ReadPhysicalDevices();
    Print(new{Status="Preflight",TestSigningActive=false,Architecture=RuntimeInformation.OSArchitecture.ToString(),Devices=devices});return 0;
   }
   string stage=RequireStage(args);
   if(command=="initialize") {
    RequireAdmin();if(File.Exists(Path.Combine(stage,"settings.json")))throw new IOException("Settings already exist.");
    var settings=VirtualDevice.Allocate(!args.Contains("--keep-option-command"));BridgeSettings.Save(stage,settings);Print(settings);return 0;
   }
   BridgeSettings.Load(stage);
   if(command=="status") {
    Print(new{Status=File.Exists(Path.Combine(stage,"armed"))?"Enabled":"Disabled",TestSigningActive=VirtualKeyboardGate.TestMode(),Heartbeat=File.Exists(Path.Combine(stage,"status.json"))?JsonDocument.Parse(File.ReadAllText(Path.Combine(stage,"status.json"))).RootElement.Clone():(JsonElement?)null});return 0;
   }
   RequireAdmin();
   switch(command) {
    case "prepare-drivers":DriverPackage.Prepare();Print(new{Status="DriverPackagesPrepared"});break;
    case "create-virtual":VirtualDevice.Create(Path.Combine(stage,"drivers","virtual","MagicKeyboardBridge.inf"));Print(new{Status="VirtualDeviceCreated"});break;
    case "gate":
     var first=VirtualKeyboardGate.TestPersistent();var second=VirtualKeyboardGate.TestPersistent();
     Print(new{Status=first.Passed&&second.Passed?"VirtualInputPassed":"VirtualInputFailed",First=first,Reconnect=second,PhysicalDriverChanged=false});return first.Passed&&second.Passed?0:1;
    case "observe":var delivery=VirtualKeyboardGate.ObserveWorker(Path.Combine(stage,"probe.request"));Print(delivery);return delivery.Passed?0:1;
    case "worker":
     DriverPackage.VerifyDependency();
     if(VirtualKeyboardGate.TestMode())throw new InvalidOperationException("Test signing must remain off.");
     PhysicalBridge.RunContinuous(Path.Combine(stage,"stop"),Path.Combine(stage,"status.json"));break;
    case "health":return CheckHealth(stage);
    case "remove-usb-package":DriverPackage.RemoveUsbPackage();Print(new{Status="UsbDriverPackageRemoved"});break;
    case "remove-virtual":
     DriverPackage.Remove();Print(new{Status="VirtualDriverRemoved"});break;
    case "bind":
     var devices=ReadPhysicalDevices();if(devices.Length!=1)throw new IOException("Connect exactly one supported A1644 USB keyboard.");
     if(!devices[0].Started || devices[0].Service!="HidUsb" || devices[0].LowerFilters.Length!=0)throw new IOException("The keyboard uses another driver/filter; restore its Microsoft driver before installation.");
     if(DeviceBinding.Select(Path.Combine(stage,"drivers","usb","MagicKeyboardUsb.inf")))throw new IOException("Windows requested a restart; rolling back the physical keyboard.");
     var bound=ReadPhysicalDevices();if(bound.Length!=1 || !bound[0].Service.Equals("WinUSB",StringComparison.OrdinalIgnoreCase))throw new IOException("USB binding did not take effect.");
     Print(new{Status="PhysicalKeyboardBound",RebootRequired=false});break;
    case "restore-device":
     foreach(var device in ReadPhysicalDevices()) {
      if(device.LowerFilters.Length!=0)throw new IOException("Unexpected keyboard filter; refusing to delete it.");
      if(!device.Service.Equals("HidUsb",StringComparison.OrdinalIgnoreCase)) {
       using var installed=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\"+device.Driver);
       string inf=installed?.GetValue("InfPath")as string;
       string expected=Path.Combine(stage,"drivers","usb","MagicKeyboardUsb.inf");
       if(!device.Service.Equals("WinUSB",StringComparison.OrdinalIgnoreCase) || String.IsNullOrEmpty(inf) || Path.GetFileName(inf)!=inf || !File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"INF",inf)).SequenceEqual(File.ReadAllBytes(expected)))throw new IOException("Another driver owns the physical keyboard; recovery left it unchanged.");
       InboxKeyboard.Select(device.InstanceId,true);
      }
      var current=ReadPhysicalDevices().Single(x=>x.InstanceId==device.InstanceId);
      using var driver=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\"+current.Driver);
      if(current.Service!="HidUsb" || !String.Equals(driver?.GetValue("InfPath")as string,"input.inf",StringComparison.OrdinalIgnoreCase))throw new IOException("Microsoft keyboard recovery could not be verified.");
     }
     Print(new{Status="MicrosoftKeyboardRestored",BootSettingsChanged=false});break;
    default:throw new ArgumentException("Unknown command: "+command);
   }
   return 0;
  }catch(Exception error) {
   if(args.FirstOrDefault()=="worker")try{BridgeSettings.AtomicWrite(Path.Combine(RequireStage(args),"status.json"),new{Status="Failed",Time=DateTimeOffset.Now,ProcessId=Environment.ProcessId,Message=error.ToString()});}catch{}
   if(args.FirstOrDefault()=="worker")try{Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"schtasks.exe"),"/Run /TN MagicKeyboardBridgeHealth"){UseShellExecute=false,CreateNoWindow=true})?.Dispose();}catch{}
   Console.Error.WriteLine(error.ToString());return 1;
  }
 }
 public sealed record PhysicalDevice(string InstanceId,string Service,string[] LowerFilters,string Driver,bool Started);
 public static PhysicalDevice[] ReadPhysicalDevices()
 {
  var devices=new List<PhysicalDevice>();
  using var parent=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_05AC&PID_0267&MI_01");
  if(parent==null)return devices.ToArray();
  foreach(string child in parent.GetSubKeyNames()) {
   string id=@"USB\VID_05AC&PID_0267&MI_01\"+child;
   bool started;try{started=BridgeHealth.ReadDevice(id).Started;}catch(InvalidOperationException){continue;}
   using var key=parent.OpenSubKey(child);
   devices.Add(new(id,key.GetValue("Service")as string??"",key.GetValue("LowerFilters")as string[]??Array.Empty<string>(),key.GetValue("Driver")as string??"",started));
  }
  return devices.ToArray();
 }
 public static int CheckHealth(string stage)
 {
  try {
   var status=JsonSerializer.Deserialize<BridgeResult>(File.ReadAllText(Path.Combine(stage,"status.json")),BridgeSettings.Json)??throw new IOException("No worker heartbeat.");
   var worker=BridgeHealth.ReadWorker(status.ProcessId);
   if(!worker.Running || worker.SessionId!=0 || !String.Equals(worker.Image,Path.Combine(stage,"app","MagicKeyboardBridge.exe"),StringComparison.OrdinalIgnoreCase))throw new IOException("Worker identity mismatch.");
   double age=(DateTimeOffset.UtcNow-status.Time).TotalSeconds;
   if(age < -2 || age>=30 || status.Time.UtcDateTime<worker.StartedUtc)throw new IOException("Worker heartbeat is stale.");
   if(status.Status is not ("Running" or "WaitingForKeyboard" or "Opening"))throw new IOException("Worker status: "+status.Status);
   if(status.Status=="Running") {
    var nodes=BridgeHealth.ReadVirtualKeyboard();
    if(!nodes[0].Started || !nodes.Skip(1).Any(x=>x.Started && x.InstanceId.StartsWith(@"HID\",StringComparison.OrdinalIgnoreCase)))throw new IOException("Virtual keyboard is not ready.");
   }
   var result=new{Status="Healthy",Time=DateTimeOffset.Now,CheckerSessionId=Process.GetCurrentProcess().SessionId,WorkerProcessId=status.ProcessId,WorkerStatus=status.Status,TestSigningActive=VirtualKeyboardGate.TestMode()};
   BridgeSettings.AtomicWrite(Path.Combine(stage,"health.json"),result);Print(result);return 0;
  }catch(Exception error) {
   var result=new{Status="Unhealthy",Time=DateTimeOffset.Now,Message=error.Message};BridgeSettings.AtomicWrite(Path.Combine(stage,"health.json"),result);Print(result);return 1;
  }
 }
}
