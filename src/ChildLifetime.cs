using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChatGptVergeLauncher
{
    // Each proxy owns only its own runtime descendants. Closing or killing the
    // proxy must not leave an unobserved CLI/Node process behind.
    internal sealed class ChildLifetime : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
            internal long PerProcessTime, PerJobTime;
            internal uint Flags;
            internal UIntPtr MinWorkingSet, MaxWorkingSet;
            internal uint ActiveProcesses;
            internal UIntPtr Affinity;
            internal uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits {
            internal BasicLimits Basic;
            internal IoCounters Io;
            internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        private readonly SafeFileHandle job;
        internal ChildLifetime(Process child)
        {
            job = CreateJobObject(IntPtr.Zero, null);
            try {
                if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var limits = new ExtendedLimits(); limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!AssignProcessToJobObject(job, child.Handle)) {
                    int error = Marshal.GetLastWin32Error();
                    if (!child.HasExited) throw new Win32Exception(error);
                }
            } catch {
                try { if (!child.HasExited) child.Kill(); } finally { job.Dispose(); }
                throw;
            }
        }
        public void Dispose() { job.Dispose(); }
    }
}
