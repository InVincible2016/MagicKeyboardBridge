using System.Text.Json;
using System.Text.RegularExpressions;
namespace MagicKeyboardBridge;
public sealed class BridgeSettings
{
 public const string OwnerName="MagicKeyboardBridge";
 public string Owner {get;set;}=OwnerName;
 public int Schema {get;set;}=1;
 public string InstallationId {get;set;}=Guid.NewGuid().ToString("N");
 public int ControllerIndex {get;set;}
 public bool SwapOptionCommand {get;set;}=true;
 public string CertificateThumbprint {get;set;}
 public string VirtualInstanceId => @"ROOT\HIDCLASS\MKB_"+InstallationId.ToUpperInvariant();
 public static BridgeSettings Current {get;private set;}
 public static string Stage {get;private set;}
 public static readonly JsonSerializerOptions Json=new(){WriteIndented=true,IncludeFields=true};
 public void Validate() {
  if(Owner!=OwnerName || Schema!=1 || !Regex.IsMatch(InstallationId??"","^[a-fA-F0-9]{32}$"))throw new InvalidDataException("Unsupported installation identity.");
  if(ControllerIndex<1024 || ControllerIndex>65535)throw new InvalidDataException("Invalid reserved controller index.");
  if(CertificateThumbprint!=null && !Regex.IsMatch(CertificateThumbprint,"^[A-Fa-f0-9]{40}$"))throw new InvalidDataException("Invalid certificate identity.");
 }
 public static void Load(string stage) {
  Stage=Path.GetFullPath(stage);
  Current=JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(Path.Combine(Stage,"settings.json")),Json)??throw new InvalidDataException("Missing settings.");
  Current.Validate();
 }
 public static void Save(string stage,BridgeSettings settings){settings.Validate();AtomicWrite(Path.Combine(stage,"settings.json"),settings);}
 public static void AtomicWrite(string file,object value) {
  string temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
  try {
   File.WriteAllText(temp,JsonSerializer.Serialize(value,Json));
   for(int retry=0;;retry++)try{File.Move(temp,file,true);break;}
   catch(Exception error) when(retry<25 && ((error is IOException && (error.HResult&0xFFFF) is 32 or 33) || (error is UnauthorizedAccessException && File.Exists(file)))){Thread.Sleep(10);}
  }finally{if(File.Exists(temp))File.Delete(temp);}
 }
}
