using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ChatGptVergeLauncher
{
    internal static class ProxyProcess
    {
        private static async Task Pump(Stream input, Stream output)
        {
            byte[] bytes = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(bytes, 0, bytes.Length)) != 0)
            {
                await output.WriteAsync(bytes, 0, count);
                // MCP responses must be visible before the process exits.
                await output.FlushAsync();
            }
        }

        internal static int Run(ProcessStartInfo info)
        {
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            // Pass the parent's input handle straight to the child. A background
            // Console input reader can remain blocked after a short-lived child
            // exits and prevent this Framework wrapper from completing shutdown.
            // The child is the sole reader, including for interactive MCP input.
            info.RedirectStandardInput = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using (Process child = Process.Start(info))
            using (var lifetime = new ChildLifetime(child))
            {
                Task output = Pump(child.StandardOutput.BaseStream, Console.OpenStandardOutput());
                Task error = Pump(child.StandardError.BaseStream, Console.OpenStandardError());
                child.WaitForExit();
                int exitCode = child.ExitCode;
                // Close descendants' inherited output handles before draining EOF.
                lifetime.Dispose();
                Task.WaitAll(output, error);
                return exitCode;
            }
        }
    }
}
