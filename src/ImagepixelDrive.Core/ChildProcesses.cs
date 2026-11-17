using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ImagepixelDrive.Core;

/// <summary>
/// Ties the programs this process starts to its own life. If the application ends for
/// any reason, even without a chance to clean up, Windows ends rclone with it, so that
/// no drive is left behind that nobody looks after.
/// </summary>
internal static class ChildProcesses
{
    private static readonly Lock Gate = new();
    private static nint _job;

    public static void EndWithThisProcess(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            AssignToJob(process);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // the process has ended already
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssignToJob(Process process)
    {
        lock (Gate)
        {
            if (_job == 0)
            {
                _job = CreateJob();
            }
        }

        // fails for a process that has ended; there is nothing to tie then
        AssignProcessToJobObject(_job, process.Handle);
    }

    [SupportedOSPlatform("windows")]
    private static nint CreateJob()
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            throw new System.ComponentModel.Win32Exception();
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                throw new System.ComponentModel.Win32Exception();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // The handle is never closed on purpose: Windows closes it when this process
        // ends, and that is what ends the processes in the job.
        return job;
    }

    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int informationClass, nint information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
}
