using System;
using System.Diagnostics;
using System.IO;
using System.Text;
internal static class CliProbe
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--wait") { Console.WriteLine(Process.GetCurrentProcess().Id); Console.Out.Flush(); System.Threading.Thread.Sleep(60000); return 0; }
        if (Environment.GetEnvironmentVariable("NODE_USE_ENV_PROXY") != "1" || Environment.GetEnvironmentVariable("BROWSER_USE_AVAILABLE_BACKENDS") != "iab,mcpapps") return 81;
        if (!String.Equals(Path.GetFileName(Environment.GetEnvironmentVariable("CODEX_MCP_NODE_PATH")),"ChatGPT-Verge-NodeProxy.exe",StringComparison.Ordinal)) return 82;
        if (Environment.GetEnvironmentVariable("HTTP_PROXY") != "http://127.0.0.1:17896") return 83;
        if (args.Length != 3 || args[0] != "中文 space" || args[1] != "quote\"trail\\" || args[2] != "") return 84;
        var reader = new StreamReader(Console.OpenStandardInput(),Encoding.UTF8);
        var output = Console.OpenStandardOutput();
        string line;
        while ((line = reader.ReadLine()) != null) { byte[] bytes=Encoding.UTF8.GetBytes(line+"\n"); output.Write(bytes,0,bytes.Length); output.Flush(); }
        Console.Error.Write("cli-stderr"); return 37;
    }
}
