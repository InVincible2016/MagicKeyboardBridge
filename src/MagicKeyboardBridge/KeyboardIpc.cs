using System.ComponentModel;
using System.Runtime.InteropServices;
namespace MagicKeyboardBridge;
// Wire layout from HIDMaestro v1.10.0 Internal/SharedMemoryIO.cs (MIT).
// Keyboard reports are readable only by SYSTEM, administrators and the UMDF
// LocalService host. Ordinary users are not granted the SDK's diagnostic read.
public sealed class KeyboardIpc : IDisposable
{
 [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes{public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
 [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text,uint revision,out IntPtr descriptor,out uint size);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
 [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr CreateFileMapping(IntPtr file,ref SecurityAttributes security,uint protection,uint high,uint low,string name);
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr MapViewOfFile(IntPtr mapping,uint access,uint high,uint low,UIntPtr size);
 [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr CreateEvent(ref SecurityAttributes security,bool manual,bool initial,string name);
 [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 readonly List<IntPtr> handles=new();
 public IntPtr Input {get;private set;}
 public IntPtr Output {get;private set;}
 public IntPtr InputEvent {get;private set;}
 public KeyboardIpc(int index) {
  if(index<1024||index>65535)throw new ArgumentOutOfRangeException(nameof(index));
  if(!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)",1,out var descriptor,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
  try {
   var security=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor};
   Input=Section(@"Global\HIDMaestroInput"+index,362,ref security);
   Output=Section(@"Global\HIDMaestroOutput"+index,16904,ref security);
   InputEvent=CreateEvent(ref security,false,false,@"Global\HIDMaestroInputEvent"+index);
   if(InputEvent==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
   handles.Add(InputEvent);
   Marshal.Copy(new byte[362],0,Input,362);
  }catch{Dispose();throw;}finally{LocalFree(descriptor);}
 }
 IntPtr Section(string name,uint size,ref SecurityAttributes security) {
  IntPtr handle=CreateFileMapping(new IntPtr(-1),ref security,4,0,size,name);
  if(handle==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
  handles.Add(handle);
  IntPtr view=MapViewOfFile(handle,0xF001F,0,0,(UIntPtr)size);
  if(view==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
  return view;
 }
 public void Dispose(){if(Input!=IntPtr.Zero)UnmapViewOfFile(Input);if(Output!=IntPtr.Zero)UnmapViewOfFile(Output);Input=Output=InputEvent=IntPtr.Zero;foreach(var handle in handles)CloseHandle(handle);handles.Clear();}
}
