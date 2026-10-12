using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("ChatGPT Verge Launcher")]
[assembly: AssemblyDescription("Starts the Microsoft Store ChatGPT app through a local Verge proxy.")]
[assembly: AssemblyCompany("Local utility")]
[assembly: AssemblyProduct("ChatGPT Verge Launcher")]
[assembly: AssemblyCopyright("Copyright (c) 2026")]
[assembly: AssemblyVersion("1.0.6.0")]
[assembly: AssemblyFileVersion("1.0.6.0")]

namespace ChatGptVergeLauncher
{
    internal static class Program
    {
        private const string WindowTitle = "ChatGPT Verge 启动器";
        private const string ProxyHost = "127.0.0.1";
        private const int FallbackProxyPort = 7896;
        private const string LauncherMutexName = @"Local\ChatGPT-Verge-Launcher-7D565FFB";
        private const string AppRepositoryRegistryPath =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        private static readonly string[] SupportedPackagePrefixes =
        {
            "OpenAI.Codex_"
        };

        private static readonly string[] VergeConfigFileNames =
        {
            "config.yaml",
            "clash-verge.yaml"
        };

        private static readonly string[] VergeConfigDirectoryNames =
        {
            "io.github.clash-verge-rev.clash-verge-rev",
            "clash-verge-rev"
        };

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 5 && args[0] == "--hosts-session")
                return RelaySession.Elevated(args[1], args[2], args[3], args[4]);
            if (HasArgument(args, "--hosts-session")) return 4;
            if (args.Length == 1 && args[0] == "--lifecycle-probe") { Thread.Sleep(2500); return 0; }
            // The package bridge runs while the outer launcher still owns the mutex.
            if (args.Length == 4 && args[0] == "--launch-packaged")
                return PackageLaunch.Reply(args[3], delegate { return LaunchInsidePackage(args[1], args[2]); });
            if (HasArgument(args, "--launch-packaged")) return 4;
            if (HasArgument(args, "--diagnose")) return RunDiagnostics();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdNew;
            using (Mutex launcherMutex = new Mutex(true, LauncherMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    var current = FindChatGptPackage();
                    if (current != null) ActivateExistingWindow(InspectExistingChatGpt(current.ExecutablePath, "").ProcessIds);
                    return 5;
                }

                if (HasArgument(args, "--diagnose"))
                {
                    return RunDiagnostics();
                }
                if (HasArgument(args, "--restore-hosts") || HasArgument(args, "--validate-durable"))
                {
                    try
                    {
                        if (HasArgument(args, "--restore-hosts")) RelaySession.Run(0, null);
                        else RelaySession.Run(DiscoverVergeProxy().Port, delegate {
                            var info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--lifecycle-probe");
                            info.UseShellExecute = false; info.CreateNoWindow = true;
                            return Process.Start(info).Id;
                        }, HasArgument(args, "--simulate-parent-exit"));
                        Console.WriteLine("DurableSessionCompleted=True; HostsRestored=True; AuthenticatedWebSocketTested=False");
                        return 0;
                    }
                    catch (Exception error) { Console.Error.WriteLine(error); return 4; }
                }

                return LaunchChatGpt();
            }
        }

        private static int LaunchChatGpt()
        {
            int result = 0;
            Action notify = null;
            try
            {
                // Recovery is acknowledged by the elevated helper before any early-return checks.
                RelaySession.RunPrepared(delegate {
                    PackagedApp app = FindChatGptPackage();
                    if (app == null || !File.Exists(app.ExecutablePath))
                    {
                        result = 1;
                        notify = delegate { ShowError("未找到微软商店版 ChatGPT。请先安装或更新后重试。上次会话残留已检查并清理。"); };
                        return null;
                    }
                    ProxyEndpoint proxy = DiscoverVergeProxy();
                    if (!proxy.IsReachable)
                    {
                        result = 2;
                        notify = delegate { ShowError("未检测到可用的本地 Verge mixed 代理：http://" + ProxyHost + ":" + proxy.Port +
                            "\r\n来源：" + proxy.Source + "\r\n\r\n上次会话残留已检查并清理。请启动 Clash Verge Rev 后重试。"); };
                        return null;
                    }
                    if (InspectExistingChatGpt(app.ExecutablePath, BuildProxyArgument(proxy.Port)).IsRunning)
                    {
                        result = 3;
                        notify = delegate { MessageBox.Show("ChatGPT 已经在运行。上次会话残留已检查并清理。\r\n\r\n请先从托盘完全退出 ChatGPT，再运行本启动器，以应用代理设置。启动器不会强制结束现有任务。",
                            WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Information); };
                        return null;
                    }
                    RuntimePaths runtime = ProxyRuntime.Prepare(AppDomain.CurrentDomain.BaseDirectory, app.ExecutablePath);
                    GuardPlan guard = GuardPlan.Create(runtime);
                    return new RelaySession.LaunchPlan(proxy.Port, delegate {
                        return Int32.Parse(PackageLaunch.RunWithReply(app, Assembly.GetExecutingAssembly().Location,
                            "--launch-packaged", app.FullName, proxy.Port.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
                    }, guard);
                });
                // Do not leave the helper waiting for IPC while a user reads a dialog.
                if (notify != null) notify();
                return result;
            }
            catch (Exception exception)
            {
                ShowError("ChatGPT 启动或恢复失败。\r\n\r\n" + exception.Message +
                    "\r\n\r\n请保留完整发布目录及恢复记录，运行诊断程序检查。取消 UAC 时，启动器无法清理系统 hosts。");
                return 4;
            }
        }
        private static string LaunchInsidePackage(string fullName, string portText)
        {
            PackageIdentity.RequireCurrent(fullName);
            PackagedApp app = FindChatGptPackage();
            if (app == null || app.FullName != fullName)
                throw new InvalidOperationException("ChatGPT 程序包在启动期间发生变化，请重新运行启动器。");
            int port;
            if (!Int32.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw new InvalidOperationException("无效的本地代理端口。");
            string proxyArgument = BuildProxyArgument(port);
            if (InspectExistingChatGpt(app.ExecutablePath, proxyArgument).IsRunning)
                throw new InvalidOperationException("ChatGPT 已在启动期间运行，请先完全退出后再试。");

            // Set these AFTER package activation, which discards the outer process environment.
            ProxyRuntime.Configure(AppDomain.CurrentDomain.BaseDirectory, port);
            var startInfo = new ProcessStartInfo(app.ExecutablePath, proxyArgument);
            startInfo.WorkingDirectory = Path.GetDirectoryName(app.ExecutablePath);
            startInfo.UseShellExecute = false;
            startInfo.ErrorDialog = false;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process process = Process.Start(startInfo))
            {
                if (PackageIdentity.ProcessFullName(process.Handle) != fullName)
                    throw new InvalidOperationException("ChatGPT 未获得预期的 Windows 程序包标识。");
                if (process.WaitForExit(1500))
                    throw new InvalidOperationException("ChatGPT 启动后立即退出，退出代码：" + process.ExitCode);
                return process.Id.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static int RunDiagnostics()
        {
            PackagedApp app = FindChatGptPackage();
            string executable = app == null ? null : app.ExecutablePath;
            bool executableFound = !String.IsNullOrWhiteSpace(executable) && File.Exists(executable);
            ProxyEndpoint proxyEndpoint = DiscoverVergeProxy();
            ExistingAppState state = executableFound
                ? InspectExistingChatGpt(executable, ProxyServerArgument(proxyEndpoint.Port))
                : new ExistingAppState();

            Console.WriteLine("LauncherVersion=1.0.6");
            Console.WriteLine("BuildRevision=" + LaunchLog.Revision);
            try { LaunchLog.PrintDiagnostics(RelaySession.StateDirectory); }
            catch (IOException error) { Console.WriteLine("LogDiagnosticsFailed=" + error.Message); }
            catch (UnauthorizedAccessException error) { Console.WriteLine("LogDiagnosticsFailed=" + error.Message); }
            Console.WriteLine("HostsTransactionDirectory=" + new HostsTransaction(HostsMapping.SystemPath).RecoveryDirectory);
            Console.WriteLine("HostsTransactionPending=" + new HostsTransaction(HostsMapping.SystemPath).HasPending);
            Console.WriteLine("DurableRelay=" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelaySession.RelayName));
            Console.WriteLine("DurableRelayFound=" + File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelaySession.RelayName)));
            try { Console.WriteLine("DurableHostsEnabled=" + HostsMapping.IsEnabled()); }
            catch (IOException error) { Console.WriteLine("DurableHostsEnabled=Unknown; " + error.Message); }
            Console.WriteLine("HostsLifecycle=OneUacHelperPerSession;CleanupOnAppExit;RecoverBeforePreflight;JournaledReplace");
            Console.WriteLine("NodeUseEnvProxy=1");
            Console.WriteLine("ProxyRuntimeOverrides=CODEX_CLI_PATH,CODEX_BROWSER_USE_NODE_PATH,CODEX_NODE_REPL_PATH");
            Console.WriteLine("BrowserBackends=iab,mcpapps;ExternalChromeControl=DisabledInCodexRuntime");
            Console.WriteLine("NetworkGuard=CodexDedicatedExecutablePathsOnly;SharedProgramsNotModified;PersistentUntilSafeCleanup");
            try {
                using (var policy = new WindowsGuardPolicy()) {
                    int count = 0;
                    foreach (var rule in policy.List()) if (rule.Group == NetworkGuard.OwnerGroup) count++;
                    Console.WriteLine("InstalledCodexGuardRuleCount=" + count);
                    policy.CheckAvailable(); Console.WriteLine("FirewallAvailableForCodexGuard=True");
                }
            } catch (Exception error) { Console.WriteLine("FirewallAvailableForCodexGuard=UnknownOrFalse; " + error.Message); }
            Console.WriteLine("ChromiumDns=NoExternalResolution;QUIC=Disabled;WebRTC=DisableNonProxiedUDP");
            string manifest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ProxyRuntime.ManifestName);
            Console.WriteLine("ProxyRuntimeManifestFound=" + File.Exists(manifest));
            if (File.Exists(manifest)) {
                try {
                    var runtime = ProxyRuntime.Read(AppDomain.CurrentDomain.BaseDirectory);
                    Console.WriteLine("ConfiguredNodeRuntime=" + runtime.Node);
                    Console.WriteLine("ConfiguredCliRuntime=" + runtime.Codex);
                } catch (Exception error) { Console.WriteLine("ProxyRuntimeManifestInvalid=" + error.Message); }
            }
            Console.WriteLine("LaunchMethod=WindowsPackageBridge");
            Console.WriteLine("PackageFullName=" + (app == null ? String.Empty : app.FullName));
            Console.WriteLine("AppUserModelId=" + (app == null ? String.Empty : app.FamilyName + "!" + app.ApplicationId));
            string helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ControlProxy.HelperName);
            Console.WriteLine("ControlProxyHelper=" + helper);
            Console.WriteLine("ControlProxyHelperFound=" + File.Exists(helper));
            Console.WriteLine("ControlProxyOverride=CODEX_NODE_REPL_PATH");
            Console.WriteLine("ProxyEnvironment=HTTP_PROXY,HTTPS_PROXY,ALL_PROXY,NO_PROXY");
            Console.WriteLine("ChatGptExecutable=" + (executable ?? String.Empty));
            Console.WriteLine("ChatGptExecutableFound=" + executableFound);
            Console.WriteLine("ProxyEndpoint=http://" + ProxyHost + ":" + proxyEndpoint.Port);
            Console.WriteLine("ProxySource=" + proxyEndpoint.Source);
            Console.WriteLine("ProxyReachable=" + proxyEndpoint.IsReachable);
            Console.WriteLine("ChatGptRunning=" + state.IsRunning);
            Console.WriteLine("RunningWithExpectedProxy=" + state.UsesExpectedProxy);
            Console.WriteLine("RunningWithProxy4Arguments=" + (executableFound && InspectExistingChatGpt(executable, BuildProxyArgument(proxyEndpoint.Port)).UsesExpectedProxy));

            if (!executableFound)
            {
                return 10;
            }

            if (!proxyEndpoint.IsReachable)
            {
                return 11;
            }

            return File.Exists(helper) && File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelaySession.RelayName)) ? 0 : 12;
        }

        private static bool HasArgument(string[] args, string expected)
        {
            if (args == null)
            {
                return false;
            }

            foreach (string argument in args)
            {
                if (String.Equals(argument, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ProxyServerArgument(int port) { return "--proxy-server=http://" + ProxyHost + ":" + port; }
        private static string BuildProxyArgument(int port)
        {
            return ProxyServerArgument(port) +
                " --disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp" +
                " --host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE *.localhost, EXCLUDE 127.*, EXCLUDE [::1], EXCLUDE ::1\"";
        }

        private static ProxyEndpoint DiscoverVergeProxy()
        {
            List<ProxyEndpoint> candidates = new List<ProxyEndpoint>();
            string roamingApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            foreach (string directoryName in VergeConfigDirectoryNames)
            {
                foreach (string fileName in VergeConfigFileNames)
                {
                    string configPath = Path.Combine(roamingApplicationData, directoryName, fileName);
                    int configuredPort;
                    if (TryReadMixedPort(configPath, out configuredPort))
                    {
                        AddProxyCandidate(candidates, configuredPort, configPath);
                    }
                }
            }

            AddProxyCandidate(candidates, FallbackProxyPort, "内置回退端口");

            foreach (ProxyEndpoint candidate in candidates)
            {
                if (CanConnectToProxy(candidate.Port, 1500))
                {
                    candidate.IsReachable = true;
                    return candidate;
                }
            }

            return candidates.Count == 0
                ? new ProxyEndpoint(FallbackProxyPort, "内置回退端口", false)
                : candidates[0];
        }

        private static void AddProxyCandidate(List<ProxyEndpoint> candidates, int port, string source)
        {
            if (port < 1 || port > 65535)
            {
                return;
            }

            foreach (ProxyEndpoint candidate in candidates)
            {
                if (candidate.Port == port)
                {
                    return;
                }
            }

            candidates.Add(new ProxyEndpoint(port, source, false));
        }

        private static bool TryReadMixedPort(string configPath, out int port)
        {
            port = 0;

            try
            {
                if (!File.Exists(configPath))
                {
                    return false;
                }

                foreach (string rawLine in File.ReadLines(configPath))
                {
                    if (String.IsNullOrWhiteSpace(rawLine) || Char.IsWhiteSpace(rawLine[0]))
                    {
                        continue;
                    }

                    int commentStart = rawLine.IndexOf('#');
                    string line = commentStart >= 0
                        ? rawLine.Substring(0, commentStart).Trim()
                        : rawLine.Trim();

                    const string key = "mixed-port:";
                    if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string portText = line.Substring(key.Length).Trim().Trim('\'', '"');
                    int parsedPort;
                    if (Int32.TryParse(portText, out parsedPort) && parsedPort >= 1 && parsedPort <= 65535)
                    {
                        port = parsedPort;
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool CanConnectToProxy(int port, int timeoutMilliseconds)
        {
            TcpClient client = null;
            IAsyncResult connectResult = null;
            try
            {
                client = new TcpClient();
                connectResult = client.BeginConnect(ProxyHost, port, null, null);
                if (!connectResult.AsyncWaitHandle.WaitOne(timeoutMilliseconds))
                {
                    return false;
                }

                client.EndConnect(connectResult);
                return client.Connected;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (connectResult != null)
                {
                    connectResult.AsyncWaitHandle.Close();
                }

                if (client != null)
                {
                    client.Close();
                }
            }
        }

        internal static PackagedApp FindChatGptPackage()
        {
            PackagedApp registryResult = FindChatGptFromRegistry();
            if (registryResult != null)
            {
                return registryResult;
            }

            return FindChatGptWithPowerShell();
        }

        private static PackagedApp FindChatGptFromRegistry()
        {
            PackageCandidate bestCandidate = null;

            try
            {
                using (RegistryKey repositoryKey = Registry.CurrentUser.OpenSubKey(AppRepositoryRegistryPath, false))
                {
                    if (repositoryKey == null)
                    {
                        return null;
                    }

                    foreach (string packageKeyName in repositoryKey.GetSubKeyNames())
                    {
                        Version packageVersion;
                        if (!TryParseSupportedPackageVersion(packageKeyName, out packageVersion))
                        {
                            continue;
                        }

                        using (RegistryKey packageKey = repositoryKey.OpenSubKey(packageKeyName, false))
                        {
                            if (packageKey == null)
                            {
                                continue;
                            }

                            string packageRoot = packageKey.GetValue("PackageRootFolder") as string;
                            PackagedApp app = PackagedApp.Read(packageKeyName, packageRoot);
                            if (app == null)
                            {
                                continue;
                            }

                            if (bestCandidate == null || packageVersion > bestCandidate.Version)
                            {
                                bestCandidate = new PackageCandidate(packageVersion, app);
                            }
                        }
                    }
                }
            }
            catch
            {
                return null;
            }

            return bestCandidate == null ? null : bestCandidate.App;
        }

        private static bool TryParseSupportedPackageVersion(string packageKeyName, out Version version)
        {
            version = null;

            foreach (string prefix in SupportedPackagePrefixes)
            {
                if (!packageKeyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int versionEnd = packageKeyName.IndexOf('_', prefix.Length);
                if (versionEnd <= prefix.Length)
                {
                    return false;
                }

                string versionText = packageKeyName.Substring(prefix.Length, versionEnd - prefix.Length);
                return Version.TryParse(versionText, out version);
            }

            return false;
        }

        private static PackagedApp FindChatGptWithPowerShell()
        {
            try
            {
                string powerShellPath = Path.Combine(
                    Environment.SystemDirectory,
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe");

                string command =
                    "$p=Get-AppxPackage | Where-Object { $_.Name -eq 'OpenAI.Codex' -or " +
                    "$_.Name -eq 'OpenAI.ChatGPT-Desktop' -or $_.Name -eq 'OpenAI.ChatGPT' } | " +
                    "Sort-Object Version -Descending | Select-Object -First 1; " +
                    "if ($p) { [Console]::Out.WriteLine($p.PackageFullName); [Console]::Out.WriteLine($p.InstallLocation) }";

                ProcessStartInfo queryInfo = new ProcessStartInfo();
                queryInfo.FileName = powerShellPath;
                queryInfo.Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"" +
                                      command.Replace("\"", "\\\"") + "\"";
                queryInfo.UseShellExecute = false;
                queryInfo.CreateNoWindow = true;
                queryInfo.RedirectStandardOutput = true;
                queryInfo.RedirectStandardError = true;

                using (Process queryProcess = Process.Start(queryInfo))
                {
                    var output = queryProcess.StandardOutput.ReadToEndAsync();
                    var error = queryProcess.StandardError.ReadToEndAsync();

                    if (!queryProcess.WaitForExit(8000))
                    {
                        queryProcess.Kill();
                        return null;
                    }

                    if (queryProcess.ExitCode != 0) return null;
                    string[] fields = output.Result.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    error.Wait();
                    return fields.Length == 2 ? PackagedApp.Read(fields[0], fields[1]) : null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static ExistingAppState InspectExistingChatGpt(
            string expectedExecutable,
            string expectedProxyArgument)
        {
            ExistingAppState state = new ExistingAppState();
            HashSet<int> matchedProcessIds = new HashSet<int>();

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='ChatGPT.exe'"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject result in results)
                    {
                        string executablePath = Convert.ToString(result["ExecutablePath"]);
                        string commandLine = Convert.ToString(result["CommandLine"]);
                        int processId = Convert.ToInt32((UInt32)result["ProcessId"]);

                        if (!PathsEqual(executablePath, expectedExecutable) &&
                            !CommandLineStartsWithExecutable(commandLine, expectedExecutable))
                        {
                            continue;
                        }

                        matchedProcessIds.Add(processId);
                        if (ContainsExpectedProxyArgument(commandLine, expectedProxyArgument))
                        {
                            state.UsesExpectedProxy = true;
                        }
                    }
                }
            }
            catch
            {
                // Fall back to Process APIs below. If the command line cannot be read,
                // treating the existing process as unverified is the safer behavior.
            }

            foreach (Process process in Process.GetProcessesByName("ChatGPT"))
            {
                try
                {
                    string processPath = process.MainModule == null
                        ? null
                        : process.MainModule.FileName;

                    if (PathsEqual(processPath, expectedExecutable))
                    {
                        matchedProcessIds.Add(process.Id);
                    }
                }
                catch
                {
                    // Ignore inaccessible unrelated processes.
                }
                finally
                {
                    process.Dispose();
                }
            }

            foreach (int processId in matchedProcessIds)
            {
                state.ProcessIds.Add(processId);
            }

            state.IsRunning = state.ProcessIds.Count > 0;
            return state;
        }

        private static bool PathsEqual(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return String.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool CommandLineStartsWithExecutable(string commandLine, string executable)
        {
            if (String.IsNullOrWhiteSpace(commandLine) || String.IsNullOrWhiteSpace(executable))
            {
                return false;
            }

            string trimmed = commandLine.TrimStart();
            return trimmed.StartsWith("\"" + executable + "\"", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith(executable, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsExpectedProxyArgument(string commandLine, string expectedProxyArgument)
        {
            return !String.IsNullOrWhiteSpace(commandLine) &&
                   commandLine.IndexOf(expectedProxyArgument, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ActivateExistingWindow(IEnumerable<int> processIds)
        {
            foreach (int processId in processIds)
            {
                try
                {
                    using (Process process = Process.GetProcessById(processId))
                    {
                        IntPtr windowHandle = process.MainWindowHandle;
                        if (windowHandle == IntPtr.Zero)
                        {
                            continue;
                        }

                        ShowWindowAsync(windowHandle, 9);
                        SetForegroundWindow(windowHandle);
                        return;
                    }
                }
                catch
                {
                    // A process may exit while the launcher is inspecting it.
                }
            }
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(
                message,
                WindowTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

        private sealed class PackageCandidate
        {
            internal PackageCandidate(Version version, PackagedApp app)
            {
                Version = version;
                App = app;
            }

            internal Version Version { get; private set; }
            internal PackagedApp App { get; private set; }
        }

        private sealed class ExistingAppState
        {
            internal ExistingAppState()
            {
                ProcessIds = new List<int>();
            }

            internal bool IsRunning { get; set; }
            internal bool UsesExpectedProxy { get; set; }
            internal List<int> ProcessIds { get; private set; }
        }

        private sealed class ProxyEndpoint
        {
            internal ProxyEndpoint(int port, string source, bool isReachable)
            {
                Port = port;
                Source = source;
                IsReachable = isReachable;
            }

            internal int Port { get; private set; }
            internal string Source { get; private set; }
            internal bool IsReachable { get; set; }
        }
    }
}
