using System.IO;
using System.Text.Json;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests.Access;

public sealed class DevTunnelIdentityCredentialAccessExecutorTests
{
    [Theory]
    [InlineData(DevTunnelIdentityScheme.GitHub)]
    [InlineData(DevTunnelIdentityScheme.Entra)]
    public async Task DevTunnelIdentityExecutor_TypedRoute_PreservesIndependentReleaseAndLineage(
        DevTunnelIdentityScheme scheme)
    {
        await using var fixture = new Fixture(scheme);
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        var parent = Assert.Single(fixture.Registry.GetSnapshot());
        Assert.Equal(0, fixture.Executions);
        var child = await fixture.ApplyParentAsync(parent);
        Assert.Equal(0, fixture.Executions);
        Assert.False(pending.IsCompleted);
        Assert.Equal(parent.UseDisplayName, child.UseDisplayName);
        Assert.True(child.IsBootstrapBlocking);

        await fixture.ApplyAsync(child);
        await using var result = Assert.IsType<AccessSucceeded<DevTunnelIdentityCredentialAccessResult>>(
            await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;

        Assert.Equal(scheme, result.Scheme);
        Assert.Equal(1, fixture.Executions);
        Assert.Equal(2, (await fixture.Policy.ListAsync(TestContext.Current.CancellationToken)).Count);
        Assert.Equal(fixture.Request.Requestor, fixture.LastRequestor);
        Assert.Equal(fixture.Request.Use, fixture.LastUse);
        Assert.NotNull(result.AccessToken);
        Assert.Equal("private material", Assert.IsType<SensitiveValueUseSucceeded<string>>(await result.AccessToken.UseAsync(
            (material, _) => ValueTask.FromResult(material),
            TestContext.Current.CancellationToken)).Result);
        var snapshots = JsonSerializer.Serialize(fixture.Registry.GetSnapshot());
        Assert.DoesNotContain("private material", snapshots);
        Assert.DoesNotContain("private principal", snapshots);
    }

    [Fact]
    public async Task DevTunnelIdentityExecutor_AnonymousRoute_ProducesNoAuthorizationHeaderOrInventedAccount()
    {
        await using var fixture = new Fixture(DevTunnelIdentityScheme.Anonymous);
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.ApplyAsync(Assert.Single(fixture.Registry.GetSnapshot()));
        await using var result = Assert.IsType<AccessSucceeded<DevTunnelIdentityCredentialAccessResult>>(await pending).Result;

        Assert.Equal(DevTunnelIdentityScheme.Anonymous, result.Scheme);
        Assert.Null(result.AccessToken);
        Assert.Equal(0, fixture.Executions);
        Assert.Null(Assert.Single(await fixture.Policy.ListAsync(TestContext.Current.CancellationToken)).AccountIdentifier);
        Assert.Null(fixture.Request.Credential.AccountIdentifier);
        Assert.Equal(CredentialKind.Anonymous, fixture.Request.Credential.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DevTunnelIdentityExecutor_ForgetEitherRelease_StopsTheCorrespondingLease(
        bool forgetParent)
    {
        await using var fixture = new Fixture(DevTunnelIdentityScheme.GitHub);
        await using var result = await fixture.AcquireAsync();
        var memory = (await fixture.Policy.ListAsync(TestContext.Current.CancellationToken))
            .Single(entry => (entry.LogicalIdentifier == fixture.Request.Credential.LogicalIdentifier) == forgetParent);
        await fixture.Policy.ForgetAsync(memory.Identifier, TestContext.Current.CancellationToken);
        var checks = fixture.GitHub.GenerationChecks;

        var required = Assert.IsType<SensitiveValueUseAuthorizationRequired<int>>(await result.AccessToken!.UseAsync<int>(
            (_, _) => throw new InvalidOperationException("Forgotten release must not expose material."),
            TestContext.Current.CancellationToken));

        Assert.Equal(checks, fixture.GitHub.GenerationChecks);
        var row = fixture.Registry.GetSnapshot().Single(entry => entry.Identifier == required.RequestIdentifier);
        Assert.Equal(UserAuthorizationState.Pending, row.State);
        Assert.Equal(1, fixture.Executions);
    }

    [Fact]
    public async Task DevTunnelIdentityExecutor_RotatedChild_DoesNotReplaceItsBoundGeneration()
    {
        await using var fixture = new Fixture(DevTunnelIdentityScheme.GitHub);
        await using var result = await fixture.AcquireAsync();
        fixture.GitHub.CurrentGeneration = false;

        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(await result.AccessToken!.UseAsync<int>(
            (_, _) => throw new InvalidOperationException("A replacement generation must not be substituted."),
            TestContext.Current.CancellationToken));

        Assert.Equal(1, fixture.Executions);
    }

    [Fact]
    public async Task DevTunnelIdentityExecutor_RememberedUses_OwnIndependentChildResults()
    {
        await using var fixture = new Fixture(DevTunnelIdentityScheme.Entra);
        var first = await fixture.AcquireAsync();
        await using var second = Assert.IsType<AccessSucceeded<DevTunnelIdentityCredentialAccessResult>>(
            await fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken)).Result;

        await first.DisposeAsync();

        Assert.NotSame(first.AccessToken, second.AccessToken);
        Assert.Equal(2, fixture.Executions);
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await second.AccessToken!.UseAsync(
            (_, _) => ValueTask.FromResult(7),
            TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseFailed<int>>(await first.AccessToken!.UseAsync(
            (_, _) => ValueTask.FromResult(7),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DevTunnelIdentityExecutor_AccountPinMismatch_CannotSwitchAccount()
    {
        await using var fixture = new Fixture(DevTunnelIdentityScheme.GitHub);
        var request = fixture.Request with
        {
            Credential = fixture.Request.Credential with
            {
                AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(
                    CredentialProviderKind.GitHub, "different principal"),
            },
        };

        var failure = Assert.IsType<AccessFailed<DevTunnelIdentityCredentialAccessResult>>(
            await fixture.Broker.AcquireAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(AccessErrorCode.IdentityMismatch, failure.ErrorCode);
        Assert.Equal(0, fixture.Executions);
        Assert.Empty(fixture.Registry.GetSnapshot());
    }

    [Fact]
    public async Task DevTunnelIdentityExecutor_Shutdown_CancelsParentAndNestedWaitWithoutMaterialization()
    {
        await using var fixture = new Fixture(DevTunnelIdentityScheme.GitHub);
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.ApplyParentAsync(Assert.Single(fixture.Registry.GetSnapshot()));
        Assert.Equal(2, fixture.Registry.GetSnapshot().Count);

        await fixture.Broker.DisposeAsync();

        Assert.IsType<AccessShuttingDown<DevTunnelIdentityCredentialAccessResult>>(await pending);
        Assert.Equal(0, fixture.Executions);
        Assert.Empty(fixture.Registry.GetSnapshot());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(
            Environment.CurrentDirectory, ".access-tunnel-identity-tests", Guid.NewGuid().ToString("N"));

        public Fixture(DevTunnelIdentityScheme scheme)
        {
            var template = ReleaseAuthorizationContractTests.Request();
            var requestor = new AccessRequestor
            {
                Identifier = template.RequestorIdentifier,
                Kind = AccessRequestorKind.Application,
                SafeDisplayName = "Application",
            };
            var gitHubRequest = GitHubCredentialAccessRequest.Create(
                GitHubCredentialPurpose.DevTunnelIdentity, requestor, template.Use.Lineage, "management",
                source: CredentialSourceKind.GitHubCli);
            var entraRequest = new EntraCredentialAccessRequest
            {
                Authority = new() { Value = new Uri("https://login.microsoftonline.com/organizations") },
                Client = new() { Value = "test-client" },
                CachePartition = new() { Value = "test-management" },
                Scopes = [new() { Value = "management/.default" }],
                Credential = gitHubRequest.Credential with { Provider = CredentialProviderKind.Entra },
                Requestor = requestor,
                Use = template.Use,
                PresentationScope = AccessPresentationScope.Runtime,
                Sources = gitHubRequest.Sources,
            };
            entraRequest = entraRequest with
            {
                Credential = entraRequest.Credential with { LogicalIdentifier = entraRequest.CreateLogicalIdentifier() },
                Sources = [new CredentialSourceChoiceDescriptor
                {
                    Identifier = entraRequest.CreateSourceIdentifier(),
                    Kind = CredentialSourceKind.Provider,
                    Provider = CredentialProviderKind.Entra,
                    Usability = CredentialSourceUsability.Usable,
                    SafeDisplayName = "Microsoft",
                }],
            };
            this.GitHub = new(CredentialExecutorKind.GitHub, lease => new GitHubCredentialAccessResult { Lease = lease });
            this.Entra = new(CredentialExecutorKind.Entra, lease => new EntraCredentialAccessResult
            {
                AccessToken = lease,
                ExpiresOn = DateTimeOffset.MaxValue,
            });
            DevTunnelIdentityRoute route = scheme switch
            {
                DevTunnelIdentityScheme.GitHub => new GitHubDevTunnelIdentityRoute { Credential = gitHubRequest },
                DevTunnelIdentityScheme.Entra => new EntraDevTunnelIdentityRoute { Credential = entraRequest },
                _ => new AnonymousDevTunnelIdentityRoute(),
            };
            this.Request = DevTunnelIdentityCredentialAccessRequest.Create(
                route, requestor, template.Use with { Kind = CredentialUseKind.Tunnel }, AccessPresentationScope.Bootstrap);
            this.Policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "policy.json")));
            var executors = new CredentialAccessExecutorRegistry();
            executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(this.GitHub);
            executors.Add<EntraCredentialAccessRequest, EntraCredentialAccessResult>(this.Entra);
            executors.Add<DevTunnelIdentityCredentialAccessRequest, DevTunnelIdentityCredentialAccessResult>(
                new DevTunnelIdentityCredentialAccessExecutor(this.GitHub, this.Entra));
            this.Broker = new AccessBroker(this.Policy, executors, new ProviderAcquisitionCoordinator(), this.Registry);
        }

        public DevTunnelIdentityCredentialAccessRequest Request { get; }
        public AccessBroker Broker { get; }
        public ReleaseAuthorizationPolicy Policy { get; }
        public UserAuthorizationRegistry Registry { get; } = new();
        public DependencyExecutor<GitHubCredentialAccessRequest, GitHubCredentialAccessResult> GitHub { get; }
        public DependencyExecutor<EntraCredentialAccessRequest, EntraCredentialAccessResult> Entra { get; }
        public int Executions => this.GitHub.Executions + this.Entra.Executions;
        public AccessRequestor? LastRequestor => this.GitHub.LastRequestor ?? this.Entra.LastRequestor;
        public CredentialUseDescriptor? LastUse => this.GitHub.LastUse ?? this.Entra.LastUse;

        public async Task<DevTunnelIdentityCredentialAccessResult> AcquireAsync()
        {
            var pending = this.Broker.AcquireAsync(this.Request, TestContext.Current.CancellationToken);
            var child = await this.ApplyParentAsync(this.Registry.GetSnapshot().Last());
            await this.ApplyAsync(child);
            return Assert.IsType<AccessSucceeded<DevTunnelIdentityCredentialAccessResult>>(
                await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Result;
        }

        public async Task<UserAuthorizationSnapshot> ApplyParentAsync(UserAuthorizationSnapshot parent)
        {
            var nested = new TaskCompletionSource<UserAuthorizationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            void FindNested()
            {
                var row = this.Registry.GetSnapshot().SingleOrDefault(candidate =>
                    candidate.Identifier != parent.Identifier && candidate.State == UserAuthorizationState.Pending);
                if (row is not null)
                {
                    nested.TrySetResult(row);
                }
            }
            void Changed(object? sender, UserAuthorizationChangedEventArgs arguments) => FindNested();
            this.Registry.Changed += Changed;
            try
            {
                await this.ApplyAsync(parent);
                FindNested();
                return await nested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            finally
            {
                this.Registry.Changed -= Changed;
            }
        }

        public Task ApplyAsync(UserAuthorizationSnapshot row) => this.Registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllUses).Identifier,
            Source = row.SelectedSource,
        }, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await this.Broker.DisposeAsync();
            if (Directory.Exists(this.directory))
            {
                Directory.Delete(this.directory, recursive: true);
            }
        }
    }

    private sealed class DependencyExecutor<TRequest, TResult>(
        CredentialExecutorKind kind,
        Func<SensitiveValueLease, TResult> createResult) :
        ICredentialAccessExecutor<TRequest, TResult>, ICredentialGenerationValidator
        where TRequest : CredentialAccessRequest<TResult>
        where TResult : ICredentialAccessResult
    {
        private readonly CredentialAccountIdentifier account =
            CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.DevTunnel, "private principal");
        private readonly CredentialMaterialGenerationIdentifier generation = new() { Value = Guid.NewGuid() };
        public CredentialExecutorKind Kind => kind;
        public int Executions { get; private set; }
        public bool CurrentGeneration { get; set; } = true;
        public int GenerationChecks { get; private set; }
        public AccessRequestor? LastRequestor { get; private set; }
        public CredentialUseDescriptor? LastUse { get; private set; }

        public ValueTask<CredentialInspection> InspectAsync(TRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = request.Credential with { AccountIdentifier = this.account },
                Sources = request.Sources.Select(source => source with { Usability = CredentialSourceUsability.Usable }).ToArray(),
            });

        public ValueTask<CredentialExecutionOutcome<TResult>> ExecuteAsync(
            TRequest request,
            CredentialInspection inspection,
            ReleaseAuthorization authorization,
            ProviderInteractionPermit? interactionPermit,
            ICredentialExecutionContext context,
            CancellationToken cancellationToken)
        {
            this.Executions++;
            this.LastRequestor = request.Requestor;
            this.LastUse = request.Use;
            return ValueTask.FromResult<CredentialExecutionOutcome<TResult>>(new CredentialExecutionReady<TResult>
            {
                Materializer = new DependencyMaterializer(() => createResult(new SensitiveValueLease(
                    "private material".AsMemory(),
                    new CredentialLeaseBinding
                    {
                        AuthorizationIdentifier = authorization.Identifier,
                        Request = context.PolicyRequest!,
                        MaterialGeneration = this.generation,
                    },
                    context.CreateLeaseContext(this)))),
            });
        }

        public ValueTask<bool> IsCurrentGenerationAsync(
            CredentialMaterialGenerationIdentifier generation,
            CancellationToken cancellationToken)
        {
            this.GenerationChecks++;
            return ValueTask.FromResult(this.CurrentGeneration && this.generation == generation);
        }

        private sealed class DependencyMaterializer(Func<TResult> create) : IAccessResultMaterializer<TResult>
        {
            public ValueTask<TResult> MaterializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(create());
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
