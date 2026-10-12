using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ChatGptVergeLauncher
{
    internal sealed class LaunchLog
    {
        internal const string Revision = "codex-proxy-6";
        internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".chatgpt-verge-launcher", "logs");
        private readonly object gate = new object();
        internal readonly string FilePath;
        internal readonly string FallbackPath;
        internal string LastError { get; private set; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

        internal LaunchLog(string path, string fallback)
        {
            FilePath = Path.GetFullPath(path);
            FallbackPath = Path.GetFullPath(fallback);
        }

        internal static string PathFor(string instance, string role, int pid)
        {
            Guid parsed;
            if (!Guid.TryParseExact(instance, "N", out parsed)) throw new ArgumentException("Invalid log session.");
            if (role != "launcher" && role != "hosts" && role != "relay") throw new ArgumentException("Invalid log role.");
            return Path.Combine(Root, instance, role + "-" + pid + ".log");
        }

        internal static LaunchLog Create(string instance, string role)
        {
            int pid = Process.GetCurrentProcess().Id;
            return CreateAt(PathFor(instance, role, pid), role);
        }

        internal static LaunchLog CreateAt(string path, string role)
        {
            var log = new LaunchLog(path, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics",
                role + "-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N") + ".error.log"));
            if (!log.Write("LOG_READY revision=" + Revision + " pid=" + Process.GetCurrentProcess().Id + " path=" + log.FilePath))
                throw new IOException("无法创建会话日志；尚未启用新会话。" + log.LastError + "；备用记录：" + log.FallbackPath);
            return log;
        }

        private static string PhysicalPath(FileStream stream)
        {
            var buffer = new StringBuilder(32768);
            uint count = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (count == 0 || count >= buffer.Capacity) throw new IOException("无法核验日志实际路径，Win32=" + Marshal.GetLastWin32Error());
            string path = buffer.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }

        internal static string ReadPhysicalPath(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return PhysicalPath(file);
        }

        private static void Append(string path, string line)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // One file per process. Readers may inspect live logs without blocking the writer.
            // Reject path redirection rather than silently writing a different file.
            if (File.Exists(path) && new FileInfo(path).Length > 1048576)
            {
                if (!String.Equals(ReadPhysicalPath(path), path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("日志轮转路径发生重定向：" + path);
                File.Move(path, path + "." + DateTime.UtcNow.Ticks);
            }
            using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                string physical = PhysicalPath(file);
                if (!String.Equals(physical, path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("日志路径被重定向：" + path + " -> " + physical);
                byte[] bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
                file.Write(bytes, 0, bytes.Length);
                file.Flush();
            }
        }

        internal bool Write(string message)
        {
            lock (gate)
            {
                string line = DateTime.UtcNow.ToString("o") + " " + message;
                try { Append(FilePath, line); return true; }
                catch (Exception error)
                {
                    if (!(error is IOException) && !(error is UnauthorizedAccessException)) throw;
                    LastError = error.GetType().Name + " HRESULT=0x" + error.HResult.ToString("X8") + " " + error.Message;
                    string failure = DateTime.UtcNow.ToString("o") + " LOG_WRITE_FAILED primary=" + FilePath + " error=" + LastError + " record=" + message;
                    // Logging failure must not tear down a working TLS tunnel or interrupt hosts cleanup.
                    try { Console.Error.WriteLine(failure); } catch (IOException) { }
                    try { Append(FallbackPath, failure); }
                    catch (Exception fallbackError)
                    {
                        if (!(fallbackError is IOException) && !(fallbackError is UnauthorizedAccessException)) throw;
                        LastError += "; fallback=" + fallbackError.Message;
                        try { Console.Error.WriteLine("LOG_FALLBACK_FAILED " + LastError); } catch (IOException) { }
                    }
                    return false;
                }
            }
        }

        internal static void PrintDiagnostics(string legacyDirectory)
        {
            Console.WriteLine("SessionLogRoot=" + Root);
            Console.WriteLine("LogFallbackDirectory=" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics"));
            string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics");
            if (Directory.Exists(fallback))
                foreach (string file in Directory.GetFiles(fallback, "*.error.log")) Console.WriteLine("LOG_WRITE_FAILURE_RECORD=" + file);
            if (Directory.Exists(Root))
            {
                string[] files = Directory.GetFiles(Root, "*.log", SearchOption.AllDirectories);
                Array.Sort(files, delegate(string a, string b) { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); });
                for (int i = 0; i < Math.Min(files.Length, 6); i++)
                    Console.WriteLine("RecentSessionLog=" + files[i] + "; PhysicalPath=" + ReadPhysicalPath(files[i]));
            }
            foreach (string name in new[] { "session.log", "relay.log" })
            {
                string path = Path.Combine(legacyDirectory, name);
                if (!File.Exists(path)) continue;
                try
                {
                    string physical = ReadPhysicalPath(path);
                    Console.WriteLine("LegacyLog=" + path + "; PhysicalPath=" + physical + "; Redirected=" + !String.Equals(path, physical, StringComparison.OrdinalIgnoreCase));
                }
                catch (IOException error) { Console.WriteLine("LegacyLogReadFailed=" + error.Message); }
            }
        }
    }
}
