using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using ChatGptVergeLauncher;

internal static class PackageTests
{
    private const string FixtureName = "OpenAI.Codex_1.2.3.4_x64__2p2nqsd0c76g0";
    private const string ComplexArgument = "中文 O'Brien $value & space \"quote\" trailing\\";

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }

    private static void Manifest(string root, string applications)
    {
        File.WriteAllText(Path.Combine(root, "AppxManifest.xml"),
            "<Package xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10'><Applications>" +
            applications + "</Applications></Package>");
    }

    private static void VerifyProxy()
    {
        foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
            if (Environment.GetEnvironmentVariable(key) != "http://127.0.0.1:17896") throw new Exception("Lost " + key);
        if (Environment.GetEnvironmentVariable("NO_PROXY") != "localhost,127.0.0.1") throw new Exception("Lost NO_PROXY");
        string helper = Environment.GetEnvironmentVariable("CODEX_NODE_REPL_PATH");
        if (String.IsNullOrWhiteSpace(helper) || !File.Exists(helper)) throw new Exception("Lost control proxy helper");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args[0] == "--environment-probe")
            {
                VerifyProxy();
                if (PackageIdentity.CurrentFullName() != null) throw new Exception("Unexpected forced package inheritance");
                if (args[2] != ComplexArgument || args[3] != "") throw new Exception("Child arguments changed");
                File.WriteAllText(args[1], "proxy-and-arguments-ok");
                return 0;
            }
            if (args[0] == "--bridge-probe")
                return PackageLaunch.Reply(args[6], delegate
                {
                    PackageIdentity.RequireCurrent(args[1]);
                    if (args[4] != ComplexArgument || args[5] != "") throw new Exception("Activation arguments changed");
                    ControlProxy.ConfigureLauncher(args[2], 17896);
                    VerifyProxy();
                    string report = Path.Combine(args[2], "child-environment.txt");
                    var info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                        String.Join(" ", Array.ConvertAll(new[] { "--environment-probe", report, ComplexArgument, "" }, PackageLaunch.QuoteArgument)));
                    info.UseShellExecute = false;
                    info.CreateNoWindow = true;
                    using (var child = Process.Start(info))
                    {
                        if (!child.WaitForExit(10000)) { child.Kill(); throw new TimeoutException("Environment probe timed out"); }
                        if (child.ExitCode != 0) throw new Exception("Environment probe failed: " + child.ExitCode);
                    }
                    if (File.ReadAllText(report) != "proxy-and-arguments-ok") throw new Exception("Missing environment report");
                    string identity = PackageProcessProbe.ReadSuspendedIdentity(args[3]);
                    if (identity != args[1]) throw new Exception("Real ChatGPT executable did not inherit package identity");
                    return identity;
                });
            if (args[0] == "--bridge-error")
                return PackageLaunch.Reply(args[1], delegate { throw new InvalidOperationException("测试错误：bridge failed"); });

            string root = Path.GetFullPath(args[0]);
            string fixture = Path.Combine(root, "manifest-fixture");
            Directory.CreateDirectory(Path.Combine(fixture, "app"));
            File.WriteAllText(Path.Combine(fixture, "app", "ChatGPT.exe"), "fixture; never executed");
            Manifest(fixture, "<Application Id='Runner' Executable='app/runner.exe' EntryPoint='Windows.FullTrustApplication'/>" +
                "<Application Id='MainApp' Executable='app/ChatGPT.exe' EntryPoint='Windows.FullTrustApplication'/>");
            PackagedApp app = PackagedApp.Read(FixtureName, fixture);
            Check(app != null && app.ApplicationId == "MainApp" && app.FamilyName == "OpenAI.Codex_2p2nqsd0c76g0",
                "manifest resolves ChatGPT entry point and dynamic AppID");
            Check(app.ExecutablePath == Path.Combine(fixture, "app", "ChatGPT.exe"), "manifest resolves slash-separated executable");
            File.WriteAllText(Path.Combine(root, "ChatGPT.exe"), "outside fixture; never executed");
            Manifest(fixture, "<Application Id='App' Executable='../ChatGPT.exe' EntryPoint='Windows.FullTrustApplication'/>");
            Check(PackagedApp.Read(FixtureName, fixture) == null, "manifest cannot escape package root");
            Manifest(fixture, "<Application Id='App' Executable='app/missing/ChatGPT.exe' EntryPoint='Windows.FullTrustApplication'/>");
            Check(PackagedApp.Read(FixtureName, fixture) == null, "missing entry point is rejected");
            File.WriteAllText(Path.Combine(fixture, "AppxManifest.xml"), "<!DOCTYPE Package [<!ENTITY x SYSTEM 'file:///invalid'>]><Package>&x;</Package>");
            Check(PackagedApp.Read(FixtureName, fixture) == null, "external manifest entities are rejected");
            bool rejected = false;
            try { PackageIdentity.RequireCurrent("wrong-package"); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "bridge refuses a missing or mismatched package identity");

            if (args.Length > 1 && args[1] == "--installed-package")
            {
                PackagedApp installed = Program.FindChatGptPackage();
                Check(installed != null, "installed ChatGPT manifest discovery");
                string self = Assembly.GetExecutingAssembly().Location;
                string reply = PackageLaunch.RunWithReply(installed, self, "--bridge-probe", installed.FullName,
                    root, installed.ExecutablePath, ComplexArgument, "");
                Check(reply == installed.FullName, "package bridge, real executable identity, proxy inheritance, Unicode and quoted arguments");
                bool errorRelayed = false;
                try { PackageLaunch.RunWithReply(installed, self, "--bridge-error"); }
                catch (InvalidOperationException error) { errorRelayed = error.Message == "测试错误：bridge failed"; }
                Check(errorRelayed, "bridge failure returns to launcher with original Unicode message");
                Manifest(fixture, "<Application Id='App' Executable='app/ChatGPT.exe' EntryPoint='Windows.FullTrustApplication'/>");
                PackagedApp missing = PackagedApp.Read("Verge.Launcher.Missing_1.2.3.4_x64__2p2nqsd0c76g0", fixture);
                bool activationFailed = false;
                try { PackageLaunch.RunWithReply(missing, self, "--bridge-error"); }
                catch (InvalidOperationException error) { activationFailed = error.Message.StartsWith("Windows 程序包激活失败："); }
                Check(activationFailed, "Windows activation failure returns without silently falling back to direct EXE launch");
            }
            else Console.WriteLine("SKIP installed-package activation (pass -InstalledPackage to tests/run.ps1)");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
