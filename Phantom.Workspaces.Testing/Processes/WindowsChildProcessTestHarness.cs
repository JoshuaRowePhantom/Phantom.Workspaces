using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Phantom.Workspaces.Testing.Processes;

public enum WindowsProbeScenario
{
    NormalChild,
    FailingChild,
    StatusDllInitFailed,
    TimeoutTree,
    Direct,
    ConPty,
    ConPtyLaunchFailure,
    OrdinaryProcessExecutor,
    MxcProcessExecutor,
    CommandShim,
    MxcCommandShim,
    ProcessRunnerNoJob,
    ProcessRunnerKillTree,
    ProcessRunnerExitedParentTree,
    ProcessRunnerTimeoutTree,
    ProcessRunnerCancellationTree,
    ProcessRunnerConfigureFailure,
    ProcessRunnerAssignFailure,
    ProcessRunnerResumeFailure,
    ConPtyConfigureFailure,
    ConPtyAssignFailure,
    ConPtyResumeFailure,
}

public enum WindowsProcessPathCategory
{
    SystemBinary,
    FixedProbeBinary,
    FixedCommandShim,
    FixedMissingBinary,
}

public enum WindowsLaunchMechanism
{
    DirectCreateProcess,
    CreateProcessWithConPty,
    OrdinaryProcess,
    MxcSpawn,
    ResolverCommandInterpreter,
    JobRunner,
}

public sealed record WindowsContainmentDescriptor
{
    public required string PolicyType { get; init; }
    public required string PolicyIdentity { get; init; }
}

public sealed record WindowsChildProcessProbeRequest
{
    public required WindowsProbeScenario Scenario { get; init; }
    public required WindowsProcessPathCategory PathCategory { get; init; }
    public required WindowsLaunchMechanism LaunchMechanism { get; init; }
    public required TimeSpan Timeout { get; init; }
    public WindowsContainmentDescriptor? Containment { get; init; }
}

public sealed record WindowsChildProcessProbeResult
{
    public required bool BrokerCreateProcessSucceeded { get; init; }
    public required int? BrokerCreateProcessWin32Error { get; init; }
    public required bool CreationStatusAvailable { get; init; }
    public required bool? CreateProcessSucceeded { get; init; }
    public required int? CreateProcessWin32Error { get; init; }
    public required int? ExitCode { get; init; }
    public required string? UnsignedNtStatus { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public required string TerminalOutput { get; init; }
    public required string? ExecutableImageIdentity { get; init; }
    public required bool TimedOut { get; init; }
    public required bool ReadinessHandshakeObserved { get; init; }
    public required WindowsProcessPathCategory PathCategory { get; init; }
    public required WindowsLaunchMechanism LaunchMechanism { get; init; }
    public WindowsContainmentDescriptor? Containment { get; init; }
    public required bool BrokerJobConfigured { get; init; }
    public required bool BrokerJobAssigned { get; init; }
    public required bool BrokerResumeSucceeded { get; init; }
    public required bool? SdkSpawnSucceeded { get; init; }
    public required bool? JobConfigured { get; init; }
    public required bool? JobAssigned { get; init; }
    public required bool? ResumeSucceeded { get; init; }
    public required bool? InnerJobAssigned { get; init; }
    public bool? DirectProcessInJob { get; init; }
    public uint? ActiveJobProcessesBeforeCleanup { get; init; }
    public uint? ActiveJobProcessesAfterCleanup { get; init; }
    public required bool? CleanupCompleted { get; init; }
    public required bool ChildExitObserved { get; init; }
    public required bool DescendantExitObserved { get; init; }
    public required bool ProcessHandleClosed { get; init; }
    public required bool ThreadHandleClosed { get; init; }
    public required bool JobHandleClosed { get; init; }
    public required bool PseudoConsoleHandleClosed { get; init; }
    public required bool InputHandleClosed { get; init; }
    public required bool OutputHandleClosed { get; init; }
    public required bool AttributeListReleased { get; init; }
    public string? FailureStage { get; init; }

    public string ToSanitizedDiagnostic() => string.Join(
        "; ",
        $"path={PathCategory}",
        $"mechanism={LaunchMechanism}",
        $"creationAvailable={CreationStatusAvailable}",
        $"created={CreateProcessSucceeded?.ToString() ?? "unavailable"}",
        $"win32={CreateProcessWin32Error?.ToString() ?? "none"}",
        $"exit={ExitCode?.ToString() ?? "none"}",
        $"status={UnsignedNtStatus ?? "none"}",
        $"image={ExecutableImageIdentity ?? "unavailable"}",
        $"timedOut={TimedOut}",
        $"ready={ReadinessHandshakeObserved}",
        $"containmentType={Containment?.PolicyType ?? "none"}",
        $"containmentIdentity={Containment?.PolicyIdentity ?? "none"}",
        $"sdkSpawn={SdkSpawnSucceeded?.ToString() ?? "unavailable"}",
        $"jobConfigured={JobConfigured?.ToString() ?? "unavailable"}",
        $"jobAssigned={JobAssigned?.ToString() ?? "unavailable"}",
        $"resumed={ResumeSucceeded?.ToString() ?? "unavailable"}");
}

public static class WindowsChildProcessTestHarness
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint StillActive = 259;

    public static async Task<WindowsChildProcessProbeResult> RunAsync(
        WindowsChildProcessProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows child-process harness requires Windows.");
        if (request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Timeout must be positive.");

        var probePath = Path.Combine(
            AppContext.BaseDirectory,
            "Phantom.Workspaces.Test.WindowsProcessProbe.exe");
        if (!File.Exists(probePath))
            throw new FileNotFoundException("The fixed Windows process probe is not present.", probePath);

        using var stdoutRead = CreateOutputPipe(out var stdoutWrite);
        using var stderrRead = CreateOutputPipe(out var stderrWrite);
        using var stdoutWriteLifetime = stdoutWrite;
        using var stderrWriteLifetime = stderrWrite;
        using var job = CreateConfiguredJob();

        var startup = new StartupInfo
        {
            Cb = Marshal.SizeOf<StartupInfo>(),
            Flags = StartfUseStdHandles,
            StdInput = GetStdHandle(-10),
            StdOutput = stdoutWrite.DangerousGetHandle(),
            StdError = stderrWrite.DangerousGetHandle(),
        };
        var commandLine = new StringBuilder()
            .Append('"').Append(probePath).Append("\" --scenario ")
            .Append(request.Scenario)
            .Append(" --path-category ").Append(request.PathCategory)
            .Append(" --mechanism ").Append(request.LaunchMechanism)
            .Append(" --timeout-ms ").Append(checked((long)request.Timeout.TotalMilliseconds));
        ProcessInformation processInfo = default;
        var created = CreateProcess(
            probePath,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            true,
            CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
            IntPtr.Zero,
            null,
            ref startup,
            out processInfo);
        int? createError = created ? null : Marshal.GetLastWin32Error();
        if (!created)
        {
            return FailedBrokerResult(request, createError);
        }

        using var process = new SafeProcessHandle(processInfo.Process, ownsHandle: true);
        using var thread = new SafeWaitHandle(processInfo.Thread, ownsHandle: true);
        var jobAssigned = false;
        var resumed = false;
        try
        {
            if (!AssignProcessToJobObject(job, process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to assign the suspended probe broker.");
            jobAssigned = true;

            var resumeResult = ResumeThread(thread);
            if (resumeResult == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to resume the probe broker.");
            resumed = true;

            stdoutWrite.Dispose();
            stderrWrite.Dispose();
            if (request.Scenario == WindowsProbeScenario.TimeoutTree)
            {
                return await ObserveTimeoutCleanupAsync(
                    request,
                    process,
                    thread,
                    job,
                    stdoutRead,
                    stderrRead,
                    cancellationToken).ConfigureAwait(false);
            }

            var stdoutTask = ReadAllAsync(stdoutRead, cancellationToken);
            var stderrTask = ReadAllAsync(stderrRead, cancellationToken);

            using var timeoutSource = new CancellationTokenSource(request.Timeout + TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            try
            {
                await WaitForProcessAsync(process, linked.Token).ConfigureAwait(false);
            }
            catch
            {
                job.Dispose();
                await WaitForProcessAsync(process, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var jsonStart = stdout.LastIndexOf('{');
            if (jsonStart < 0)
                throw new InvalidDataException("The probe returned no structured result.");
            var result = JsonSerializer.Deserialize<WindowsChildProcessProbeResult>(
                stdout[jsonStart..],
                JsonOptions)
                ?? throw new InvalidDataException("The probe returned no result.");
            return result with
            {
                BrokerCreateProcessSucceeded = true,
                BrokerCreateProcessWin32Error = null,
                BrokerJobConfigured = true,
                BrokerJobAssigned = jobAssigned,
                BrokerResumeSucceeded = resumed,
                StandardError = string.Concat(result.StandardError, stderr),
            };
        }
        finally
        {
            if (!process.IsClosed
                && (!GetExitCodeProcess(process, out var exitCode) || exitCode == StillActive))
                TerminateProcess(process, 0xC000013A);
        }
    }

    private static async Task<WindowsChildProcessProbeResult> ObserveTimeoutCleanupAsync(
            WindowsChildProcessProbeRequest request,
            SafeProcessHandle broker,
            SafeWaitHandle brokerThread,
            SafeFileHandle job,
            SafeFileHandle stdoutHandle,
            SafeFileHandle stderrHandle,
            CancellationToken cancellationToken)
        {
            using var stdout = new StreamReader(
                new FileStream(stdoutHandle, FileAccess.Read, 4096, isAsync: false),
                Encoding.UTF8);
            using var stderr = new StreamReader(
                new FileStream(stderrHandle, FileAccess.Read, 4096, isAsync: false),
                Encoding.UTF8);
            var stderrTask = stderr.ReadToEndAsync(cancellationToken);
            var readiness = await stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (readiness is null || !readiness.StartsWith("TIMEOUT_READY:", StringComparison.Ordinal))
                throw new InvalidDataException("The timeout probe did not report its process tree.");

            var ids = readiness.Split(':');
            if (ids.Length != 3
                || !uint.TryParse(ids[1], out var childId)
                || !uint.TryParse(ids[2], out var descendantId))
            {
                throw new InvalidDataException("The timeout probe reported an invalid process tree.");
            }

            using var child = OpenProcess(Synchronize, false, childId);
            using var descendant = OpenProcess(Synchronize, false, descendantId);
            if (child.IsInvalid || descendant.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to observe the timeout process tree.");

            using var timeout = new CancellationTokenSource(request.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            try
            {
                await WaitForProcessAsync(broker, linked.Token).ConfigureAwait(false);
                throw new InvalidDataException("The timeout probe exited before its harness timeout.");
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                job.Dispose();
            }

            await Task.WhenAll(
                WaitForProcessAsync(broker, CancellationToken.None),
                WaitForProcessAsync(child, CancellationToken.None),
                WaitForProcessAsync(descendant, CancellationToken.None)).ConfigureAwait(false);
            var remainingOutput = await stdout.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);

            stdout.Dispose();
            stderr.Dispose();
            child.Dispose();
            descendant.Dispose();
            brokerThread.Dispose();
            broker.Dispose();
            return new WindowsChildProcessProbeResult
            {
                BrokerCreateProcessSucceeded = true,
                BrokerCreateProcessWin32Error = null,
                CreationStatusAvailable = true,
                CreateProcessSucceeded = true,
                CreateProcessWin32Error = null,
                ExitCode = unchecked((int)0xC000013A),
                UnsignedNtStatus = "0xC000013A",
                StandardOutput = remainingOutput,
                StandardError = error,
                TerminalOutput = readiness,
                ExecutableImageIdentity = null,
                TimedOut = true,
                ReadinessHandshakeObserved = true,
                PathCategory = request.PathCategory,
                LaunchMechanism = request.LaunchMechanism,
                Containment = null,
                BrokerJobConfigured = true,
                BrokerJobAssigned = true,
                BrokerResumeSucceeded = true,
                SdkSpawnSucceeded = null,
                JobConfigured = null,
                JobAssigned = null,
                ResumeSucceeded = null,
                InnerJobAssigned = false,
                CleanupCompleted = job.IsClosed
                    && broker.IsClosed
                    && brokerThread.IsClosed
                    && child.IsClosed
                    && descendant.IsClosed
                    && stdoutHandle.IsClosed
                    && stderrHandle.IsClosed,
                ChildExitObserved = true,
                DescendantExitObserved = true,
                ProcessHandleClosed = broker.IsClosed,
                ThreadHandleClosed = brokerThread.IsClosed,
                JobHandleClosed = job.IsClosed,
                PseudoConsoleHandleClosed = true,
                InputHandleClosed = stderrHandle.IsClosed,
                OutputHandleClosed = stdoutHandle.IsClosed,
                AttributeListReleased = true,
            };
    }

    public static string? NormalizeUnsignedNtStatus(int? exitCode) =>
        exitCode is < 0 ? $"0x{unchecked((uint)exitCode.Value):X8}" : null;

    private static WindowsChildProcessProbeResult FailedBrokerResult(
        WindowsChildProcessProbeRequest request,
        int? error) => new()
    {
        BrokerCreateProcessSucceeded = false,
        BrokerCreateProcessWin32Error = error,
        CreationStatusAvailable = true,
        CreateProcessSucceeded = false,
        CreateProcessWin32Error = null,
        ExitCode = null,
        UnsignedNtStatus = null,
        StandardOutput = string.Empty,
        StandardError = string.Empty,
        TerminalOutput = string.Empty,
        ExecutableImageIdentity = null,
        TimedOut = false,
        ReadinessHandshakeObserved = false,
        PathCategory = request.PathCategory,
        LaunchMechanism = request.LaunchMechanism,
        Containment = request.Containment,
        BrokerJobConfigured = true,
        BrokerJobAssigned = false,
        BrokerResumeSucceeded = false,
        SdkSpawnSucceeded = null,
        JobConfigured = null,
        JobAssigned = null,
        ResumeSucceeded = null,
        InnerJobAssigned = null,
        CleanupCompleted = null,
        ChildExitObserved = true,
        DescendantExitObserved = true,
        ProcessHandleClosed = true,
        ThreadHandleClosed = true,
        JobHandleClosed = true,
        PseudoConsoleHandleClosed = true,
        InputHandleClosed = true,
        OutputHandleClosed = true,
        AttributeListReleased = true,
    };

    private static SafeFileHandle CreateOutputPipe(out SafeFileHandle write)
    {
        var attributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = 1,
        };
        if (!CreatePipe(out var read, out write, ref attributes, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create a probe output pipe.");
        if (!SetHandleInformation(read, HandleFlagInherit, 0))
        {
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to protect a probe pipe handle.");
        }
        return read;
    }

    private static SafeFileHandle CreateConfiguredJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the probe job.");

        var information = new JobObjectExtendedLimitInformation();
        information.BasicLimitInformation.LimitFlags =
            JobObjectLimitDieOnUnhandledException | JobObjectLimitKillOnJobClose;
        var size = Marshal.SizeOf(information);
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(job, 9, pointer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure the probe job.");
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

    private static async Task<string> ReadAllAsync(
        SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task WaitForProcessAsync(
        SafeProcessHandle process,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
        waitHandle.SafeWaitHandle = new SafeWaitHandle(
            process.DangerousGetHandle(),
            ownsHandle: false);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes attributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(
        SafeFileHandle handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    private const uint Synchronize = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int standardHandle);
}
