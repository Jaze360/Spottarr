using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Spottarr.Windows;

// Closing the launcher (including Task Scheduler termination) must not orphan the server.
internal sealed class ServerJob : IDisposable
{
    private readonly SafeFileHandle handle;

    public ServerJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
            throw new Win32Exception();
        var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            handle.Dispose();
            throw new Win32Exception();
        }
    }

    public void Attach(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle))
            throw new Win32Exception();
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessTime,
            PerJobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet,
            MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass,
            SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount,
            WriteOperationCount,
            OtherOperationCount,
            ReadTransferCount,
            WriteTransferCount,
            OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit,
            JobMemoryLimit,
            PeakProcessMemoryUsed,
            PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int infoClass,
        ref ExtendedLimits info,
        uint length
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
