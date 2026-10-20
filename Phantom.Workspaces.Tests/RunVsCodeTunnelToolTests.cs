using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Moq;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class RunVsCodeTunnelToolTests
{
    [Theory]
    [InlineData("github", "github")]
    [InlineData("microsoft", "microsoft")]
    public async Task AuthenticationRequiredBeforePerform_DoesNotLaunchLoginOrHost(
        string provider,
        string expectedArgument)
    {
        await using var harness = new Harness { HasStoredCredential = false };
        harness.Children.Enqueue(Child.Completed(connected: true));
        var execution = harness.Tool.ExecuteAsync(Context(provider));
        var row = await harness.WaitForPendingRowAsync();
        Assert.Empty(harness.Logins);
        Assert.Empty(harness.Hosts);
        await VsCodeTunnelAuthorizationTests.PerformAsync(harness.Registry, row);
        var result = await execution;
        Assert.True(result.IsSuccess);
        Assert.Equal(expectedArgument, Assert.Single(harness.Logins).ArgumentList.Last());
        Assert.Single(harness.Hosts);
        Assert.All(harness.ChildrenStarted, child => Assert.True(child.Disposed && child.Drained));
        harness.Policy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UserShowRecognizedCredential_IsPresenceGuardOnly()
    {
        await using var harness = new Harness { PresenceProvider = "microsoft" };
        harness.Children.Enqueue(Child.Completed(tokenError: true));
        var execution = harness.Tool.ExecuteAsync(Context("github"));
        var row = await harness.WaitForPendingRowAsync();
        Assert.Contains("GitHub", row.SourceOptions.Single().SafeDisplayName);
        Assert.Empty(harness.Logins);
        await DenyAsync(harness.Registry, row);
        var result = await execution;
        Assert.False(result.IsSuccess);
        Assert.Contains("AuthenticationRequired", result.ErrorMessage);
        Assert.Single(harness.Hosts);
    }

    [Fact]
    public async Task AuthenticationSucceeded_RestartsExactlyOnce()
    {
        await using var harness = new Harness();
        harness.Children.Enqueue(Child.Completed(tokenError: true));
        harness.Children.Enqueue(Child.Completed(connected: true));
        var execution = harness.Tool.ExecuteAsync(Context());
        await VsCodeTunnelAuthorizationTests.PerformAsync(harness.Registry, await harness.WaitForPendingRowAsync());
        var result = await execution;
        Assert.True(result.IsSuccess);
        Assert.Equal(2, harness.Hosts.Count);
        Assert.Single(harness.Logins);
        Assert.All(harness.Hosts, arguments =>
            Assert.Equal(new[] { "tunnel", "--accept-server-license-terms", "--name", "test-host" }, arguments));
    }

    [Fact]
    public async Task AuthenticationRecurs_DoesNotAutoLoginLoop()
    {
        await using var harness = new Harness();
        harness.Children.Enqueue(Child.Completed(tokenError: true));
        harness.Children.Enqueue(Child.Completed(unauthorized: true));
        var execution = harness.Tool.ExecuteAsync(Context());
        await VsCodeTunnelAuthorizationTests.PerformAsync(harness.Registry, await harness.WaitForPendingRowAsync());
        var renewal = await harness.WaitForPendingRowAsync();
        Assert.Equal(2, harness.Hosts.Count);
        Assert.Single(harness.Logins);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, renewal.PrimaryAction);
        await DenyAsync(harness.Registry, renewal);
        Assert.False((await execution).IsSuccess);
        Assert.Equal(2, harness.Hosts.Count);
        Assert.Single(harness.Logins);
    }

    [Fact]
    public async Task ProcessExitBeforeFinalDiagnostic_WaitsForFullDrainBeforeAuthentication()
    {
        await using var harness = new Harness();
        var child = new Child { HasExited = true };
        harness.Children.Enqueue(child);
        var execution = harness.Tool.ExecuteAsync(Context());
        await child.WaitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(harness.Registry.GetSnapshot());
        Assert.False(execution.IsCompleted);
        child.Completion.SetResult(Completion(unauthorized: true));
        var row = await harness.WaitForPendingRowAsync();
        Assert.True(child.Disposed);
        Assert.True(child.Drained);
        await DenyAsync(harness.Registry, row);
        Assert.Contains("AuthenticationRequired", (await execution).ErrorMessage);
    }

    [Theory]
    [InlineData("401 Unauthorized")]
    [InlineData("HTTP 401\nUnauthorized")]
    [InlineData("last_fail_reason: 401 Unauthorized")]
    [InlineData("{\"last_fail_reason\":\"HTTP 401 Unauthorized\"}")]
    [InlineData("failed with exit code 401")]
    public async Task WeakEvidence_NeverRequestsAuthentication(string record)
    {
        await using var harness = new Harness();
        var reducer = new ProcessBackedVsCodeTunnelChildProcess.EvidenceReducer();
        await ProcessBackedVsCodeTunnelChildProcess.DrainAsync(new System.IO.StringReader(record), reducer.AcceptRecord);
        harness.Children.Enqueue(Child.Completed(reducer.Complete(1)));
        var result = await harness.Tool.ExecuteAsync(Context());
        Assert.False(result.IsSuccess);
        Assert.Contains("NotRunning", result.ErrorMessage);
        Assert.Empty(harness.Registry.GetSnapshot());
        Assert.Empty(harness.Logins);
    }

    [Theory]
    [InlineData(true, true, true, true, VsCodeTunnelOutcome.NotRunning, VsCodeTunnelOutcome.Cancelled)]
    [InlineData(false, true, true, true, VsCodeTunnelOutcome.NotRunning, VsCodeTunnelOutcome.Ready)]
    [InlineData(false, false, true, true, VsCodeTunnelOutcome.Ready, VsCodeTunnelOutcome.AuthenticationRequired)]
    [InlineData(false, false, false, true, VsCodeTunnelOutcome.Ready, VsCodeTunnelOutcome.AuthenticationRequired)]
    [InlineData(false, false, false, false, VsCodeTunnelOutcome.NotRunning, VsCodeTunnelOutcome.NotRunning)]
    [InlineData(false, false, false, false, VsCodeTunnelOutcome.Ready, VsCodeTunnelOutcome.Ready)]
    [InlineData(false, false, false, false, VsCodeTunnelOutcome.RunningDisconnected, VsCodeTunnelOutcome.RunningDisconnected)]
    [InlineData(false, false, false, false, VsCodeTunnelOutcome.InvalidStatus, VsCodeTunnelOutcome.InvalidStatus)]
    [InlineData(false, false, false, false, VsCodeTunnelOutcome.CommandFailed, VsCodeTunnelOutcome.CommandFailed)]
    public void EvidenceClassification_UsesRequiredPrecedence(
        bool cancelled,
        bool connected,
        bool tokenError,
        bool unauthorized,
        VsCodeTunnelOutcome status,
        VsCodeTunnelOutcome expected)
    {
        var outcome = RunVsCodeTunnelTool.Classify(
            cancelled,
            new VsCodeTunnelResolution { Outcome = status },
            new VsCodeTunnelProcessEvidence { ConnectedSeen = connected, TokenErrorSeen = tokenError },
            Completion(connected, tokenError, unauthorized));
        Assert.Equal(expected, outcome);
    }

    [Fact]
    public async Task Cancellation_KillsDrainsThenDisposesHost()
    {
        using var cancellation = new CancellationTokenSource();
        await using var harness = new Harness { Status = VsCodeTunnelOutcome.Ready };
        var child = new Child();
        harness.Children.Enqueue(child);
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = harness.CreateTool(async token =>
        {
            polling.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        var context = Context() with { CancellationToken = cancellation.Token };
        var execution = tool.ExecuteAsync(context);
        await polling.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await execution;
        Assert.Contains("Cancelled", result.ErrorMessage);
        Assert.True(child.Killed);
        Assert.True(child.Drained);
        Assert.True(child.Disposed);
    }

    [Fact]
    public void HostStart_EnablesMachineEvidenceWithoutCredentialInjection()
    {
        var start = RunVsCodeTunnelTool.BuildHostStartInfo(
            "code.exe",
            ["tunnel", "--accept-server-license-terms", "--name", "host with spaces"]);
        Assert.Equal("1", start.Environment["VSCODE_CLI_MACHINE_STATUS"]);
        Assert.False(start.Environment.ContainsKey("VSCODE_CLI_ACCESS_TOKEN"));
        Assert.Equal("host with spaces", start.ArgumentList.Last());
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
    }

    [Fact]
    public async Task CommandFailure_NeverEchoesRawOutputOrException()
    {
        var broker = new Mock<IAccessBroker>(MockBehavior.Strict);
        var invoker = new VsCodeCliInvoker(processRunner: (_, _) => throw new InvalidOperationException("private-device-url"));
        var tool = new RunVsCodeTunnelTool(
            broker.Object,
            defaultCliPathResolver: () => "code",
            cliInvoker: invoker);
        var result = await tool.ExecuteAsync(Context());
        Assert.Equal("VS Code tunnel: CommandFailed.", result.ErrorMessage);
        Assert.DoesNotContain("private", result.ToString());
        broker.VerifyNoOtherCalls();
    }

    private static WorkspaceToolExecutionContext Context(string provider = "github") =>
        WorkspaceToolExecutionContextTestFactory.Create(
            new InMemoryDataAccessLayer(),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["entity-types"] = new[] { "entity", "tool" },
                ["tool-type"] = "run-vscode-tunnel",
                ["provider"] = provider,
                ["cli-path"] = "code.exe",
                ["tunnel-name"] = "test-host",
            }));

    private static Task DenyAsync(
        UserAuthorizationRegistry registry,
        UserAuthorizationSnapshot row) => registry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);

    private static VsCodeTunnelProcessCompletion Completion(
        bool connected = false,
        bool tokenError = false,
        bool unauthorized = false) => new()
        {
            ExitCode = 0,
            ConnectedSeen = connected,
            TokenErrorSeen = tokenError,
            UnauthorizedHttp401Seen = unauthorized,
        };

    private sealed class Child : IVsCodeTunnelChildProcess
    {
        public bool HasExited { get; set; }
        public bool Killed { get; private set; }
        public bool Drained { get; private set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<VsCodeTunnelProcessCompletion> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public VsCodeTunnelProcessEvidence Evidence { get; } = new() { ConnectedSeen = false, TokenErrorSeen = false };

        internal static Child Completed(
            bool connected = false,
            bool tokenError = false,
            bool unauthorized = false) => Completed(RunVsCodeTunnelToolTests.Completion(connected, tokenError, unauthorized));

        internal static Child Completed(VsCodeTunnelProcessCompletion completion)
        {
            var child = new Child { HasExited = true };
            child.Completion.SetResult(completion);
            return child;
        }

        public async Task<VsCodeTunnelProcessCompletion> WaitForExitAndDrainAsync(CancellationToken cancellationToken)
        {
            this.WaitStarted.TrySetResult();
            var result = await this.Completion.Task.WaitAsync(cancellationToken);
            this.Drained = true;
            return result;
        }

        public void Kill()
        {
            this.Killed = true;
            if (!this.HasExited)
            {
                this.HasExited = true;
                this.Completion.TrySetResult(RunVsCodeTunnelToolTests.Completion());
            }
        }

        public void Dispose()
        {
            Assert.True(this.Drained);
            this.Disposed = true;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Channel<bool> changed = Channel.CreateUnbounded<bool>();
        private readonly VsCodeCliInvoker invoker;
        private readonly FakeStatusResolver resolver;
        internal Mock<IReleaseAuthorizationPolicy> Policy { get; } = new(MockBehavior.Strict);
        internal UserAuthorizationRegistry Registry { get; } = new();
        internal AccessBroker Broker { get; }
        internal RunVsCodeTunnelTool Tool { get; }
        internal Queue<Child> Children { get; } = new();
        internal List<Child> ChildrenStarted { get; } = [];
        internal List<IReadOnlyList<string>> Hosts { get; } = [];
        internal List<ProcessStartInfo> Logins { get; } = [];
        internal bool HasStoredCredential { get; set; } = true;
        internal string PresenceProvider { get; set; } = "github";
        internal VsCodeTunnelOutcome Status { get; set; } = VsCodeTunnelOutcome.NotRunning;

        public Harness()
        {
            this.Registry.Changed += (_, _) => this.changed.Writer.TryWrite(true);
            this.resolver = new FakeStatusResolver(() => this.Status);
            this.invoker = new VsCodeCliInvoker(processRunner: (parameters, _) =>
            {
                Assert.Equal(new[] { "tunnel", "user", "show" }, parameters.Arguments);
                var output = this.HasStoredCredential ? "logged in with provider " + this.PresenceProvider : "not logged in";
                return Task.FromResult(new ProcessResult(this.HasStoredCredential ? 0 : 1, output, "private-device-artifact", output));
            });
            var executor = new VsCodeTunnelCliAuthorizationExecutor(
                this.invoker,
                this.resolver,
                new VsCodeTunnelInteractiveLoginLauncher((startInfo, _) =>
                {
                    this.Logins.Add(startInfo);
                    this.HasStoredCredential = true;
                    return Task.FromResult(0);
                }));
            var executors = new CredentialAccessExecutorRegistry();
            executors.AddCompletion<CliOwnedAuthorizationRequest, AuthorizationCompletedAccessResult>(executor);
            this.Broker = new AccessBroker(this.Policy.Object, executors, new ProviderAcquisitionCoordinator(), this.Registry);
            this.Tool = this.CreateTool();
        }

        internal RunVsCodeTunnelTool CreateTool(Func<CancellationToken, Task>? wait = null) => new(
            this.Broker,
            cliInvoker: this.invoker,
            tunnelStatusResolver: this.resolver,
            processLauncher: (_, arguments) =>
            {
                this.Hosts.Add(arguments);
                var child = this.Children.Dequeue();
                this.ChildrenStarted.Add(child);
                return child;
            },
            initialStatusCheckDelayAsync: _ => Task.CompletedTask,
            waitBetweenPollsAsync: wait ?? (_ => throw new InvalidOperationException("Unexpected poll.")));

        internal async Task<UserAuthorizationSnapshot> WaitForPendingRowAsync()
        {
            while (true)
            {
                var row = this.Registry.GetSnapshot().FirstOrDefault(row => row.State == UserAuthorizationState.Pending);
                if (row is not null)
                    return row;
                await this.changed.Reader.ReadAsync(TestContext.Current.CancellationToken);
            }
        }

        public ValueTask DisposeAsync() => this.Broker.DisposeAsync();
    }

    private sealed class FakeStatusResolver(Func<VsCodeTunnelOutcome> outcome) : IVsCodeTunnelStatusResolver
    {
        public Task<VsCodeTunnelResolution> ResolveAsync(
            string cliPath,
            CancellationToken cancellationToken) => Task.FromResult(new VsCodeTunnelResolution { Outcome = outcome() });
    }
}
