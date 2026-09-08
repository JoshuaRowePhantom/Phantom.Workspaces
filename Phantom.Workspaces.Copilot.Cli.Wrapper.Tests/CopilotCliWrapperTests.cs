using Phantom.Workspaces.Copilot.Cli.Wrapper;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Processes;
using System.Text;

namespace Phantom.Workspaces.Copilot.Cli.Wrapper.Tests;

public sealed class CopilotCliWrapperTests
{
    [Fact]
    public async Task Wrapper_CopilotPathEqualsWrapper_RejectsRecursion()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        using var stderr = new MemoryStream();

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.WrapperPath],
            Stream.Null,
            Stream.Null,
            stderr,
            new RecordingExecutor(),
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(CopilotCliWrapper.UnsafePathExitCode, exitCode);
        Assert.Contains("unsafe", Encoding.UTF8.GetString(stderr.ToArray()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrapper_StdioTraffic_RelaysBytesWithoutProtocolContamination()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        var executor = new RecordingExecutor
        {
            ChildOutput = [0, 1, 2, 255],
            ChildError = Encoding.UTF8.GetBytes("warning"),
            ExitCode = 23,
        };
        using var stdin = new MemoryStream([9, 8, 7]);
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath, "--stdio"],
            stdin,
            stdout,
            stderr,
            executor,
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(23, exitCode);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, stdout.ToArray());
        Assert.Equal("warning", Encoding.UTF8.GetString(stderr.ToArray()));
        Assert.Equal(new byte[] { 9, 8, 7 }, executor.Handle!.WrittenInput);
    }

    [Fact]
    public async Task Wrapper_SdkArguments_ForwardsAllRemainingArgumentsUnchanged()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        var executor = new RecordingExecutor();
        var forwarded = new[]
        {
            "--headless", "--no-auto-update", "--log-level", "debug", "--stdio",
            "--auth-token-env", "COPILOT_SDK_AUTH_TOKEN", "--no-auto-login",
            "--session-idle-timeout", "300", "--remote",
        };

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath, .. forwarded],
            Stream.Null,
            Stream.Null,
            Stream.Null,
            executor,
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(forwarded, executor.Request!.Arguments);
        Assert.Equal(Path.GetFullPath(files.CopilotPath), executor.Request.Executable);
        Assert.NotNull(executor.Request.MxcPolicy);
    }

    [Fact]
    public async Task Wrapper_MxcLaunchFails_DoesNotLaunchDirectCli()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        var executor = new ThrowingExecutor();
        using var stderr = new MemoryStream();

        var exitCode = await CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath],
            Stream.Null,
            Stream.Null,
            stderr,
            executor,
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);

        Assert.Equal(CopilotCliWrapper.ExecutorFailureExitCode, exitCode);
        Assert.Equal(1, executor.StartCount);
        Assert.Contains("containment", Encoding.UTF8.GetString(stderr.ToArray()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrapper_ParentTerminates_KillsContainedProcessTree()
    {
        using var files = new WrapperFiles();
        using var lease = files.Store.Create(CreatePolicy(), Environment.ProcessId);
        var executor = new BlockingExecutor();
        var run = CopilotCliWrapper.RunAsync(
            ["--policy", lease.Path, "--copilot", files.CopilotPath],
            Stream.Null,
            Stream.Null,
            Stream.Null,
            executor,
            files.LaunchRoot,
            Environment.ProcessId,
            files.WrapperPath);
        await executor.Started.Task;

        Assert.Equal(137, await run);
        Assert.True(executor.Handle.Killed);
    }

    private static MxcProcessPolicy CreatePolicy() =>
        new(
            MxcProcessPolicy.CurrentSchemaVersion,
            [],
            [],
            [],
            new Dictionary<string, string>(),
            new MxcProcessContainment(
                MxcContainmentBackend.ProcessContainer,
                LeastPrivilege: true,
                LearningMode: false,
                PermissiveMode: false));

    private sealed class RecordingExecutor : IProcessExecutor
    {
        public byte[] ChildOutput { get; init; } = [];
        public byte[] ChildError { get; init; } = [];
        public int ExitCode { get; init; }
        public ProcessExecutionRequest? Request { get; private set; }
        public RecordingHandle? Handle { get; private set; }

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            Request = request;
            Handle = new RecordingHandle(ChildOutput, ChildError, ExitCode);
            return Handle;
        }
    }

    private sealed class ThrowingExecutor : IProcessExecutor
    {
        public int StartCount { get; private set; }

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            StartCount++;
            throw new InvalidOperationException("MXC failed");
        }
    }

    private sealed class BlockingExecutor : IProcessExecutor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BlockingHandle Handle { get; } = new();

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            Started.TrySetResult();
            return Handle;
        }
    }

    private sealed class BlockingHandle : IProcessHandle
    {
        private readonly TaskCompletionSource<ProcessExitResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream StandardInput { get; } = new MemoryStream();
        public Stream StandardOutput { get; } = new MemoryStream();
        public Stream StandardError { get; } = new MemoryStream();
        public ProcessLaunchInfo LaunchInfo { get; } = CreateLaunchInfo();
        public bool Killed { get; private set; }

        public Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken = default) =>
            completion.Task.WaitAsync(cancellationToken);

        public void Kill()
        {
            Killed = true;
            completion.TrySetResult(CreateExitResult(137));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingHandle(byte[] output, byte[] error, int exitCode) : IProcessHandle
    {
        private readonly MemoryStream input = new();
        public Stream StandardInput => input;
        public Stream StandardOutput { get; } = new MemoryStream(output, writable: false);
        public Stream StandardError { get; } = new MemoryStream(error, writable: false);
        public ProcessLaunchInfo LaunchInfo { get; } = CreateLaunchInfo();
        public byte[] WrittenInput => input.ToArray();
        public Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateExitResult(exitCode));
        public void Kill() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ProcessLaunchInfo CreateLaunchInfo() => new()
    {
        ProcessId = 123,
        IsContained = true,
        Warnings = [],
        PathCategory = ProcessPathCategory.PackagedTool,
        LaunchMechanism = ProcessLaunchMechanism.MxcSpawn,
        CreationStatusAvailable = false,
        CreateProcessSucceeded = null,
        CreateProcessWin32Error = null,
        SdkSpawnSucceeded = true,
        JobConfigured = null,
        JobAssigned = null,
        ResumeSucceeded = null,
    };

    private static ProcessExitResult CreateExitResult(int exitCode) => new()
    {
        ExitCode = exitCode,
        UnsignedNtStatus = exitCode < 0 ? $"0x{unchecked((uint)exitCode):X8}" : null,
        TimedOut = false,
        OutputMetadata = null,
    };

    private sealed class WrapperFiles : IDisposable
    {
        public WrapperFiles()
        {
            BaseDirectory = Path.Combine(
                Environment.CurrentDirectory,
                "TestResults",
                $"copilot-wrapper-{Guid.NewGuid():N}");
            var native = Directory.CreateDirectory(Path.Combine(BaseDirectory, "runtime")).FullName;
            CopilotPath = Path.Combine(native, "copilot.exe");
            WrapperPath = Path.Combine(native, "phantom-copilot-wrapper.exe");
            LaunchRoot = Path.Combine(BaseDirectory, "launch");
            File.WriteAllText(CopilotPath, "cli");
            File.WriteAllText(WrapperPath, "wrapper");
            Store = new CopilotLaunchPolicyStore(LaunchRoot, TimeProvider.System);
        }

        public string BaseDirectory { get; }
        public string CopilotPath { get; }
        public string WrapperPath { get; }
        public string LaunchRoot { get; }
        public CopilotLaunchPolicyStore Store { get; }

        public void Dispose() => Directory.Delete(BaseDirectory, recursive: true);
    }
}
