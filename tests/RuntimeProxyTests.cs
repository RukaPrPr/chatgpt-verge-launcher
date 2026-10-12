using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using ChatGptVergeLauncher;

internal static class RuntimeProxyTests
{
    private static void Check(bool value,string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS "+label); }
    private static ProcessStartInfo Info(string executable,string arguments)
    {
        return new ProcessStartInfo(executable, arguments) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true };
    }
    private static int Main(string[] args)
    {
        try {
            string root = args[0];
            RuntimePaths runtime = ProxyRuntime.Read(root);
            ProxyRuntime.Configure(root,17896);
            Check(Environment.GetEnvironmentVariable("CODEX_CLI_PATH")==Path.Combine(root,ProxyRuntime.CliHelper) && Environment.GetEnvironmentVariable("CODEX_BROWSER_USE_NODE_PATH")==Path.Combine(root,ProxyRuntime.NodeHelper),"actual startup overrides select both runtime entrypoints");
            Environment.SetEnvironmentVariable("NODE_REPL_NODE_PATH",Path.Combine(root,ProxyRuntime.NodeHelper));
            // ResolveRuntime uses its own executable directory, which is this fixture directory.
            Check(ControlProxy.ResolveRuntime()==runtime.NodeRepl,"control proxy resolves real REPL behind Node entrypoint");
            string cli = Path.Combine(root,ProxyRuntime.CliHelper);
            using (var version=Process.Start(Info(cli,"--version"))) {
                version.StandardInput.Close(); var stdout=version.StandardOutput.ReadToEndAsync(); var stderr=version.StandardError.ReadToEndAsync();
                if (!version.WaitForExit(10000)) {version.Kill();throw new Exception("CLI version timeout");}
                Check(version.ExitCode==0 && stdout.Result.Contains("codex-cli"),"real installed CLI accepts wrapper and preserves version output");
            }
            runtime.Codex=Path.Combine(root,"fixture","codex.exe");
            ProxyRuntime.AtomicWrite(Path.Combine(root,ProxyRuntime.ManifestName),new JavaScriptSerializer().Serialize(runtime));
            var info=Info(cli,String.Join(" ",new[]{"中文 space","quote\"trail\\",""}.Select(ProxyRuntime.Quote)));
            foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","NO_PROXY","NODE_USE_ENV_PROXY"}) info.EnvironmentVariables.Remove(key);
            info.EnvironmentVariables["CODEX_MCP_NODE_PATH"]=@"C:\desktop-overwrote\node.exe";
            info.EnvironmentVariables["BROWSER_USE_AVAILABLE_BACKENDS"]="chrome,edge,iab";
            using(var process=Process.Start(info)) {
                var error=process.StandardError.ReadToEndAsync();
                byte[] payload=Encoding.UTF8.GetBytes("{\"message\":\"中文 héllo\"}\n");
                process.StandardInput.BaseStream.Write(payload,0,payload.Length);process.StandardInput.BaseStream.Flush();
                var line=process.StandardOutput.ReadLineAsync();
                Check(line.Wait(3000) && line.Result=="{\"message\":\"中文 héllo\"}","CLI proxy preserves interactive UTF-8 before EOF");
                process.StandardInput.Close();
                if(!process.WaitForExit(10000)){process.Kill();throw new Exception("CLI fixture timeout");}
                Check(process.ExitCode==37 && error.Result=="cli-stderr","filtered environment and overwritten Node path restored; arguments stderr and exit code preserved");
            }
            using(var process=Process.Start(Info(cli,"--wait"))) {
                var pidLine=process.StandardOutput.ReadLineAsync();
                if(!pidLine.Wait(3000)){process.Kill();throw new Exception("PID fixture timeout");}
                using(var child=Process.GetProcessById(Int32.Parse(pidLine.Result))) {
                    process.Kill(); process.WaitForExit();
                    Check(child.WaitForExit(5000),"killing proxy also terminates only its owned runtime child");
                }
            }
            runtime.Node=Path.Combine(root,"missing-node.exe");
            ProxyRuntime.AtomicWrite(Path.Combine(root,ProxyRuntime.ManifestName),new JavaScriptSerializer().Serialize(runtime));
            using(var process=Process.Start(Info(cli,"--version"))) {
                process.StandardInput.Close(); var stdout=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();process.WaitForExit();
                Check(process.ExitCode==1 && stdout.Result=="" && error.Result.Contains("runtime changed"),"stale runtime manifest refuses launch rather than using an unguarded binary");
            }
            return 0;
        } catch(Exception error) { Console.Error.WriteLine(error);return 1; }
    }
}
