using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ChatGptVergeLauncher
{
    internal static class ControlProxyHost
    {
        private static async Task Pump(Stream source, Stream destination)
        {
            byte[] buffer = new byte[4096];
            int count;
            while ((count = await source.ReadAsync(buffer, 0, buffer.Length)) != 0)
            {
                await destination.WriteAsync(buffer, 0, count);
                // Framework pipe streams buffer small writes; MCP cannot wait for EOF.
                await destination.FlushAsync();
            }
        }

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
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardInput = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (Process child = Process.Start(info))
                {
                    // Preserve MCP byte streams, including UTF-8 and newline framing.
                    Task.Run(async delegate
                    {
                        try { await Pump(Console.OpenStandardInput(), child.StandardInput.BaseStream); child.StandardInput.Close(); }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                    });
                    Task output = Pump(child.StandardOutput.BaseStream, Console.OpenStandardOutput());
                    Task error = Pump(child.StandardError.BaseStream, Console.OpenStandardError());
                    child.WaitForExit();
                    Task.WaitAll(output, error);
                    return child.ExitCode;
                }
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
