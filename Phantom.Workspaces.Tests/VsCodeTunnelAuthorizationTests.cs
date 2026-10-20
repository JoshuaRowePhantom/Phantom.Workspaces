using System.Diagnostics;
using Moq;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class VsCodeTunnelAuthorizationTests
{
    [Theory]
    [InlineData(CliOwnedAuthorizationKind.VsCodeTunnelGitHub, "github")]
    [InlineData(CliOwnedAuthorizationKind.VsCodeTunnelMicrosoft, "microsoft")]
    public async Task Perform_RunsExactVisibleProviderCommandWithoutExportOrReleasePolicy(
        CliOwnedAuthorizationKind kind,
        string provider)
    {
        var launches = new List<ProcessStartInfo>();
        await using var harness = new Harness((startInfo, _) =>
        {
            launches.Add(startInfo);
            return Task.FromResult(0);
        });
        var task = harness.Broker.AcquireAsync(Request(kind), TestContext.Current.CancellationToken);
        var row = Assert.Single(harness.Registry.GetSnapshot());
        Assert.Empty(launches);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
        await PerformAsync(harness.Registry, row);
        var succeeded = Assert.IsType<AccessSucceeded<AuthorizationCompletedAccessResult>>(await task);
        await succeeded.Result.DisposeAsync();
        Assert.False(typeof(ICredentialAccessResult).IsAssignableFrom(succeeded.Result.GetType()));
        var start = Assert.Single(launches);
        Assert.Equal(new[] { "tunnel", "user", "login", "--provider", provider }, start.ArgumentList);
        Assert.False(start.RedirectStandardInput);
        Assert.False(start.RedirectStandardOutput);
        Assert.False(start.RedirectStandardError);
        Assert.False(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
        Assert.False(start.Environment.ContainsKey("VSCODE_CLI_ACCESS_TOKEN"));
        harness.Policy.VerifyNoOtherCalls();
        Assert.DoesNotContain("private", string.Join(" ", harness.Registry.GetSnapshot()));
    }

    [Theory]
    [InlineData(0, "logged in with provider github", VsCodeTunnelOutcome.Ready)]
    [InlineData(0, "logged in with provider microsoft", VsCodeTunnelOutcome.Ready)]
    [InlineData(1, "not logged in", VsCodeTunnelOutcome.AuthenticationRequired)]
    [InlineData(0, "not logged in", VsCodeTunnelOutcome.CommandFailed)]
    [InlineData(1, "logged in with provider github", VsCodeTunnelOutcome.CommandFailed)]
    [InlineData(0, "logged in with provider unknown", VsCodeTunnelOutcome.CommandFailed)]
    [InlineData(1, "prefix not logged in", VsCodeTunnelOutcome.CommandFailed)]
    [InlineData(0, "private-account", VsCodeTunnelOutcome.CommandFailed)]
    public void UserShow_IsStrictPresenceOnly(
        int exitCode,
        string output,
        VsCodeTunnelOutcome expected) =>
        Assert.Equal(expected, VsCodeTunnelCliAuthorizationExecutor.ReducePresence(exitCode, output));

    [Fact]
    public async Task StoredMicrosoftPresence_DoesNotInferProviderOrOverrideRemoteFailure()
    {
        await using var harness = new Harness((_, _) => Task.FromResult(0));
        harness.PresenceOutput = "logged in with provider microsoft";
        var request = Request(CliOwnedAuthorizationKind.VsCodeTunnelGitHub) with { RequiresRenewal = false };
        var silent = Assert.IsType<AccessSucceeded<AuthorizationCompletedAccessResult>>(
            await harness.Broker.AcquireAsync(request, TestContext.Current.CancellationToken));
        await silent.Result.DisposeAsync();
        Assert.Empty(harness.Registry.GetSnapshot());
        var renewal = harness.Broker.AcquireAsync(request with { RequiresRenewal = true }, TestContext.Current.CancellationToken);
        var row = Assert.Single(harness.Registry.GetSnapshot());
        Assert.Contains("GitHub", row.SourceOptions.Single().SafeDisplayName);
        await harness.Registry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);
        Assert.IsType<AccessDenied<AuthorizationCompletedAccessResult>>(await renewal);
    }

    [Fact]
    public async Task SameProviderPerformedRows_JoinOneLogin()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var harness = new Harness(async (_, token) =>
        {
            Interlocked.Increment(ref count);
            started.TrySetResult();
            await finish.Task.WaitAsync(token);
            return 0;
        });
        var first = harness.Broker.AcquireAsync(Request(CliOwnedAuthorizationKind.VsCodeTunnelGitHub), TestContext.Current.CancellationToken);
        var second = harness.Broker.AcquireAsync(Request(CliOwnedAuthorizationKind.VsCodeTunnelGitHub), TestContext.Current.CancellationToken);
        var rows = harness.Registry.GetSnapshot();
        Assert.Equal(2, rows.Count);
        await PerformAsync(harness.Registry, rows[0]);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await PerformAsync(harness.Registry, rows[1]);
        await harness.BothJoined.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, count);
        finish.SetResult();
        await (Assert.IsType<AccessSucceeded<AuthorizationCompletedAccessResult>>(await first)).Result.DisposeAsync();
        await (Assert.IsType<AccessSucceeded<AuthorizationCompletedAccessResult>>(await second)).Result.DisposeAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public void ProviderKeys_AreIncompatibleAcrossProviderOrExecutable()
    {
        var github = Request(CliOwnedAuthorizationKind.VsCodeTunnelGitHub);
        Assert.NotEqual(VsCodeTunnelCliAuthorizationExecutor.CreateKey(github),
            VsCodeTunnelCliAuthorizationExecutor.CreateKey(github with { Kind = CliOwnedAuthorizationKind.VsCodeTunnelMicrosoft }));
        Assert.NotEqual(VsCodeTunnelCliAuthorizationExecutor.CreateKey(github),
            VsCodeTunnelCliAuthorizationExecutor.CreateKey(github with { CliPath = "other-code.exe" }));
    }

    [Fact]
    public async Task LoginFailure_LeavesRetryableRowWithoutPublishingMaterial()
    {
        await using var harness = new Harness((_, _) => Task.FromResult(1));
        var task = harness.Broker.AcquireAsync(Request(CliOwnedAuthorizationKind.VsCodeTunnelMicrosoft), TestContext.Current.CancellationToken);
        await PerformAsync(harness.Registry, Assert.Single(harness.Registry.GetSnapshot()));
        var failure = Assert.IsType<AccessFailed<AuthorizationCompletedAccessResult>>(await task);
        Assert.True(failure.CanRetry);
        var row = Assert.Single(harness.Registry.GetSnapshot());
        Assert.Equal(UserAuthorizationState.Failed, row.State);
        Assert.True(row.CanAct);
        harness.Policy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnconsumedPermit_CannotLaunchVisibleProcess()
    {
        var launches = 0;
        var request = Request(CliOwnedAuthorizationKind.VsCodeTunnelGitHub);
        var key = VsCodeTunnelCliAuthorizationExecutor.CreateKey(request);
        var launcher = new VsCodeTunnelInteractiveLoginLauncher((_, _) =>
        {
            launches++;
            return Task.FromResult(0);
        });
        var permit = new ProviderInteractionPermit(new() { Value = Guid.NewGuid() }, 1,
            CredentialExecutorKind.CliOwned, Guid.NewGuid(), key, () => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(
            "code.exe", VsCodeTunnelProvider.GitHub, permit, key, TestContext.Current.CancellationToken));
        Assert.Equal(0, launches);
    }

    internal static CliOwnedAuthorizationRequest Request(CliOwnedAuthorizationKind kind)
    {
        var requestor = new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Tool, "run-vscode-tunnel"),
            Kind = AccessRequestorKind.Tool,
            SafeDisplayName = "VS Code tunnel",
        };
        var lineage = new CredentialUseLineage();
        return new CliOwnedAuthorizationRequest
        {
            Kind = kind,
            CliPath = "code.exe",
            RequiresRenewal = true,
            Requestor = requestor,
            PresentationScope = AccessPresentationScope.Runtime,
            Use = new CredentialUseDescriptor
            {
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                    CredentialUseKind.Completion, lineage, requestor.Identifier, "renew-vscode-tunnel"),
                Kind = CredentialUseKind.Completion,
                Lineage = lineage,
                SafeDisplayName = "Renew VS Code tunnel authentication",
                MemoryScopes = [],
            },
        };
    }

    internal static Task PerformAsync(
        UserAuthorizationRegistry registry,
        UserAuthorizationSnapshot row) => registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.SelectedMemory,
            Source = row.SelectedSource,
        }, TestContext.Current.CancellationToken);

    private sealed class Harness : IAsyncDisposable
    {
        internal Mock<IReleaseAuthorizationPolicy> Policy { get; } = new(MockBehavior.Strict);
        internal UserAuthorizationRegistry Registry { get; } = new();
        internal AccessBroker Broker { get; }
        internal TaskCompletionSource BothJoined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string PresenceOutput { get; set; } = "logged in with provider github";

        public Harness(Func<ProcessStartInfo, CancellationToken, Task<int>> runVisible)
        {
            var invoker = new VsCodeCliInvoker(processRunner: (parameters, _) =>
            {
                var output = parameters.Arguments.Contains("show") ? this.PresenceOutput : "{\"tunnel\":null}";
                return Task.FromResult(new ProcessResult(0, output, "private-device-code", output));
            });
            var executors = new CredentialAccessExecutorRegistry();
            executors.AddCompletion<CliOwnedAuthorizationRequest, AuthorizationCompletedAccessResult>(
                new VsCodeTunnelCliAuthorizationExecutor(invoker, launcher: new VsCodeTunnelInteractiveLoginLauncher(runVisible)));
            this.Broker = new AccessBroker(this.Policy.Object, executors, new ObservedCoordinator(this.BothJoined), this.Registry);
        }

        public ValueTask DisposeAsync() => this.Broker.DisposeAsync();
    }

    private sealed class ObservedCoordinator(TaskCompletionSource bothJoined) : IProviderAcquisitionCoordinator
    {
        private readonly ProviderAcquisitionCoordinator coordinator = new();
        private int participants;

        public ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit permit,
            CancellationToken cancellationToken)
        {
            var result = this.coordinator.ExecuteOrJoinAsync(operation, permit, cancellationToken);
            if (Interlocked.Increment(ref this.participants) == 2)
            {
                bothJoined.TrySetResult();
            }

            return result;
        }

        public ValueTask DisposeAsync() => this.coordinator.DisposeAsync();
    }
}
