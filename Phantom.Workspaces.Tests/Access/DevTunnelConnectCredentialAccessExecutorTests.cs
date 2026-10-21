using System.IO;
using System.Text.Json;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests.Access;

public sealed class DevTunnelConnectCredentialAccessExecutorTests
{
    [Fact]
    public async Task ConnectExecutor_PrivateCapabilityDenial_DoesNotPublishOrRetryMint()
    {
        await using var fixture = new Fixture();
        fixture.Session.DenyUse = true;
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(Assert.Single(fixture.Registry.GetSnapshot()));

        Assert.IsType<AccessDenied<DevTunnelConnectCredentialAccessResult>>(await pending);
        Assert.Equal(1, fixture.Session.Mints);
        Assert.Null(fixture.Session.LastMaterial);
        Assert.Empty(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectExecutor_InspectionAndPendingRelease_DoNotMintOrExposeProtocolData()
    {
        await using var fixture = new Fixture();
        var inspection = await fixture.Executor.InspectAsync(fixture.Request, TestContext.Current.CancellationToken);
        Assert.Equal(CredentialSourceUsability.AcquisitionRequired, Assert.Single(inspection.Sources).Usability);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = fixture.Broker.AcquireAsync(fixture.Request, cancellation.Token);

        Assert.Equal(0, fixture.Session.Mints);
        var snapshots = JsonSerializer.Serialize(fixture.Registry.GetSnapshot());
        Assert.DoesNotContain("private connect material", snapshots);
        Assert.DoesNotContain(fixture.Session.Endpoint.Host, snapshots);
        Assert.Single(fixture.Registry.GetSnapshot());
        await cancellation.CancelAsync();
        Assert.IsType<AccessCancelled<DevTunnelConnectCredentialAccessResult>>(await pending);
    }

    [Fact]
    public async Task ConnectExecutor_FinalReleaseProducesOnlyConnectLease_AndRememberedCacheAvoidsMint()
    {
        await using var fixture = new Fixture();
        await using var first = await fixture.AcquireAsync();
        Assert.Equal(1, fixture.Session.Mints);
        Assert.Single(fixture.Registry.GetSnapshot());
        Assert.Single(await fixture.Policy.ListAsync(TestContext.Current.CancellationToken));
        Assert.All(fixture.Session.LastMaterial!, character => Assert.Equal('\0', character));
        Assert.Equal(fixture.Session.Endpoint, first.Endpoint);
        Assert.Equal("private connect material", Assert.IsType<SensitiveValueUseSucceeded<string>>(
            await first.ConnectToken!.UseAsync((material, _) => ValueTask.FromResult(material),
                TestContext.Current.CancellationToken)).Result);

        await using var second = Assert.IsType<AccessSucceeded<DevTunnelConnectCredentialAccessResult>>(
            await fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken)).Result;
        await first.DisposeAsync();
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await second.ConnectToken!.UseAsync(
            (_, _) => ValueTask.FromResult(42), TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Session.Mints);
    }

    [Fact]
    public async Task ConnectExecutor_ForgetPreventsMaterialUseWithoutReacquiring()
    {
        await using var fixture = new Fixture();
        await using var result = await fixture.AcquireAsync();
        var memory = Assert.Single(await fixture.Policy.ListAsync(TestContext.Current.CancellationToken));
        await fixture.Policy.ForgetAsync(memory.Identifier, TestContext.Current.CancellationToken);

        Assert.IsType<SensitiveValueUseAuthorizationRequired<int>>(await result.ConnectToken!.UseAsync<int>(
            (_, _) => throw new InvalidOperationException("Forgotten release must guard material."),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Session.Mints);
    }

    [Fact]
    public async Task ConnectExecutor_ExactRejectedGenerationCannotInvalidateReplacement()
    {
        await using var fixture = new Fixture();
        await using var first = await fixture.AcquireAsync();
        Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
            await first.InvalidateRejectedGenerationAsync(TestContext.Current.CancellationToken));

        await using var second = await fixture.AcquireAsync();
        Assert.Equal(CredentialGenerationInvalidationOutcome.NoLongerCurrent,
            await first.InvalidateRejectedGenerationAsync(TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await first.ConnectToken!.UseAsync<int>(
            (_, _) => throw new InvalidOperationException("A rejected lease cannot use replacement material."),
            TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await second.ConnectToken!.UseAsync(
            (_, _) => ValueTask.FromResult(42), TestContext.Current.CancellationToken));
        Assert.Equal(2, fixture.Session.Mints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectExecutor_Management401WaitsForNewExplicitPerform_AndStopsAfterSecond401(
        bool rejectAgain)
    {
        await using var fixture = new Fixture();
        fixture.Session.UnauthorizedRemaining = rejectAgain ? 2 : 1;
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        var initial = Assert.Single(fixture.Registry.GetSnapshot());
        var renewal = await fixture.WaitForStateAsync(initial.Identifier, UserAuthorizationState.RenewalNeeded,
            () => fixture.ApplyAsync(initial));

        Assert.False(pending.IsCompleted);
        Assert.Equal(1, fixture.Session.Mints);
        Assert.Single(fixture.Registry.GetSnapshot());
        Assert.True(renewal.Revision > initial.Revision);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, renewal.PrimaryAction);
        await Assert.ThrowsAsync<UserAuthorizationActionRejectedException>(() => fixture.ApplyAsync(initial));
        Assert.Equal(1, fixture.Session.Mints);

        await fixture.ApplyAsync(renewal);
        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (rejectAgain)
        {
            Assert.False(Assert.IsType<AccessFailed<DevTunnelConnectCredentialAccessResult>>(outcome).CanRetry);
        }
        else
        {
            await using var result = Assert.IsType<AccessSucceeded<DevTunnelConnectCredentialAccessResult>>(outcome).Result;
            Assert.NotNull(result.ConnectToken);
        }
        Assert.Equal(2, fixture.Session.Mints);
    }

    [Fact]
    public async Task ConnectExecutor_CancellingExplicitRenewalEndsOriginalCaller()
    {
        await using var fixture = new Fixture();
        fixture.Session.UnauthorizedRemaining = 1;
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        var initial = Assert.Single(fixture.Registry.GetSnapshot());
        var renewal = await fixture.WaitForStateAsync(initial.Identifier, UserAuthorizationState.RenewalNeeded,
            () => fixture.ApplyAsync(initial));
        await fixture.Registry.ActAsync(new CancelUserAuthorizationAction
        {
            RequestIdentifier = renewal.Identifier,
            ExpectedRevision = renewal.Revision,
        }, TestContext.Current.CancellationToken);

        Assert.IsType<AccessCancelled<DevTunnelConnectCredentialAccessResult>>(await pending);
        Assert.Equal(1, fixture.Session.Mints);
    }

    [Theory]
    [InlineData("https://another-123.euw.devtunnels.ms/")]
    [InlineData("https://attacker.example/")]
    [InlineData("https://tunnel-123.euw.devtunnels.ms/?signed=private")]
    public async Task ConnectExecutor_MintedEndpointMustMatchAuthorizedRelay(string endpoint)
    {
        await using var fixture = new Fixture();
        fixture.Session.Endpoint = new Uri(endpoint);
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(Assert.Single(fixture.Registry.GetSnapshot()));

        var outcome = Assert.IsType<AccessFailed<DevTunnelConnectCredentialAccessResult>>(await pending);
        Assert.Equal(AccessErrorCode.IdentityMismatch, outcome.ErrorCode);
        Assert.All(fixture.Session.LastMaterial!, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task ConnectExecutor_AccountPinCannotBeReplacedByMintedPrincipal()
    {
        await using var fixture = new Fixture();
        var request = fixture.Request with
        {
            Credential = fixture.Request.Credential with
            {
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "different principal"),
            },
        };
        var pending = fixture.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(Assert.Single(fixture.Registry.GetSnapshot()));

        Assert.Equal(AccessErrorCode.IdentityMismatch,
            Assert.IsType<AccessFailed<DevTunnelConnectCredentialAccessResult>>(await pending).ErrorCode);
        Assert.All(fixture.Session.LastMaterial!, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task ConnectExecutor_SafelyInspectedAccountPinsCommitEvenWhenOriginalRequestIsUnpinned()
    {
        await using var fixture = new Fixture();
        fixture.Session.InspectedAccount = CredentialIdentityFactory.CreateAccountIdentifier(
            CredentialProviderKind.GitHub, "approved principal");
        fixture.Session.MintedAccount = CredentialIdentityFactory.CreateAccountIdentifier(
            CredentialProviderKind.GitHub, "different principal");
        Assert.Null(fixture.Request.Credential.AccountIdentifier);
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(Assert.Single(fixture.Registry.GetSnapshot()));

        Assert.Equal(AccessErrorCode.IdentityMismatch,
            Assert.IsType<AccessFailed<DevTunnelConnectCredentialAccessResult>>(await pending).ErrorCode);
        Assert.All(fixture.Session.LastMaterial!, character => Assert.Equal('\0', character));
        Assert.Empty(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectExecutor_BindsExactKeyToSafelySelectedSourceAccountBeforeAcquisition()
    {
        var clock = new TestClock();
        await using var fixture = new Fixture(multipleIdentitySources: true, timeProvider: clock);
        var source = fixture.Request.Sources[0].Identifier;
        await using var original = await fixture.AcquireAsync(source);
        var account = fixture.Session.MintedAccount;
        clock.Now = clock.Now.AddHours(2);
        fixture.Session.ExpiresOn = clock.Now.AddHours(1);
        fixture.Session.MintedAccount = CredentialIdentityFactory.CreateAccountIdentifier(
            CredentialProviderKind.GitHub, "different principal");
        fixture.Session.MintProofObserved = (context, key) =>
        {
            Assert.Equal(account, key.Account);
            Assert.Equal(account, context.PolicyRequest!.Credential.AccountIdentifier);
        };
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(fixture.Registry.GetSnapshot().Last(row =>
            row.State is UserAuthorizationState.Pending or UserAuthorizationState.RenewalNeeded), source);

        Assert.Equal(AccessErrorCode.IdentityMismatch,
            Assert.IsType<AccessFailed<DevTunnelConnectCredentialAccessResult>>(await pending).ErrorCode);
        Assert.All(fixture.Session.LastMaterial!, character => Assert.Equal('\0', character));
        Assert.DoesNotContain(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken),
            descriptor => descriptor.Generation != original.ConnectToken!.MaterialGeneration);
    }

    [Fact]
    public async Task ConnectExecutor_ShutdownCancelsBlockedMintWithoutPublishingMaterial()
    {
        await using var fixture = new Fixture();
        fixture.Session.MintEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.ContinueMint = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        var approval = fixture.ApplyAsync(Assert.Single(fixture.Registry.GetSnapshot()));
        await fixture.Session.MintEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await fixture.Broker.DisposeAsync();
        await approval;
        Assert.IsType<AccessShuttingDown<DevTunnelConnectCredentialAccessResult>>(await pending);
        Assert.Null(fixture.Session.LastMaterial);
        Assert.Empty(fixture.Registry.GetSnapshot());
    }

    [Theory]
    [InlineData("http://tunnel-123.euw.devtunnels.ms/")]
    [InlineData("https://attacker.example/")]
    [InlineData("https://tunnel-123.euw.devtunnels.ms:444/")]
    public async Task ConnectExecutor_AnonymousCannotAuthorizeAnUnsafeRelay(string endpoint)
    {
        await using var fixture = new Fixture(anonymous: true);
        var request = DevTunnelConnectCredentialAccessRequest.Create(fixture.Request.Tunnel with
        {
            Endpoint = new Uri(endpoint),
        }, fixture.Request.IdentityRoute, fixture.Request.Requestor, fixture.Request.Use);
        var inspection = await fixture.Executor.InspectAsync(request, TestContext.Current.CancellationToken);
        Assert.Empty(inspection.Sources);
        Assert.Equal(0, fixture.Session.Inspections);
        Assert.Equal(0, fixture.Session.Mints);
    }

    [Fact]
    public async Task ConnectExecutor_ApprovedFollowerKeepsCoalescedMintAliveAfterLeaderCancels()
    {
        await using var fixture = new Fixture();
        fixture.Session.MintEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.ContinueMint = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaderCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var leader = fixture.Broker.AcquireAsync(fixture.Request, leaderCancellation.Token);
        var first = Assert.Single(fixture.Registry.GetSnapshot());
        var firstApproval = fixture.ApplyAsync(first);
        await fixture.Session.MintEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var follower = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        var second = fixture.Registry.GetSnapshot().Single(row => row.Identifier != first.Identifier);
        var secondApproval = fixture.ApplyAsync(second);
        await fixture.AcquisitionJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await leaderCancellation.CancelAsync();
        Assert.Equal(1, fixture.Session.Mints);
        fixture.Session.ContinueMint.TrySetResult();

        await Task.WhenAll(firstApproval, secondApproval);
        Assert.IsType<AccessCancelled<DevTunnelConnectCredentialAccessResult>>(await leader);
        await using var result = Assert.IsType<AccessSucceeded<DevTunnelConnectCredentialAccessResult>>(
            await follower.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        Assert.Equal(1, fixture.Session.Mints);
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await result.ConnectToken!.UseAsync(
            (_, _) => ValueTask.FromResult(42), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectExecutor_AdministrationEnumeratesMetadataAndRejectsStaleDelete()
    {
        await using var fixture = new Fixture();
        await using var first = await fixture.AcquireAsync();
        var originalMemory = Assert.Single(await fixture.Policy.ListAsync(TestContext.Current.CancellationToken));
        var descriptor = Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private connect material", JsonSerializer.Serialize(descriptor));
        Assert.DoesNotContain(fixture.Session.Endpoint.Host, JsonSerializer.Serialize(descriptor));
        await fixture.Executor.DeleteAsync(descriptor.Identifier, TestContext.Current.CancellationToken);
        Assert.Equal(originalMemory, Assert.Single(await fixture.Policy.ListAsync(TestContext.Current.CancellationToken)));
        await using var replacement = await fixture.AcquireAsync();

        var error = await Assert.ThrowsAsync<CredentialAdministrationException>(() =>
            fixture.Executor.DeleteAsync(descriptor.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, error.Failure);
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await first.ConnectToken!.UseAsync<int>(
            (_, _) => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await replacement.ConnectToken!.UseAsync(
            (_, _) => ValueTask.FromResult(42), TestContext.Current.CancellationToken));
        Assert.Contains(originalMemory, await fixture.Policy.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectExecutor_AnonymousUsesNoMintIdentityOrInventedAccount()
    {
        await using var fixture = new Fixture(anonymous: true);
        await using var result = await fixture.AcquireAsync();
        Assert.Null(result.ConnectToken);
        Assert.Equal(fixture.Request.Tunnel.Endpoint, result.Endpoint);
        Assert.Equal(0, fixture.Session.Mints);
        Assert.Equal(0, fixture.Session.Inspections);
        Assert.Null(Assert.Single(await fixture.Policy.ListAsync(TestContext.Current.CancellationToken)).AccountIdentifier);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(
            Environment.CurrentDirectory, ".access-tunnel-connect-tests", Guid.NewGuid().ToString("N"));

        public Fixture(bool anonymous = false, bool discoverEndpoint = false,
            bool multipleIdentitySources = false, TimeProvider? timeProvider = null)
        {
            var template = ReleaseAuthorizationContractTests.Request();
            var requestor = new AccessRequestor
            {
                Identifier = template.RequestorIdentifier,
                Kind = AccessRequestorKind.Application,
                SafeDisplayName = "Application",
            };
            var github = GitHubCredentialAccessRequest.Create(
                GitHubCredentialPurpose.DevTunnelIdentity, requestor, template.Use.Lineage, "management",
                source: multipleIdentitySources ? null : CredentialSourceKind.GitHubCli);
            this.Request = DevTunnelConnectCredentialAccessRequest.Create(new DevTunnelAccessDescriptor
            {
                Access = anonymous ? DevTunnelAccessKind.Anonymous : DevTunnelAccessKind.Private,
                Name = "test tunnel",
                Endpoint = anonymous || !discoverEndpoint ? this.Session.Endpoint : null,
            }, anonymous ? new AnonymousDevTunnelIdentityRoute() : new GitHubDevTunnelIdentityRoute { Credential = github },
                requestor, template.Use with { Kind = CredentialUseKind.Tunnel });
            this.Policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "policy.json")));
            this.Executor = new DevTunnelConnectCredentialAccessExecutor(this.Session, this.Registry, timeProvider);
            var executors = new CredentialAccessExecutorRegistry();
            executors.Add<DevTunnelConnectCredentialAccessRequest, DevTunnelConnectCredentialAccessResult>(this.Executor);
            this.Broker = new AccessBroker(this.Policy, executors, new ObservedCoordinator(this.AcquisitionJoined), this.Registry);
        }

        public DevTunnelConnectCredentialAccessRequest Request { get; }
        public MintSession Session { get; } = new();
        public UserAuthorizationRegistry Registry { get; } = new();
        public DevTunnelConnectCredentialAccessExecutor Executor { get; }
        public ReleaseAuthorizationPolicy Policy { get; }
        public AccessBroker Broker { get; }
        public TaskCompletionSource AcquisitionJoined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DevTunnelConnectCredentialAccessResult> AcquireAsync(CredentialSourceIdentifier? source = null)
        {
            var pending = this.Broker.AcquireAsync(this.Request, TestContext.Current.CancellationToken);
            await this.ApplyAsync(this.Registry.GetSnapshot().Last(row => row.State == UserAuthorizationState.Pending), source);
            return Assert.IsType<AccessSucceeded<DevTunnelConnectCredentialAccessResult>>(
                await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        }

        private sealed class ObservedCoordinator(TaskCompletionSource joined) : IProviderAcquisitionCoordinator
        {
            private readonly ProviderAcquisitionCoordinator inner = new();
            private int participants;
            public ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
                IProviderAcquisitionOperation operation,
                ProviderInteractionPermit permit,
                CancellationToken cancellationToken)
            {
                var result = this.inner.ExecuteOrJoinAsync(operation, permit, cancellationToken);
                if (Interlocked.Increment(ref this.participants) == 2)
                {
                    joined.TrySetResult();
                }
                return result;
            }
            public ValueTask DisposeAsync() => this.inner.DisposeAsync();
        }

        public Task ApplyAsync(UserAuthorizationSnapshot row, CredentialSourceIdentifier? source = null) => this.Registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllUses).Identifier,
            Source = source ?? row.SelectedSource,
        }, TestContext.Current.CancellationToken);

        public async Task<UserAuthorizationSnapshot> WaitForStateAsync(
            UserAuthorizationRequestIdentifier identifier,
            UserAuthorizationState state,
            Func<Task> act)
        {
            var observed = new TaskCompletionSource<UserAuthorizationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Find()
            {
                var snapshot = this.Registry.GetSnapshot().SingleOrDefault(row => row.Identifier == identifier && row.State == state);
                if (snapshot is not null)
                {
                    observed.TrySetResult(snapshot);
                }
            }
            void Changed(object? sender, UserAuthorizationChangedEventArgs arguments) => Find();
            this.Registry.Changed += Changed;
            try
            {
                await act();
                Find();
                return await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            finally
            {
                this.Registry.Changed -= Changed;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await this.Broker.DisposeAsync();
            if (Directory.Exists(this.directory))
            {
                Directory.Delete(this.directory, recursive: true);
            }
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => this.Now;
    }

    internal sealed class MintSession : IDevTunnelConnectMintSession
    {
        public Uri Endpoint { get; set; } = new("https://tunnel-123.euw.devtunnels.ms/");
        public int Mints { get; private set; }
        public int Inspections { get; private set; }
        public int UnauthorizedRemaining { get; set; }
        public bool DenyUse { get; set; }
        public CredentialAccountIdentifier? InspectedAccount { get; set; }
        public CredentialAccountIdentifier MintedAccount { get; set; } =
            CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "private principal");
        public DateTimeOffset ExpiresOn { get; set; } = DateTimeOffset.UtcNow.AddHours(1);
        public Action<ICredentialExecutionContext, ProviderAcquisitionKey>? MintProofObserved { get; set; }
        public char[]? LastMaterial { get; private set; }
        public TaskCompletionSource? MintEntered { get; set; }
        public TaskCompletionSource? ContinueMint { get; set; }

        public ValueTask<CredentialInspection> InspectIdentityAsync(
            DevTunnelIdentityRoute route,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Inspections++;
            return ValueTask.FromResult(new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = route.Reference! with
                {
                    AccountIdentifier = this.InspectedAccount ?? route.Reference!.AccountIdentifier,
                },
                Sources = route.Sources,
            });
        }

        public async ValueTask<DevTunnelConnectMintOutcome> MintAsync(
            DevTunnelConnectCredentialAccessRequest request,
            CredentialSourceChoiceDescriptor identitySource,
            ICredentialExecutionContext context,
            ProviderAcquisitionKey parentAcquisitionKey,
            ProviderInteractionPermit parentGenerationPermit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ProviderProtocolKind.TunnelConnect, parentAcquisitionKey.Protocol);
            Assert.Equal(context.PolicyRequest!.Credential.LogicalIdentifier, parentAcquisitionKey.Configuration);
            Assert.Equal(context.PolicyRequest.SourceIdentifier, parentAcquisitionKey.Source);
            Assert.True(parentGenerationPermit.Authorizes(
                context.RequestIdentifier, context.Revision, CredentialExecutorKind.DevTunnelConnect,
                context.AcquisitionGeneration, parentAcquisitionKey));
            Assert.True(parentGenerationPermit.AuthorizesProvider(CredentialExecutorKind.DevTunnelConnect, parentAcquisitionKey));
            this.MintProofObserved?.Invoke(context, parentAcquisitionKey);
            this.Mints++;
            if (this.DenyUse)
            {
                return new DevTunnelConnectMintDenied();
            }
            this.MintEntered?.TrySetResult();
            if (this.ContinueMint is not null)
            {
                await this.ContinueMint.Task.WaitAsync(cancellationToken);
            }
            if (this.UnauthorizedRemaining > 0)
            {
                this.UnauthorizedRemaining--;
                return new DevTunnelConnectMintUnauthorized();
            }
            this.LastMaterial = "private connect material".ToCharArray();
            return new DevTunnelConnectMintSucceeded
            {
                Material = this.LastMaterial,
                Account = this.MintedAccount,
                ExpiresOn = this.ExpiresOn,
                Endpoint = this.Endpoint,
            };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
