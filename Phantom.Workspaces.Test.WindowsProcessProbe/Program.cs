using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Mxc.Sdk;
using Phantom.Workspaces;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.Testing.Processes;

const uint SemFailCriticalErrors = 0x0001;
const uint SemNoGpFaultErrorBox = 0x0002;
NativeMethods.SetErrorMode(
    NativeMethods.GetErrorMode() | SemFailCriticalErrors | SemNoGpFaultErrorBox);

if (args.Length >= 2 && args[0] == "--child-exit")
{
    Console.WriteLine("PROBE_READY");
    Console.Error.WriteLine("probe-error");
    return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
}

if (args.Length == 1 && args[0] == "--child-fail-fast")
{
    Console.WriteLine("PROBE_READY");
    Console.Out.Flush();
    Environment.FailFast("Fixed modal-safety probe failure.");
}

if (args.Length >= 2 && args[0] == "--child-ready-event")
{
    using var ready = EventWaitHandle.OpenExisting(args[1]);
    ready.Set();
    Console.WriteLine("PROBE_READY");
    Console.Out.Flush();
    return 0;
}

if (args.Length >= 3 && args[0] == "--child-wait-events")
{
    using var ready = EventWaitHandle.OpenExisting(args[1]);
    using var release = EventWaitHandle.OpenExisting(args[2]);
    ready.Set();
    Console.WriteLine("PROBE_READY");
    Console.Out.Flush();
    release.WaitOne();
    return 0;
}

if (args.Length >= 3 && args[0] == "--timeout-leaf")
{
    using var ready = EventWaitHandle.OpenExisting(args[1]);
    using var release = EventWaitHandle.OpenExisting(args[2]);
    ready.Set();
    release.WaitOne();
    return 0;
}

if (args.Length >= 4 && args[0] == "--timeout-parent")
{
    using var ready = EventWaitHandle.OpenExisting(args[1]);
    using var descendantReady = EventWaitHandle.OpenExisting(args[2]);
    using var release = EventWaitHandle.OpenExisting(args[3]);
    using var descendant = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "--timeout-leaf", args[2], args[3] },
    }) ?? throw new InvalidOperationException("Timeout descendant launch failed.");
    descendantReady.WaitOne();
    Console.WriteLine($"TIMEOUT_DESCENDANT:{descendant.Id}");
    Console.Out.Flush();
    ready.Set();
    release.WaitOne();
    return 0;
}

if (args.Length >= 4 && args[0] == "--exiting-parent")
{
    using var descendantReady = EventWaitHandle.OpenExisting(args[1]);
    using var parentRelease = args[2] == "-"
        ? null
        : EventWaitHandle.OpenExisting(args[2]);
    using var descendant = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "--timeout-leaf", args[1], args[3] },
    }) ?? throw new InvalidOperationException("Retained-pipe descendant launch failed.");
    descendantReady.WaitOne();
    Console.WriteLine($"EXITING_PARENT_DESCENDANT:{descendant.Id}");
    Console.WriteLine("parent-stdout");
    Console.Error.WriteLine("parent-stderr");
    Console.Out.Flush();
    Console.Error.Flush();
    parentRelease?.WaitOne();
    return 23;
}

var options = ParseOptions(args);
var scenario = Enum.Parse<WindowsProbeScenario>(options["scenario"]);
var pathCategory = scenario switch
{
    WindowsProbeScenario.CommandShim or WindowsProbeScenario.MxcCommandShim =>
        WindowsProcessPathCategory.FixedCommandShim,
    WindowsProbeScenario.ConPtyLaunchFailure =>
        WindowsProcessPathCategory.FixedMissingBinary,
    WindowsProbeScenario.Direct
        or WindowsProbeScenario.ConPty
        or WindowsProbeScenario.OrdinaryProcessExecutor
        or WindowsProbeScenario.MxcProcessExecutor
        or WindowsProbeScenario.ProcessRunnerNoJob
        or WindowsProbeScenario.ProcessRunnerKillTree =>
        WindowsProcessPathCategory.SystemBinary,
    _ => WindowsProcessPathCategory.FixedProbeBinary,
};
var mechanism = Enum.Parse<WindowsLaunchMechanism>(options["mechanism"]);
var timeout = TimeSpan.FromMilliseconds(long.Parse(
    options["timeout-ms"],
    System.Globalization.CultureInfo.InvariantCulture));
WindowsContainmentDescriptor? containment = null;

WindowsChildProcessProbeResult result;
try
{
    result = scenario switch
    {
        WindowsProbeScenario.NormalChild or WindowsProbeScenario.Direct =>
            scenario == WindowsProbeScenario.Direct
                ? await RunDirectCreateProcessAsync(pathCategory, mechanism)
                : await RunOrdinaryAsync(0, scenario, pathCategory, mechanism, containment),
        WindowsProbeScenario.FailingChild =>
            await RunFailFastAsync(pathCategory, mechanism, containment),
        WindowsProbeScenario.StatusDllInitFailed =>
            await RunOrdinaryAsync(
                unchecked((int)0xC0000142),
                scenario,
                pathCategory,
                mechanism,
                containment),
        WindowsProbeScenario.TimeoutTree =>
            await RunTimeoutTreeAsync(pathCategory, mechanism, containment),
        WindowsProbeScenario.ConPty =>
            await RunConPtyAsync(false, null, timeout, pathCategory, mechanism, containment),
        WindowsProbeScenario.ConPtyLaunchFailure =>
            await RunConPtyAsync(true, null, timeout, pathCategory, mechanism, containment),
        WindowsProbeScenario.ConPtyConfigureFailure =>
            await RunConPtyAsync(
                false,
                WindowsProcessLaunchStage.ConfigureJob,
                timeout,
                pathCategory,
                mechanism,
                containment),
        WindowsProbeScenario.ConPtyAssignFailure =>
            await RunConPtyAsync(
                false,
                WindowsProcessLaunchStage.AssignJob,
                timeout,
                pathCategory,
                mechanism,
                containment),
        WindowsProbeScenario.ConPtyResumeFailure =>
            await RunConPtyAsync(
                false,
                WindowsProcessLaunchStage.ResumeThread,
                timeout,
                pathCategory,
                mechanism,
                containment),
        WindowsProbeScenario.OrdinaryProcessExecutor =>
            await RunProcessExecutorAsync(false, false, pathCategory, mechanism, containment),
        WindowsProbeScenario.MxcProcessExecutor =>
            await RunProcessExecutorAsync(true, false, pathCategory, mechanism, containment),
        WindowsProbeScenario.CommandShim =>
            await RunProcessExecutorAsync(false, true, pathCategory, mechanism, containment),
        WindowsProbeScenario.MxcCommandShim =>
            await RunProcessExecutorAsync(true, true, pathCategory, mechanism, containment),
        WindowsProbeScenario.ProcessRunnerNoJob =>
            await RunProcessRunnerAsync(scenario, pathCategory, mechanism, containment),
        WindowsProbeScenario.ProcessRunnerKillTree =>
            await RunProcessRunnerAsync(scenario, pathCategory, mechanism, containment),
        WindowsProbeScenario.ProcessRunnerExitedParentTree =>
            await RunProcessRunnerAsync(scenario, pathCategory, mechanism, containment),
        WindowsProbeScenario.ProcessRunnerTimeoutTree
            or WindowsProbeScenario.ProcessRunnerCancellationTree
            or WindowsProbeScenario.ProcessRunnerConfigureFailure
            or WindowsProbeScenario.ProcessRunnerAssignFailure
            or WindowsProbeScenario.ProcessRunnerResumeFailure =>
            await RunProcessRunnerAsync(scenario, pathCategory, mechanism, containment),
        _ => throw new InvalidOperationException("Unknown fixed probe scenario."),
    };
}
catch (Exception ex)
{
    result = Result(
        pathCategory,
        mechanism,
        containment,
        created: false,
        createError: ex is Win32Exception win32 ? win32.NativeErrorCode : null,
        exitCode: null,
        standardError: ex is InvalidDataException
            ? ex.Message
            : ex.GetType().Name,
        cleanupCompleted: true);
}

Console.Write(JsonSerializer.Serialize(result));
return 0;

static async Task<WindowsChildProcessProbeResult> RunOrdinaryAsync(
    int exitCode,
    WindowsProbeScenario scenario,
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment)
{
    var probe = Environment.ProcessPath
        ?? throw new InvalidOperationException("Probe executable path is unavailable.");
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = probe,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        ArgumentList = { "--child-exit", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) },
    }) ?? throw new InvalidOperationException("Fixed child launch failed.");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    return Result(
        pathCategory,
        mechanism,
        containment,
        created: true,
        createError: null,
        exitCode: process.ExitCode,
        standardOutput: scenario == WindowsProbeScenario.NormalChild
            ? stdout.Replace("PROBE_READY", "PROBE_READY" + Environment.NewLine + "probe-output")
            : stdout,
        standardError: stderr,
        ready: stdout.Contains("PROBE_READY", StringComparison.Ordinal));
}

static async Task<WindowsChildProcessProbeResult> RunFailFastAsync(
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment)
{
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        ArgumentList = { "--child-fail-fast" },
    }) ?? throw new InvalidOperationException("Fixed fail-fast child launch failed.");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    return Result(
        pathCategory,
        mechanism,
        containment,
        created: true,
        createError: null,
        exitCode: process.ExitCode,
        standardOutput: stdout,
        standardError: stderr,
        ready: stdout.Contains("PROBE_READY", StringComparison.Ordinal));
}

static async Task<WindowsChildProcessProbeResult> RunDirectCreateProcessAsync(
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism)
{
    var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
    using var output = new AnonymousPipeServerStream(
        PipeDirection.In,
        HandleInheritability.Inheritable);
    var commandLine = new StringBuilder(
        $"\"{commandInterpreter}\" /d /s /c \"echo PROBE_READY\"");
    var startup = new NativeMethods.StartupInfo
    {
        Cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
        Flags = 0x00000100,
        StdInput = NativeMethods.GetStdHandle(-10),
        StdOutput = output.ClientSafePipeHandle.DangerousGetHandle(),
        StdError = output.ClientSafePipeHandle.DangerousGetHandle(),
    };
    var created = NativeMethods.CreateProcess(
        commandInterpreter,
        commandLine,
        IntPtr.Zero,
        IntPtr.Zero,
        true,
        NativeMethods.CreateNoWindow | NativeMethods.CreateUnicodeEnvironment,
        IntPtr.Zero,
        null,
        ref startup,
        out var processInformation);
    if (!created)
    {
        return Result(
            pathCategory,
            mechanism,
            null,
            false,
            Marshal.GetLastWin32Error(),
            null,
            jobAssigned: null,
            resumed: null);
    }

    output.DisposeLocalCopyOfClientHandle();
    using var process = new Microsoft.Win32.SafeHandles.SafeProcessHandle(
        processInformation.Process,
        ownsHandle: true);
    using var thread = new Microsoft.Win32.SafeHandles.SafeWaitHandle(
        processInformation.Thread,
        ownsHandle: true);
    using var reader = new StreamReader(output, Encoding.UTF8);
    var standardOutput = await reader.ReadToEndAsync();
    NativeMethods.WaitForSingleObject(process, uint.MaxValue);
    if (!NativeMethods.GetExitCodeProcess(process, out var rawExitCode))
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Direct child exit status was unavailable.");
    return Result(
        pathCategory,
        mechanism,
        null,
        true,
        null,
        unchecked((int)rawExitCode),
        standardOutput: standardOutput,
        ready: standardOutput.Contains("PROBE_READY", StringComparison.Ordinal),
        jobAssigned: null,
        resumed: null);
}

static async Task<WindowsChildProcessProbeResult> RunTimeoutTreeAsync(
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment)
{
    var readyName = $"Local\\PhantomProbeReady-{Guid.NewGuid():N}";
    var descendantReadyName = $"Local\\PhantomProbeDescendantReady-{Guid.NewGuid():N}";
    var releaseName = $"Local\\PhantomProbeRelease-{Guid.NewGuid():N}";
    using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
    using var descendantReadyEvent = new EventWaitHandle(
        false,
        EventResetMode.ManualReset,
        descendantReadyName);
    using var releaseEvent = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
    var startInfo = new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    startInfo.ArgumentList.Add("--timeout-parent");
    startInfo.ArgumentList.Add(readyName);
    startInfo.ArgumentList.Add(descendantReadyName);
    startInfo.ArgumentList.Add(releaseName);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Timeout child launch failed.");
    readyEvent.WaitOne();
    var descendantLine = await process.StandardOutput.ReadLineAsync()
        ?? throw new InvalidDataException("Timeout descendant did not report readiness.");
    var descendantId = descendantLine["TIMEOUT_DESCENDANT:".Length..];
    Console.WriteLine($"TIMEOUT_READY:{process.Id}:{descendantId}");
    Console.Out.Flush();
    await process.WaitForExitAsync();
    throw new InvalidOperationException("The timeout process tree was unexpectedly released.");
}

[SupportedOSPlatform("windows")]
static async Task<WindowsChildProcessProbeResult> RunConPtyAsync(
    bool missingImage,
    WindowsProcessLaunchStage? injectedFailure,
    TimeSpan timeout,
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment)
{
    var observer = new RecordingObserver();
    var readyName = $"Local\\PhantomProbe-{Guid.NewGuid():N}";
    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
    var payload = new ShellOpenPayload
    {
        Command = missingImage
            ? Path.Combine(AppContext.BaseDirectory, "fixed-missing-probe.exe")
            : injectedFailure is null
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : Environment.ProcessPath!,
        CommandArguments = missingImage
            ? []
            : injectedFailure is null
                ? [
                    "/d",
                    "/c",
                    $"{Environment.ProcessPath} --child-ready-event {readyName}",
                ]
                : ["--child-exit", "0"],
        Columns = 80,
        Rows = 24,
    };

    ConPtyPseudoTerminal? terminal = null;
    try
    {
        terminal = ConPtyPseudoTerminal.Start(new ConPtyStartOptions
        {
            Payload = payload,
            ShutdownTimeout = timeout,
            LaunchObserver = observer,
            InjectFailureAt = injectedFailure,
        });
        var readyObserved = ready.WaitOne(timeout);
        using var drainTimeout = new CancellationTokenSource(timeout);
        var (exitCode, terminalOutput) =
            await terminal.WaitForExitAndDrainOutputAsync(drainTimeout.Token);
        await terminal.DisposeAsync();
        terminal = null;
        return Result(
            pathCategory,
            mechanism,
            containment,
            true,
            null,
            exitCode,
            terminalOutput: terminalOutput,
            ready: readyObserved,
            jobConfigured: observer.Succeeded(WindowsProcessLaunchStage.ConfigureJob),
            jobAssigned: observer.Succeeded(WindowsProcessLaunchStage.AssignJob),
            resumed: observer.Succeeded(WindowsProcessLaunchStage.ResumeThread),
            innerJobAssigned: observer.Succeeded(WindowsProcessLaunchStage.AssignJob),
            cleanupCompleted: observer.AllConPtyResourcesReleased);
    }
    catch (Win32Exception ex) when (missingImage || injectedFailure is not null)
    {
        var expected = new List<WindowsProcessResource>
        {
            WindowsProcessResource.InputPipe,
            WindowsProcessResource.OutputPipe,
            WindowsProcessResource.PseudoConsole,
            WindowsProcessResource.AttributeList,
        };
        if (injectedFailure is not null)
        {
            expected.Add(WindowsProcessResource.Process);
            expected.Add(WindowsProcessResource.Thread);
        }
        if (injectedFailure is not null)
        {
            expected.Add(WindowsProcessResource.Job);
        }

        return Result(
            pathCategory,
            mechanism,
            containment,
            observer.Succeeded(WindowsProcessLaunchStage.CreateProcess),
            ex.NativeErrorCode,
            null,
            cleanupCompleted: expected.All(observer.Released),
            jobConfigured: observer.Succeeded(WindowsProcessLaunchStage.ConfigureJob),
            jobAssigned: observer.Succeeded(WindowsProcessLaunchStage.AssignJob),
            resumed: observer.Succeeded(WindowsProcessLaunchStage.ResumeThread),
            innerJobAssigned: observer.Succeeded(WindowsProcessLaunchStage.AssignJob),
            processHandleClosed: !expected.Contains(WindowsProcessResource.Process)
                || observer.Released(WindowsProcessResource.Process),
            threadHandleClosed: !expected.Contains(WindowsProcessResource.Thread)
                || observer.Released(WindowsProcessResource.Thread),
            jobHandleClosed: !expected.Contains(WindowsProcessResource.Job)
                || observer.Released(WindowsProcessResource.Job),
            pseudoConsoleHandleClosed: observer.Released(WindowsProcessResource.PseudoConsole),
            inputHandleClosed: observer.Released(WindowsProcessResource.InputPipe),
            outputHandleClosed: observer.Released(WindowsProcessResource.OutputPipe),
            attributeListReleased: observer.Released(WindowsProcessResource.AttributeList),
            failureStage: observer.FailedStage?.ToString());
    }
    finally
    {
        if (terminal is not null)
            await terminal.DisposeAsync();
    }
}

static async Task<WindowsChildProcessProbeResult> RunProcessExecutorAsync(
    bool mxc,
    bool shim,
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment)
{
    if (mxc && Environment.GetEnvironmentVariable("MXC_E2E_HOST_PREPPED") != "1")
    {
        return Result(
            pathCategory,
            mechanism,
            containment,
            null,
            null,
            null,
            standardError: "MXC capability unavailable",
            jobConfigured: null,
            jobAssigned: null,
            resumed: null,
            cleanupCompleted: null,
            creationStatusAvailable: false);
    }

    var resolved = shim
        ? Phantom.Workspaces.Llm.Mcp.StdioCommandResolver.Resolve(
            Path.Combine(AppContext.BaseDirectory, "FixedProbe.cmd"),
            [])
        : new Phantom.Workspaces.Llm.Mcp.StdioCommandResolver.ResolvedCommand(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/d", "/s", "/c", "echo PROBE_READY"]);
    var request = new ProcessExecutionRequest(resolved.Executable, resolved.Arguments)
    {
        PathCategory = shim
            ? ProcessPathCategory.ResolvedCommandShim
            : ProcessPathCategory.SystemBinary,
        Mxc = mxc
            ? new MxcProcessConfiguration(
                new SandboxPolicy { Version = SchemaVersions.LatestStable },
                new ProcessContainerContainment { LeastPrivilege = true })
            : null,
    };
    var executor = new ProcessExecutor();
    var handle = executor.Start(request);
    var launch = handle.LaunchInfo;
    string stdout;
    string stderr;
    ProcessExitResult exit;
    try
    {
        var stdoutTask = ReadStreamAsync(handle.StandardOutput);
        var stderrTask = ReadStreamAsync(handle.StandardError);
        exit = await handle.WaitAsync();
        stdout = await stdoutTask;
        stderr = await stderrTask;
    }
    finally
    {
        await handle.DisposeAsync();
    }

    return Result(
        pathCategory,
        mechanism,
        containment,
        launch.CreateProcessSucceeded,
        launch.CreateProcessWin32Error,
        exit.ExitCode,
        stdout,
        stderr,
        ready: stdout.Contains("PROBE_READY", StringComparison.Ordinal),
        sdkSpawnSucceeded: launch.SdkSpawnSucceeded,
        jobConfigured: launch.JobConfigured,
        jobAssigned: launch.JobAssigned,
        resumed: launch.ResumeSucceeded,
        cleanupCompleted: true,
        creationStatusAvailable: launch.CreationStatusAvailable,
        observedContainment: launch.Containment is null
            ? null
            : new WindowsContainmentDescriptor
            {
                PolicyType = launch.Containment.PolicyType,
                PolicyIdentity = launch.Containment.PolicyIdentity,
            });
}

static async Task<WindowsChildProcessProbeResult> RunProcessRunnerAsync(
    WindowsProbeScenario scenario,
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment)
{
    if (scenario == WindowsProbeScenario.ProcessRunnerNoJob)
    {
        var ordinaryResult = await ProcessRunner.RunProcessAsync(new RunProcessParameters(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/d", "/s", "/c", "echo PROBE_READY"],
            KillOnCloseAction.None));
        return Result(
            pathCategory,
            mechanism,
            containment,
            true,
            null,
            ordinaryResult.ExitCode,
            ordinaryResult.StandardOut,
            ordinaryResult.StandardError,
            ready: ordinaryResult.StandardOut.Contains("PROBE_READY", StringComparison.Ordinal),
            jobConfigured: null,
            jobAssigned: null,
            resumed: null,
            innerJobAssigned: null);
    }

    var observer = new ProcessRunnerRecordingObserver();
    var injectedStage = scenario switch
    {
        WindowsProbeScenario.ProcessRunnerConfigureFailure =>
            ProcessRunnerWindowsStage.ConfigureJob,
        WindowsProbeScenario.ProcessRunnerAssignFailure =>
            ProcessRunnerWindowsStage.AssignJob,
        WindowsProbeScenario.ProcessRunnerResumeFailure =>
            ProcessRunnerWindowsStage.ResumeThread,
        _ => (ProcessRunnerWindowsStage?)null,
    };

    if (scenario is WindowsProbeScenario.ProcessRunnerTimeoutTree
        or WindowsProbeScenario.ProcessRunnerCancellationTree)
    {
        return await RunProcessRunnerTimeoutTreeAsync(
            observer,
            pathCategory,
            mechanism,
            containment,
            scenario == WindowsProbeScenario.ProcessRunnerCancellationTree);
    }
    if (scenario == WindowsProbeScenario.ProcessRunnerExitedParentTree)
    {
        return await RunProcessRunnerExitedParentTreeAsync(
            observer,
            pathCategory,
            mechanism,
            containment);
    }

    try
    {
        var executable = injectedStage is null
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : Environment.ProcessPath!;
        IReadOnlyList<string> arguments = injectedStage is null
            ? ["/d", "/s", "/c", "echo PROBE_READY"]
            : ["--child-exit", "0"];
        var result = await ProcessRunner.RunProcessAsync(
            new RunProcessParameters(
                executable,
                arguments,
                KillOnCloseAction.KillTree)
            {
                WindowsTestOptions = new ProcessRunnerWindowsTestOptions
                {
                    InjectFailureAt = injectedStage,
                    Observer = observer,
                },
            });
        return Result(
            pathCategory,
            mechanism,
            containment,
            true,
            null,
            result.ExitCode,
            result.StandardOut,
            result.StandardError,
            ready: result.StandardOut.Contains("PROBE_READY", StringComparison.Ordinal),
            jobConfigured: observer.Succeeded(ProcessRunnerWindowsStage.ConfigureJob),
            jobAssigned: observer.Succeeded(ProcessRunnerWindowsStage.AssignJob),
            resumed: observer.Succeeded(ProcessRunnerWindowsStage.ResumeThread),
            innerJobAssigned: result.JobAssigned);
    }
    catch (Win32Exception ex) when (injectedStage is not null)
    {
        return ProcessRunnerFailureResult(
            observer,
            pathCategory,
            mechanism,
            containment,
            ex.NativeErrorCode);
    }
}

static async Task<WindowsChildProcessProbeResult> RunProcessRunnerExitedParentTreeAsync(
    ProcessRunnerRecordingObserver observer,
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment)
{
    var descendantReadyName = $"Local\\PhantomRunnerDescendantReady-{Guid.NewGuid():N}";
    var parentReleaseName = $"Local\\PhantomRunnerParentRelease-{Guid.NewGuid():N}";
    var descendantReleaseName = $"Local\\PhantomRunnerDescendantRelease-{Guid.NewGuid():N}";
    using var descendantReady = new EventWaitHandle(
        false,
        EventResetMode.ManualReset,
        descendantReadyName);
    using var parentRelease = new EventWaitHandle(
        false,
        EventResetMode.ManualReset,
        parentReleaseName);
    using var descendantRelease = new EventWaitHandle(
        false,
        EventResetMode.ManualReset,
        descendantReleaseName);
    Process? descendant = null;
    var run = ProcessRunner.RunProcessAsync(
        new RunProcessParameters(
            Environment.ProcessPath!,
            [
                "--exiting-parent",
                descendantReadyName,
                parentReleaseName,
                descendantReleaseName,
            ],
            KillOnCloseAction.KillTree)
        {
            WindowsTestOptions = new ProcessRunnerWindowsTestOptions
            {
                Observer = observer,
                StandardOutputObserver = line =>
                {
                    if (!line.StartsWith("EXITING_PARENT_DESCENDANT:", StringComparison.Ordinal))
                        return;

                    var descendantId = int.Parse(
                        line["EXITING_PARENT_DESCENDANT:".Length..],
                        System.Globalization.CultureInfo.InvariantCulture);
                    descendant = Process.GetProcessById(descendantId);
                    parentRelease.Set();
                },
            },
        });

    var result = await run;
    using (descendant)
    {
        return Result(
            pathCategory,
            mechanism,
            containment,
            true,
            null,
            result.ExitCode,
            result.StandardOut,
            result.StandardError,
            ready: result.StandardOut.Contains("parent-stdout", StringComparison.Ordinal),
            jobConfigured: observer.Succeeded(ProcessRunnerWindowsStage.ConfigureJob),
            jobAssigned: observer.Succeeded(ProcessRunnerWindowsStage.AssignJob),
            resumed: observer.Succeeded(ProcessRunnerWindowsStage.ResumeThread),
            innerJobAssigned: result.JobAssigned,
            cleanupCompleted: observer.AllResourcesReleased,
            childExitObserved: true,
            descendantExitObserved: descendant?.HasExited == true,
            processHandleClosed: observer.Released(ProcessRunnerWindowsResource.Process),
            threadHandleClosed: observer.Released(ProcessRunnerWindowsResource.Thread),
            jobHandleClosed: observer.Released(ProcessRunnerWindowsResource.Job),
            inputHandleClosed: true,
            outputHandleClosed:
                observer.Released(ProcessRunnerWindowsResource.StandardOutputPipe)
                && observer.Released(ProcessRunnerWindowsResource.StandardErrorPipe));
    }
}

static async Task<WindowsChildProcessProbeResult> RunProcessRunnerTimeoutTreeAsync(
    ProcessRunnerRecordingObserver observer,
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment,
    bool cancel)
{
    var readyName = $"Local\\PhantomRunnerReady-{Guid.NewGuid():N}";
    var descendantReadyName = $"Local\\PhantomRunnerDescendantReady-{Guid.NewGuid():N}";
    var releaseName = $"Local\\PhantomRunnerRelease-{Guid.NewGuid():N}";
    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
    using var descendantReady = new EventWaitHandle(
        false,
        EventResetMode.ManualReset,
        descendantReadyName);
    using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
    var time = new ManualTimeoutTimeProvider();
    using var cancellation = new CancellationTokenSource();
    using var outputObserved = new ManualResetEventSlim(false);
    uint? descendantId = null;
    var run = ProcessRunner.RunProcessAsync(
        new RunProcessParameters(
            Environment.ProcessPath!,
            ["--timeout-parent", readyName, descendantReadyName, releaseName],
            KillOnCloseAction.KillTree,
            Timeout: TimeSpan.FromMinutes(1))
        {
            WindowsTestOptions = new ProcessRunnerWindowsTestOptions
            {
                Observer = observer,
                TimeProvider = time,
                StandardOutputObserver = line =>
                {
                    if (line.StartsWith("TIMEOUT_DESCENDANT:", StringComparison.Ordinal))
                    {
                        descendantId = uint.Parse(
                            line["TIMEOUT_DESCENDANT:".Length..],
                            System.Globalization.CultureInfo.InvariantCulture);
                        outputObserved.Set();
                    }
                },
            },
        },
        cancellation.Token);

    ready.WaitOne();
    outputObserved.Wait();
    var childId = observer.ProcessId
        ?? throw new InvalidDataException("ProcessRunner did not report the child process.");
    var observedDescendantId = descendantId
        ?? throw new InvalidDataException("ProcessRunner did not report the descendant process.");
    using var child = Process.GetProcessById(checked((int)childId));
    using var descendant = Process.GetProcessById(checked((int)observedDescendantId));
    if (cancel)
    {
        cancellation.Cancel();
        await AssertCancellationAsync(run, cancellation.Token);
    }
    else
    {
        time.Expire();
        await AssertTimeoutAsync(run);
    }
    child.WaitForExit();
    descendant.WaitForExit();

    return Result(
        pathCategory,
        mechanism,
        containment,
        true,
        null,
        unchecked((int)0xC000013A),
        timedOut: !cancel,
        ready: true,
        jobConfigured: observer.Succeeded(ProcessRunnerWindowsStage.ConfigureJob),
        jobAssigned: observer.Succeeded(ProcessRunnerWindowsStage.AssignJob),
        resumed: observer.Succeeded(ProcessRunnerWindowsStage.ResumeThread),
        innerJobAssigned: true,
        cleanupCompleted: observer.AllResourcesReleased,
        childExitObserved: child.HasExited,
        descendantExitObserved: descendant.HasExited,
        processHandleClosed: observer.Released(ProcessRunnerWindowsResource.Process),
        threadHandleClosed: observer.Released(ProcessRunnerWindowsResource.Thread),
        jobHandleClosed: observer.Released(ProcessRunnerWindowsResource.Job),
        inputHandleClosed: true,
        outputHandleClosed:
            observer.Released(ProcessRunnerWindowsResource.StandardOutputPipe)
            && observer.Released(ProcessRunnerWindowsResource.StandardErrorPipe),
        failureStage: cancel ? "Cancellation" : "Timeout");
}

static async Task AssertTimeoutAsync(Task<ProcessResult> run)
{
    try
    {
        await run;
        throw new InvalidDataException("ProcessRunner did not report its deterministic timeout.");
    }
    catch (TimeoutException)
    {
    }
}

static async Task AssertCancellationAsync(Task<ProcessResult> run, CancellationToken token)
{
    try
    {
        await run;
        throw new InvalidDataException("ProcessRunner did not report deterministic cancellation.");
    }
    catch (OperationCanceledException ex) when (ex.CancellationToken == token)
    {
    }
}

static WindowsChildProcessProbeResult ProcessRunnerFailureResult(
    ProcessRunnerRecordingObserver observer,
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment,
    int error) =>
    Result(
        pathCategory,
        mechanism,
        containment,
        observer.Succeeded(ProcessRunnerWindowsStage.CreateProcess),
        error,
        null,
        jobConfigured: observer.Succeeded(ProcessRunnerWindowsStage.ConfigureJob),
        jobAssigned: observer.Succeeded(ProcessRunnerWindowsStage.AssignJob),
        resumed: observer.Succeeded(ProcessRunnerWindowsStage.ResumeThread),
        innerJobAssigned: observer.Succeeded(ProcessRunnerWindowsStage.AssignJob),
        cleanupCompleted: observer.AllResourcesReleased,
        processHandleClosed: observer.Released(ProcessRunnerWindowsResource.Process),
        threadHandleClosed: observer.Released(ProcessRunnerWindowsResource.Thread),
        jobHandleClosed: observer.Released(ProcessRunnerWindowsResource.Job),
        outputHandleClosed:
            observer.Released(ProcessRunnerWindowsResource.StandardOutputPipe)
            && observer.Released(ProcessRunnerWindowsResource.StandardErrorPipe),
        failureStage: observer.FailedStage?.ToString());

static WindowsChildProcessProbeResult Result(
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment,
    bool? created,
    int? createError,
    int? exitCode,
    string standardOutput = "",
    string standardError = "",
    string terminalOutput = "",
    bool timedOut = false,
    bool ready = false,
    bool? sdkSpawnSucceeded = null,
    bool? jobConfigured = null,
    bool? jobAssigned = null,
    bool? resumed = null,
    bool? innerJobAssigned = null,
    bool? cleanupCompleted = true,
    bool creationStatusAvailable = true,
    WindowsContainmentDescriptor? observedContainment = null,
    bool childExitObserved = true,
    bool descendantExitObserved = true,
    bool processHandleClosed = true,
    bool threadHandleClosed = true,
    bool jobHandleClosed = true,
    bool pseudoConsoleHandleClosed = true,
    bool inputHandleClosed = true,
    bool outputHandleClosed = true,
    bool attributeListReleased = true,
    string? failureStage = null) => new()
{
    BrokerCreateProcessSucceeded = true,
    BrokerCreateProcessWin32Error = null,
    BrokerJobConfigured = false,
    BrokerJobAssigned = false,
    BrokerResumeSucceeded = false,
    CreationStatusAvailable = creationStatusAvailable,
    CreateProcessSucceeded = created,
    CreateProcessWin32Error = createError,
    ExitCode = exitCode,
    UnsignedNtStatus = WindowsChildProcessTestHarness.NormalizeUnsignedNtStatus(exitCode),
    StandardOutput = standardOutput,
    StandardError = standardError,
    TerminalOutput = terminalOutput,
    ExecutableImageIdentity = GetExecutableImageIdentity(pathCategory),
    TimedOut = timedOut,
    ReadinessHandshakeObserved = ready,
    PathCategory = pathCategory,
    LaunchMechanism = mechanism,
    Containment = observedContainment ?? containment,
    SdkSpawnSucceeded = sdkSpawnSucceeded,
    JobConfigured = jobConfigured,
    JobAssigned = jobAssigned,
    ResumeSucceeded = resumed,
    InnerJobAssigned = innerJobAssigned,
    CleanupCompleted = cleanupCompleted,
    ChildExitObserved = childExitObserved,
    DescendantExitObserved = descendantExitObserved,
    ProcessHandleClosed = processHandleClosed,
    ThreadHandleClosed = threadHandleClosed,
    JobHandleClosed = jobHandleClosed,
    PseudoConsoleHandleClosed = pseudoConsoleHandleClosed,
    InputHandleClosed = inputHandleClosed,
    OutputHandleClosed = outputHandleClosed,
    AttributeListReleased = attributeListReleased,
    FailureStage = failureStage,
};

static string? GetExecutableImageIdentity(WindowsProcessPathCategory pathCategory)
{
    var path = pathCategory switch
    {
        WindowsProcessPathCategory.FixedProbeBinary => Environment.ProcessPath,
        WindowsProcessPathCategory.FixedCommandShim =>
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
        WindowsProcessPathCategory.SystemBinary =>
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
        _ => null,
    };
    if (path is null || !File.Exists(path))
        return null;

    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static async Task<string> ReadStreamAsync(Stream stream)
{
    using var reader = new StreamReader(stream, Encoding.UTF8);
    return await reader.ReadToEndAsync();
}

static Dictionary<string, string> ParseOptions(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
        result.Add(values[index].TrimStart('-'), values[index + 1]);
    return result;
}

internal sealed class RecordingObserver : IWindowsProcessLaunchObserver
{
    private readonly List<WindowsProcessLaunchEvent> events = [];

    public void Observe(WindowsProcessLaunchEvent launchEvent) => events.Add(launchEvent);

    public bool Succeeded(WindowsProcessLaunchStage stage) =>
        events.Any(launchEvent => launchEvent.Stage == stage && launchEvent.Succeeded);

    public bool Released(WindowsProcessResource resource) =>
        events.Any(
            launchEvent => launchEvent.Stage == WindowsProcessLaunchStage.ReleaseResource
                && launchEvent.Resource == resource
                && launchEvent.Succeeded);

    public WindowsProcessLaunchStage? FailedStage =>
        events.LastOrDefault(launchEvent => !launchEvent.Succeeded)?.Stage;

    public bool AllConPtyResourcesReleased =>
        Enum.GetValues<WindowsProcessResource>().All(Released);
}

internal sealed class ProcessRunnerRecordingObserver : IProcessRunnerWindowsObserver
{
    private readonly object gate = new();
    private readonly List<ProcessRunnerWindowsEvent> events = [];

    public uint? ProcessId { get; private set; }

    public void Observe(ProcessRunnerWindowsEvent processEvent)
    {
        lock (gate)
        {
            events.Add(processEvent);
            ProcessId ??= processEvent.ProcessId;
        }
    }

    public bool Succeeded(ProcessRunnerWindowsStage stage)
    {
        lock (gate)
            return events.Any(processEvent => processEvent.Stage == stage && processEvent.Succeeded);
    }

    public bool Released(ProcessRunnerWindowsResource resource)
    {
        lock (gate)
        {
            return events.Any(
                processEvent => processEvent.Stage == ProcessRunnerWindowsStage.ReleaseResource
                    && processEvent.Resource == resource
                    && processEvent.Succeeded);
        }
    }

    public ProcessRunnerWindowsStage? FailedStage
    {
        get
        {
            lock (gate)
                return events.LastOrDefault(processEvent => !processEvent.Succeeded)?.Stage;
        }
    }

    public bool AllResourcesReleased =>
        Released(ProcessRunnerWindowsResource.StandardOutputPipe)
        && Released(ProcessRunnerWindowsResource.StandardErrorPipe)
        && Released(ProcessRunnerWindowsResource.Process)
        && Released(ProcessRunnerWindowsResource.Thread)
        && Released(ProcessRunnerWindowsResource.Job);
}

internal sealed class ManualTimeoutTimeProvider : TimeProvider
{
    private ManualTimer? timer;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        timer = new ManualTimer(callback, state);
        return timer;
    }

    public void Expire() =>
        (timer ?? throw new InvalidOperationException("The timeout timer was not armed.")).Fire();

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;

        public void Fire()
        {
            if (!disposed)
                callback(state);
        }

        public void Dispose() => disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal static partial class NativeMethods
{
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        internal int Cb;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal short ShowWindow;
        internal short Reserved2;
        internal IntPtr Reserved2Pointer;
        internal IntPtr StdInput;
        internal IntPtr StdOutput;
        internal IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
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

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(
        Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(
        Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        out uint exitCode);

    [DllImport("kernel32.dll")]
    internal static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    internal static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetStdHandle(int standardHandle);

}
