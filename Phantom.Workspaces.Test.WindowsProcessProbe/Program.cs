using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

if (args.Length >= 2 && args[0] == "--child-ready-event")
{
    using var ready = EventWaitHandle.OpenExisting(args[1]);
    ready.Set();
    Console.WriteLine("PROBE_READY");
    return 0;
}

if (args.Length >= 3 && args[0] == "--child-wait-events")
{
    using var ready = EventWaitHandle.OpenExisting(args[1]);
    using var release = EventWaitHandle.OpenExisting(args[2]);
    ready.Set();
    Console.WriteLine("PROBE_READY");
    release.WaitOne();
    return 0;
}

var options = ParseOptions(args);
var scenario = Enum.Parse<WindowsProbeScenario>(options["scenario"]);
var pathCategory = Enum.Parse<WindowsProcessPathCategory>(options["path-category"]);
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
            await RunOrdinaryAsync(0, scenario, pathCategory, mechanism, containment),
        WindowsProbeScenario.FailingChild =>
            await RunOrdinaryAsync(3, scenario, pathCategory, mechanism, containment),
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
            await RunConPtyAsync(false, timeout, pathCategory, mechanism, containment),
        WindowsProbeScenario.ConPtyLaunchFailure =>
            await RunConPtyAsync(true, timeout, pathCategory, mechanism, containment),
        WindowsProbeScenario.OrdinaryProcessExecutor =>
            await RunProcessExecutorAsync(false, false, pathCategory, mechanism, containment),
        WindowsProbeScenario.MxcProcessExecutor =>
            await RunProcessExecutorAsync(true, false, pathCategory, mechanism, containment),
        WindowsProbeScenario.CommandShim =>
            await RunProcessExecutorAsync(false, true, pathCategory, mechanism, containment),
        WindowsProbeScenario.MxcCommandShim =>
            await RunProcessExecutorAsync(true, true, pathCategory, mechanism, containment),
        WindowsProbeScenario.ProcessRunnerNoJob =>
            await RunProcessRunnerAsync(false, pathCategory, mechanism, containment),
        WindowsProbeScenario.ProcessRunnerKillTree =>
            await RunProcessRunnerAsync(true, pathCategory, mechanism, containment),
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
        standardError: ex.GetType().Name,
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

static async Task<WindowsChildProcessProbeResult> RunTimeoutTreeAsync(
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment)
{
    var readyName = $"Local\\PhantomProbeReady-{Guid.NewGuid():N}";
    var releaseName = $"Local\\PhantomProbeRelease-{Guid.NewGuid():N}";
    using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
    using var releaseEvent = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
    var startInfo = new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    startInfo.ArgumentList.Add("--child-wait-events");
    startInfo.ArgumentList.Add(readyName);
    startInfo.ArgumentList.Add(releaseName);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Timeout child launch failed.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    var readyObserved = readyEvent.WaitOne(TimeSpan.FromSeconds(10));
    process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
    var output = await outputTask;
    var error = await errorTask;
    return Result(
        pathCategory,
        mechanism,
        containment,
        created: true,
        createError: null,
        exitCode: unchecked((int)0xC000013A),
        standardOutput: output,
        standardError: error,
        timedOut: true,
        ready: readyObserved);
}

[SupportedOSPlatform("windows")]
static async Task<WindowsChildProcessProbeResult> RunConPtyAsync(
    bool fail,
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
        Command = fail
            ? Path.Combine(AppContext.BaseDirectory, "fixed-missing-probe.exe")
            : Environment.ProcessPath!,
        CommandArguments = fail
            ? []
            : ["--child-ready-event", readyName],
        Columns = 80,
        Rows = 24,
    };

    try
    {
        await using var terminal = ConPtyPseudoTerminal.Start(new ConPtyStartOptions
        {
            Payload = payload,
            ShutdownTimeout = timeout,
            LaunchObserver = observer,
        });
        var readyObserved = ready.WaitOne(timeout);
        var exitCode = await terminal.WaitForExitAsync();
        return Result(
            pathCategory,
            mechanism,
            containment,
            true,
            null,
            exitCode,
            terminalOutput: string.Empty,
            ready: readyObserved,
            jobAssigned: observer.Succeeded(WindowsProcessLaunchStage.AssignJob),
            resumed: observer.Succeeded(WindowsProcessLaunchStage.ResumeThread));
    }
    catch (Win32Exception ex) when (fail)
    {
        return Result(
            pathCategory,
            mechanism,
            containment,
            false,
            ex.NativeErrorCode,
            null,
            cleanupCompleted: true,
            jobAssigned: observer.Succeeded(WindowsProcessLaunchStage.AssignJob),
            resumed: observer.Succeeded(WindowsProcessLaunchStage.ResumeThread));
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
            false,
            null,
            null,
            standardError: "MXC capability unavailable",
            cleanupCompleted: true);
    }

    var resolved = shim
        ? Phantom.Workspaces.Llm.Mcp.StdioCommandResolver.Resolve(
            Path.Combine(AppContext.BaseDirectory, "FixedProbe.cmd"),
            [])
        : new Phantom.Workspaces.Llm.Mcp.StdioCommandResolver.ResolvedCommand(
            Environment.ProcessPath!,
            ["--child-exit", "0"]);
    var request = new ProcessExecutionRequest(resolved.Executable, resolved.Arguments)
    {
        PathCategory = shim
            ? ProcessPathCategory.ResolvedCommandShim
            : ProcessPathCategory.FixedProbeBinary,
        Mxc = mxc
            ? new MxcProcessConfiguration(
                new SandboxPolicy { Version = SchemaVersions.LatestStable },
                new ProcessContainerContainment { LeastPrivilege = true })
            : null,
    };
    var executor = new ProcessExecutor();
    await using var handle = executor.Start(request);
    var stdoutTask = ReadStreamAsync(handle.StandardOutput);
    var stderrTask = ReadStreamAsync(handle.StandardError);
    var exit = await handle.WaitAsync();
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    return Result(
        pathCategory,
        mechanism,
        containment,
        handle.LaunchInfo.CreateProcessSucceeded ?? handle.LaunchInfo.ProcessId is not null,
        handle.LaunchInfo.CreateProcessWin32Error,
        exit.ExitCode,
        stdout,
        stderr,
        ready: stdout.Contains("PROBE_READY", StringComparison.Ordinal));
}

static async Task<WindowsChildProcessProbeResult> RunProcessRunnerAsync(
    bool killTree,
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment)
{
    var result = await ProcessRunner.RunProcessAsync(new RunProcessParameters(
        Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
        ["/d", "/c", "echo PROBE_READY"],
        killTree ? KillOnCloseAction.KillTree : KillOnCloseAction.None));
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
        innerJobAssigned: result.JobAssigned);
}

static WindowsChildProcessProbeResult Result(
    WindowsProcessPathCategory pathCategory,
    WindowsLaunchMechanism mechanism,
    WindowsContainmentDescriptor? containment,
    bool created,
    int? createError,
    int? exitCode,
    string standardOutput = "",
    string standardError = "",
    string terminalOutput = "",
    bool timedOut = false,
    bool ready = false,
    bool jobAssigned = true,
    bool resumed = true,
    bool innerJobAssigned = false,
    bool cleanupCompleted = true) => new()
{
    BrokerCreateProcessSucceeded = true,
    BrokerCreateProcessWin32Error = null,
    CreateProcessSucceeded = created,
    CreateProcessWin32Error = createError,
    ExitCode = exitCode,
    UnsignedNtStatus = WindowsChildProcessTestHarness.NormalizeUnsignedNtStatus(exitCode),
    StandardOutput = standardOutput,
    StandardError = standardError,
    TerminalOutput = terminalOutput,
    TimedOut = timedOut,
    ReadinessHandshakeObserved = ready,
    PathCategory = pathCategory,
    LaunchMechanism = mechanism,
    Containment = containment,
    JobConfigured = true,
    JobAssigned = jobAssigned,
    ResumeSucceeded = resumed,
    InnerJobAssigned = innerJobAssigned,
    CleanupCompleted = cleanupCompleted,
};

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
}

internal static partial class NativeMethods
{
    [DllImport("kernel32.dll")]
    internal static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    internal static extern uint GetErrorMode();
}
