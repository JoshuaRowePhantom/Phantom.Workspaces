using GitHub.Copilot;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotRuntimeConnectionFactoryTests
{
    [Fact]
    public async Task CreateConnection_UnconstrainedProfile_UsesDirectSdkRuntime()
    {
        using var files = new RuntimeFiles();
        var compiler = new RecordingCompiler(new(false, null, []));
        var factory = new CopilotRuntimeConnectionFactory(
            new CopilotLaunchPolicyStore(files.LaunchRoot, TimeProvider.System),
            files.BaseDirectory,
            "win-x64");
        var context = new AgentExecutionTrustContext(new TrustProfile(), compiler);

        await using var selection = await factory.CreateConnectionAsync(
            context,
            files.CopilotPath);

        Assert.Equal(Path.GetFullPath(files.CopilotPath), selection.ExecutablePath);
        Assert.Empty(selection.Arguments);
        Assert.Null(selection.PolicyFilePath);
        Assert.Equal(1, compiler.CallCount);
    }

    [Fact]
    public async Task CreateConnection_ConstrainedProfile_UsesWrapperWithPolicyAndCopilotArguments()
    {
        using var files = new RuntimeFiles();
        var compiler = new RecordingCompiler(new(true, CreatePolicy(), []));
        var factory = new CopilotRuntimeConnectionFactory(
            new CopilotLaunchPolicyStore(files.LaunchRoot, TimeProvider.System),
            files.BaseDirectory,
            "win-x64");
        var context = new AgentExecutionTrustContext(
            new TrustProfile
            {
                FilesystemPaths =
                [
                    new(files.BaseDirectory, null, TrustFilesystemAccessMode.ReadOnly),
                ],
            },
            compiler);

        await using var selection = await factory.CreateConnectionAsync(
            context,
            files.CopilotPath);

        Assert.Equal(Path.GetFullPath(files.WrapperPath), selection.ExecutablePath);
        Assert.Equal(
            ["--policy", selection.PolicyFilePath!, "--copilot", Path.GetFullPath(files.CopilotPath)],
            selection.Arguments);
        Assert.True(File.Exists(selection.PolicyFilePath));
    }

    [Fact]
    public async Task CreateConnection_CompileFailure_FailsClosed()
    {
        using var files = new RuntimeFiles();
        var compiler = new RecordingCompiler(new(
            true,
            null,
            [new("host.unsupported", TrustProfilePolicyDiagnosticSeverity.Error, "MXC unavailable")]));
        var factory = new CopilotRuntimeConnectionFactory(
            new CopilotLaunchPolicyStore(files.LaunchRoot, TimeProvider.System),
            files.BaseDirectory,
            "win-x64");
        var context = new AgentExecutionTrustContext(
            new TrustProfile { NetworkCapabilities = [] },
            compiler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await factory.CreateConnectionAsync(context, files.CopilotPath));

        Assert.DoesNotContain("MXC unavailable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateConnectionAsync_ConcurrentLifecyclePaths_ReturnsOneSelection()
    {
        var connectionFactory = new RecordingConnectionFactory();
        await using var client = new CopilotSdkChatClient(
            "gpt-5",
            "GitHub Copilot",
            gitHubToken: null,
            loggerFactory: null,
            executionTrustContext: new AgentExecutionTrustContext(
                new TrustProfile(),
                new RecordingCompiler(new(false, null, []))));
        client.SetRuntimeConnectionFactoryForTest(connectionFactory);

        var connectedTask = client.CreateClientOptionsForTestAsync(workingDirectory: null);
        await connectionFactory.Started.Task;
        var sessionTask = client.CreateClientOptionsForTestAsync(@"C:\workspace");
        connectionFactory.Release.TrySetResult();
        var connected = await connectedTask;
        var session = await sessionTask;

        Assert.Equal(1, connectionFactory.CallCount);
        Assert.Same(connected.Connection, session.Connection);
        Assert.Equal(@"C:\workspace", session.WorkingDirectory);
    }

    [Fact]
    public async Task CreateConnectionAsync_Cancelled_RemovesUnconsumedEnvelope()
    {
        using var files = new RuntimeFiles();
        using var cancellation = new CancellationTokenSource();
        var policyPath = Path.Combine(files.LaunchRoot, "cancelled", "policy.json");
        var policyStore = new CancellingPolicyStore(policyPath, cancellation);
        var factory = new CopilotRuntimeConnectionFactory(
            policyStore,
            files.BaseDirectory,
            "win-x64");
        var context = new AgentExecutionTrustContext(
            new TrustProfile { NetworkCapabilities = [] },
            new RecordingCompiler(new(true, CreatePolicy(), [])));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.CreateConnectionAsync(
                context,
                files.CopilotPath,
                cancellation.Token));

        Assert.False(File.Exists(policyPath));
        Assert.Equal(1, policyStore.CreateCount);
    }

    [Fact]
    public async Task DisposeAsync_UnconsumedHandoff_DeletesFileOnce()
    {
        using var files = new RuntimeFiles();
        var factory = new CopilotRuntimeConnectionFactory(
            new CopilotLaunchPolicyStore(files.LaunchRoot, TimeProvider.System),
            files.BaseDirectory,
            "win-x64");
        var context = new AgentExecutionTrustContext(
            new TrustProfile { NetworkCapabilities = [] },
            new RecordingCompiler(new(true, CreatePolicy(), [])));
        var lease = await factory.CreateConnectionAsync(context, files.CopilotPath);
        var policyPath = lease.PolicyFilePath!;

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.False(File.Exists(policyPath));
    }

    [Fact]
    public async Task DisposeAsync_TransferredConnection_DoesNotKillSdkRuntime()
    {
        using var files = new RuntimeFiles();
        var store = new CopilotLaunchPolicyStore(files.LaunchRoot, TimeProvider.System);
        var factory = new CopilotRuntimeConnectionFactory(store, files.BaseDirectory, "win-x64");
        var context = new AgentExecutionTrustContext(
            new TrustProfile { NetworkCapabilities = [] },
            new RecordingCompiler(new(true, CreatePolicy(), [])));
        var lease = await factory.CreateConnectionAsync(context, files.CopilotPath);
        var connection = lease.Connection;
        var policyPath = lease.PolicyFilePath!;

        _ = store.Consume(policyPath, Environment.ProcessId);
        await lease.DisposeAsync();

        Assert.Same(connection, lease.Connection);
        Assert.False(File.Exists(policyPath));
    }

    internal static MxcProcessPolicy CreatePolicy() =>
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

    private sealed class RecordingCompiler(TrustProfileProcessPolicyCompilation result)
        : ITrustProfileProcessPolicyCompiler
    {
        public int CallCount { get; private set; }

        public TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile)
        {
            CallCount++;
            return result;
        }
    }

    private sealed class RecordingConnectionFactory : ICopilotRuntimeConnectionFactory
    {
        public int CallCount { get; private set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CopilotRuntimeConnectionLease> CreateConnectionAsync(
            AgentExecutionTrustContext trustContext,
            string? cliPath,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new CopilotRuntimeConnectionLease(
                RuntimeConnection.ForStdio("wrapper.exe", ["--policy", "one"]),
                policyLease: null);
        }
    }

    private sealed class CancellingPolicyStore(
        string policyPath,
        CancellationTokenSource cancellation) : ICopilotLaunchPolicyStore
    {
        public int CreateCount { get; private set; }

        public CopilotLaunchPolicyLease Create(
            MxcProcessPolicy policy,
            int? parentProcessId = null)
        {
            CreateCount++;
            Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
            File.WriteAllText(policyPath, "policy");
            cancellation.Cancel();
            return new CopilotLaunchPolicyLease(policyPath);
        }
    }

    private sealed class RuntimeFiles : IDisposable
    {
        public RuntimeFiles()
        {
            BaseDirectory = Path.Combine(
                Environment.CurrentDirectory,
                "TestResults",
                $"copilot-runtime-{Guid.NewGuid():N}");
            var native = Path.Combine(BaseDirectory, "runtimes", "win-x64", "native");
            Directory.CreateDirectory(native);
            CopilotPath = Path.Combine(native, "copilot.exe");
            WrapperPath = Path.Combine(native, CopilotRuntimeConnectionFactory.WrapperFileName);
            LaunchRoot = Path.Combine(BaseDirectory, "launch");
            File.WriteAllText(CopilotPath, "cli");
            File.WriteAllText(WrapperPath, "wrapper");
        }

        public string BaseDirectory { get; }
        public string CopilotPath { get; }
        public string WrapperPath { get; }
        public string LaunchRoot { get; }

        public void Dispose() => Directory.Delete(BaseDirectory, recursive: true);
    }
}
