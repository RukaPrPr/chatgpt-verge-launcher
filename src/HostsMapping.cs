using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatGptVergeLauncher
{
    internal static class HostsMapping
    {
        internal const string Domain = "codex-cloud-backend.chatgpt.com";
        internal const string Marker = "ChatGPT-Verge-DurableRelay";
        internal const string Block = "\r\n# BEGIN ChatGPT-Verge-DurableRelay v1\r\n127.0.0.1 codex-cloud-backend.chatgpt.com\r\n# END ChatGPT-Verge-DurableRelay v1\r\n";
        internal static readonly string SystemPath = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        [DllImport("dnsapi.dll", SetLastError = true)] private static extern bool DnsFlushResolverCache();

        internal static byte[] Transform(byte[] bytes, bool enable)
        {
            if (bytes.Length > 1048576 || Array.IndexOf(bytes, (byte)0) >= 0)
                throw new InvalidDataException("hosts 编码或大小异常，未修改文件。");
            Encoding encoding = Encoding.GetEncoding(28591);
            string text = encoding.GetString(bytes);
            int first = text.IndexOf(Block, StringComparison.Ordinal);
            if (first >= 0 && text.IndexOf(Block, first + Block.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("hosts 存在重复的启动器区块，需人工检查。");
            string outside = first < 0 ? text : text.Remove(first, Block.Length);
            if (outside.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidDataException("启动器的 hosts 区块已被修改，未覆盖它。");
            foreach (string line in outside.Split('\n'))
            {
                string[] tokens = line.Split('#')[0].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < tokens.Length; i++)
                    if (enable && String.Equals(tokens[i].TrimEnd('.'), Domain, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("hosts 已有该域名的其他映射，未覆盖它。");
            }
            byte[] result = encoding.GetBytes(enable ? (first < 0 ? text + Block : text) : outside);
            if (result.Length > 1048576) throw new InvalidDataException("hosts 加入映射后超过大小限制，未修改。");
            return result;
        }

        internal static bool IsEnabled()
        {
            return Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(SystemPath)).Contains(Block);
        }

        internal static void Set(bool enable)
        {
            new HostsTransaction(SystemPath).Set(enable);
            if (!DnsFlushResolverCache()) throw new IOException("hosts 已写入，但刷新 DNS 缓存失败。");
        }
    }
}
