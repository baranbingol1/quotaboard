// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiLimits.Infrastructure.Providers.Antigravity;

/// <summary>
/// The operating-system side of the <c>agy</c> CLI session, kept behind an
/// interface so the session's launch and shutdown rules can be tested without
/// starting real processes.
/// </summary>
internal interface IAgyProcessHost
{
    IReadOnlyList<AgyListener> FindListeners();

    /// <summary>Starts <c>agy</c> and returns its pid, or null when it could not start.</summary>
    int? Launch(string binaryPath);

    bool IsAlive(int processId);

    void Kill(int processId);
}

/// <summary>
/// Launches <c>agy</c> as an invisible background process whose embedded
/// localhost server answers the same quota endpoints as the Antigravity app's
/// language server.
///
/// Three launch details are load-bearing and were established by observation
/// rather than documentation:
///
/// <list type="bullet">
/// <item>
/// Standard streams must NOT be redirected. <c>agy</c> is an interactive TUI, and
/// with a redirected stdin it waits for piped input forever: the process stays
/// alive but never starts its quota server, so probing would time out every time.
/// </item>
/// <item>
/// <c>CreateNoWindow</c> keeps this process off screen. Letting it inherit a
/// console instead leaves the CLI fighting over a terminal it does not own, and
/// it again never binds a port.
/// </item>
/// <item>
/// <c>CreateNoWindow</c> reaches this process and nothing it spawns, so it is not
/// on its own enough. <c>agy</c>'s auto-updater launches <c>agy --bg-updater</c>,
/// which runs <c>agy --version</c> in a console of its own; where Windows Terminal
/// is the default terminal, that is a real window opening and closing on the
/// user's screen. Refreshing more often than the CLI's fifteen-minute update
/// throttle turns it into a flash every quarter of an hour, all day.
/// <c>AGY_CLI_DISABLE_AUTO_UPDATE</c> is what stops it, and suppressing the check
/// is the honest behaviour besides: the updater rewrites <c>agy.exe</c> in place,
/// which is not a quota dashboard's call to make, and a headless check consumes
/// the same throttle the user's own <c>agy</c> sessions rely on.
/// </item>
/// </list>
///
/// Every launched process joins a kill-on-close job object, so a QuotaBoard that
/// crashes or is force-killed cannot leave an invisible <c>agy</c> behind with
/// nothing left to shut it down.
/// </summary>
internal sealed class AgyProcessHost : IAgyProcessHost, IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    /// <summary>Set on the child alone, so the user's own CLI still self-updates.</summary>
    internal const string DisableAutoUpdateVariable = "AGY_CLI_DISABLE_AUTO_UPDATE";

    private readonly AgyProcessDiscovery _discovery;
    private readonly object _sync = new();
    private SafeJobHandle? _job;
    private bool _jobUnavailable;
    private bool _disposed;

    public AgyProcessHost(AgyProcessDiscovery discovery)
    {
        _discovery = discovery;
    }

    public IReadOnlyList<AgyListener> FindListeners() => _discovery.FindListeners();

    public int? Launch(string binaryPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using Process? process = Process.Start(CreateStartInfo(binaryPath));
            if (process is null)
            {
                return null;
            }
            Adopt(process);
            return process.Id;
        }
        catch (Exception error)
            when (error
                    is System.ComponentModel.Win32Exception
                        or InvalidOperationException
                        or PlatformNotSupportedException
            )
        {
            return null;
        }
    }

    /// <summary>
    /// The launch parameters, split out from the launch itself so the rules that
    /// keep <c>agy</c> and its children off screen can be asserted without
    /// starting a real process.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string binaryPath)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = binaryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(binaryPath) ?? string.Empty,
        };
        // Scoped to this process's environment block; the user's shell is untouched.
        startInfo.Environment[DisableAutoUpdateVariable] = "true";
        return startInfo;
    }

    public bool IsAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Kill(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            // agy spawns helper children; killing only the root would strand them.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error)
            when (error
                    is ArgumentException
                        or InvalidOperationException
                        or NotSupportedException
                        or System.ComponentModel.Win32Exception
            )
        {
            // Already gone, or no longer ours to touch. Either way there is
            // nothing left to shut down.
        }
    }

    /// <summary>
    /// Puts the process in a job that dies with QuotaBoard. Best effort: when the
    /// job cannot be created the session still shuts the process down itself, so
    /// a failure here must not fail the launch.
    /// </summary>
    private void Adopt(Process process)
    {
        lock (_sync)
        {
            if (_disposed || _jobUnavailable)
            {
                return;
            }
            _job ??= CreateKillOnCloseJob();
            if (_job is null)
            {
                _jobUnavailable = true;
                return;
            }
            if (!AssignProcessToJobObject(_job, process.Handle))
            {
                _jobUnavailable = true;
            }
        }
    }

    private static SafeJobHandle? CreateKillOnCloseJob()
    {
        SafeJobHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            return null;
        }

        JobObjectExtendedLimitInformation limits = new();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                job.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return job;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            // Closing the last handle terminates everything left in the job.
            _job?.Dispose();
            _job = null;
        }
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

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
    private struct JobObjectBasicLimitInformation
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
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
