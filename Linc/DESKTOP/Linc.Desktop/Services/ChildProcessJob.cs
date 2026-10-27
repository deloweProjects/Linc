using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Linc.Desktop.Services;

/// <summary>
/// Ties the scrcpy processes Linc starts (mirror window, app windows, Desktop Mode, the phone's
/// sound) to Linc's own life (TIDY-UP B2). They go into one Windows job object that kills them when
/// its last handle closes, and Windows closes it whenever Linc ends: a normal exit, a crash or
/// <c>Environment.Exit</c>. Before this a crashed Linc could leave a mirror window open, or the
/// phone's sound playing on the PC, with nothing left to stop it.
/// Only the process itself is held. Whatever it starts breaks away silently, so adb (and the adb
/// server Linc shares with them) is never taken down when Linc ends.
/// </summary>
public static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(Create);

    /// <summary>Adds a started process to the job. False, logged, if Windows refuses: the
    /// process still runs, it just isn't ended with Linc (how it always was).</summary>
    public static bool Add(Process process, ILogService? log = null)
    {
        try
        {
            var job = Job.Value;
            if (job == IntPtr.Zero)
            {
                return false;
            }
            if (AssignProcessToJobObject(job, process.Handle))
            {
                return true;
            }
            log?.Log(LogLevel.Warn, $"Couldn't tie a scrcpy process to Linc's life (Windows error {Marshal.GetLastWin32Error()}); it may outlive a crash");
        }
        catch (Exception ex)
        {
            // Exited already (no handle), or not a process of ours: nothing to tie.
            log?.Log(LogLevel.Warn, $"Couldn't tie a scrcpy process to Linc's life ({ex.Message})");
        }
        return false;
    }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK,
            },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }
        // Never closed on purpose: Windows closes it when Linc's process ends, and that is what
        // ends the children.
        return job;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
