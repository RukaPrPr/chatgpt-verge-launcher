using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ChatGptVergeLauncher;

internal sealed class FakePolicy : IGuardPolicy
{
    internal List<GuardRule> Rules = new List<GuardRule>();
    internal int FailAfter = -1;
    internal bool Available = true;
    public void CheckAvailable() { if (!Available) throw new IOException("policy unavailable"); }
    public IList<GuardRule> List() { return Rules.ToList(); }
    public void Add(GuardRule rule) { if (FailAfter == 0) throw new IOException("injected rule write failure"); if (FailAfter > 0) FailAfter--; Rules.Add(rule); }
    public void Remove(string name) { Rules.RemoveAll(rule => rule.Name == name); }
    public void Dispose() { }
}
internal static class GuardTests
{
    private static void Check(bool result, string label) { if (!result) throw new Exception(label); Console.WriteLine("PASS " + label); }
    private static void Reject(Action action, string label) { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } Check(rejected, label); }
    private static int Main(string[] args)
    {
        try {
            var runtime = ProxyRuntime.Prepare(args[0], args[1]);
            var plan = GuardPlan.Create(runtime);
            Check(plan.Programs.Contains(runtime.PackageExecutable) && plan.Programs.Contains(runtime.Node) && plan.Programs.Contains(runtime.Codex), "guard includes installed Codex, CLI and dedicated Node");
            foreach (string path in new[] { @"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"C:\Python\python.exe", @"C:\Program Files\Git\bin\git.exe", @"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe", Path.Combine(GuardPlan.PluginRoot,"openai-bundled","chrome","1","extension-host.exe"), Path.Combine(GuardPlan.CodexDataRoot,"bin-extra","node.exe") })
                Check(!GuardPlan.DedicatedPath(path), "scope excludes " + Path.GetFileName(path) + " / " + Path.GetDirectoryName(path));
            Check(GuardPlan.Decode(plan.Encode()).Programs.SequenceEqual(plan.Programs), "guard IPC plan round trip");
            var hostile = new GuardPlan { PackageRoot=plan.PackageRoot, Programs=new[]{ @"C:\Windows\System32\cmd.exe" } };
            Reject(hostile.Validate, "shared executable cannot enter guard plan");
            bool running = false;
            var policy = new FakePolicy();
            var foreign = new GuardRule {Name="OtherSoftware", Group="Unrelated",Program=@"C:\Other\app.exe"}; policy.Rules.Add(foreign);
            var guard = new NetworkGuard(policy, paths => running && paths.Any(), message => { });
            guard.Install(plan);
            Check(policy.Rules.Count == plan.Programs.Length * 2 + 1, "installs exact Codex target count in both directions");
            Check(policy.Rules.Where(r => r != foreign).All(r => r.Action==0 && (r.Direction==1 || r.Direction==2) && r.Protocol==256 && r.Profiles==7 && r.RemoteAddresses==NetworkGuard.NonLoopback), "direct TCP UDP IPv4 IPv6 blocked while loopback excluded");
            running = true;
            Check(!guard.Cleanup() && policy.Rules.Count==plan.Programs.Length*2+1, "helper exit retains protection for surviving Codex processes");
            Reject(guard.Recover, "recovery cannot remove active session protection");
            running = false; guard.Recover();
            Check(policy.Rules.Count==1 && policy.Rules[0]==foreign, "recovery removes only launcher-owned rules");
            var serviceGuard = new NetworkGuard(policy, paths => paths.Any(NetworkGuard.PersistentService), message => { });
            serviceGuard.Install(plan);
            serviceGuard.Cleanup();
            Check(policy.Rules.Count==3 && policy.Rules.Where(r=>r!=foreign).All(r=>NetworkGuard.PersistentService(r.Program)), "persistent Codex service keeps only its own two rules without being stopped");
            serviceGuard.Recover(); serviceGuard.Install(plan);
            Check(policy.Rules.Count==plan.Programs.Length*2+1, "next session reuses service rules without removing its live protection");
            guard.Recover();
            policy.FailAfter = 2;
            Reject(() => guard.Install(plan), "partial rule installation prevents application launch");
            Check(policy.Rules.Count==3, "partially installed protection survives interruption");
            var restarted = new NetworkGuard(policy, paths => false, message => { }); restarted.Recover();
            Check(policy.Rules.Count==1 && policy.Rules[0]==foreign, "new helper recovers partial install without touching foreign rules");
            policy.FailAfter = -1; policy.Available = false;
            Reject(() => guard.Install(plan), "disabled firewall fails closed without changing global settings");
            Check(policy.Rules.Count==1, "unavailable firewall does not modify any rule");
            policy.Available = true;
            var collision = NetworkGuard.RuleFor(plan.Programs[0]); collision.Group="Foreign"; policy.Rules.Add(collision);
            Reject(() => guard.Install(plan), "foreign rule name collision cannot be overwritten");
            policy.Rules.Remove(collision);
            guard.Recover(); // Inbound rule can precede the conflicting outbound name.
            var corrupt = NetworkGuard.RuleFor(plan.Programs[0]); corrupt.Program=@"C:\Program Files\Other\app.exe"; policy.Rules.Add(corrupt);
            Reject(guard.Recover, "unexpected owned rule path is preserved for inspection");
            policy.Rules.Remove(corrupt);
            dynamic detached = WindowsGuardPolicy.DetachedRule(NetworkGuard.RuleFor(plan.Programs[0]));
            try { Check((string)detached.RemoteAddresses==NetworkGuard.NonLoopback && (int)detached.Protocol==256, "Windows COM accepts scoped address and protocol fields without installing rule"); }
            finally { Marshal.ReleaseComObject(detached); }
            using (var liveReadOnly = new WindowsGuardPolicy()) { liveReadOnly.CheckAvailable(); Check(liveReadOnly.List().Count > 0, "live firewall availability and rules read without mutation"); }
            Console.WriteLine("GuardTargetCount=" + plan.Programs.Length + ";SystemFirewallModified=False;LiveFilteringTested=False");
            File.WriteAllLines(Path.Combine(args[0],"guard-plan.txt"), plan.Programs);
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
