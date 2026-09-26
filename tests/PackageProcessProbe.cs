using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ChatGptVergeLauncher;

internal static class PackageProcessProbe
{
    // Inspect the real installed executable without executing a single instruction.
    internal static string ReadSuspendedIdentity(string executable)
    {
        var startup = new StartupInfo();
        startup.Size = Marshal.SizeOf(startup);
        ProcessInfo child;
        if (!CreateProcess(executable, new StringBuilder(PackageLaunch.QuoteArgument(executable)),
            IntPtr.Zero, IntPtr.Zero, false, 4, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out child))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return PackageIdentity.ProcessFullName(child.Process); }
        finally
        {
            // Only our new, still-suspended process is terminated. Never enumerate or stop user processes.
            try
            {
                if (!TerminateProcess(child.Process, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (WaitForSingleObject(child.Process, 5000) != 0) throw new TimeoutException("Probe cleanup timed out.");
            }
            finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        internal short Show, ReservedSize;
        internal IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
