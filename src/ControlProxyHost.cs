using System;
using System.Diagnostics;
using System.Text;

[assembly: System.Reflection.AssemblyVersion("1.0.6.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.6.0")]

namespace ChatGptVergeLauncher
{
    internal static class ControlProxyHost
    {
        private static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                result.Append(c);
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }

        private static int Main(string[] args)
        {
            try
            {
                string runtime = ControlProxy.ResolveRuntime();
                ControlProxy.SetProcessProxy(ControlProxy.ReadPort(AppDomain.CurrentDomain.BaseDirectory));
                string[] quoted = Array.ConvertAll(args, Quote);
                ProcessStartInfo info = new ProcessStartInfo(runtime, String.Join(" ", quoted));
                return ProxyProcess.Run(info);
            }
            catch (Exception error)
            {
                // Never put launcher diagnostics into MCP stdout.
                Console.Error.WriteLine("Control proxy: " + error.Message);
                return 1;
            }
        }
    }
}
