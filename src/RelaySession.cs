using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ChatGptVergeLauncher
{
    internal static class RelaySession
    {
        internal const string RelayName = "ChatGPT-DurableRelay.exe";
        internal static readonly string StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatGPT-Verge-Launcher", "DurableRelay");
        private static LaunchLog sessionLog;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

        internal static void Log(string message)
        {
            if (sessionLog != null) sessionLog.Write(message);
            else Console.Error.WriteLine("SESSION_LOG_UNAVAILABLE " + message);
        }

        private static void LogRelayLifecycle(string action, Process relay, string reason, int appPid = 0)
        {
            // Extra diagnostics must not prevent the existing cleanup action.
            try
            {
                Log(action + " relayPid=" + relay.Id + " reason=" + reason + " appPid=" + appPid +
                    " exitCode=" + (relay.HasExited ? relay.ExitCode.ToString() : "running"));
            }
            catch (Exception error)
            {
                try { Console.Error.WriteLine("RELAY_DIAGNOSTIC_FAILED type=" + error.GetType().Name); } catch { }
            }
        }

        private static string ReadLine(StreamReader reader, int milliseconds)
        {
            var pending = reader.ReadLineAsync();
            if (!pending.Wait(milliseconds)) throw new TimeoutException("等待权限管理进程响应超时。");
            string line = pending.Result;
            if (line == null) throw new IOException("启动器通信中断。");
            if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                throw new IOException(Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(6))));
            return line;
        }

        internal static void CheckNetwork(int port)
        {
            using (var tcp = new TcpClient())
            {
                if (!tcp.ConnectAsync(IPAddress.Loopback, port).Wait(5000)) throw new TimeoutException("本地转发连接超时。");
                using (var tls = new SslStream(tcp.GetStream(), false))
                {
                    if (!tls.AuthenticateAsClientAsync(HostsMapping.Domain, null, SslProtocols.Tls12, false).Wait(15000))
                        throw new TimeoutException("经本地代理的 TLS 验证超时。");
                    if (!tls.IsAuthenticated || !tls.IsEncrypted) throw new IOException("后台 TLS 校验失败。");
                }
            }
        }

        internal static void Run(int proxyPort, Func<int> launch, bool simulateParentExit = false)
        {
            RunPrepared(delegate { return launch == null ? null : new LaunchPlan(proxyPort, launch); }, simulateParentExit);
        }

        internal sealed class LaunchPlan
        {
            internal readonly int ProxyPort;
            internal readonly Func<int> Launch;
            internal readonly GuardPlan Guard;
            internal LaunchPlan(int proxyPort, Func<int> launch, GuardPlan guard = null) { ProxyPort = proxyPort; Launch = launch; Guard = guard; }
        }

        internal static LaunchPlan PrepareAfterRecovery(Action recover, Func<LaunchPlan> prepare)
        {
            recover();
            return prepare();
        }

        internal static void RunPrepared(Func<LaunchPlan> prepare, bool simulateParentExit = false)
        {
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            if (principal.IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("请直接双击启动器，不要选择“以管理员身份运行”。启动器会为 Codex 专用网络规则和 hosts 管理弹出一次 UAC，应用保持普通权限。");
            Directory.CreateDirectory(StateDirectory);
            string instance = Guid.NewGuid().ToString("N");
            sessionLog = LaunchLog.Create(instance, "launcher");
            string pipeName = "ChatGPT-Verge-Hosts-" + instance;
            string relayPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelayName);
            Process relay = null;
            Process elevated = null;
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security))
            {
                try
                {
                    IAsyncResult connecting = pipe.BeginWaitForConnection(null, null);
                    using (connecting.AsyncWaitHandle)
                    {
                        var info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location);
                        info.UseShellExecute = true;
                        info.Verb = "runas";
                        info.WindowStyle = ProcessWindowStyle.Hidden;
                        info.Arguments = "--hosts-session " + pipeName + " " + Process.GetCurrentProcess().Id + " " + (relay == null ? 0 : relay.Id) + " " + instance;
                        elevated = Process.Start(info);
                        if (!connecting.AsyncWaitHandle.WaitOne(30000)) throw new TimeoutException("权限管理进程未连接。");
                        pipe.EndWaitForConnection(connecting);
                    }
                    uint client;
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out client) || client != elevated.Id) throw new IOException("权限管理进程身份不匹配。");
                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                    {
                        writer.AutoFlush = true;
                        LaunchPlan plan = PrepareAfterRecovery(delegate {
                            if (ReadLine(reader, 15000) != "PREPARED") throw new IOException("未完成残留映射检查。");
                        }, prepare);
                        if (plan == null) writer.WriteLine("DONE");
                        else
                        {
                            int proxyPort = plan.ProxyPort;
                            string ready = Path.Combine(StateDirectory, instance + ".ready.json");
                            var relayInfo = new ProcessStartInfo(relayPath);
                            relayInfo.UseShellExecute = false;
                            relayInfo.CreateNoWindow = true;
                            relayInfo.Arguments = "--instance " + instance + " --listen-port 443 --proxy-port " + proxyPort + " --ready-file " + PackageLaunch.QuoteArgument(ready);
                            relay = Process.Start(relayInfo);
                            var watch = Stopwatch.StartNew();
                            while (!File.Exists(ready) && !relay.HasExited && watch.ElapsedMilliseconds < 5000) Thread.Sleep(50);
                            if (!File.Exists(ready) || relay.HasExited) throw new IOException("转发进程未就绪。请检查端口占用、会话日志和发布目录 diagnostics 备用记录。会话：" + instance);
                            CheckNetwork(443);
                            if (plan.Guard != null) {
                                writer.WriteLine("GUARD:" + plan.Guard.Encode());
                                if (ReadLine(reader, 30000) != "GUARD_READY") throw new IOException("Codex 网络保护未就绪。");
                            }
                            writer.WriteLine("ENABLE:" + relay.Id);
                            if (ReadLine(reader, 15000) != "READY") throw new IOException("hosts 未就绪。");
                            IPAddress[] addresses = Dns.GetHostAddresses(HostsMapping.Domain);
                            if (addresses.Length == 0) throw new IOException("后台域名没有解析结果。");
                            foreach (IPAddress address in addresses)
                                if (!address.Equals(IPAddress.Loopback)) throw new IOException("后台域名没有仅解析到本机。");
                            CheckNetwork(443);
                            int appPid = plan.Launch();
                            writer.WriteLine("WATCH:" + appPid);
                            if (ReadLine(reader, 15000) != "WATCHING") throw new IOException("未开始退出监控。");
                            Log("ACTIVE appPid=" + appPid + " relayPid=" + relay.Id + " proxyPort=" + proxyPort);
                            if (simulateParentExit) Environment.Exit(91);
                        }
                        // The elevated helper owns cleanup, including if this launcher crashes.
                        elevated.WaitForExit();
                        if (elevated.ExitCode != 0) throw new IOException("权限管理进程报告失败，请查看 session.log。");
                    }
                }
                finally
                {
                    pipe.Dispose();
                    if (elevated != null) { if (!elevated.HasExited) elevated.WaitForExit(6000); elevated.Dispose(); }
                    if (relay != null)
                    {
                        bool removed = false;
                        try { removed = !HostsMapping.IsEnabled(); } catch { }
                        if (removed && !relay.HasExited)
                        {
                            LogRelayLifecycle("RELAY_KILL_REQUESTED", relay, "launcher_cleanup_mapping_removed");
                            relay.Kill();
                        }
                        relay.Dispose();
                    }
                }
            }
        }

        private static string TrackedRelayPath { get { return Path.Combine(StateDirectory, "owned-relay.txt"); } }

        internal static void WatchApplication(Process app, Process relay)
        {
            while (!app.WaitForExit(500))
                if (relay.HasExited)
                {
                    LogRelayLifecycle("RELAY_EXIT_OBSERVED", relay, "app_still_running", app.Id);
                    throw new IOException("转发进程意外退出，正在撤销映射。");
                }
        }

        private static void RecoverRelay()
        {
            if (!File.Exists(TrackedRelayPath)) return;
            string[] record = File.ReadAllLines(TrackedRelayPath);
            int pid; long ticks;
            if (record.Length != 2 || !Int32.TryParse(record[0], out pid) || !Int64.TryParse(record[1], out ticks))
                throw new IOException("转发恢复记录损坏，未终止任何进程。");
            try
            {
                using (var previous = Process.GetProcessById(pid))
                {
                    if (previous.StartTime.ToUniversalTime().Ticks == ticks &&
                        String.Equals(previous.MainModule.FileName, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelayName), StringComparison.OrdinalIgnoreCase))
                    {
                        LogRelayLifecycle("RELAY_KILL_REQUESTED", previous, "recover_tracked_relay");
                        previous.Kill(); previous.WaitForExit(5000);
                    }
                }
            }
            catch (ArgumentException) { }
            File.Delete(TrackedRelayPath);
        }

        internal static int Elevated(string pipeName, string parentText, string relayText, string token)
        {
            int parentPid, relayPid;
            Guid instance;
            if (!Guid.TryParseExact(token, "N", out instance) || pipeName != "ChatGPT-Verge-Hosts-" + token ||
                !Int32.TryParse(parentText, out parentPid) || !Int32.TryParse(relayText, out relayPid) ||
                !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 4;
            Process relay = null;
            NetworkGuard guard = null;
            bool locked = false;
            bool cleanup = false;
            bool failed = false;
            using (var mutex = new Mutex(false, @"Global\ChatGPT-Verge-DurableHosts-v106"))
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try
                {
                    pipe.Connect(15000);
                    uint server;
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out server) || server != parentPid) throw new IOException("启动器管道身份不匹配。");
                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                    {
                        writer.AutoFlush = true;
                        try
                        {
                            sessionLog = LaunchLog.Create(token, "hosts");
                            try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                            if (!locked) throw new IOException("已有其他转发会话管理 hosts。");
                            guard = NetworkGuard.Open();
                            guard.Recover();
                            cleanup = true;
                            HostsMapping.Set(false);
                            RecoverRelay();
                            Log("STALE_MAPPING_CLEARED");
                            writer.WriteLine("PREPARED");
                            string command = ReadLine(reader, 30000);
                            if (command.StartsWith("GUARD:", StringComparison.Ordinal)) {
                                guard.Install(GuardPlan.Decode(command.Substring(6)));
                                writer.WriteLine("GUARD_READY");
                                command = ReadLine(reader, 30000);
                            }
                            if (command != "DONE")
                            {
                                if (!command.StartsWith("ENABLE:", StringComparison.Ordinal) || !Int32.TryParse(command.Substring(7), out relayPid)) throw new IOException("无效的启用请求。");
                                relay = Process.GetProcessById(relayPid);
                                if (!String.Equals(relay.MainModule.FileName, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelayName), StringComparison.OrdinalIgnoreCase)) throw new IOException("转发进程身份不匹配。");
                                Directory.CreateDirectory(StateDirectory);
                                File.WriteAllLines(TrackedRelayPath, new[] { relay.Id.ToString(), relay.StartTime.ToUniversalTime().Ticks.ToString() });
                                HostsMapping.Set(true);
                                Log("MAPPING_ENABLED");
                                writer.WriteLine("READY");
                                command = ReadLine(reader, 60000);
                                int appPid;
                                if (!command.StartsWith("WATCH:", StringComparison.Ordinal) || !Int32.TryParse(command.Substring(6), out appPid)) throw new IOException("未收到应用进程。");
                                using (Process app = Process.GetProcessById(appPid))
                                {
                                    // Hold handles before acknowledgement: PID reuse cannot retarget cleanup.
                                    IntPtr appHandle = app.Handle;
                                    IntPtr relayHandle = relay.Handle;
                                    writer.WriteLine("WATCHING");
                                    WatchApplication(app, relay);
                                }
                                Log("APP_EXITED");
                            }
                        }
                        catch (Exception error)
                        {
                            failed = true;
                            Log("SESSION_FAILED " + error.GetType().Name + " " + error.Message);
                            try { writer.WriteLine("ERROR:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(error.Message))); } catch (IOException) { }
                        }
                    }
                }
                catch (Exception error) { failed = true; Log("HELPER_FAILED " + error.Message); }
                finally
                {
                    bool removed = !cleanup;
                    if (cleanup)
                    {
                        for (int attempt = 0; attempt < 10 && !removed; attempt++)
                        {
                            try { HostsMapping.Set(false); removed = true; Log("MAPPING_REMOVED"); }
                            catch (Exception error) { Log("CLEANUP_RETRY " + error.Message); Thread.Sleep(200); }
                        }
                        if (!removed)
                        {
                            failed = true;
                            MessageBox.Show("hosts 自动撤销失败。请保留启动器文件，使用诊断程序 --restore-hosts 恢复。转发进程将保留。", "ChatGPT Verge 1.0.6", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    if (relay != null)
                    {
                        if (removed && !relay.HasExited)
                        {
                            LogRelayLifecycle("RELAY_KILL_REQUESTED", relay, "hosts_helper_cleanup_mapping_removed");
                            relay.Kill();
                        }
                        relay.Dispose();
                    }
                    if (removed && locked && File.Exists(TrackedRelayPath)) File.Delete(TrackedRelayPath);
                    if (guard != null) {
                        try { if (cleanup) guard.Cleanup(); }
                        catch (Exception error) { failed = true; Log("GUARD_CLEANUP_PENDING " + error.Message); }
                        finally { guard.Dispose(); }
                    }
                    if (locked) mutex.ReleaseMutex();
                }
            }
            return failed ? 4 : 0;
        }
    }
}
