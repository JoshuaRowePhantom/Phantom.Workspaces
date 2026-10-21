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
    [InlineData(AccessPresentationScope.Bootstrap, true)]
    [InlineData(AccessPresentationScope.Runtime, false)]
    public async Task DevTunnelIdentityExecutor_ManagementHandlerUsesCurrentPresentationScope(
        AccessPresentationScope scope, bool bootstrap)
    {
        await using var fixture = new Fixture(DevTunnelIdentityScheme.GitHub);
        using var client = new System.Net.Http.HttpClient(new DevTunnelManagementAuthenticationHandler(
            fixture.Broker, fixture.Request, new RecordingHandler(_ =>
                throw new InvalidOperationException("Unapproved identity reached transport.")), () => scope));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = client.GetAsync("https://global.rel.tunnels.api.visualstudio.com/tunnels", cancellation.Token);
        Assert.Equal(bootstrap, Assert.Single(fixture.Registry.GetSnapshot()).IsBootstrapBlocking);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, fixture.Executions);
    }

    [Theory]
    [InlineData(DevTunnelIdentityScheme.GitHub, "github", false)]
    [InlineData(DevTunnelIdentityScheme.Entra, "aad", false)]
    [InlineData(DevTunnelIdentityScheme.OAuth, "Bearer", false)]
    [InlineData(DevTunnelIdentityScheme.GitHub, "github", true)]
    [InlineData(DevTunnelIdentityScheme.Entra, "aad", true)]
    [InlineData(DevTunnelIdentityScheme.OAuth, "Bearer", true)]
    public async Task DevTunnelIdentityExecutor_ManagementSendIsInsideLease_AndClearsHeaderAfterUse(
        DevTunnelIdentityScheme scheme,
        string expectedScheme,
        bool returnsCallerRequest)
    {
        await using var fixture = new Fixture(scheme);
        await using var initial = await fixture.AcquireAsync();
        using var original = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,
            "https://global.rel.tunnels.api.visualstudio.com/tunnels");
        original.Headers.Authorization = new("Bearer", "caller identity");
        original.Headers.TryAddWithoutValidation("X-Tunnel-Authorization", "tunnel must not reach management");
        using var client = new System.Net.Http.HttpClient(new DevTunnelManagementAuthenticationHandler(
            fixture.Broker, fixture.Request, new RecordingHandler(message =>
            {
                Assert.NotSame(original, message);
                Assert.Equal("caller identity", original.Headers.Authorization!.Parameter);
                Assert.True(original.Headers.Contains("X-Tunnel-Authorization"));
                Assert.Equal(expectedScheme, message.Headers.Authorization!.Scheme);
                Assert.Equal("private material", message.Headers.Authorization.Parameter);
                Assert.False(message.Headers.Contains("X-Tunnel-Authorization"));
                return new(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = returnsCallerRequest ? original : new System.Net.Http.HttpRequestMessage(message.Method, message.RequestUri)
                    {
                        Headers = { Authorization = message.Headers.Authorization },
                    },
                };
            })));
        using var response = await client.SendAsync(original, TestContext.Current.CancellationToken);
        using var sent = response.RequestMessage;
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("caller identity", original.Headers.Authorization!.Parameter);
        Assert.True(original.Headers.Contains("X-Tunnel-Authorization"));
        Assert.NotNull(sent);
        Assert.NotSame(original, sent);
        Assert.Null(sent.Headers.Authorization);
        Assert.False(sent.Headers.Contains("X-Tunnel-Authorization"));
    }

    [Fact]
    public async Task DevTunnelIdentityExecutor_FactoryRequiresLivePresentationScope()
    {
        await using var fixture = new Fixture(DevTunnelIdentityScheme.GitHub);
        Assert.Throws<ArgumentNullException>(() =>
            new DevTunnelServiceFactory(fixture.Broker, fixture.Request, null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevTunnelIdentityExecutor_FailedManagementSendPreservesCallerAndScrubsOwnedRequest(bool cancelled)
    {
        await using var fixture = new Fixture(DevTunnelIdentityScheme.GitHub);
        await using var initial = await fixture.AcquireAsync();
        using var original = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,
            "https://global.rel.tunnels.api.visualstudio.com/tunnels");
        original.Headers.Authorization = new("Bearer", "caller identity");
        original.Headers.TryAddWithoutValidation("X-Tunnel-Authorization", "caller tunnel header");
        System.Net.Http.HttpRequestMessage? outgoing = null;
        using var client = new System.Net.Http.HttpClient(new DevTunnelManagementAuthenticationHandler(
            fixture.Broker, fixture.Request, new RecordingHandler(message =>
            {
                outgoing = message;
                Assert.NotSame(original, message);
                Assert.Equal("caller identity", original.Headers.Authorization!.Parameter);
                Assert.True(original.Headers.Contains("X-Tunnel-Authorization"));
                Assert.Equal("private material", message.Headers.Authorization!.Parameter);
                Assert.False(message.Headers.Contains("X-Tunnel-Authorization"));
                Exception failure = cancelled
                    ? new OperationCanceledException(new CancellationToken(true))
                    : new System.Net.Http.HttpRequestException("transport failed");
                throw failure;
            })));

        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.SendAsync(original, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
                client.SendAsync(original, TestContext.Current.CancellationToken));
        }
        Assert.Equal("caller identity", original.Headers.Authorization!.Parameter);
        Assert.True(original.Headers.Contains("X-Tunnel-Authorization"));
        Assert.NotNull(outgoing);
        Assert.Null(outgoing.Headers.Authorization);
        Assert.False(outgoing.Headers.Contains("X-Tunnel-Authorization"));
    }

    [Fact]
    public async Task DevTunnelIdentityExecutor_ManagementIdentityCannotBeSentToRelayOrOtherAuthority()
    {
        await using var fixture = new Fixture(DevTunnelIdentityScheme.GitHub);
        using var client = new System.Net.Http.HttpClient(new DevTunnelManagementAuthenticationHandler(
            fixture.Broker, fixture.Request, new RecordingHandler(_ =>
                throw new InvalidOperationException("Identity must never reach this transport."))));
        await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() =>
            client.GetAsync("https://tunnel.euw.devtunnels.ms/", TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Executions);
        Assert.Empty(fixture.Registry.GetSnapshot());
    }

    private sealed class RecordingHandler(
        Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> callback) :
        System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }

    [Theory]
    [InlineData(DevTunnelIdentityScheme.GitHub)]
    [InlineData(DevTunnelIdentityScheme.Entra)]
    [InlineData(DevTunnelIdentityScheme.OAuth)]
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
            this.OAuth = new(CredentialExecutorKind.OAuth, lease => new OAuthCredentialAccessResult { AccessToken = lease });
            var oauthRequest = new OAuthCredentialAccessRequest
            {
                Provider = new OAuthProviderDescriptor
                {
                    Authority = new Uri("https://provider.example/"),
                    AuthorizationEndpoint = new Uri("https://provider.example/authorize"),
                    TokenEndpoint = new Uri("https://provider.example/token"),
                    AccountEndpoint = new Uri("https://provider.example/account"),
                    Audience = "management",
                    AccountIdentifierClaim = OAuthAccountIdentifierClaim.Subject,
                },
                ClientAuthentication = new PublicOAuthClient { Client = new() { Value = "test-client" } },
                Scopes = [new() { Value = "management" }],
                Credential = entraRequest.Credential with { Provider = CredentialProviderKind.OAuth },
                Sources = entraRequest.Sources.Select(source => source with { Provider = CredentialProviderKind.OAuth }).ToArray(),
                Requestor = requestor,
                Use = template.Use,
                PresentationScope = AccessPresentationScope.Runtime,
            };
            DevTunnelIdentityRoute route = scheme switch
            {
                DevTunnelIdentityScheme.GitHub => new GitHubDevTunnelIdentityRoute { Credential = gitHubRequest },
                DevTunnelIdentityScheme.Entra => new EntraDevTunnelIdentityRoute { Credential = entraRequest },
                DevTunnelIdentityScheme.OAuth => new OAuthDevTunnelIdentityRoute { Credential = oauthRequest },
                _ => new AnonymousDevTunnelIdentityRoute(),
            };
            this.Request = DevTunnelIdentityCredentialAccessRequest.Create(
                route, requestor, template.Use with { Kind = CredentialUseKind.Tunnel }, AccessPresentationScope.Bootstrap);
            this.Policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "policy.json")));
            var executors = new CredentialAccessExecutorRegistry();
            executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(this.GitHub);
            executors.Add<EntraCredentialAccessRequest, EntraCredentialAccessResult>(this.Entra);
            executors.Add<OAuthCredentialAccessRequest, OAuthCredentialAccessResult>(this.OAuth);
            executors.Add<DevTunnelIdentityCredentialAccessRequest, DevTunnelIdentityCredentialAccessResult>(
                new DevTunnelIdentityCredentialAccessExecutor(this.GitHub, this.Entra, this.OAuth));
            this.Broker = new AccessBroker(this.Policy, executors, new ProviderAcquisitionCoordinator(), this.Registry);
        }

        public DevTunnelIdentityCredentialAccessRequest Request { get; }
        public AccessBroker Broker { get; }
        public ReleaseAuthorizationPolicy Policy { get; }
        public UserAuthorizationRegistry Registry { get; } = new();
        public DependencyExecutor<GitHubCredentialAccessRequest, GitHubCredentialAccessResult> GitHub { get; }
        public DependencyExecutor<EntraCredentialAccessRequest, EntraCredentialAccessResult> Entra { get; }
        public DependencyExecutor<OAuthCredentialAccessRequest, OAuthCredentialAccessResult> OAuth { get; }
        public int Executions => this.GitHub.Executions + this.Entra.Executions + this.OAuth.Executions;
        public AccessRequestor? LastRequestor => this.GitHub.LastRequestor ?? this.Entra.LastRequestor ?? this.OAuth.LastRequestor;
        public CredentialUseDescriptor? LastUse => this.GitHub.LastUse ?? this.Entra.LastUse ?? this.OAuth.LastUse;

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
