using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace ChatGptVergeLauncher
{
    internal sealed class PackagedApp
    {
        internal string FullName { get; private set; }
        internal string FamilyName { get; private set; }
        internal string ApplicationId { get; private set; }
        internal string ExecutablePath { get; private set; }

        internal static PackagedApp Read(string fullName, string packageRoot)
        {
            if (String.IsNullOrWhiteSpace(fullName) || String.IsNullOrWhiteSpace(packageRoot))
                return null;
            try
            {
                string root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                var manifest = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(Path.Combine(root, "AppxManifest.xml"), settings))
                    manifest.Load(reader);
                var namespaces = new XmlNamespaceManager(manifest.NameTable);
                namespaces.AddNamespace("p", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
                foreach (XmlElement application in manifest.SelectNodes("/p:Package/p:Applications/p:Application", namespaces))
                {
                    string relative = application.GetAttribute("Executable").Replace('/', '\\');
                    if (Path.IsPathRooted(relative) || !String.Equals(Path.GetFileName(relative), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Only the registered full-trust ChatGPT entry point is supported.
                    if (application.GetAttribute("EntryPoint") != "Windows.FullTrustApplication") continue;
                    string executable = Path.GetFullPath(Path.Combine(root, relative));
                    string id = application.GetAttribute("Id");
                    if (!executable.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(executable) || String.IsNullOrWhiteSpace(id))
                        continue;
                    return new PackagedApp {
                        FullName = fullName, FamilyName = PackageIdentity.FamilyFromFullName(fullName),
                        ApplicationId = id, ExecutablePath = executable
                    };
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (XmlException) { }
            catch (ArgumentException) { }
            catch (Win32Exception) { }
            return null;
        }
    }

    internal static class PackageIdentity
    {
        private const int InsufficientBuffer = 122;
        private const int NoPackage = 15700;

        internal static string FamilyFromFullName(string fullName)
        {
            uint length = 0;
            int result = PackageFamilyNameFromFullName(fullName, ref length, null);
            if (result != InsufficientBuffer) throw new Win32Exception(result);
            var family = new StringBuilder((int)length);
            result = PackageFamilyNameFromFullName(fullName, ref length, family);
            if (result != 0) throw new Win32Exception(result);
            return family.ToString();
        }

        internal static string CurrentFullName()
        {
            return ProcessFullName(GetCurrentProcess());
        }

        internal static string ProcessFullName(IntPtr process)
        {
            uint length = 0;
            int result = GetPackageFullName(process, ref length, null);
            if (result == NoPackage) return null;
            if (result != InsufficientBuffer) throw new Win32Exception(result);
            var name = new StringBuilder((int)length);
            result = GetPackageFullName(process, ref length, name);
            if (result != 0) throw new Win32Exception(result);
            return name.ToString();
        }

        internal static void RequireCurrent(string expected)
        {
            if (String.IsNullOrWhiteSpace(expected) || !String.Equals(CurrentFullName(), expected, StringComparison.Ordinal))
                throw new InvalidOperationException("启动桥接未获得预期的 Windows 程序包标识，请从完整发布目录运行启动器。");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int PackageFamilyNameFromFullName(string fullName, ref uint length, StringBuilder familyName);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder fullName);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
    }
}
