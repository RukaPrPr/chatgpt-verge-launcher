using System;
using System.Collections.Generic;
using System.Diagnostics;
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
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]

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
            "OpenAI.Codex_",
            "OpenAI.ChatGPT-Desktop_",
            "OpenAI.ChatGPT_"
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdNew;
            using (Mutex launcherMutex = new Mutex(true, LauncherMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    return 5;
                }

                if (HasArgument(args, "--diagnose"))
                {
                    return RunDiagnostics();
                }

                return LaunchChatGpt();
            }
        }

        private static int LaunchChatGpt()
        {
            string chatGptExecutable = FindChatGptExecutable();
            if (String.IsNullOrWhiteSpace(chatGptExecutable) || !File.Exists(chatGptExecutable))
            {
                ShowError(
                    "未找到微软商店版 ChatGPT。\r\n\r\n" +
                    "请先从 Microsoft Store 安装或更新 ChatGPT，然后重试。");
                return 1;
            }

            ProxyEndpoint proxyEndpoint = DiscoverVergeProxy();
            if (!proxyEndpoint.IsReachable)
            {
                ShowError(
                    "未检测到可用的本地 Verge mixed 代理。\r\n\r\n" +
                    "检测结果：\r\n" +
                    "    http://" + ProxyHost + ":" + proxyEndpoint.Port + "\r\n" +
                    "    来源：" + proxyEndpoint.Source + "\r\n\r\n" +
                    "请先启动 Clash Verge Rev，并确认 mixed-port 已正常监听本机回环地址。");
                return 2;
            }

            string proxyArgument = BuildProxyArgument(proxyEndpoint.Port);
            ExistingAppState existingState = InspectExistingChatGpt(chatGptExecutable, proxyArgument);
            if (existingState.IsRunning)
            {
                if (existingState.UsesExpectedProxy)
                {
                    ActivateExistingWindow(existingState.ProcessIds);
                    return 0;
                }

                MessageBox.Show(
                    "检测到 ChatGPT 已经在运行，但它不是通过本启动器启动的。\r\n\r\n" +
                    "代理参数只能在 ChatGPT 启动时生效。请先在系统托盘中完全退出 ChatGPT，" +
                    "确认其所有窗口和后台进程均已结束，然后再次运行本启动器。\r\n\r\n" +
                    "启动器不会强制结束现有 ChatGPT，以免中断正在进行的任务。",
                    WindowTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 3;
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = chatGptExecutable;
                startInfo.Arguments = proxyArgument;
                startInfo.WorkingDirectory = Path.GetDirectoryName(chatGptExecutable);
                startInfo.UseShellExecute = true;
                startInfo.ErrorDialog = true;

                Process.Start(startInfo);
                return 0;
            }
            catch (Exception exception)
            {
                ShowError(
                    "ChatGPT 启动失败。\r\n\r\n" +
                    exception.Message + "\r\n\r\n" +
                    "可尝试更新或重置微软商店版 ChatGPT 后再试。");
                return 4;
            }
        }

        private static int RunDiagnostics()
        {
            string executable = FindChatGptExecutable();
            bool executableFound = !String.IsNullOrWhiteSpace(executable) && File.Exists(executable);
            ProxyEndpoint proxyEndpoint = DiscoverVergeProxy();
            ExistingAppState state = executableFound
                ? InspectExistingChatGpt(executable, BuildProxyArgument(proxyEndpoint.Port))
                : new ExistingAppState();

            Console.WriteLine("LauncherVersion=1.0.2");
            Console.WriteLine("ChatGptExecutable=" + (executable ?? String.Empty));
            Console.WriteLine("ChatGptExecutableFound=" + executableFound);
            Console.WriteLine("ProxyEndpoint=http://" + ProxyHost + ":" + proxyEndpoint.Port);
            Console.WriteLine("ProxySource=" + proxyEndpoint.Source);
            Console.WriteLine("ProxyReachable=" + proxyEndpoint.IsReachable);
            Console.WriteLine("ChatGptRunning=" + state.IsRunning);
            Console.WriteLine("RunningWithExpectedProxy=" + state.UsesExpectedProxy);

            if (!executableFound)
            {
                return 10;
            }

            if (!proxyEndpoint.IsReachable)
            {
                return 11;
            }

            return 0;
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

        private static string BuildProxyArgument(int port)
        {
            return "--proxy-server=http://" + ProxyHost + ":" + port;
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

        private static string FindChatGptExecutable()
        {
            string registryResult = FindChatGptFromRegistry();
            if (!String.IsNullOrWhiteSpace(registryResult))
            {
                return registryResult;
            }

            return FindChatGptWithPowerShell();
        }

        private static string FindChatGptFromRegistry()
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
                            string executable = FindExecutableUnderPackageRoot(packageRoot);
                            if (String.IsNullOrWhiteSpace(executable))
                            {
                                continue;
                            }

                            if (bestCandidate == null || packageVersion > bestCandidate.Version)
                            {
                                bestCandidate = new PackageCandidate(packageVersion, executable);
                            }
                        }
                    }
                }
            }
            catch
            {
                return null;
            }

            return bestCandidate == null ? null : bestCandidate.ExecutablePath;
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

        private static string FindExecutableUnderPackageRoot(string packageRoot)
        {
            if (String.IsNullOrWhiteSpace(packageRoot) || !Directory.Exists(packageRoot))
            {
                return null;
            }

            string[] preferredLocations =
            {
                Path.Combine(packageRoot, "app", "ChatGPT.exe"),
                Path.Combine(packageRoot, "ChatGPT.exe")
            };

            foreach (string preferredLocation in preferredLocations)
            {
                if (File.Exists(preferredLocation))
                {
                    return preferredLocation;
                }
            }

            try
            {
                string[] matches = Directory.GetFiles(packageRoot, "ChatGPT.exe", SearchOption.AllDirectories);
                return matches.Length == 0 ? null : matches[0];
            }
            catch
            {
                return null;
            }
        }

        private static string FindChatGptWithPowerShell()
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
                    "if ($p) { [Console]::Out.Write($p.InstallLocation) }";

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
                    string output = queryProcess.StandardOutput.ReadToEnd().Trim();
                    queryProcess.StandardError.ReadToEnd();

                    if (!queryProcess.WaitForExit(8000))
                    {
                        queryProcess.Kill();
                        return null;
                    }

                    return FindExecutableUnderPackageRoot(output);
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
            internal PackageCandidate(Version version, string executablePath)
            {
                Version = version;
                ExecutablePath = executablePath;
            }

            internal Version Version { get; private set; }
            internal string ExecutablePath { get; private set; }
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
