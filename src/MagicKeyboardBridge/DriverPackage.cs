using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
namespace MagicKeyboardBridge;
public static class DriverPackage
{
 public const string CoreSha256="DA0BE0B400AE095CA694AADB94CFF390282B43EB318E6349B0BC2358222A6A94";
 public static void VerifyDependency()
 {
  string dll=Path.Combine(AppContext.BaseDirectory,"HIDMaestro.Core.dll");
  using var file=File.OpenRead(dll);
  if(Convert.ToHexString(SHA256.HashData(file))!=CoreSha256)throw new InvalidDataException("HIDMaestro dependency integrity check failed.");
 }
 public static void ExtractPayload(string destination, Architecture architecture)
 {
  VerifyDependency();
  if(architecture is not (Architecture.X64 or Architecture.Arm64))throw new ArgumentException("Unsupported native payload.");
  Directory.CreateDirectory(destination);
  string prefix=architecture==Architecture.Arm64?"HIDMaestro.Native.arm64.":"HIDMaestro.Native.x64.";
  string[] native={"HIDMaestro.dll","hidmaestro.inf","signtool.exe","signtool.exe.manifest","mssign32.dll","Microsoft.Windows.Build.Signing.mssign32.dll.manifest","wintrust.dll","wintrust.dll.ini","Microsoft.Windows.Build.Signing.wintrust.dll.manifest","appxsip.dll","Microsoft.Windows.Build.Appx.AppxSip.dll.manifest","appxpackaging.dll","Microsoft.Windows.Build.Appx.AppxPackaging.dll.manifest","opcservices.dll","Microsoft.Windows.Build.Appx.OpcServices.dll.manifest"};
  string[] neutral={"Inf2Cat.exe","inf2cat.exe.manifest","WindowsProtectedFiles.xml","Microsoft.UniversalStore.HardwareWorkflow.Cabinets.dll","Microsoft.UniversalStore.HardwareWorkflow.Catalogs.dll","Microsoft.UniversalStore.HardwareWorkflow.InfReader.dll","Microsoft.UniversalStore.HardwareWorkflow.SubmissionBuilder.dll","Microsoft.Kits.Logger.dll"};
  var assembly=Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"HIDMaestro.Core.dll"));
  var resources=assembly.GetManifestResourceNames().ToDictionary(x=>x,StringComparer.OrdinalIgnoreCase);
  foreach(var pair in native.Select(file=>(file,resource:prefix+file)).Concat(neutral.Select(file=>(file,resource:"HIDMaestro.Resources."+file)))) {
   if(!resources.TryGetValue(pair.resource,out string actual))throw new InvalidDataException("Missing native dependency: "+pair.file);
   using var input=assembly.GetManifestResourceStream(actual)??throw new IOException("Missing driver resource.");
   using var output=File.Create(Path.Combine(destination,pair.file));input.CopyTo(output);
  }
 }
 public static string VirtualInf(string source)=>source.Replace("HIDMaestro","MagicKeyboardBridge",StringComparison.Ordinal).Replace("hidmaestro.cat","magickeyboardbridge.cat",StringComparison.OrdinalIgnoreCase).Replace("Game Controller","Magic Keyboard Bridge",StringComparison.Ordinal);
 public static void Prepare()
 {
  VerifyDependency();
  string stage=BridgeSettings.Stage;
  var settings=BridgeSettings.Current;
  string tools=Path.Combine(stage,"driver-tools"),virtualDir=Path.Combine(stage,"drivers","virtual"),usbDir=Path.Combine(stage,"drivers","usb");
  foreach(string directory in new[]{tools,virtualDir,usbDir})Directory.CreateDirectory(directory);
  ExtractPayload(tools,RuntimeInformation.OSArchitecture);
  string text=File.ReadAllText(Path.Combine(tools,"hidmaestro.inf"));
  text=VirtualInf(text);
  File.WriteAllText(Path.Combine(virtualDir,"MagicKeyboardBridge.inf"),text,Encoding.Unicode);
  File.Copy(Path.Combine(tools,"HIDMaestro.dll"),Path.Combine(virtualDir,"MagicKeyboardBridge.dll"),true);
  string arch=RuntimeInformation.OSArchitecture==Architecture.Arm64?"NTarm64":"NTamd64";
  File.WriteAllText(Path.Combine(usbDir,"MagicKeyboardUsb.inf"),UsbInf(arch),Encoding.ASCII);
  string thumb=settings.CertificateThumbprint;
  if(thumb==null) {
   using var rsa=RSA.Create(3072);
   var request=new CertificateRequest("CN=MagicKeyboardBridge-"+settings.InstallationId,rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
   request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,true));
   request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new Oid("1.3.6.1.5.5.7.3.3")},true));
   request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
   using var temporary=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10),DateTimeOffset.UtcNow.AddYears(5));
   byte[] pfx=temporary.Export(X509ContentType.Pfx);
   try {
    using var privateCert=X509CertificateLoader.LoadPkcs12(pfx,null,X509KeyStorageFlags.MachineKeySet|X509KeyStorageFlags.PersistKeySet);
    using var store=new X509Store(StoreName.My,StoreLocation.LocalMachine);store.Open(OpenFlags.ReadWrite);store.Add(privateCert);
    thumb=privateCert.Thumbprint;
    using var publicCert=X509CertificateLoader.LoadCertificate(privateCert.Export(X509ContentType.Cert));
    foreach(var name in new[]{StoreName.Root,StoreName.TrustedPublisher}){using var trust=new X509Store(name,StoreLocation.LocalMachine);trust.Open(OpenFlags.ReadWrite);trust.Add(publicCert);}
   }finally{CryptographicOperations.ZeroMemory(pfx);}
   settings.CertificateThumbprint=thumb;BridgeSettings.Save(stage,settings);
  }
  using(var store=new X509Store(StoreName.My,StoreLocation.LocalMachine)) {
   store.Open(OpenFlags.ReadOnly);var matches=store.Certificates.Find(X509FindType.FindByThumbprint,thumb,false);
   if(matches.Count!=1 || !matches[0].HasPrivateKey || matches[0].NotAfter<DateTime.Now || matches[0].Subject!="CN=MagicKeyboardBridge-"+settings.InstallationId)throw new IOException("Installation signing certificate is unavailable or does not match.");
  }
  string signing=Path.Combine(tools,"signtool.exe"),catalog=Path.Combine(tools,"Inf2Cat.exe");
  NativeCommand.Check(signing,new[]{"sign","/sm","/s","My","/sha1",thumb,"/fd","SHA256",Path.Combine(virtualDir,"MagicKeyboardBridge.dll")});
  foreach(string directory in new[]{virtualDir,usbDir}) {
   string os=RuntimeInformation.OSArchitecture==Architecture.Arm64?"10_RS3_ARM64":"10_X64";
   NativeCommand.Check(catalog,new[]{"/driver:"+directory,"/os:"+os});
   foreach(string cat in Directory.GetFiles(directory,"*.cat")) {
    NativeCommand.Check(signing,new[]{"sign","/sm","/s","My","/sha1",thumb,"/fd","SHA256",cat});
    NativeCommand.Check(signing,new[]{"verify","/pa",cat});
   }
  }
 }
 public static void RemoveUsbPackage()
 {
  string source=Path.Combine(BridgeSettings.Stage,"drivers","usb","MagicKeyboardUsb.inf");
  if(!File.Exists(source))return;
  byte[] expected=File.ReadAllBytes(source);
  string pnputil=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"pnputil.exe");
  foreach(string inf in Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"INF"),"oem*.inf")) {
   if(!File.ReadAllBytes(inf).SequenceEqual(expected))continue;
   var result=NativeCommand.Run(pnputil,new[]{"/delete-driver",Path.GetFileName(inf),"/uninstall"});
   if(result.ExitCode is not (0 or 3010))throw new IOException("USB driver package removal failed: "+result.Output+result.Error);
  }
 }
 public static void Remove()
 {
  var settings=BridgeSettings.Current;
  string pnputil=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"pnputil.exe");
  using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\"+settings.VirtualInstanceId)) {
   if(key!=null) {
    var ids=key.GetValue("HardwareID")as string[];
    using var parameters=key.OpenSubKey("Device Parameters");
    if(ids==null || !ids.Contains(@"root\MagicKeyboardBridge",StringComparer.OrdinalIgnoreCase) || !Equals(parameters?.GetValue("ControllerIndex"),settings.ControllerIndex))throw new IOException("Virtual device ownership mismatch.");
    var removal=NativeCommand.Run(pnputil,new[]{"/remove-device",settings.VirtualInstanceId});
    if(removal.ExitCode!=0)throw new IOException("Physical keyboard restored, but Windows could not remove the virtual device: "+removal.Output+removal.Error);
   }
  }
  foreach(string source in new[]{Path.Combine(BridgeSettings.Stage,"drivers","virtual","MagicKeyboardBridge.inf"),Path.Combine(BridgeSettings.Stage,"drivers","usb","MagicKeyboardUsb.inf")}) {
   if(!File.Exists(source))continue;
   byte[] expected=File.ReadAllBytes(source);
   foreach(string inf in Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"INF"),"oem*.inf")) {
    if(!File.ReadAllBytes(inf).SequenceEqual(expected))continue;
    var removal=NativeCommand.Run(pnputil,new[]{"/delete-driver",Path.GetFileName(inf),"/uninstall"});
    if(removal.ExitCode!=0)throw new IOException("Driver package removal needs review: "+removal.Output+removal.Error);
   }
  }
  string configPath=@"SOFTWARE\HIDMaestro\Controller"+settings.ControllerIndex;
  using(var config=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(configPath)) {
   if(config!=null && !Equals(config.GetValue("MagicKeyboardBridgeOwner"),settings.InstallationId))throw new IOException("Controller configuration owner changed.");
  }
  Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(configPath,false);
  if(settings.CertificateThumbprint!=null)foreach(var name in new[]{StoreName.My,StoreName.Root,StoreName.TrustedPublisher}) {
   using var store=new X509Store(name,StoreLocation.LocalMachine);store.Open(OpenFlags.ReadWrite);
   foreach(var certificate in store.Certificates.Find(X509FindType.FindByThumbprint,settings.CertificateThumbprint,false)) {
    if(certificate.Subject!="CN=MagicKeyboardBridge-"+settings.InstallationId)throw new IOException("Certificate owner changed.");
    if(name==StoreName.My && certificate.HasPrivateKey) {
     using var privateKey=certificate.GetRSAPrivateKey();
     if(privateKey is RSACng cng)cng.Key.Delete();
     else if(privateKey is RSACryptoServiceProvider csp){csp.PersistKeyInCsp=false;csp.Clear();}
     else throw new IOException("Unknown private-key provider; certificate retained for manual cleanup.");
    }
    store.Remove(certificate);
   }
  }
 }
 public static string UsbInf(string arch)
 {
  if(arch!="NTamd64"&&arch!="NTarm64")throw new ArgumentException("Unsupported architecture.");
  return "[Version]\r\nSignature=\"$Windows NT$\"\r\nClass=USBDevice\r\nClassGUID={88BAE032-5A81-49f0-BC3D-A4FF138216D6}\r\nProvider=%Provider%\r\nCatalogFile=MagicKeyboardUsb.cat\r\nDriverVer=10/03/2026,0.4.0.0\r\nPnpLockdown=1\r\n\r\n[Manufacturer]\r\n%Provider%=Devices,"+arch+"\r\n[Devices."+arch+"]\r\n%Device%=USB_Install,USB\\VID_05AC&PID_0267&MI_01\r\n[USB_Install]\r\nInclude=winusb.inf\r\nNeeds=WINUSB.NT\r\n[USB_Install.Services]\r\nInclude=winusb.inf\r\nNeeds=WINUSB.NT.Services\r\n[USB_Install.HW]\r\nAddReg=Interface\r\n[Interface]\r\nHKR,,DeviceInterfaceGUIDs,0x00010000,\"{B7F3DB12-302E-4C46-A8F1-016D438F2CA1}\"\r\n[Strings]\r\nProvider=\"MagicKeyboardBridge\"\r\nDevice=\"Apple Magic Keyboard A1644 USB Bridge\"\r\n";
 }
}
