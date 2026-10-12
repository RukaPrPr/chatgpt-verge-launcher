using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

[assembly: System.Reflection.AssemblyVersion("1.0.6.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.6.0")]

namespace ChatGptVergeLauncher
{
    internal static class RuntimeProxyHost
    {
        private static int Main(string[] args)
        {
            try {
                string root = AppDomain.CurrentDomain.BaseDirectory;
                RuntimePaths paths = ProxyRuntime.Read(root);
                ControlProxy.SetProcessProxy(ControlProxy.ReadPort(root));
#if NODE_PROXY
                string target = paths.Node;
                string preload = Path.Combine(root, ProxyRuntime.PreloadName);
                if (!File.Exists(preload)) throw new FileNotFoundException("Proxy preload is missing.", preload);
                args = new[] { "--require", preload }.Concat(args).ToArray();
#else
                string target = paths.Codex;
                // Desktop code overwrites this before launching the CLI; restore it here,
                // before the MCP allowlist copies CODEX_MCP_NODE_PATH to plugins.
                Environment.SetEnvironmentVariable("CODEX_MCP_NODE_PATH", Path.Combine(root, ProxyRuntime.NodeHelper));
#endif
                var info = new ProcessStartInfo(target, String.Join(" ", Array.ConvertAll(args, ProxyRuntime.Quote)));
                return ProxyProcess.Run(info);
            } catch (Exception error) { Console.Error.WriteLine("Codex proxy runtime: " + error.Message); return 1; }
        }
    }
}
