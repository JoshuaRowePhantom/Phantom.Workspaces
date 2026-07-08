using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Phantom.Workspaces;

/// <summary>Captures the result of a completed process invocation.</summary>
public sealed record ProcessResult(
    int ExitCode,
    string StandardOut,
    string StandardError,
    string StandardOutAndError);

/// <summary>Parameters for a <see cref="ProcessRunner.RunProcessAsync"/> invocation.</summary>
public sealed record RunProcessParameters(
    string Command,
    IReadOnlyList<string> Arguments,
    KillOnCloseAction KillOnClose = KillOnCloseAction.None,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null);

/// <summary>Controls whether a child process tree is killed when the parent process exits.</summary>
public enum KillOnCloseAction
{
    /// <summary>The child process outlives the parent (default behaviour).</summary>
    None,

    /// <summary>
    /// On Windows, assigns the child to a Job Object with
    /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> so the entire process tree is killed when the
    /// parent process exits unexpectedly.
    /// </summary>
    KillTree,
}

/// <summary>
/// Shared utility for running a child process, capturing its output, and waiting for it to exit.
/// </summary>
public static class ProcessRunner
{
    // Keeps job-object SafeHandles alive for the lifetime of the process so the kernel
    // does not close the job (and fire kill-on-close) prematurely due to GC collection.
    private static readonly ConcurrentBag<SafeHandle> s_jobObjects = new();

    /// <summary>
    /// Starts <paramref name="parameters.Command"/> with the supplied arguments, captures stdout
    /// and stderr concurrently to avoid deadlock, and returns a <see cref="ProcessResult"/> when
    /// the process exits.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// The executable was not found or could not be started.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the process exited.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// <paramref name="parameters.Timeout"/> elapsed before the process exited.
    /// </exception>
    public static async Task<ProcessResult> RunProcessAsync(
        RunProcessParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var startInfo = new ProcessStartInfo
        {
            FileName = parameters.Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(parameters.WorkingDirectory))
        {
            startInfo.WorkingDirectory = parameters.WorkingDirectory;
        }

        foreach (var arg in parameters.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (parameters.Timeout.HasValue)
        {
            cts.CancelAfter(parameters.Timeout.Value);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        if (parameters.KillOnClose == KillOnCloseAction.KillTree && OperatingSystem.IsWindows())
        {
            AssignToWindowsJobObject(process);
        }

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var combinedLines = new List<string>();
        var combinedLock = new object();

        var stdoutTask = ReadLinesAsync(process.StandardOutput, stdoutLines, combinedLines, combinedLock);
        var stderrTask = ReadLinesAsync(process.StandardError, stderrLines, combinedLines, combinedLock);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }

            await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var partialOutput = string.Join(Environment.NewLine, combinedLines);
            throw new TimeoutException(
                $"Process '{parameters.Command}' did not complete within the allotted {parameters.Timeout}. Partial output:\n{partialOutput}");
        }

        await stdoutTask.ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);

        return new ProcessResult(
            process.ExitCode,
            string.Join(Environment.NewLine, stdoutLines),
            string.Join(Environment.NewLine, stderrLines),
            string.Join(Environment.NewLine, combinedLines));
    }

    /// <summary>
    /// Runs a process and logs the combined stdout+stderr output via the supplied
    /// <paramref name="logger"/>. Logs at Debug level on success (exit code 0), at Warning level
    /// on non-zero exit, and at Error level on timeout.
    /// </summary>
    public static async Task<ProcessResult> RunAndLogAsync(
        RunProcessParameters parameters,
        ILogger logger,
        string? operationDescription = null,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result;
        try
        {
            result = await RunProcessAsync(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            logger.LogError(
                ex,
                "Process '{Command}' timed out after {Timeout}{Description}. {ExceptionMessage}",
                parameters.Command,
                parameters.Timeout,
                operationDescription is null ? string.Empty : $" ({operationDescription})",
                ex.Message);
            throw;
        }

        if (result.ExitCode != 0)
        {
            logger.LogWarning(
                "Process '{Command}' exited with code {ExitCode}{Description}.\nOutput:\n{Output}",
                parameters.Command,
                result.ExitCode,
                operationDescription is null ? string.Empty : $" ({operationDescription})",
                result.StandardOutAndError);
        }
        else if (!string.IsNullOrWhiteSpace(result.StandardOutAndError))
        {
            logger.LogDebug(
                "Process '{Command}' completed successfully{Description}.\nOutput:\n{Output}",
                parameters.Command,
                operationDescription is null ? string.Empty : $" ({operationDescription})",
                result.StandardOutAndError);
        }

        return result;
    }

    /// <summary>
    /// Creates a Win32 Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> and assigns
    /// <paramref name="process"/> to it. The handle is kept alive in a static collection so GC
    /// cannot close it before the parent process exits.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void AssignToWindowsJobObject(Process process)
    {
        var jobHandle = Win32.CreateJobObject(IntPtr.Zero, null);
        if (jobHandle == IntPtr.Zero)
        {
            return;
        }

        var limitInfo = new Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limitInfo.BasicLimitInformation.LimitFlags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        int size = Marshal.SizeOf(limitInfo);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limitInfo, ptr, false);
            Win32.SetInformationJobObject(
                jobHandle,
                Win32.JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                ptr,
                (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        s_jobObjects.Add(new JobObjectSafeHandle(jobHandle));
        Win32.AssignProcessToJobObject(jobHandle, process.Handle);
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        List<string> lines,
        List<string> combined,
        object combinedLock)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            lines.Add(line);
            lock (combinedLock)
            {
                combined.Add(line);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class JobObjectSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobObjectSafeHandle(IntPtr handle) : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => Win32.CloseHandle(handle);
    }

    [SupportedOSPlatform("windows")]
    private static class Win32
    {
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        public enum JOBOBJECTINFOCLASS
        {
            JobObjectExtendedLimitInformation = 9,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(
            IntPtr hJob,
            JOBOBJECTINFOCLASS infoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
