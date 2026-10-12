using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ChatGptVergeLauncher
{
    internal sealed class RuntimePaths
    {
        public string PackageExecutable { get; set; }
        public string Codex { get; set; }
        public string Node { get; set; }
        public string NodeRepl { get; set; }
    }

    internal static class ProxyRuntime
    {
        internal const string NodeHelper = "ChatGPT-Verge-NodeProxy.exe";
        internal const string CliHelper = "ChatGPT-Verge-CodexProxy.exe";
        internal const string ManifestName = "proxy-runtime.json";
        internal const string PreloadName = "proxy-preload.cjs";

        internal static RuntimePaths Read(string root)
        {
            RuntimePaths paths = new JavaScriptSerializer().Deserialize<RuntimePaths>(File.ReadAllText(Path.Combine(root, ManifestName)));
            if (paths == null) throw new InvalidDataException("Missing proxy runtime manifest.");
            foreach (string path in new[] { paths.PackageExecutable, paths.Codex, paths.Node, paths.NodeRepl })
                if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path))
                    throw new InvalidDataException("Codex runtime changed or is unavailable. Restart with the launcher.");
            if (!String.Equals(Path.GetFileName(paths.Node), "node.exe", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetFileName(paths.NodeRepl), "node_repl.exe", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetFileName(paths.Codex), "codex.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid Codex runtime filenames.");
            return paths;
        }

        internal static RuntimePaths Prepare(string root, string packageExecutable)
        {
            foreach (string name in new[] { NodeHelper, CliHelper, PreloadName, ControlProxy.HelperName })
                if (!File.Exists(Path.Combine(root, name))) throw new FileNotFoundException("请保留完整发布目录：" + name);
            // Use the runtime from the selected package, rather than guessing which
            // extracted cache belongs to this application version after an update.
            string node = Path.Combine(Path.GetDirectoryName(packageExecutable), "resources", "cua_node", "bin", "node.exe");
            if (!File.Exists(node)) throw new FileNotFoundException("当前 Codex 程序包未提供预期的专用 Node 运行时；未使用其他软件的 Node。", node);
            var paths = new RuntimePaths {
                PackageExecutable = Path.GetFullPath(packageExecutable),
                Codex = Path.Combine(Path.GetDirectoryName(packageExecutable), "resources", "codex.exe"),
                Node = node,
                NodeRepl = Path.Combine(Path.GetDirectoryName(node), "node_repl.exe")
            };
            foreach (string path in new[] { paths.Codex, paths.NodeRepl })
                if (!File.Exists(path)) throw new FileNotFoundException("Codex runtime is incomplete.", path);
            AtomicWrite(Path.Combine(root, ManifestName), new JavaScriptSerializer().Serialize(paths));
            return Read(root);
        }

        internal static void AtomicWrite(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0; result.Append(c);
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        internal static void Configure(string root, int port)
        {
            ControlProxy.ConfigureLauncher(root, port);
            Read(root);
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", Path.Combine(root, CliHelper));
            Environment.SetEnvironmentVariable("CODEX_BROWSER_USE_NODE_PATH", Path.Combine(root, NodeHelper));
        }
    }
}
