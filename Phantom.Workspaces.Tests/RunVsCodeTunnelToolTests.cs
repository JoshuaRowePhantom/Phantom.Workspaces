using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class RunVsCodeTunnelToolTests
{
    private sealed class FakeExecutionContextProvider : ICurrentExecutionContextProvider
    {
        public string ComputerName => "test-machine";
        public string UserName => "test-user";
        public string OperatingSystemName => "windows";
        public string HomeDirectoryPath => "C:/Users/test-user";
    }

    private sealed record CliCall(string CliPath, string Arguments, IReadOnlyDictionary<string, string>? EnvironmentVariables);

    private sealed record SpawnCall(string CliPath, string Arguments);

    private sealed class FakeChildProcess : IVsCodeTunnelChildProcess
    {
        private bool hasExited;
        private int exitCode;
        private string capturedStandardError = string.Empty;

        public bool WasKilled { get; private set; }
        public bool WasDisposed { get; private set; }

        public bool HasExited => this.hasExited;
        public int ExitCode => this.exitCode;
        public string CapturedStandardError => this.capturedStandardError;

        public void SimulateExit(int exitCode, string capturedStandardError)
        {
            this.exitCode = exitCode;
            this.capturedStandardError = capturedStandardError;
            this.hasExited = true;
        }

        public void Kill()
        {
            this.WasKilled = true;
            if (!this.hasExited)
            {
                this.hasExited = true;
                this.exitCode = -1;
            }
        }

        public void Dispose()
        {
            this.WasDisposed = true;
        }
    }

    private WorkspaceToolExecutionContext Context(
        string? cliPath = null,
        string? tunnelName = null)
    {
        var props = new List<string>
        {
            "\"entity-types\": [\"entity\", \"tool\"]",
            "\"tool-type\": \"run-vscode-tunnel\"",
        };

        if (cliPath is not null)
        {
            props.Add($"\"{RunVsCodeTunnelTool.CliPathProperty}\": {JsonSerializer.Serialize(cliPath)}");
        }

        if (tunnelName is not null)
        {
            props.Add($"\"{RunVsCodeTunnelTool.TunnelNameProperty}\": {JsonSerializer.Serialize(tunnelName)}");
        }

        var toolJson = "{" + string.Join(", ", props) + "}";
        return WorkspaceToolExecutionContextTestFactory.Create(
            new Phantom.Workspaces.Data.Offline.InMemoryDataAccessLayer(),
            toolJson);
    }

    /// <summary>
    /// A synchronous, manually-driven "wait between polls" seam. Each call to the delegate blocks
    /// until <see cref="ReleaseOnePoll"/> is invoked from the test — no timing dependencies.
    /// </summary>
    private sealed class ManualPollGate
    {
        private readonly ConcurrentQueue<TaskCompletionSource> pending = new();
        private readonly ConcurrentQueue<TaskCompletionSource> releases = new();
        private readonly object gate = new();

        public int PollCount { get; private set; }

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            lock (this.gate)
            {
                this.PollCount++;
                if (this.releases.TryDequeue(out var release))
                {
                    release.SetResult();
                    return Task.CompletedTask;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.pending.Enqueue(tcs);
                cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                return tcs.Task;
            }
        }

        public Task ReleaseOnePoll()
        {
            lock (this.gate)
            {
                if (this.pending.TryDequeue(out var tcs))
                {
                    tcs.SetResult();
                    return Task.CompletedTask;
                }

                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.releases.Enqueue(release);
                return release.Task;
            }
        }
    }

    // ---- Expected Tests -------------------------------------------------------------------

    [Fact]
    public async Task RunVsCodeTunnelTool_SpawnsCodeTunnelDirectly_NotServiceInstall()
    {
        var calls = new List<CliCall>();
        var spawns = new List<SpawnCall>();
        var child = new FakeChildProcess();
        child.SimulateExit(0, string.Empty); // exit immediately so ExecuteAsync returns

        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall(cli, args, env));
                return Task.FromResult(("running", 0));
            },
            defaultCliPathResolver: () => "code",
            tokenResolver: () => null,
            processLauncher: (cli, args) =>
            {
                spawns.Add(new SpawnCall(cli, args));
                return child;
            });

        await tool.ExecuteAsync(this.Context());

        Assert.Single(spawns);
        Assert.StartsWith("tunnel --accept-server-license-terms --name ", spawns[0].Arguments);
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("service install"));
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("service uninstall"));
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("service status"));
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ChildAliveAndStatusRunning_BlocksUntilConditionBreaks()
    {
        var gate = new ManualPollGate();
        var statusCount = 0;
        var child = new FakeChildProcess();

        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, ct) =>
            {
                if (args == "tunnel status")
                {
                    Interlocked.Increment(ref statusCount);
                    return Task.FromResult(("tunnel is running", 0));
                }
                return Task.FromResult(("", 0));
            },
            defaultCliPathResolver: () => "code",
            tokenResolver: () => null,
            processLauncher: (_, _) => child,
            waitBetweenPollsAsync: gate.WaitAsync);

        var runTask = tool.ExecuteAsync(this.Context());

        // First poll: status "running" then wait for tick.
        await gate.ReleaseOnePoll();
        // Second poll: status "running" then wait for tick.
        await gate.ReleaseOnePoll();

        Assert.False(runTask.IsCompleted);
        Assert.True(statusCount >= 2);

        // Break the conjunction so ExecuteAsync can return.
        child.SimulateExit(0, "child died");
        await gate.ReleaseOnePoll();

        await runTask;
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_ChildExits_ReturnsFailureWithCliOutput()
    {
        var child = new FakeChildProcess();
        child.SimulateExit(42, "child-stderr-marker");

        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) => Task.FromResult(("tunnel is running", 0)),
            defaultCliPathResolver: () => "code",
            tokenResolver: () => null,
            processLauncher: (_, _) => child);

        var result = await tool.ExecuteAsync(this.Context());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("42", result.ErrorMessage);
        Assert.Contains("child-stderr-marker", result.ErrorMessage);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_StatusStopsReportingRunning_ReturnsAndKillsChild()
    {
        var child = new FakeChildProcess();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) => Task.FromResult(("tunnel is stopped", 0)),
            defaultCliPathResolver: () => "code",
            tokenResolver: () => null,
            processLauncher: (_, _) => child);

        var result = await tool.ExecuteAsync(this.Context());

        Assert.True(result.IsSuccess);
        Assert.True(child.WasKilled);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_StatusNonZeroExit_TreatedAsNotRunning()
    {
        var child = new FakeChildProcess();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) => Task.FromResult(("running", 7)),  // exit 7, contains "running" in output but non-zero → not running
            defaultCliPathResolver: () => "code",
            tokenResolver: () => null,
            processLauncher: (_, _) => child);

        var result = await tool.ExecuteAsync(this.Context());

        Assert.True(result.IsSuccess);
        Assert.True(child.WasKilled);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_WithGitHubToken_InvokesLoginProviderGitHubWithTokenEnvVar_BeforeSpawn()
    {
        var events = new List<string>();
        var child = new FakeChildProcess();
        child.SimulateExit(0, string.Empty);
        CliCall? loginCall = null;

        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                if (args == "tunnel user login --provider github")
                {
                    loginCall = new CliCall(cli, args, env);
                    events.Add("login");
                }
                return Task.FromResult(("running", 0));
            },
            defaultCliPathResolver: () => "code",
            tokenResolver: () => "gh-token-xyz",
            processLauncher: (_, _) =>
            {
                events.Add("spawn");
                return child;
            });

        await tool.ExecuteAsync(this.Context());

        Assert.NotNull(loginCall);
        Assert.NotNull(loginCall!.EnvironmentVariables);
        Assert.True(loginCall.EnvironmentVariables!.ContainsKey("VSCODE_CLI_ACCESS_TOKEN"));
        Assert.Equal("gh-token-xyz", loginCall.EnvironmentVariables["VSCODE_CLI_ACCESS_TOKEN"]);
        Assert.DoesNotContain("gh-token-xyz", loginCall.Arguments);
        Assert.Equal(["login", "spawn"], events);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_WithoutGitHubToken_SkipsLoginAndStillSpawns()
    {
        var calls = new List<CliCall>();
        var child = new FakeChildProcess();
        child.SimulateExit(0, string.Empty);
        var spawned = false;

        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall(cli, args, env));
                return Task.FromResult(("running", 0));
            },
            defaultCliPathResolver: () => "code",
            tokenResolver: () => null,
            processLauncher: (_, _) => { spawned = true; return child; });

        var result = await tool.ExecuteAsync(this.Context());

        Assert.True(spawned);
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("user login"));
        // Result is failure because the fake child exited immediately, but that's not the point:
        // the assertion here is on login-skip + spawn-still-happened.
        _ = result;
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_LoginNonZeroExit_LogsWarningAndStillSpawns()
    {
        var testLogger = new TestLogger<RunVsCodeTunnelTool>();
        var child = new FakeChildProcess();
        child.SimulateExit(0, string.Empty);
        var spawned = false;

        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                if (args == "tunnel user login --provider github")
                    return Task.FromResult(("insufficient_scope", 1));
                return Task.FromResult(("running", 0));
            },
            defaultCliPathResolver: () => "code",
            tokenResolver: () => "some-token",
            processLauncher: (_, _) => { spawned = true; return child; },
            logger: testLogger);

        await tool.ExecuteAsync(this.Context());

        Assert.True(spawned);
        Assert.Contains(testLogger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_CancellationRequested_KillsChildAndReturns()
    {
        using var cts = new CancellationTokenSource();
        var gate = new ManualPollGate();
        var child = new FakeChildProcess();
        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) => Task.FromResult(("tunnel is running", 0)),
            defaultCliPathResolver: () => "code",
            tokenResolver: () => null,
            processLauncher: (_, _) => child,
            waitBetweenPollsAsync: gate.WaitAsync);

        var runTask = tool.ExecuteAsync(this.Context() with { CancellationToken = cts.Token });

        // Let the first poll happen and enter the wait.
        // Cancel before releasing the wait — this causes WaitAsync to throw OperationCanceledException.
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask);
        Assert.True(child.WasKilled);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_TunnelNameOverride_PassedToCodeTunnelSpawn()
    {
        SpawnCall? spawn = null;
        var child = new FakeChildProcess();
        child.SimulateExit(0, string.Empty);

        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) => Task.FromResult(("running", 0)),
            defaultCliPathResolver: () => "code",
            tokenResolver: () => null,
            processLauncher: (cli, args) => { spawn = new SpawnCall(cli, args); return child; });

        await tool.ExecuteAsync(this.Context(tunnelName: "my-custom-tunnel"));

        Assert.NotNull(spawn);
        Assert.Contains("--name my-custom-tunnel", spawn!.Arguments);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_TunnelNameDefault_UsesMachineName()
    {
        SpawnCall? spawn = null;
        var child = new FakeChildProcess();
        child.SimulateExit(0, string.Empty);

        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) => Task.FromResult(("running", 0)),
            defaultCliPathResolver: () => "code",
            tokenResolver: () => null,
            processLauncher: (cli, args) => { spawn = new SpawnCall(cli, args); return child; });

        await tool.ExecuteAsync(this.Context());

        Assert.NotNull(spawn);
        Assert.Contains("--name test-machine", spawn!.Arguments);
    }

    [Fact]
    public void RunVsCodeTunnelTool_DefaultScheduleFrequency_IsOneMinute()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), RunVsCodeTunnelTool.DefaultScheduleFrequency);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_CliNotFound_ReturnsFailure()
    {
        var loginCalled = false;
        var spawned = false;

        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                if (args.Contains("login")) loginCalled = true;
                return Task.FromResult(("", 0));
            },
            defaultCliPathResolver: () => null,
            tokenResolver: () => "token",
            processLauncher: (_, _) => { spawned = true; return new FakeChildProcess(); });

        var result = await tool.ExecuteAsync(this.Context());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(loginCalled);
        Assert.False(spawned);
    }

    [Fact]
    public async Task RunVsCodeTunnelTool_NeverInvokesTunnelServiceSubcommands()
    {
        var calls = new List<CliCall>();
        var child = new FakeChildProcess();
        child.SimulateExit(0, string.Empty);

        var tool = new RunVsCodeTunnelTool(
            new FakeExecutionContextProvider(),
            (cli, args, env, _) =>
            {
                calls.Add(new CliCall(cli, args, env));
                return Task.FromResult(("running", 0));
            },
            defaultCliPathResolver: () => "code",
            tokenResolver: () => "token",
            processLauncher: (_, _) => child);

        await tool.ExecuteAsync(this.Context());

        Assert.DoesNotContain(calls, c => c.Arguments.Contains("tunnel service install"));
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("tunnel service uninstall"));
        Assert.DoesNotContain(calls, c => c.Arguments.Contains("tunnel service status"));
    }
}
