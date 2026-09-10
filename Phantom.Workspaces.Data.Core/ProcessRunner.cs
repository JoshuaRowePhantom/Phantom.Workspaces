using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Phantom.Workspaces;

/// <summary>Captures the result of a completed process invocation.</summary>
public sealed record ProcessResult(
    int ExitCode,
    string StandardOut,
    string StandardError,
    string StandardOutAndError,
    bool JobAssigned = false,
    string? UnsignedNtStatus = null);

/// <summary>Parameters for a <see cref="ProcessRunner.RunProcessAsync"/> invocation.</summary>
public sealed record RunProcessParameters(
    string Command,
    IReadOnlyList<string> Arguments,
    KillOnCloseAction KillOnClose = KillOnCloseAction.None,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

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

        if (parameters.KillOnClose == KillOnCloseAction.KillTree && OperatingSystem.IsWindows())
            return await RunWindowsContainedAsync(parameters, cancellationToken).ConfigureAwait(false);

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

        if (parameters.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in parameters.EnvironmentVariables)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (parameters.Timeout.HasValue)
        {
            cts.CancelAfter(parameters.Timeout.Value);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

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
            string.Join(Environment.NewLine, combinedLines),
            UnsignedNtStatus: NormalizeUnsignedStatus(process.ExitCode));
    }

    /// <summary>
    /// Runs a process and logs its output via the supplied <paramref name="logger"/>. Standard
    /// output and standard error are always logged as distinct fields so both streams are visible
    /// on success and on failure. Logs at <paramref name="successLogLevel"/> (default
    /// <see cref="LogLevel.Information"/>) on success (exit code 0), at Warning level on non-zero
    /// exit, and at Error level on timeout. Callers that shell out to commands whose stdout may
    /// contain secrets (for example <c>gh auth token</c>) should pass <see cref="LogLevel.Debug"/>
    /// so the sensitive output is not surfaced at Information.
    /// </summary>
    public static async Task<ProcessResult> RunAndLogAsync(
        RunProcessParameters parameters,
        ILogger logger,
        string? operationDescription = null,
        CancellationToken cancellationToken = default,
        LogLevel successLogLevel = LogLevel.Information)
    {
        var description = operationDescription is null ? string.Empty : $" ({operationDescription})";

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
                description,
                ex.Message);
            throw;
        }

        if (result.ExitCode != 0)
        {
            logger.LogWarning(
                "Process '{Command}' exited with code {ExitCode}{Description}."
                + "\nStandard output:\n{StandardOutput}\nStandard error:\n{StandardError}",
                parameters.Command,
                result.ExitCode,
                description,
                result.StandardOut,
                result.StandardError);
        }
        else if (!string.IsNullOrWhiteSpace(result.StandardOut)
            || !string.IsNullOrWhiteSpace(result.StandardError))
        {
            logger.Log(
                successLogLevel,
                "Process '{Command}' completed successfully{Description}."
                + "\nStandard output:\n{StandardOutput}\nStandard error:\n{StandardError}",
                parameters.Command,
                description,
                result.StandardOut,
                result.StandardError);
        }

        return result;
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
    private static async Task<ProcessResult> RunWindowsContainedAsync(
                RunProcessParameters parameters,
                CancellationToken cancellationToken)
            {
                using var stdoutRead = Win32.CreateOutputPipe(out var stdoutWrite);
                using var stderrRead = Win32.CreateOutputPipe(out var stderrWrite);
                using var stdoutWriteLifetime = stdoutWrite;
                using var stderrWriteLifetime = stderrWrite;
                using var job = Win32.CreateConfiguredJob();

                var startup = new Win32.STARTUPINFO
                {
                    cb = Marshal.SizeOf<Win32.STARTUPINFO>(),
                    dwFlags = Win32.STARTF_USESTDHANDLES,
                    hStdInput = Win32.GetStdHandle(Win32.STD_INPUT_HANDLE),
                    hStdOutput = stdoutWrite.DangerousGetHandle(),
                    hStdError = stderrWrite.DangerousGetHandle(),
                };
                var commandLine = new StringBuilder();
                AppendWindowsArgument(commandLine, parameters.Command);
                foreach (var argument in parameters.Arguments)
                {
                    commandLine.Append(' ');
                    AppendWindowsArgument(commandLine, argument);
                }

                var environment = Win32.CreateEnvironmentBlock(parameters.EnvironmentVariables);
                try
                {
                    if (!Win32.CreateProcessW(
                            null,
                            commandLine,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            true,
                            Win32.CREATE_SUSPENDED | Win32.CREATE_NO_WINDOW | Win32.CREATE_UNICODE_ENVIRONMENT,
                            environment,
                            parameters.WorkingDirectory,
                            ref startup,
                            out var processInformation))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed.");
                    }

                    using var process = new SafeProcessHandle(processInformation.hProcess, ownsHandle: true);
                    using var thread = new SafeWaitHandle(processInformation.hThread, ownsHandle: true);
                    try
                    {
                        if (!Win32.AssignProcessToJobObject(job, process))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
                        if (Win32.ResumeThread(thread) == uint.MaxValue)
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed.");

                        stdoutWrite.Dispose();
                        stderrWrite.Dispose();
                        using var stdoutReader = new StreamReader(
                            new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false));
                        using var stderrReader = new StreamReader(
                            new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false));
                        var stdoutLines = new List<string>();
                        var stderrLines = new List<string>();
                        var combinedLines = new List<string>();
                        var combinedLock = new object();
                        var stdoutTask = ReadLinesAsync(stdoutReader, stdoutLines, combinedLines, combinedLock);
                        var stderrTask = ReadLinesAsync(stderrReader, stderrLines, combinedLines, combinedLock);

                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        if (parameters.Timeout is { } timeout)
                            linked.CancelAfter(timeout);
                        try
                        {
                            await Win32.WaitForProcessAsync(process, linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            job.Dispose();
                            await Win32.WaitForProcessAsync(process, CancellationToken.None).ConfigureAwait(false);
                            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new TimeoutException(
                                $"Process did not complete within the allotted {parameters.Timeout}.");
                        }

                        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                        if (!Win32.GetExitCodeProcess(process, out var rawExitCode))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess failed.");
                        var exitCode = unchecked((int)rawExitCode);
                        return new ProcessResult(
                            exitCode,
                            string.Join(Environment.NewLine, stdoutLines),
                            string.Join(Environment.NewLine, stderrLines),
                            string.Join(Environment.NewLine, combinedLines),
                            JobAssigned: true,
                            UnsignedNtStatus: NormalizeUnsignedStatus(exitCode));
                    }
                    catch
                    {
                        if (Win32.GetExitCodeProcess(process, out var code) && code == Win32.STILL_ACTIVE)
                            Win32.TerminateProcess(process, 0xC000013A);
                        throw;
                    }
                }
                finally
                {
                    if (environment != IntPtr.Zero)
                        Marshal.FreeHGlobal(environment);
                }
            }

    private static string? NormalizeUnsignedStatus(int exitCode) =>
                exitCode < 0 ? $"0x{unchecked((uint)exitCode):X8}" : null;

    private static void AppendWindowsArgument(StringBuilder commandLine, string argument)
            {
                if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
                {
                    commandLine.Append(argument);
                    return;
                }

                commandLine.Append('"');
                var backslashes = 0;
                foreach (var character in argument)
                {
                    if (character == '\\')
                    {
                        backslashes++;
                        continue;
                    }
                    if (character == '"')
                    {
                        commandLine.Append('\\', backslashes * 2 + 1).Append('"');
                        backslashes = 0;
                        continue;
                    }
                    commandLine.Append('\\', backslashes).Append(character);
                    backslashes = 0;
                }
                commandLine.Append('\\', backslashes * 2).Append('"');
    }

    [SupportedOSPlatform("windows")]
    private sealed class JobObjectSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private JobObjectSafeHandle() : base(ownsHandle: true)
        {
        }

        public JobObjectSafeHandle(IntPtr handle) : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => Win32.CloseHandle(handle);
    }

    [SupportedOSPlatform("windows")]
    private static class Win32
    {
        public const uint CREATE_SUSPENDED = 0x00000004;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint CREATE_NO_WINDOW = 0x08000000;
        public const uint STARTF_USESTDHANDLES = 0x00000100;
        public const uint HANDLE_FLAG_INHERIT = 0x00000001;
        public const int STD_INPUT_HANDLE = -10;
        public const uint STILL_ACTIVE = 259;
        public const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x0400;
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

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern JobObjectSafeHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(
            JobObjectSafeHandle hJob,
            SafeProcessHandle hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(
            JobObjectSafeHandle hJob,
            JOBOBJECTINFOCLASS infoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(
            out SafeFileHandle hReadPipe,
            out SafeFileHandle hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes,
            uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetHandleInformation(
            SafeFileHandle hObject,
            uint dwMask,
            uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateProcessW(
            string? lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(SafeWaitHandle hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(
            SafeProcessHandle hProcess,
            out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int nStdHandle);

        public static SafeFileHandle CreateOutputPipe(out SafeFileHandle write)
        {
            var attributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = 1,
            };
            if (!CreatePipe(out var read, out write, ref attributes, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
            if (!SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0))
            {
                read.Dispose();
                write.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation failed.");
            }
            return read;
        }

        public static JobObjectSafeHandle CreateConfiguredJob()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");

            var information = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            information.BasicLimitInformation.LimitFlags =
                JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION | JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            var size = Marshal.SizeOf(information);
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, pointer, false);
                if (!SetInformationJobObject(
                        job,
                        JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                        pointer,
                        (uint)size))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "SetInformationJobObject failed.");
                }
            }
            catch
            {
                job.Dispose();
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
            return job;
        }

        public static IntPtr CreateEnvironmentBlock(
            IReadOnlyDictionary<string, string>? overrides)
        {
            if (overrides is null || overrides.Count == 0)
                return IntPtr.Zero;

            var environment = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Where(entry => entry.Key is string && entry.Value is string)
                .ToDictionary(
                    entry => (string)entry.Key,
                    entry => (string)entry.Value!,
                    StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in overrides)
                environment[key] = value;
            var block = string.Join(
                '\0',
                environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
            return Marshal.StringToHGlobalUni(block);
        }

        public static Task WaitForProcessAsync(
            SafeProcessHandle process,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var waitHandle = new EventWaitHandle(false, EventResetMode.ManualReset)
            {
                SafeWaitHandle = new SafeWaitHandle(
                    process.DangerousGetHandle(),
                    ownsHandle: false),
            };
            RegisteredWaitHandle? registration = null;
            CancellationTokenRegistration cancellationRegistration = default;
            var completed = 0;
            void Complete(Action action)
            {
                if (Interlocked.Exchange(ref completed, 1) != 0)
                    return;
                registration?.Unregister(null);
                cancellationRegistration.Dispose();
                waitHandle.Dispose();
                action();
            }
            registration = ThreadPool.RegisterWaitForSingleObject(
                waitHandle,
                (_, _) => Complete(() => completion.TrySetResult()),
                null,
                Timeout.Infinite,
                true);
            cancellationRegistration = cancellationToken.Register(
                () => Complete(() => completion.TrySetCanceled(cancellationToken)));
            return completion.Task;
        }
    }
}
