using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace ChatGptVergeLauncher
{
    internal sealed class GuardPlan
    {
        public string PackageRoot { get; set; }
        public string[] Programs { get; set; }

        internal static string CodexDataRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex"); } }
        internal static string PluginRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "plugins", "cache"); } }
        internal static bool Within(string path, string root)
        {
            return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool DedicatedPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
            if (Within(path, Path.Combine(PluginRoot, "openai-bundled", "chrome"))) return false;
            if (Within(path, Path.Combine(CodexDataRoot, "bin")) || Within(path, Path.Combine(CodexDataRoot, "runtimes")) || Within(path, PluginRoot)) return true;
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (!Within(path, packages)) return false;
            string relative = Path.GetFullPath(path).Substring(packages.Length + 1);
            string folder = relative.Split('\\')[0];
            // Never target another Store app, or a shared executable outside Codex.
            return folder.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
                folder.EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase) &&
                relative.StartsWith(folder + "\\app\\", StringComparison.OrdinalIgnoreCase) &&
                !relative.StartsWith(folder + "\\app\\resources\\plugins\\openai-bundled\\plugins\\chrome\\", StringComparison.OrdinalIgnoreCase);
        }

        private static void Collect(string directory, ISet<string> programs)
        {
            if (!Directory.Exists(directory)) return;
            if (String.Equals(directory, Path.Combine(PluginRoot, "openai-bundled", "chrome"), StringComparison.OrdinalIgnoreCase)) return;
            // Junctions/symlinks could lead outside the explicitly scoped directories.
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Codex 专用目录含重解析点，未扩大规则范围：" + directory);
            foreach (string file in Directory.GetFiles(directory, "*.exe")) {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Codex 程序含重解析点：" + file);
                if (DedicatedPath(file)) programs.Add(Path.GetFullPath(file));
            }
            foreach (string child in Directory.GetDirectories(directory)) Collect(child, programs);
        }

        internal static GuardPlan Create(RuntimePaths runtime)
        {
            if (!DedicatedPath(runtime.PackageExecutable)) throw new IOException("此修订版只处理 OpenAI.Codex 商店程序包。");
            var programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string packageRoot = Path.GetDirectoryName(runtime.PackageExecutable);
            foreach (string root in new[] { packageRoot, Path.Combine(CodexDataRoot, "bin"), Path.Combine(CodexDataRoot, "runtimes"), PluginRoot }) Collect(root, programs);
            var plan = new GuardPlan { PackageRoot = packageRoot, Programs = programs.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray() };
            plan.Validate(); return plan;
        }

        internal void Validate()
        {
            if (Programs == null || Programs.Length == 0 || Programs.Length > 4096 ||
                !DedicatedPath(Path.Combine(PackageRoot ?? "", "ChatGPT.exe"))) throw new IOException("无效的 Codex 网络保护范围。");
            foreach (string path in Programs) {
                if (!DedicatedPath(path) || !File.Exists(path)) throw new IOException("网络规则不能指向共享或缺失的程序：" + path);
                for (string current = path; current != null; current = Path.GetDirectoryName(current)) {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("网络规则路径含重解析点：" + path);
                }
            }
        }

        internal string Encode() { return Convert.ToBase64String(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(this))); }
        internal static GuardPlan Decode(string value)
        {
            if (value.Length > 1024 * 1024) throw new IOException("网络保护范围过大。");
            var plan = new JavaScriptSerializer().Deserialize<GuardPlan>(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
            if (plan == null) throw new IOException("缺少网络保护范围。");
            plan.Validate(); return plan;
        }
    }

    internal sealed class GuardRule
    {
        public string Name;
        public string Program;
        public string Group;
        public bool Enabled;
        public int Direction;
        public int Action;
        public int Protocol;
        public int Profiles;
        public string RemoteAddresses;
    }

    internal interface IGuardPolicy : IDisposable
    {
        void CheckAvailable();
        IList<GuardRule> List();
        void Add(GuardRule rule);
        void Remove(string name);
    }

    internal sealed class WindowsGuardPolicy : IGuardPolicy
    {
        private readonly dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true));
        public void CheckAvailable()
        {
            foreach (int profile in new[] { 1, 2, 4 }) if (!(bool)policy.FirewallEnabled[profile])
                throw new IOException("Windows 防火墙有未启用的配置文件。启动器不会修改全局设置，无法确认 Codex 直连保护。");
            if ((int)policy.LocalPolicyModifyState != 0) throw new IOException("系统策略不允许可靠应用 Codex 专用防火墙规则。");
        }
        public IList<GuardRule> List()
        {
            var result = new List<GuardRule>();
            foreach (dynamic rule in policy.Rules) {
                try {
                    result.Add(new GuardRule { Name = (string)rule.Name, Program = (string)rule.ApplicationName, Group = (string)rule.Grouping,
                        Enabled = (bool)rule.Enabled, Direction = (int)rule.Direction, Action = (int)rule.Action, Protocol = (int)rule.Protocol,
                        Profiles = (int)rule.Profiles, RemoteAddresses = (string)rule.RemoteAddresses });
                } finally { Marshal.ReleaseComObject(rule); }
            }
            return result;
        }
        internal static dynamic DetachedRule(GuardRule rule)
        {
            dynamic value = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true));
            value.Name = rule.Name; value.Grouping = rule.Group;
            value.Description = "Codex-only direct-network block. Local proxy and IPC remain available. Managed by ChatGPT Verge Launcher 1.0.6.";
            value.ApplicationName = rule.Program; value.Protocol = rule.Protocol;
            value.Direction = rule.Direction; value.Action = rule.Action; value.Profiles = rule.Profiles;
            value.RemoteAddresses = rule.RemoteAddresses; value.Enabled = rule.Enabled;
            return value;
        }
        public void Add(GuardRule rule)
        {
            dynamic value = DetachedRule(rule);
            try { policy.Rules.Add(value); } finally { Marshal.ReleaseComObject(value); }
        }
        public void Remove(string name) { policy.Rules.Remove(name); }
        public void Dispose() { Marshal.ReleaseComObject(policy); }
    }

    internal sealed class NetworkGuard : IDisposable
    {
        internal const string NonLoopback = "0.0.0.0-126.255.255.255,128.0.0.0-255.255.255.255,::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff";
        internal static string OwnerGroup { get { return "ChatGPT Verge Codex 1.0.6 " + WindowsIdentity.GetCurrent().User.Value; } }
        private readonly IGuardPolicy policy;
        private readonly Func<IEnumerable<string>, bool> running;
        private readonly Action<string> log;
        internal NetworkGuard(IGuardPolicy policy, Func<IEnumerable<string>, bool> running, Action<string> log) { this.policy = policy; this.running = running; this.log = log; }
        internal static NetworkGuard Open() { return new NetworkGuard(new WindowsGuardPolicy(), AnyRunning, RelaySession.Log); }

        internal static string NameFor(string path, int direction = 2)
        {
            using (var sha = SHA256.Create()) return "CodexVerge-106-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(OwnerGroup + "\n" + Path.GetFullPath(path).ToLowerInvariant()))).Replace("-", "").Substring(0, 32) + (direction == 1 ? "-in" : "-out");
        }
        internal static GuardRule RuleFor(string path, int direction = 2)
        {
            return new GuardRule { Name = NameFor(path, direction), Program = path, Group = OwnerGroup, Enabled = true, Direction = direction, Action = 0, Protocol = 256, Profiles = 7, RemoteAddresses = NonLoopback };
        }
        private static bool Matches(GuardRule actual, GuardRule expected)
        {
            return String.Equals(actual.Program, expected.Program, StringComparison.OrdinalIgnoreCase) && actual.Name == expected.Name &&
                actual.Group == expected.Group && actual.Enabled && actual.Direction == expected.Direction && actual.Action == 0 && actual.Protocol == 256 &&
                (actual.Profiles & 7) == 7 && String.Equals(actual.RemoteAddresses, NonLoopback, StringComparison.OrdinalIgnoreCase);
        }
        private IList<GuardRule> Owned()
        {
            var owned = policy.List().Where(rule => rule.Group == OwnerGroup).ToList();
            foreach (var rule in owned) if (!GuardPlan.DedicatedPath(rule.Program) || (rule.Direction != 1 && rule.Direction != 2) || rule.Name != NameFor(rule.Program, rule.Direction))
                throw new IOException("网络保护规则身份不匹配；未删除规则：" + rule.Name);
            return owned;
        }
        internal void Recover()
        {
            var owned = Owned();
            if (owned.Count == 0) return;
            if (running(owned.Where(rule => !PersistentService(rule.Program)).Select(rule => rule.Program))) throw new IOException("仍有受保护的 Codex 进程运行，已保留网络规则和当前连接。请先正常退出 Codex。");
            RemoveStoppedRules(owned, "GUARD_RECOVERED");
        }
        internal static bool PersistentService(string path) { return String.Equals(Path.GetFileName(path), "codex-windows-sandbox-service.exe", StringComparison.OrdinalIgnoreCase); }
        private void RemoveStoppedRules(IList<GuardRule> owned, string label)
        {
            int removed = 0;
            foreach (var rule in owned) {
                if (PersistentService(rule.Program) && running(new[] { rule.Program })) continue;
                policy.Remove(rule.Name); removed++;
            }
            var remaining = Owned();
            if (remaining.Any(rule => !PersistentService(rule.Program))) throw new IOException("Codex 网络规则恢复未完成。");
            log(label + " removed=" + removed + " serviceRulesRetained=" + remaining.Count);
        }
        internal void Install(GuardPlan plan)
        {
            plan.Validate(); policy.CheckAvailable();
            var existing = policy.List();
            if (Owned().Any(rule => !PersistentService(rule.Program) || !Matches(rule, RuleFor(rule.Program, rule.Direction)))) throw new IOException("存在尚未恢复的 Codex 网络规则。");
            if (running(plan.Programs.Where(path => !PersistentService(path)))) throw new IOException("Codex 专用组件仍在运行；未改变其网络连接。");
            foreach (string path in plan.Programs) {
                foreach (int direction in new[] { 1, 2 }) {
                    var rule = RuleFor(path, direction);
                    var prior = existing.FirstOrDefault(value => value.Name == rule.Name);
                    if (prior != null) {
                        if (PersistentService(path) && Matches(prior, rule)) continue;
                        throw new IOException("防火墙规则名称冲突，未覆盖已有规则。");
                    }
                    policy.Add(rule);
                }
            }
            var installed = Owned();
            foreach (string path in plan.Programs) foreach (int direction in new[] { 1, 2 })
                if (!installed.Any(rule => Matches(rule, RuleFor(path, direction)))) throw new IOException("未能确认 Codex 程序的网络保护：" + path);
            log("GUARD_READY count=" + installed.Count + " scope=CodexDedicatedPaths transport=TCP,UDP,IPv4,IPv6");
        }
        internal bool Cleanup()
        {
            var owned = Owned();
            if (running(owned.Where(rule => !PersistentService(rule.Program)).Select(rule => rule.Program))) { log("GUARD_RETAINED protected_processes_running; recover_after_exit"); return false; }
            RemoveStoppedRules(owned, "GUARD_REMOVED"); return true;
        }
        internal static bool AnyRunning(IEnumerable<string> paths)
        {
            var targets = new HashSet<string>(paths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(targets.Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
            foreach (Process process in Process.GetProcesses()) {
                try {
                    if (!names.Contains(process.ProcessName)) continue;
                    if (targets.Contains(process.MainModule.FileName)) return true;
                } catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { return true; } // Uncertain identity: retain only our own rules.
                finally { process.Dispose(); }
            }
            return false;
        }
        public void Dispose() { policy.Dispose(); }
    }
}
