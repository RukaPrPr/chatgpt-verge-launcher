using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using ChatGptVergeLauncher;

internal static class RelayTests
{
    private static void Check(bool test, string name) { if (!test) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static bool Same(byte[] a, byte[] b) { return Convert.ToBase64String(a) == Convert.ToBase64String(b); }
    private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, name); }
    private static Process Child(int milliseconds) {
        var info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--child " + milliseconds);
        info.UseShellExecute = false; info.CreateNoWindow = true; return Process.Start(info);
    }
    private sealed class UnwritableLog : TextWriter
    {
        public override Encoding Encoding { get { return Encoding.UTF8; } }
        public override void WriteLine(string value) { throw new IOException("isolated diagnostic write failure"); }
    }
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--child") { Thread.Sleep(Int32.Parse(args[1])); return 0; }
        try
        {
            byte[] original = Encoding.UTF8.GetBytes("\uFEFF# 原始内容\n127.0.0.1 localhost\r\n# no final newline");
            byte[] enabled = HostsMapping.Transform(original, true);
            Check(Same(original, HostsMapping.Transform(enabled, false)), "hosts exact byte and BOM round trip");
            Check(Same(enabled, HostsMapping.Transform(enabled, true)), "hosts enable is idempotent");
            byte[] suffix = Encoding.UTF8.GetBytes("\r\n192.0.2.1 example.test\r\n");
            byte[] edited = new byte[enabled.Length + suffix.Length]; enabled.CopyTo(edited, 0); suffix.CopyTo(edited, enabled.Length);
            byte[] expected = new byte[original.Length + suffix.Length]; original.CopyTo(expected, 0); suffix.CopyTo(expected, original.Length);
            Check(Same(expected, HostsMapping.Transform(edited, false)), "cleanup preserves unrelated subsequent edits");
            Reject(delegate { HostsMapping.Transform(Encoding.ASCII.GetBytes("192.0.2.3 CODEX-CLOUD-BACKEND.CHATGPT.COM."), true); }, "existing domain alias mapping rejected");
            Reject(delegate { HostsMapping.Transform(Encoding.ASCII.GetBytes(HostsMapping.Block + HostsMapping.Block), false); }, "duplicate owned blocks rejected");
            Reject(delegate { HostsMapping.Transform(Encoding.ASCII.GetBytes(HostsMapping.Block.Replace("127.0.0.1", "127.0.0.2")), false); }, "edited owned block preserved for review");
            Reject(delegate { HostsMapping.Transform(Encoding.Unicode.GetBytes("hosts"), true); }, "unsupported encoding rejected");
            using (var app = Child(600)) using (var relay = Child(10000))
            {
                try { RelaySession.WatchApplication(app, relay); Check(app.HasExited && !relay.HasExited, "application exit releases session watch"); }
                finally { if (!relay.HasExited) relay.Kill(); }
            }
            using (var app = Child(10000)) using (var relay = Child(100))
            {
                bool failed = false;
                var captured = new StringWriter();
                TextWriter previous = Console.Error;
                try { Console.SetError(captured); RelaySession.WatchApplication(app, relay); } catch (IOException) { failed = true; }
                finally { Console.SetError(previous); if (!app.HasExited) app.Kill(); }
                Check(failed, "relay crash ends watch with explicit failure");
                string record = captured.ToString();
                Check(record.Contains("RELAY_EXIT_OBSERVED relayPid=" + relay.Id) && record.Contains("reason=app_still_running appPid=" + app.Id) && record.Contains("exitCode=0"), "relay exit log correlates process IDs and observed exit code");
            }
            using (var app = Child(10000)) using (var relay = Child(100))
            {
                Exception observed = null;
                TextWriter previous = Console.Error;
                try { Console.SetError(new UnwritableLog()); RelaySession.WatchApplication(app, relay); } catch (IOException error) { observed = error; }
                finally { Console.SetError(previous); if (!app.HasExited) app.Kill(); }
                Check(observed != null && observed.Message == "转发进程意外退出，正在撤销映射。", "diagnostic write failure preserves the existing relay-exit handling");
            }
            Console.WriteLine("SystemHostsModified=False");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
