using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace ChatGptVergeLauncher
{
    internal static class PackageLaunch
    {
        private const string PipePrefix = "ChatGPT-Verge-Launch-";

        internal static string RunWithReply(PackagedApp app, string bridge, params string[] arguments)
        {
            string name = PipePrefix + Guid.NewGuid().ToString("N");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            using (var identity = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            using (var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security))
            {
                IAsyncResult connected = pipe.BeginWaitForConnection(null, null);
                using (connected.AsyncWaitHandle)
                {
                    var allArguments = new string[arguments.Length + 1];
                    arguments.CopyTo(allArguments, 0);
                    allArguments[arguments.Length] = name;
                    Invoke(app, bridge, allArguments);
                    if (!connected.AsyncWaitHandle.WaitOne(15000))
                        throw new TimeoutException("Windows 已接收启动请求，但程序包桥接未响应。请检查完整发布目录是否可用。");
                    pipe.EndWaitForConnection(connected);
                }
                using (var reader = new StreamReader(pipe, Encoding.UTF8))
                {
                    var reply = reader.ReadLineAsync();
                    if (!reply.Wait(15000)) throw new TimeoutException("等待程序包启动结果超时。");
                    string message = reply.Result;
                    if (message != null && message.StartsWith("OK:", StringComparison.Ordinal))
                        return message.Substring(3);
                    if (message != null && message.StartsWith("ERROR:", StringComparison.Ordinal))
                        throw new InvalidOperationException(Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(6))));
                    throw new InvalidOperationException("程序包桥接退出，未返回有效的启动结果。");
                }
            }
        }

        internal static int Reply(string pipeName, Func<string> launch)
        {
            Guid token;
            if (pipeName == null || !pipeName.StartsWith(PipePrefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(pipeName.Substring(PipePrefix.Length), "N", out token)) return 4;
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
                {
                    pipe.Connect(10000);
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)))
                    {
                        writer.AutoFlush = true;
                        try { writer.WriteLine("OK:" + launch()); return 0; }
                        catch (Exception error)
                        {
                            writer.WriteLine("ERROR:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(error.Message)));
                            return 4;
                        }
                    }
                }
            }
            catch (IOException) { return 4; }
            catch (TimeoutException) { return 4; }
            catch (UnauthorizedAccessException) { return 4; }
        }

        private static void Invoke(PackagedApp app, string bridge, string[] arguments)
        {
            // Windows PowerShell 5.1 is required for the built-in Appx module.
            // Activation does not inherit our environment. Configure proxy variables inside the bridge.
            string script = "$ErrorActionPreference='Stop'; try { Invoke-CommandInDesktopPackage -PackageFamilyName " +
                PowerShellLiteral(app.FamilyName) + " -AppId " + PowerShellLiteral(app.ApplicationId) +
                " -Command " + PowerShellLiteral(bridge) + " -Args " +
                PowerShellLiteral(String.Join(" ", Array.ConvertAll(arguments, QuoteArgument))) +
                " -ErrorAction Stop; exit 0 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
            var info = new ProcessStartInfo();
            info.FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            // Set the redirected encoding inside PowerShell as well, including localized errors.
            script = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + script;
            info.Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(20000))
                {
                    process.Kill();
                    throw new TimeoutException("Windows 程序包启动命令超时。");
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Windows 程序包激活失败：" + error.Result.Trim());
                // Drain both redirected streams before disposing the process.
                output.Wait();
                error.Wait();
            }
        }

        private static string PowerShellLiteral(string value) { return "'" + value.Replace("'", "''") + "'"; }

        internal static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                result.Append(c);
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
