using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Text;
using ChatGptVergeLauncher;

internal static class RecoveryTests
{
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("\uFEFF# 原始内容\n127.0.0.1 localhost\r\n# no final newline");
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static bool Same(byte[] a, byte[] b) { return Convert.ToBase64String(a) == Convert.ToBase64String(b); }
    private static string Fixture(string root)
    {
        string dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "hosts"); File.WriteAllBytes(path, Original); return path;
    }
    private static void Crash(string path, bool enable, string phase)
    {
        var info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
            "--crash " + PackageLaunch.QuoteArgument(path) + " " + enable + " " + phase);
        info.UseShellExecute = false; info.CreateNoWindow = true;
        using (var process = Process.Start(info))
        {
            if (!process.WaitForExit(10000)) { process.Kill(); throw new Exception("Crash fixture hung"); }
            if (process.ExitCode != 93) throw new Exception("Checkpoint not reached: " + phase + ", exit=" + process.ExitCode);
        }
    }
    private static void ExpectIOException(Action action, string name)
    {
        bool rejected = false; try { action(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } Check(rejected, name);
    }
    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--crash")
        {
            new HostsTransaction(args[1]).Set(Boolean.Parse(args[2]), delegate(string phase) { if (phase == args[3]) Environment.Exit(93); });
            return 94;
        }
        try
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recovery-fixtures"); Directory.CreateDirectory(root);
            string[] phases = { "stage-half", "stage-flushed", "journal-half", "journal-flushed", "journal-published", "before-replace", "after-replace", "commit-flushed" };
            foreach (bool enable in new[] { true, false }) foreach (string phase in phases)
            {
                string path = Fixture(root); var tx = new HostsTransaction(path);
                string acl = File.GetAccessControl(path).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                if (!enable) tx.Set(true);
                Crash(path, enable, phase);
                tx.Set(false);
                Check(Same(File.ReadAllBytes(path), Original) && !tx.HasPending &&
                    File.GetAccessControl(path).GetSecurityDescriptorSddlForm(AccessControlSections.Access) == acl,
                    "crash recovery exact bytes and ACL: " + (enable ? "enable/" : "disable/") + phase);
            }
            {
                string path = Fixture(root); var tx = new HostsTransaction(path);
                Crash(path, true, "before-replace");
                File.Move(path, Path.Combine(tx.RecoveryDirectory, "replaced.bak"));
                tx.Set(false);
                Check(Same(File.ReadAllBytes(path), Original) && !tx.HasPending, "partial ReplaceFile missing destination recovered from verified original");
            }
            {
                string path = Fixture(root); var tx = new HostsTransaction(path);
                Crash(path, true, "journal-published");
                File.AppendAllText(path, "\r\n192.0.2.10 external.test\r\n"); byte[] changed = File.ReadAllBytes(path);
                ExpectIOException(delegate { tx.Set(false); }, "external edit during pending transaction refused");
                Check(Same(changed, File.ReadAllBytes(path)) && tx.HasPending, "external edit and pending evidence preserved");
            }
            {
                string path = Fixture(root); var tx = new HostsTransaction(path);
                Crash(path, true, "journal-published");
                File.AppendAllText(Path.Combine(tx.RecoveryDirectory, "pending.txn"), "corruption");
                ExpectIOException(delegate { tx.Set(false); }, "corrupt journal refused");
                Check(Same(Original, File.ReadAllBytes(path)) && tx.HasPending, "corrupt journal does not overwrite hosts");
            }
            {
                string path = Fixture(root); var tx = new HostsTransaction(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ExpectIOException(delegate { tx.Set(true); }, "replacement sharing violation has no in-place fallback");
                tx.Set(false);
                Check(Same(Original, File.ReadAllBytes(path)), "sharing violation recovers unchanged original");
            }
            {
                string path = Fixture(root); var tx = new HostsTransaction(path); tx.Set(true);
                string addition = "\r\n192.0.2.11 another.test\r\n"; File.AppendAllText(path, addition);
                tx.Set(false);
                Check(Same(File.ReadAllBytes(path), Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Original) + addition)), "completed transaction permits unrelated later edits");
            }
            foreach (string reason in new[] { "proxy offline", "app already running", "package absent" })
            {
                string path = Fixture(root); var tx = new HostsTransaction(path); tx.Set(true);
                var plan = RelaySession.PrepareAfterRecovery(delegate { tx.Set(false); }, delegate {
                    Check(Same(Original, File.ReadAllBytes(path)), "recovery precedes preflight: " + reason); return null;
                });
                Check(plan == null, "early return does not launch relay: " + reason);
            }
            {
                bool called = false;
                ExpectIOException(delegate { RelaySession.PrepareAfterRecovery(delegate { throw new IOException("fixture"); }, delegate { called = true; return null; }); }, "failed recovery blocks preflight");
                Check(!called, "failed recovery cannot start app");
            }
            Console.WriteLine("SystemHostsModified=False; ActualPowerCutTested=False"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
