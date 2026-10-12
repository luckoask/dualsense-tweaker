using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DualSenseTweaker.MacroGamer.Scripting;

/// <summary>Windows process memory bound, one-process limit and kill-on-parent-close. Assigned before any user script is sent.</summary>
internal sealed class WorkerJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    public WorkerJob(Process process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var limits = new ExtendedLimits
        {
            Basic = new() { LimitFlags = 0x2000 | 0x0100 | 0x0008, ActiveProcessLimit = 1 },
            ProcessMemoryLimit = (UIntPtr)(256 * 1024 * 1024),
        };
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(_handle, process.Handle))
        { int error = Marshal.GetLastPInvokeError(); _handle.Dispose(); throw new Win32Exception(error, "Could not enforce script worker resource limits."); }
    }
    public void Dispose() => _handle.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
