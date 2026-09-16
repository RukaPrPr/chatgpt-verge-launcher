using System;
using System.Globalization;
using System.IO;

namespace ChatGptVergeLauncher
{
    internal static class ControlProxy
    {
        internal const string HelperName = "ChatGPT-Verge-ControlProxy.exe";
        internal const string PortFile = "control-proxy.port";

        internal static int ReadPort(string root)
        {
            int port;
            if (!Int32.TryParse(File.ReadAllText(Path.Combine(root, PortFile)).Trim(),
                NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw new InvalidDataException("Invalid local proxy port. Run ChatGPT-Verge-Launcher.exe again.");
            return port;
        }

        internal static void ConfigureLauncher(string root, int port)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
            string helper = Path.Combine(root, HelperName);
            if (!File.Exists(helper)) throw new FileNotFoundException("请保留完整发布目录，缺少控制代理组件。", helper);
            // The sidecar crosses Codex's filtered MCP environment; it contains only a local port.
            string file = Path.Combine(root, PortFile);
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, port.ToString(CultureInfo.InvariantCulture));
                if (File.Exists(file)) File.Replace(temporary, file, null);
                else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            SetProcessProxy(port);
            Environment.SetEnvironmentVariable("CODEX_NODE_REPL_PATH", helper, EnvironmentVariableTarget.Process);
        }

        internal static void SetProcessProxy(int port)
        {
            string proxy = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
                Environment.SetEnvironmentVariable(key, proxy, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("NO_PROXY", "localhost,127.0.0.1", EnvironmentVariableTarget.Process);
        }

        internal static string ResolveRuntime()
        {
            // Codex regenerates this path for the currently selected runtime at every launch.
            string node = Environment.GetEnvironmentVariable("NODE_REPL_NODE_PATH");
            if (String.IsNullOrWhiteSpace(node) || !Path.IsPathRooted(node) || !File.Exists(node))
                throw new FileNotFoundException("Codex did not provide a valid NODE_REPL_NODE_PATH.");
            string runtime = Path.Combine(Path.GetDirectoryName(node), "node_repl.exe");
            if (!File.Exists(runtime)) throw new FileNotFoundException("Original node_repl.exe was not found next to node.exe.", runtime);
            return runtime;
        }
    }
}
