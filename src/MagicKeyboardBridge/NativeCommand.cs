using System.Diagnostics;
namespace MagicKeyboardBridge;
public static class NativeCommand
{
 public sealed record Result(int ExitCode,string Output,string Error);
 public static Result Run(string executable,IEnumerable<string> arguments,int timeoutMs=30000)
 {
  var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  foreach(string argument in arguments)start.ArgumentList.Add(argument);
  using var process=Process.Start(start)??throw new IOException("Cannot start "+executable);
  var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
  if(!process.WaitForExit(timeoutMs)){try{process.Kill(true);}catch{}throw new TimeoutException(Path.GetFileName(executable)+" exceeded "+timeoutMs+"ms.");}
  if(!Task.WaitAll(new Task[]{output,error},5000))throw new TimeoutException("Process output did not close.");
  return new(process.ExitCode,output.Result,error.Result);
 }
 public static void Check(string executable,IEnumerable<string> arguments,int timeoutMs=30000)
 {
  var result=Run(executable,arguments,timeoutMs);
  if(result.ExitCode!=0)throw new IOException(Path.GetFileName(executable)+" failed ("+result.ExitCode+"): "+result.Output+result.Error);
 }
}
