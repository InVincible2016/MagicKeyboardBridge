using MagicKeyboardBridge;
using System.Text.Json;
if(args.FirstOrDefault()=="child") {Console.WriteLine(JsonSerializer.Serialize(args.Skip(1)));Console.Error.Write("stderr marker");return 7;}
if(args.FirstOrDefault()=="wait") {Thread.Sleep(5000);return 0;}
if(args.FirstOrDefault()=="bulk") {Console.Write(new string('x',131072));Console.Error.Write(new string('y',131072));return 0;}
int checks=0;
void Assert(bool ok,string reason){checks++;if(!ok)throw new Exception(reason);}
byte[] Report(byte modifiers=0,byte flags=0,params byte[] keys){var result=new byte[10];result[0]=1;result[1]=modifiers;result[9]=flags;keys.CopyTo(result,3);return result;}
void Golden(byte[] source,string expected,bool swap=false){Assert(Convert.ToHexString(A1644Mapper.Translate(source,swap))==expected,"Golden keyboard report: "+expected);}
Golden(Report(),"0000000000000000");
Golden(Report(0,2,0x06),"0100060000000000"); // Fn+C -> Ctrl+C
Golden(Report(2,2,0x04),"0300040000000000"); // Fn+Shift+A
Golden(Report(2,0,0x04),"0200040000000000"); // release Fn before A
Golden(Report(1,0,0x50,0x4F,0x52,0x51,0x2A,0x28),"00004A4D4B4E4C49");
Golden(Report(1,0,0x3A,0x45,0x13,0x16,0x05),"0000687346474800");
Golden(Report(1,2,0x04),"1000040000000000");
Golden(Report(0,1),"00004C0000000000");
Golden(Report(0,1,0x4C),"00004C0000000000");
Golden(Report(0,1,4,5,6,7,8,9),"0000040506070809");
Golden(Report(4),"0800000000000000",true); // physical Option -> Windows
Golden(Report(8),"0400000000000000",true); // physical Command -> Alt
Golden(Report(64),"8000000000000000",true);
Golden(Report(128),"4000000000000000",true);
for(int i=0;i<256;i++){
 byte swapped=A1644Mapper.SwapOptionAndCommand((byte)i);
 Assert(A1644Mapper.SwapOptionAndCommand(swapped)==i,"Option/Command swapping must be an involution.");
 Assert((swapped&0x33)==(i&0x33),"Shift and Control must be preserved.");
}
foreach(var malformed in new[]{Array.Empty<byte>(),new byte[9],new byte[11],new byte[10]}){bool rejected=false;try{A1644Mapper.Translate(malformed);}catch(ArgumentException){rejected=true;}Assert(rejected,"Malformed or non-keyboard USB reports must be rejected.");}
foreach(string architecture in new[]{"NTamd64","NTarm64"}){
 string inf=DriverPackage.UsbInf(architecture);
 Assert(inf.Contains("[Devices."+architecture+"]"),"Architecture-specific INF section.");
 Assert(inf.Contains(@"USB\VID_05AC&PID_0267&MI_01"),"Only the A1644 keyboard USB interface is bound.");
 Assert(!inf.Contains("DelReg"),"Installer must not delete other keyboard filters.");
}
var settings=new BridgeSettings{ControllerIndex=1024};settings.Validate();
Assert(settings.VirtualInstanceId.StartsWith(@"ROOT\HIDCLASS\MKB_"),"Owned virtual-device namespace.");
foreach(int index in new[]{-1,0,1023,65536}){settings.ControllerIndex=index;bool rejected=false;try{settings.Validate();}catch(InvalidDataException){rejected=true;}Assert(rejected,"Out-of-range/reserved controller index rejected.");}
settings.ControllerIndex=2048;settings.InstallationId=@"..\another-device";
bool badIdentity=false;try{settings.Validate();}catch(InvalidDataException){badIdentity=true;}Assert(badIdentity,"Reject malformed device identity.");
for(byte error=1;error<=3;error++)Golden(Report(0,1,error,error,error,error,error,error),"0000"+String.Concat(Enumerable.Repeat(error.ToString("X2"),6)));
string child=Environment.ProcessPath!;
string[] arguments={"child","a b","a\"b","trailing\\","test-\u4e2d\u6587"};
var result=NativeCommand.Run(child,arguments);
Assert(result.ExitCode==7,"Native exit code survives redirected output.");
Assert(JsonSerializer.Deserialize<string[]>(result.Output)!.SequenceEqual(arguments.Skip(1)),"Native arguments preserve spaces, quotes, Unicode and trailing backslashes.");
Assert(result.Error=="stderr marker","Native stderr is preserved.");
var bulk=NativeCommand.Run(child,new[]{"bulk"});
Assert(bulk.Output.Length==131072 && bulk.Error.Length==131072,"Large parallel stdout/stderr do not deadlock.");
bool timedOut=false;var clock=System.Diagnostics.Stopwatch.StartNew();
try{NativeCommand.Run(child,new[]{"wait"},200);}catch(TimeoutException){timedOut=true;}
Assert(timedOut && clock.Elapsed.TotalSeconds<3,"Hung helpers are bounded and terminated.");
string fixture=Path.Combine(Path.GetTempPath(),"MagicKeyboardBridge-tests-"+Guid.NewGuid().ToString("N"));
try {
 Directory.CreateDirectory(fixture);
 string heartbeat=Path.Combine(fixture,"heartbeat.json");BridgeSettings.AtomicWrite(heartbeat,new{Value=1});
 using(var reader=new FileStream(heartbeat,FileMode.Open,FileAccess.Read,FileShare.Read)) {
  var writer=Task.Run(()=>BridgeSettings.AtomicWrite(heartbeat,new{Value=2}));
  Thread.Sleep(100);Assert(!writer.IsFaulted,"Atomic status update must tolerate a reader: "+writer.Exception);
  reader.Dispose();Assert(writer.Wait(2000),"Atomic status writer recovers after sharing contention.");
 }
 Assert(JsonDocument.Parse(File.ReadAllText(heartbeat)).RootElement.GetProperty("Value").GetInt32()==2,"Completed status stays valid JSON.");
 foreach(var architecture in new[]{System.Runtime.InteropServices.Architecture.X64,System.Runtime.InteropServices.Architecture.Arm64}) {
  string directory=Path.Combine(fixture,architecture.ToString());DriverPackage.ExtractPayload(directory,architecture);
  Assert(File.Exists(Path.Combine(directory,"Inf2Cat.exe")),"Neutral catalog tools extracted for "+architecture);
  Assert(File.Exists(Path.Combine(directory,"signtool.exe.manifest")),"Signing tool dependencies extracted for "+architecture);
  string inf=DriverPackage.VirtualInf(File.ReadAllText(Path.Combine(directory,"hidmaestro.inf")));
  Assert(inf.Contains(@"root\MagicKeyboardBridge") && !inf.Contains(@"root\HIDMaestro"),"Isolated driver hardware ID.");
  Assert(inf.Contains("MagicKeyboardBridge.dll") && inf.Contains("magickeyboardbridge.cat"),"Renamed catalog and service binary.");
  string virtualPackage=Path.Combine(directory,"virtual"),usbPackage=Path.Combine(directory,"usb");
  Directory.CreateDirectory(virtualPackage);Directory.CreateDirectory(usbPackage);
  File.WriteAllText(Path.Combine(virtualPackage,"MagicKeyboardBridge.inf"),inf,System.Text.Encoding.Unicode);
  File.Copy(Path.Combine(directory,"HIDMaestro.dll"),Path.Combine(virtualPackage,"MagicKeyboardBridge.dll"));
  File.WriteAllText(Path.Combine(usbPackage,"MagicKeyboardUsb.inf"),DriverPackage.UsbInf(architecture==System.Runtime.InteropServices.Architecture.X64?"NTamd64":"NTarm64"),System.Text.Encoding.ASCII);
  foreach(string package in new[]{virtualPackage,usbPackage}) {
   NativeCommand.Check(Path.Combine(directory,"Inf2Cat.exe"),new[]{"/driver:"+package,"/os:"+(architecture==System.Runtime.InteropServices.Architecture.X64?"10_X64":"10_RS3_ARM64")});
   Assert(Directory.GetFiles(package,"*.cat").Length==1,"Driver catalog builds for "+architecture+" "+Path.GetFileName(package));
  }
 }
}finally{if(Directory.Exists(fixture))Directory.Delete(fixture,true);}
Console.WriteLine(JsonSerializer.Serialize(new{Status="Passed",Checks=checks,PrototypeChecks=A1644Mapper.SelfTest(),PhysicalDeviceChanged=false}));

return 0;
