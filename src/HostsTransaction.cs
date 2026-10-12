using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ChatGptVergeLauncher
{
    // All transaction files live beside hosts on the same volume, with its directory's ACLs.
    // No in-place write fallback: an unsupported/failed replacement must leave evidence.
    internal sealed class HostsTransaction
    {
        private const string Magic = "ChatGPT-Verge-HostsTransaction/1";
        private readonly string hosts, root, pending, journalTemp, stage, backup;
        internal HostsTransaction(string hostsPath)
        {
            hosts = Path.GetFullPath(hostsPath);
            root = Path.Combine(Path.GetDirectoryName(hosts), ".chatgpt-verge-v106");
            pending = Path.Combine(root, "pending.txn");
            journalTemp = Path.Combine(root, "pending.tmp");
            stage = Path.Combine(root, "replacement.tmp");
            backup = Path.Combine(root, "replaced.bak");
        }
        internal string RecoveryDirectory { get { return root; } }
        internal bool HasPending { get { return File.Exists(pending); } }
        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes));
        }
        private static bool Same(byte[] a, byte[] b) { return Hash(a) == Hash(b); }
        private static void CheckPoint(Action<string> checkpoint, string name) { if (checkpoint != null) checkpoint(name); }
        private static void RefuseReparse(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("hosts 或恢复路径是重解析点，未修改。");
        }
        private void PrepareDirectory()
        {
            RefuseReparse(hosts); RefuseReparse(root);
            Directory.CreateDirectory(root);
            foreach (string path in new[] { pending, journalTemp, stage, backup }) RefuseReparse(path);
        }
        private static byte[] ReadBounded(string path, int maximum)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > maximum) throw new InvalidDataException("hosts 或恢复文件大小异常。");
                byte[] bytes = new byte[(int)file.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int n = file.Read(bytes, offset, bytes.Length - offset);
                    if (n == 0) throw new EndOfStreamException();
                    offset += n;
                }
                return bytes;
            }
        }
        private static void WriteDurable(string path, byte[] bytes, Action<string> checkpoint, string label)
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                int half = bytes.Length / 2;
                file.Write(bytes, 0, half); file.Flush(true);
                CheckPoint(checkpoint, label + "-half");
                file.Write(bytes, half, bytes.Length - half); file.Flush(true);
            }
            CheckPoint(checkpoint, label + "-flushed");
        }
        private void Archive(string path, string label)
        {
            if (File.Exists(path)) File.Move(path, Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + "." + label));
        }
        private byte[] Journal(byte[] before, byte[] after)
        {
            string payload = String.Join("\n", new[] { Magic, Convert.ToBase64String(Encoding.UTF8.GetBytes(hosts)), Hash(before), Hash(after), Convert.ToBase64String(before), Convert.ToBase64String(after) });
            return Encoding.UTF8.GetBytes(payload + "\n" + Hash(Encoding.UTF8.GetBytes(payload)));
        }
        private void ReadJournal(out byte[] before, out byte[] after)
        {
            string[] fields = Encoding.UTF8.GetString(ReadBounded(pending, 3000000)).Split('\n');
            if (fields.Length != 7 || fields[0] != Magic || fields[6] != Hash(Encoding.UTF8.GetBytes(String.Join("\n", fields, 0, 6))))
                throw new InvalidDataException("hosts 恢复记录校验失败，已保留文件等待检查。");
            try
            {
                if (!String.Equals(Encoding.UTF8.GetString(Convert.FromBase64String(fields[1])), hosts, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("hosts 恢复记录属于其他路径。");
                before = Convert.FromBase64String(fields[4]); after = Convert.FromBase64String(fields[5]);
                if (before.Length > 1048576 || after.Length > 1050000 || Hash(before) != fields[2] || Hash(after) != fields[3])
                    throw new InvalidDataException("hosts 恢复内容校验失败。");
            }
            catch (FormatException error) { throw new InvalidDataException("hosts 恢复记录格式错误。", error); }
        }
        internal void Recover()
        {
            PrepareDirectory();
            if (File.Exists(pending))
            {
                byte[] before, after;
                ReadJournal(out before, out after);
                if (!File.Exists(hosts))
                {
                    // ReplaceFile can rename the original to its backup before reporting failure.
                    // Move that exact original back, preserving its ACL and metadata.
                    if (!File.Exists(backup) || !Same(ReadBounded(backup, 1048576), before))
                        throw new IOException("hosts 丢失且原文件备份无法确认，未猜测恢复。");
                    File.Move(backup, hosts);
                }
                byte[] current = ReadBounded(hosts, 1050000);
                if (!Same(current, before) && !Same(current, after))
                    throw new IOException("hosts 与事务前后版本均不一致；可能有外部修改或损坏，已保留记录，未覆盖。");
                Archive(pending, "completed.txn");
            }
            // These files cannot have affected hosts unless a valid pending journal was published.
            Archive(backup, "original.bak");
            if (File.Exists(stage)) File.Delete(stage);
            if (File.Exists(journalTemp)) File.Delete(journalTemp);
        }
        internal void Set(bool enable, Action<string> checkpoint = null)
        {
            Recover();
            byte[] before = ReadBounded(hosts, 1048576);
            byte[] after = HostsMapping.Transform(before, enable);
            if (Same(before, after)) return;
            WriteDurable(stage, after, checkpoint, "stage");
            WriteDurable(journalTemp, Journal(before, after), checkpoint, "journal");
            File.Move(journalTemp, pending);
            CheckPoint(checkpoint, "journal-published");
            if (!Same(ReadBounded(hosts, 1048576), before))
                throw new IOException("hosts 在准备期间被其他程序修改，未替换。");
            CheckPoint(checkpoint, "before-replace");
            File.Replace(stage, hosts, backup, false);
            CheckPoint(checkpoint, "after-replace");
            using (var committed = new FileStream(hosts, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) committed.Flush(true);
            if (!Same(ReadBounded(hosts, 1050000), after) || !Same(ReadBounded(backup, 1048576), before))
                throw new IOException("hosts 替换结果或并发修改检查失败，已保留恢复记录。");
            CheckPoint(checkpoint, "commit-flushed");
            Recover();
        }
    }
}
