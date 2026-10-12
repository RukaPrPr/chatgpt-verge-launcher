using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ChatGptVergeLauncher;

internal static class LaunchLogTests
{
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 4 && args[0] == "--shared-probe")
            {
                var log = LaunchLog.CreateAt(args[2], "probe");
                if (!log.Write(args[3])) throw new Exception(log.LastError);
                File.WriteAllText(args[1], LaunchLog.ReadPhysicalPath(args[2]) + "\n" + File.ReadAllText(args[2]), new UTF8Encoding(false));
                return 0;
            }
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logging-fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "main.log"), fallback = Path.Combine(root, "fallback.log");
            var logger = new LaunchLog(path, fallback);
            Check(logger.Write("中文 UTF-8 marker"), "UTF-8 log write succeeds");
            byte[] bytes = File.ReadAllBytes(path);
            Check(!(bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) && Encoding.UTF8.GetString(bytes).Contains("中文 UTF-8 marker"), "log bytes have UTF-8 content without BOM");
            Check(LaunchLog.ReadPhysicalPath(path) == Path.GetFullPath(path), "physical log handle resolves to requested file");
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                Check(logger.Write("reader-active"), "live reader does not block log writer");
            Parallel.For(0, 80, delegate(int n) { if (!logger.Write("parallel=" + n)) throw new Exception(logger.LastError); });
            Check(File.ReadAllLines(path).Length == 82, "concurrent records are complete and serialized");
            var errors = new StringWriter(); TextWriter original = Console.Error;
            try
            {
                Console.SetError(errors);
                using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check(!logger.Write("locked-primary-marker"), "primary sharing violation is reported without crashing lifecycle");
                Check(File.ReadAllText(fallback).Contains("locked-primary-marker") && File.ReadAllText(fallback).Contains("HRESULT=0x"), "fallback retains failed record and OS error");
                Check(errors.ToString().Contains("LOG_WRITE_FAILED"), "primary failure is emitted on stderr");
                Check(logger.Write("primary-recovered"), "primary logging resumes after sharing lock released");
                string blocker = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocker, "fixture");
                var broken = new LaunchLog(Path.Combine(blocker, "primary.log"), Path.Combine(blocker, "fallback.log"));
                Check(!broken.Write("both-paths-broken"), "double logging failure remains nonfatal to active session");
                Check(errors.ToString().Contains("LOG_FALLBACK_FAILED"), "double logging failure is explicit on stderr");
                bool rejected = false;
                try { LaunchLog.CreateAt(Path.Combine(blocker, "init.log"), "fixture"); } catch (IOException) { rejected = true; }
                Check(rejected, "initial log failure prevents starting a new session");
            }
            finally { Console.SetError(original); }
            File.WriteAllText(path, new string('x', 1048577));
            Check(logger.Write("after-rotation") && Array.FindAll(Directory.GetFiles(root), delegate(string file) { return Path.GetFileName(file).StartsWith("main.log.", StringComparison.Ordinal); }).Length == 1 && File.ReadAllText(path).Contains("after-rotation"), "large per-process log rotates without dropping next record");
            string id = Guid.NewGuid().ToString("N");
            Check(LaunchLog.PathFor(id, "launcher", 1) != LaunchLog.PathFor(id, "hosts", 1) && LaunchLog.PathFor(id, "launcher", 1) != LaunchLog.PathFor(id, "launcher", 2), "session roles and process IDs get separate files");
            bool invalid = false; try { LaunchLog.PathFor("../escape", "relay", 1); } catch (ArgumentException) { invalid = true; }
            Check(invalid, "invalid session paths rejected");
            Console.WriteLine("SystemHostsModified=False; ActiveRelayModified=False"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
