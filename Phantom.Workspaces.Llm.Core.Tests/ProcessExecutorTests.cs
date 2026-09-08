using Microsoft.Mxc.Sdk;
using Phantom.Workspaces.Llm.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ProcessExecutorTests
{
    [Fact]
    public void ProcessExecutor_UncontainedRequest_UsesSystemProcess()
    {
        var systemFactory = new RecordingSystemProcessFactory();
        var sandboxRunner = new FakeSandboxRunner();
        var executor = new ProcessExecutor(systemFactory, sandboxRunner);

        _ = executor.Start(new ProcessExecutionRequest("tool", ["one", "two"]));

        Assert.NotNull(systemFactory.Request);
        Assert.Equal(0, sandboxRunner.SpawnCount);
    }

    [Fact]
    public void ProcessExecutor_MxcPolicy_UsesMxcSpawn()
    {
        var systemFactory = new RecordingSystemProcessFactory();
        var sandboxRunner = new FakeSandboxRunner();
        var executor = new ProcessExecutor(systemFactory, sandboxRunner);
        var configuration = new MxcProcessConfiguration(
            new SandboxPolicy { Version = "0.8.0-alpha" },
            new ProcessContainerContainment { LeastPrivilege = true });

        _ = executor.Start(new ProcessExecutionRequest("tool", ["argument"])
        {
            WorkingDirectory = @"C:\work",
            Environment = new Dictionary<string, string> { ["NAME"] = "value" },
            Timeout = TimeSpan.FromSeconds(2),
            Mxc = configuration,
        });

        Assert.Null(systemFactory.Request);
        var request = Assert.IsType<SandboxRequest>(sandboxRunner.LastRequest);
        Assert.Equal("tool argument", request.Command);
        Assert.Equal(@"C:\work", request.WorkingDirectory);
        Assert.Equal("value", request.Environment["NAME"]);
        Assert.Equal((uint)2000, request.Policy.TimeoutMs);
        Assert.Same(configuration.Containment, request.Containment);
    }

    [Fact]
    public void ProcessExecutor_MxcLaunchFails_DoesNotFallbackUnsandboxed()
    {
        var systemFactory = new RecordingSystemProcessFactory();
        var expected = new MxcException(ErrorCode.BackendError, "spawn failed");
        var sandboxRunner = new FakeSandboxRunner { SpawnException = expected };
        var executor = new ProcessExecutor(systemFactory, sandboxRunner);

        var actual = Assert.Throws<MxcException>(() => executor.Start(
            new ProcessExecutionRequest("tool")
            {
                Mxc = new MxcProcessConfiguration(
                    new SandboxPolicy { Version = "0.8.0-alpha" }),
            }));

        Assert.Same(expected, actual);
        Assert.Null(systemFactory.Request);
    }

    [Fact]
    public void ProcessExecutor_MxcWarning_PreservesDiagnostic()
    {
        var sandboxProcess = new FakeSandboxProcess
        {
            WarningsValue = ["DACL mutation was required."],
            OutputMetadataValue = new SandboxOutputMetadata(),
        };
        var sandboxRunner = new FakeSandboxRunner { Process = sandboxProcess };
        var executor = new ProcessExecutor(new RecordingSystemProcessFactory(), sandboxRunner);

        var handle = executor.Start(new ProcessExecutionRequest("tool")
        {
            Mxc = new MxcProcessConfiguration(
                new SandboxPolicy { Version = "0.8.0-alpha" }),
        });

        Assert.Equal(["DACL mutation was required."], handle.LaunchInfo.Warnings);
    }

    [Fact]
    public void ProcessExecutor_TimeoutZero_Throws()
    {
        var executor = new ProcessExecutor(new RecordingSystemProcessFactory(), new FakeSandboxRunner());

        Assert.Throws<ArgumentOutOfRangeException>(() => executor.Start(
            new ProcessExecutionRequest("tool") { Timeout = TimeSpan.Zero }));
    }

    [Fact]
    public void ProcessExecutor_TimeoutNegative_Throws()
    {
        var executor = new ProcessExecutor(new RecordingSystemProcessFactory(), new FakeSandboxRunner());

        Assert.Throws<ArgumentOutOfRangeException>(() => executor.Start(
            new ProcessExecutionRequest("tool") { Timeout = TimeSpan.FromMilliseconds(-1) }));
    }

    [Fact]
    public void ProcessExecutor_TimeoutExceedsUInt_Throws()
    {
        var executor = new ProcessExecutor(new RecordingSystemProcessFactory(), new FakeSandboxRunner());

        Assert.Throws<ArgumentOutOfRangeException>(() => executor.Start(
            new ProcessExecutionRequest("tool")
            {
                Timeout = TimeSpan.FromMilliseconds((double)uint.MaxValue + 1),
            }));
    }

    [Fact]
    public void ProcessExecutor_NullRequest_Throws()
    {
        var executor = new ProcessExecutor(new RecordingSystemProcessFactory(), new FakeSandboxRunner());
        Assert.Throws<ArgumentNullException>(() => executor.Start(null!));
    }

    [Fact]
    public void ProcessExecutor_EmptyExecutable_Throws()
    {
        var executor = new ProcessExecutor(new RecordingSystemProcessFactory(), new FakeSandboxRunner());
        Assert.Throws<ArgumentException>(() => executor.Start(new ProcessExecutionRequest("")));
    }
}

internal sealed class RecordingSystemProcessFactory : ISystemProcessFactory
{
    public ProcessExecutionRequest? Request { get; private set; }
    public IProcessBackend Process { get; set; } = new FakeProcessBackend();

    public IProcessBackend Start(ProcessExecutionRequest request)
    {
        Request = request;
        return Process;
    }
}

internal sealed class FakeSandboxRunner : ISandboxRunner
{
    public int SpawnCount { get; private set; }
    public SandboxRequest? LastRequest { get; private set; }
    public Exception? SpawnException { get; init; }
    public ISandboxProcess Process { get; init; } = new FakeSandboxProcess();

    public string NativeVersion => "test";
    public IReadOnlyList<AvailableBackend> GetAvailableBackends() => [];
    public PlatformSupport GetPlatformSupport() => new();
    public RunResult Run(SandboxPolicy policy, string command) => throw new NotSupportedException();
    public RunResult Run(SandboxRequest request) => throw new NotSupportedException();
    public Task<RunResult> RunAsync(SandboxPolicy policy, string command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<RunResult> RunAsync(SandboxRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public ISandboxProcess Spawn(SandboxPolicy policy, string command) => throw new NotSupportedException();

    public ISandboxProcess Spawn(SandboxRequest request)
    {
        SpawnCount++;
        LastRequest = request;
        if (SpawnException is not null)
            throw SpawnException;
        return Process;
    }
}

internal sealed class FakeSandboxProcess : ISandboxProcess
{
    public uint Id => 42;
    public Stream? StandardInput { get; init; } = new MemoryStream();
    public Stream? StandardOutput { get; init; } = new MemoryStream();
    public Stream? StandardError { get; init; } = new MemoryStream();
    public ISandboxStreamCloser? StandardOutputCloser => null;
    public ISandboxStreamCloser? StandardErrorCloser => null;
    public IReadOnlyList<string> Warnings => WarningsValue;
    public IReadOnlyList<string> WarningsValue { get; init; } = [];
    public SandboxOutputMetadata? OutputMetadata => OutputMetadataValue;
    public SandboxOutputMetadata? OutputMetadataValue { get; init; }
    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }
    public SandboxWaitResult Wait() => new() { ExitCode = 0 };
    public Task<SandboxWaitResult> WaitAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SandboxWaitResult { ExitCode = 0 });
    public bool TryGetExitCode(out int exitCode) { exitCode = 0; return true; }
    public Task<(SandboxWaitResult Result, byte[] Stdout, byte[] Stderr)> WaitForExitWithOutputAsync(
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Kill() => Killed = true;
    public void Dispose() => Disposed = true;
}
