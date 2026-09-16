using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using ChatGptVergeLauncher;
class IntegrationTests {
 static void Check(bool condition,string label){if(!condition)throw new Exception(label);Console.WriteLine("PASS "+label);}
 static string[] Run(string helper,string input,out int exit){
  var info=new ProcessStartInfo(helper,"\"space value\" \"quote\\\"value\" \"trailing\\\\\"");
  info.UseShellExecute=false;info.CreateNoWindow=true;info.RedirectStandardInput=true;info.RedirectStandardOutput=true;info.RedirectStandardError=true;
  using(var p=Process.Start(info)){
   var error=p.StandardError.ReadToEndAsync();
   var bytes=Encoding.UTF8.GetBytes(input);p.StandardInput.BaseStream.Write(bytes,0,bytes.Length);p.StandardInput.BaseStream.Flush();
   string first="";
   if(input.Length>0){var line=p.StandardOutput.ReadLineAsync();if(!line.Wait(3000)){p.Kill();throw new Exception("No response before stdin EOF");}first=line.Result+"\n";Check(first==input.Substring(0,input.IndexOf('\n')+1),"interactive response before stdin EOF");}
   var output=p.StandardOutput.ReadToEndAsync();p.StandardInput.Close();
   if(!p.WaitForExit(10000)){p.Kill();throw new Exception("Forwarding timed out");}
   exit=p.ExitCode;return new[]{first+output.Result,error.Result};
  }
 }
 static int Main(string[] args){try{
  string root=args[0],helper=Path.Combine(root,ControlProxy.HelperName);
  ControlProxy.ConfigureLauncher(root,17896);
  Check(Environment.GetEnvironmentVariable("CODEX_NODE_REPL_PATH")==helper,"launcher sets runtime override");
  Check(ControlProxy.ReadPort(root)==17896,"launcher persists selected local port");
  ControlProxy.ConfigureLauncher(root,17897);Check(ControlProxy.ReadPort(root)==17897,"atomic port update");
  ControlProxy.ConfigureLauncher(root,17896);
  Environment.SetEnvironmentVariable("NODE_REPL_NODE_PATH",Path.Combine(root,"node.exe"));
  foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","NO_PROXY"})Environment.SetEnvironmentVariable(key,null);
  int code;string payload="{\"message\":\"中文 héllo\"}\nsecond frame\n";
  var result=Run(helper,payload,out code);
  Check(code==37,"proxy restoration, quoted arguments, and exit-code forwarding");
  Check(result[0]==payload,"UTF-8 MCP stdout and stdin preserved");Check(result[1]=="probe-stderr","stderr remains separate");
  File.WriteAllText(Path.Combine(root,ControlProxy.PortFile),"0");result=Run(helper,"",out code);
  Check(code==1&&result[0]==""&&result[1].Contains("Invalid local proxy port"),"invalid proxy fails closed on stderr");
  File.WriteAllText(Path.Combine(root,ControlProxy.PortFile),"17896");Environment.SetEnvironmentVariable("NODE_REPL_NODE_PATH",null);result=Run(helper,"",out code);
  Check(code==1&&result[0]==""&&result[1].Contains("NODE_REPL_NODE_PATH"),"missing runtime fails clearly");
  return 0;
 }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
