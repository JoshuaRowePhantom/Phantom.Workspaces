using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Services.Mcp;

namespace Phantom.Workspaces.Tests.Access;

public sealed class OAuthCredentialAccessExecutorTests
{
    [Fact]
    public async Task DynamicRegistry_RegistersRealProviderAfterBrokerHasAlreadyDispatched()
    {
        await using var fixture = new Fixture();
        var first = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var firstResult = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(await first).Result;
        var template = fixture.Request("dynamic");
        var provider = template.Provider with { Audience = "https://second-resource.test/" };
        var unregistered = template with { Provider = provider };
        Assert.Empty((await fixture.Executor.InspectAsync(unregistered, TestContext.Current.CancellationToken)).Sources);
        var request = fixture.Providers.CreateRequest(
            provider, template.ClientAuthentication, template.Scopes,
            template.Requestor, template.Use, template.PresentationScope);
        Assert.NotEqual(template.Credential.LogicalIdentifier, request.Credential.LogicalIdentifier);
        var reads = fixture.Store.Reads;
        var inspection = await fixture.Executor.InspectAsync(request, TestContext.Current.CancellationToken);
        Assert.Single(inspection.Sources);
        Assert.Equal(reads, fixture.Store.Reads);
        Assert.Equal(1, fixture.Browser.Opens);
        var second = fixture.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var result = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(await second).Result;
        Assert.Equal(2, fixture.Store.Writes);
        Assert.Equal(2, fixture.Browser.Opens);
        Assert.Equal(2, (await fixture.Executor.ListAsync(TestContext.Current.CancellationToken)).Count);
        Assert.IsType<SensitiveValueUseSucceeded<int>>(
            await firstResult.AccessToken.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DynamicRegistry_ConcurrentRegistrationIsMetadataOnlyAndDefensivelyCopiesScopes()
    {
        await using var fixture = new Fixture();
        var scopes = new List<OAuthScope> { new() { Value = "write" }, new() { Value = "read" } };
        var registrations = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(
            () => fixture.Providers.RegisterProvider(fixture.Configuration.Provider, fixture.Configuration.ClientAuthentication, scopes),
            TestContext.Current.CancellationToken)));
        Assert.Single(registrations.Select(registration => registration.Credential.LogicalIdentifier).Distinct());
        Assert.Single(registrations.Select(registration => registration.Source).Distinct());
        scopes.Clear();
        Assert.All(registrations, registration => Assert.Equal(new[] { "read", "write" }, registration.Scopes.Select(scope => scope.Value)));
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Store.Writes);
        Assert.Equal(0, fixture.Browser.Opens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrRejectedPermit_CannotAcquireOrReadMaterial(bool rejected)
    {
        await using var fixture = new Fixture();
        await using var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore());
        await using var coordinator = new ProviderAcquisitionCoordinator();
        var request = fixture.Request();
        var inspection = await fixture.Executor.InspectAsync(request, TestContext.Current.CancellationToken);
        inspection = inspection with { SelectedSource = request.Sources[0].Identifier };
        var policyRequest = new ReleasePolicyRequest
        {
            RequestIdentifier = new() { Value = Guid.NewGuid() },
            RequestGeneration = 1,
            Credential = request.Credential,
            SourceIdentifier = request.Sources[0].Identifier,
            RequestorIdentifier = request.Requestor.Identifier,
            Use = request.Use,
        };
        var authorization = await policy.CommitAsync(new()
        {
            Request = policyRequest,
            Scope = ReleaseMemoryScopeKind.AllowOnce,
        }, TestContext.Current.CancellationToken);
        var generation = Guid.NewGuid();
        var context = new ExecutionContext(request, policyRequest, policy, coordinator, generation);
        var invalid = rejected ? new ProviderInteractionPermit(
            policyRequest.RequestIdentifier, 1, CredentialExecutorKind.OAuth, generation,
            inspection.AcquisitionKeys[policyRequest.SourceIdentifier] with { Account = new() { Digest = new string('d', 64) } },
            () => true) : null;
        var outcome = await fixture.Executor.ExecuteAsync(request, inspection, authorization, invalid, context, TestContext.Current.CancellationToken);
        if (rejected) { Assert.IsType<CredentialExecutionDenied<OAuthCredentialAccessResult>>(outcome); }
        else { Assert.IsType<CredentialExecutionInteractionRequired<OAuthCredentialAccessResult>>(outcome); }
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Browser.Opens);
    }

    [Fact]
    public async Task MetadataHelpers_BindEndpointsScopesClientAndSecretReferenceWithoutDelimiterCollisions()
    {
        await using var fixture = new Fixture();
        var provider = fixture.Configuration.Provider;
        var first = new ConfidentialOAuthClient { Client = new() { Value = "a:b" }, ClientSecret = new() { Name = "c" } };
        var second = new ConfidentialOAuthClient { Client = new() { Value = "a" }, ClientSecret = new() { Name = "b:c" } };
        Assert.NotEqual(OAuthCredentialAccessExecutor.DescribeCredential(provider, first, fixture.Configuration.Scopes),
            OAuthCredentialAccessExecutor.DescribeCredential(provider, second, fixture.Configuration.Scopes));
        Assert.NotEqual(OAuthCredentialAccessExecutor.CreateSourceIdentifier(provider, first, fixture.Configuration.Scopes),
            OAuthCredentialAccessExecutor.CreateSourceIdentifier(provider, second, fixture.Configuration.Scopes));
        Assert.NotEqual(OAuthCredentialAccessExecutor.CreateSourceIdentifier(provider, first, fixture.Configuration.Scopes),
            OAuthCredentialAccessExecutor.CreateSourceIdentifier(provider with { TokenEndpoint = new("https://other.test/token") }, first, fixture.Configuration.Scopes));
    }

    [Fact]
    public async Task Inspection_IsMetadataOnlyAndDoesNotAcquireOrReadMaterial()
    {
        await using var fixture = new Fixture();
        var inspection = await fixture.Executor.InspectAsync(fixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(CredentialSourceUsability.AcquisitionRequired, Assert.Single(inspection.Sources).Usability);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Empty(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        Assert.False((object)fixture.Executor is IProviderGrantAdministration);
    }

    [Fact]
    public async Task Perform_UsesRealPkceAndCommitsActualCacheBeforePublication()
    {
        await using var fixture = new Fixture();
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, Assert.Single(fixture.Registry.GetSnapshot()).PrimaryAction);
        Assert.Empty(fixture.Events);
        await fixture.AllowAsync();
        await using var result = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(await acquisition).Result;
        Assert.True(fixture.Events.IndexOf("release") < fixture.Events.IndexOf("browser"));
        Assert.True(fixture.Events.IndexOf("release") < fixture.Events.IndexOf("token"));
        Assert.True(fixture.Events.IndexOf("account") < fixture.Events.IndexOf("store"));
        Assert.True(fixture.Events.IndexOf("store") < fixture.Events.IndexOf("finalize"));
        Assert.Equal(1, fixture.Store.Writes);
        Assert.True(fixture.Server.SawValidVerifier);
        Assert.True(fixture.Server.SawAudience);
        Assert.NotNull(result.ExpiresOn);
        var used = await result.AccessToken.UseAsync((value, _) => ValueTask.FromResult(value == "private-access-1"), TestContext.Current.CancellationToken);
        Assert.True(Assert.IsType<SensitiveValueUseSucceeded<bool>>(used).Result);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(fixture.Registry.GetSnapshot()));
        await fixture.Browser.Callback!;
    }

    [Fact]
    public async Task Deny_DoesNotTouchProviderBrowserSecretOrCache()
    {
        await using var fixture = new Fixture(confidential: true);
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        var row = Assert.Single(fixture.Registry.GetSnapshot());
        await fixture.Registry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);
        Assert.IsType<AccessDenied<OAuthCredentialAccessResult>>(await acquisition);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Secret.Reads);
        Assert.Equal(0, fixture.Store.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfidentialClient_UsesActualTypedSecretDependencyAndRejectsCycles(bool cycle)
    {
        await using var fixture = new Fixture(confidential: true, cycle: cycle);
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        var nested = await fixture.WaitForPendingAsync(acquisition);
        Assert.Equal(0, fixture.Secret.Reads);
        Assert.Equal(0, fixture.Browser.Opens);
        await fixture.AllowAsync(nested);
        var outcome = await acquisition;
        if (cycle)
        {
            Assert.Equal(AccessErrorCode.DependencyCycle, Assert.IsType<AccessFailed<OAuthCredentialAccessResult>>(outcome).ErrorCode);
            Assert.Equal(0, fixture.Secret.Reads);
            Assert.Equal(0, fixture.Browser.Opens);
        }
        else
        {
            await using var result = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(outcome).Result;
            Assert.Equal(1, fixture.Secret.Reads);
            Assert.True(fixture.Server.SawClientSecret);
            Assert.Equal(1, fixture.Browser.Opens);
        }
        Assert.Equal(2, fixture.Registry.GetSnapshot().Count);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(fixture.Registry.GetSnapshot()));
    }

    [Fact]
    public async Task IndependentResults_ReuseExactGenerationAndDisposeIndependently()
    {
        await using var fixture = new Fixture();
        var firstTask = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var first = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(await firstTask).Result;
        var reads = fixture.Store.Reads;
        var calls = fixture.Events.Count;
        var inspection = await fixture.Executor.InspectAsync(fixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(reads, fixture.Store.Reads);
        Assert.Equal(calls, fixture.Events.Count);
        Assert.NotNull(inspection.CachedGenerations.Single().Value.AccountIdentifier);
        var secondTask = fixture.Broker.AcquireAsync(fixture.Request("second"), TestContext.Current.CancellationToken);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Allow, fixture.Registry.GetSnapshot().Last().PrimaryAction);
        await fixture.AllowAsync();
        await using var second = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(await secondTask).Result;
        Assert.Equal(1, fixture.Browser.Opens);
        Assert.Equal(1, fixture.Store.Writes);
        await first.DisposeAsync();
        Assert.IsType<SensitiveValueUseFailed<int>>(await first.AccessToken.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseSucceeded<int>>(await second.AccessToken.UseAsync((_, _) => ValueTask.FromResult(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApprovedFollower_KeepsSharedAcquisitionAliveAfterLeadingCallerCancels()
    {
        await using var fixture = new Fixture();
        fixture.Browser.Block = true;
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Broker.AcquireAsync(fixture.Request(), cancellation.Token);
        await fixture.AllowAsync();
        await fixture.Browser.Opened.Task.WaitAsync(TestContext.Current.CancellationToken);
        var follower = fixture.Broker.AcquireAsync(fixture.Request("follower"), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await fixture.Acquisitions.Joined.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.IsType<AccessCancelled<OAuthCredentialAccessResult>>(await first);
        fixture.Browser.Continue.TrySetResult();
        await using var result = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(await follower).Result;
        Assert.Equal(1, fixture.Browser.Opens);
        Assert.Equal(1, fixture.Store.Writes);
        Assert.Equal(2, fixture.Events.Count(value => value == "release"));
        Assert.IsType<SensitiveValueUseSucceeded<int>>(
            await result.AccessToken.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiryAndRotation_RequireExplicitRenewalWithoutSubstitutingOldLease()
    {
        await using var fixture = new Fixture();
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var old = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(await acquisition).Result;
        var before = await fixture.Executor.InspectGenerationAsync(fixture.Request(), TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(
            await old.AccessToken.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Browser.Opens);
        var renewal = fixture.Broker.AcquireAsync(fixture.Request("renew"), TestContext.Current.CancellationToken);
        var row = fixture.Registry.GetSnapshot().Last(snapshot => snapshot.State == UserAuthorizationState.Pending);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
        await fixture.AllowAsync(row);
        await using var current = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(await renewal).Result;
        var after = await fixture.Executor.InspectGenerationAsync(fixture.Request(), TestContext.Current.CancellationToken);
        Assert.NotEqual(before!.Generation, after!.Generation);
        Assert.Equal(before.AccountIdentifier, after.AccountIdentifier);
        Assert.Equal(2, fixture.Browser.Opens);
        Assert.IsNotType<SensitiveValueUseSucceeded<int>>(
            await old.AccessToken.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        Assert.IsType<SensitiveValueUseSucceeded<int>>(
            await current.AccessToken.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StableAccountPinMismatch_DoesNotWriteOrExportAnotherAccount()
    {
        await using var fixture = new Fixture();
        var request = fixture.Request();
        request = request with { Credential = request.Credential with { AccountIdentifier = new() { Digest = new string('f', 64) } } };
        var acquisition = fixture.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<AccessFailed<OAuthCredentialAccessResult>>(await acquisition).ErrorCode);
        Assert.Equal(0, fixture.Store.Writes);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(fixture.Registry.GetSnapshot()));
    }

    [Theory]
    [InlineData("{\"name\":\"private-display-name\"}")]
    [InlineData("{\"sub\":null}")]
    [InlineData("{\"sub\":123}")]
    public async Task MissingAuthoritativePrincipal_FailsExplicitlyWithoutFabrication(string accountResponse)
    {
        await using var fixture = new Fixture();
        fixture.Server.AccountResponse = accountResponse;
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        var failed = Assert.IsType<AccessFailed<OAuthCredentialAccessResult>>(await acquisition);
        Assert.Equal(AccessErrorCode.ProviderFailure, failed.ErrorCode);
        Assert.Equal(0, fixture.Store.Writes);
        Assert.DoesNotContain("private-", failed.SafeMessage);
    }

    [Fact]
    public async Task TrustedNumericId_IsStablePrincipalEvidenceWhenConfigured()
    {
        await using var fixture = new Fixture(identifierClaim: true);
        fixture.Server.AccountResponse = "{\"id\":12345}";
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var result = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(await acquisition).Result;
        var metadata = await fixture.Executor.InspectGenerationAsync(fixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.OAuth, "https://authority.test/\n12345"),
            metadata!.AccountIdentifier);
    }

    [Fact]
    public async Task CacheAdministration_StaleDeleteCannotEraseRotatedGeneration()
    {
        await using var fixture = new Fixture();
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var old = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(await acquisition).Result;
        var listed = Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        var renewal = fixture.Broker.AcquireAsync(fixture.Request("renew"), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var current = Assert.IsType<AccessSucceeded<OAuthCredentialAccessResult>>(await renewal).Result;
        var stale = await Assert.ThrowsAsync<CredentialAdministrationException>(
            () => fixture.Executor.DeleteAsync(listed.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, stale.Failure);
        Assert.Equal(CredentialGenerationInvalidationOutcome.NoLongerCurrent,
            await fixture.Executor.InvalidateGenerationAsync(fixture.Request(), listed.Generation, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Store.Deletes);
        var actual = Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        await fixture.Executor.DeleteAsync(actual.Identifier, TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Store.Deletes);
        Assert.IsType<SensitiveValueUseReacquisitionRequired<int>>(
            await current.AccessToken.UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(actual));
    }

    [Fact]
    public async Task PrivateIdentity_UsesParentPerformAndNeverExportsIdentityOrAddsIdentityRelease()
    {
        await using var fixture = new Fixture(derived: true);
        var request = fixture.Derived!.Request(fixture.Request());
        var acquisition = fixture.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var result = Assert.IsType<AccessSucceeded<DerivedResult>>(await acquisition).Result;
        Assert.Equal(42, result.Value);
        Assert.Single(fixture.Registry.GetSnapshot());
        Assert.Equal(1, fixture.Events.Count(value => value == "release"));
        Assert.Equal(1, fixture.Events.Count(value => value == "finalize"));
        Assert.IsType<OAuthGenerationUseDenied<int>>(await fixture.Derived.LateUse!(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Store.Writes);
    }

    [Fact]
    public async Task ProviderFailure_IsSanitizedAndActualFailedWriteCannotPublishReceipt()
    {
        await using var fixture = new Fixture();
        fixture.Store.FailWrite = true;
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        var failed = Assert.IsType<AccessFailed<OAuthCredentialAccessResult>>(await acquisition);
        Assert.True(failed.CanRetry);
        Assert.DoesNotContain("private-", failed.SafeMessage);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(fixture.Registry.GetSnapshot()));
        Assert.Null(await fixture.Executor.InspectGenerationAsync(fixture.Request(), TestContext.Current.CancellationToken));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal List<string> Events { get; } = [];
        internal MemoryStore Store { get; }
        internal Server Server { get; }
        internal Browser Browser { get; }
        internal SecretSource Secret { get; } = new();
        internal Clock Clock { get; } = new();
        internal OAuthProviderRegistry Providers { get; }
        internal OAuthProviderRegistration Configuration { get; }
        internal OAuthCredentialAccessExecutor Executor { get; }
        internal DerivedExecutor? Derived { get; }
        internal AccessBroker Broker { get; }
        internal UserAuthorizationRegistry Registry { get; } = new();
        internal ObservedCoordinator Acquisitions { get; } = new();

        internal Fixture(bool confidential = false, bool cycle = false, bool derived = false, bool identifierClaim = false)
        {
            this.Store = new MemoryStore(this.Events);
            this.Server = new Server(this.Events);
            this.Browser = new Browser(this.Events, this.Server);
            this.Providers = new OAuthProviderRegistry(this.Store, RedirectUri, () => new Handler(this.Server), this.Clock);
            this.Executor = new OAuthCredentialAccessExecutor(this.Providers, this.Browser);
            this.Configuration = this.Providers.RegisterProvider(
                new OAuthProviderDescriptor
                {
                    Authority = new("https://authority.test/"),
                    AuthorizationEndpoint = new("https://authority.test/authorize"),
                    TokenEndpoint = new("https://authority.test/token"),
                    AccountEndpoint = new("https://authority.test/account"),
                    Audience = "https://resource.test/",
                    AccountIdentifierClaim = identifierClaim ? OAuthAccountIdentifierClaim.Identifier : OAuthAccountIdentifierClaim.Subject,
                },
                confidential ? new ConfidentialOAuthClient
                {
                    Client = new() { Value = "private-client" },
                    ClientSecret = new() { Name = "private-secret-name" },
                } : new PublicOAuthClient { Client = new() { Value = "private-client" } },
                [new() { Value = "read" }]);
            var executors = new CredentialAccessExecutorRegistry();
            executors.Add<OAuthCredentialAccessRequest, OAuthCredentialAccessResult>(this.Executor);
            executors.Add<StoredSecretAccessRequest, StoredSecretAccessResult>(cycle
                ? new CycleExecutor(this.Request()) : new StoredSecretAccessExecutor(this.Secret));
            if (derived)
            {
                this.Derived = new DerivedExecutor(this.Executor);
                executors.Add<DerivedRequest, DerivedResult>(this.Derived);
            }
            this.Broker = new AccessBroker(new RecordingPolicy(this.Events), executors, this.Acquisitions, this.Registry);
        }

        internal OAuthCredentialAccessRequest Request(string purpose = "first")
        {
            var requestor = new AccessRequestor
            {
                Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Tool, "generic-oauth-test"),
                Kind = AccessRequestorKind.Tool,
                SafeDisplayName = "OAuth use",
            };
            var lineage = new CredentialUseLineage();
            return this.Providers.CreateRequest(
                this.Configuration.Provider,
                this.Configuration.ClientAuthentication,
                this.Configuration.Scopes,
                requestor,
                new CredentialUseDescriptor
                {
                    Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Application, lineage, requestor.Identifier, purpose),
                    Kind = CredentialUseKind.Application,
                    Lineage = lineage,
                    SafeDisplayName = "Use OAuth credential",
                    MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
                },
                AccessPresentationScope.Runtime);
        }

        internal Task AllowAsync(UserAuthorizationSnapshot? row = null)
        {
            row ??= this.Registry.GetSnapshot().Last(snapshot => snapshot.State == UserAuthorizationState.Pending);
            return this.Registry.ActAsync(new ApplyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
                Memory = row.SelectedMemory,
                Source = row.SelectedSource,
            }, TestContext.Current.CancellationToken);
        }

        internal async Task<UserAuthorizationSnapshot> WaitForPendingAsync(Task acquisition)
        {
            var pending = new TaskCompletionSource<UserAuthorizationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Refresh(object? sender, UserAuthorizationChangedEventArgs arguments)
            {
                var row = this.Registry.GetSnapshot().FirstOrDefault(snapshot => snapshot.State == UserAuthorizationState.Pending);
                if (row is not null) { pending.TrySetResult(row); }
            }
            this.Registry.Changed += Refresh;
            try
            {
                Refresh(null, null!);
                var completed = await Task.WhenAny(pending.Task, acquisition)
                    .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                Assert.Same(pending.Task, completed);
                return await pending.Task;
            }
            finally { this.Registry.Changed -= Refresh; }
        }
        public ValueTask DisposeAsync() => this.Broker.DisposeAsync();
        private static Uri RedirectUri()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            return new($"http://localhost:{port}/");
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => this.now;
        internal void Advance(TimeSpan amount) => this.now += amount;
    }

    private sealed class ExecutionContext(
        OAuthCredentialAccessRequest request,
        ReleasePolicyRequest policyRequest,
        IReleaseAuthorizationPolicy policy,
        IProviderAcquisitionCoordinator coordinator,
        Guid generation) : ICredentialExecutionContext
    {
        public UserAuthorizationRequestIdentifier RequestIdentifier => policyRequest.RequestIdentifier;
        public long Revision => 1;
        public Guid AcquisitionGeneration => generation;
        public AccessRequestor Requestor => request.Requestor;
        public CredentialUseDescriptor Use => request.Use;
        public AccessPresentationScope PresentationScope => request.PresentationScope;
        public ReleasePolicyRequest PolicyRequest => policyRequest;
        public IReleaseAuthorizationPolicy Policy => policy;
        public ValueTask<AccessOutcome<TResult>> AcquireDependencyAsync<TResult>(AccessRequest<TResult> dependency, CancellationToken cancellationToken)
            where TResult : IAccessResult => throw new InvalidOperationException("Unexpected dependency.");
        public ValueTask<ProviderAcquisitionOutcome> ExecuteProviderAcquisitionAsync(
            IProviderAcquisitionOperation operation, ProviderInteractionPermit permit, CancellationToken cancellationToken) =>
            coordinator.ExecuteOrJoinAsync(operation, permit, cancellationToken);
        public ValueTask<ProviderAcquisitionOutcome> ExecutePrivateProviderDependencyAsync(
            IProviderAcquisitionOperation operation, ProviderInteractionPermit parentPermit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected private dependency.");
        public ICredentialLeaseContext CreateLeaseContext(ICredentialGenerationValidator generationValidator) =>
            throw new InvalidOperationException("Unexpected lease.");
    }

    private sealed class Browser(List<string> events, Server server) : ISystemBrowserLauncher
    {
        internal int Opens { get; private set; }
        internal bool Block { get; set; }
        internal Task? Callback { get; private set; }
        internal TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open(Uri uri)
        {
            this.Opens++;
            events.Add("browser");
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            server.Challenge = query["code_challenge"];
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal("code", query["response_type"]);
            this.Callback = CallbackAsync(new UriBuilder(query["redirect_uri"]!)
            {
                Query = "code=private-code&state=" + Uri.EscapeDataString(query["state"]!),
            }.Uri);
            this.Opened.TrySetResult();
        }
        private async Task CallbackAsync(Uri callback)
        {
            if (this.Block) { await this.Continue.Task.WaitAsync(TestContext.Current.CancellationToken); }
            using var client = new HttpClient();
            using var response = await client.GetAsync(callback, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    private sealed class Handler(Server server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            server.SendAsync(request, cancellationToken);
    }

    private sealed class ObservedCoordinator : IProviderAcquisitionCoordinator
    {
        private readonly ProviderAcquisitionCoordinator coordinator = new();
        private int participants;
        internal TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
            IProviderAcquisitionOperation operation, ProviderInteractionPermit permit, CancellationToken cancellationToken)
        {
            var pending = this.coordinator.ExecuteOrJoinAsync(operation, permit, cancellationToken);
            if (Interlocked.Increment(ref this.participants) == 2) { this.Joined.TrySetResult(); }
            return pending;
        }
        public ValueTask DisposeAsync() => this.coordinator.DisposeAsync();
    }

    private sealed class Server(List<string> events)
    {
        internal string? Challenge { get; set; }
        internal bool SawValidVerifier { get; private set; }
        internal bool SawClientSecret { get; private set; }
        internal bool SawAudience { get; private set; }
        internal int TokenCalls { get; private set; }
        internal string AccountResponse { get; set; } = "{\"sub\":\"private-stable-principal\"}";
        internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                events.Add("token");
                this.TokenCalls++;
                var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                this.SawValidVerifier = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
                    Encoding.ASCII.GetBytes(form["code_verifier"]!))).TrimEnd('=').Replace('+', '-').Replace('/', '_') == this.Challenge;
                this.SawClientSecret = form["client_secret"] == "private-client-secret";
                this.SawAudience = form["resource"] == "https://resource.test/";
                Assert.Equal("authorization_code", form["grant_type"]);
                return Json($$$"""{"access_token":"private-access-{{{this.TokenCalls}}}","refresh_token":"private-refresh","token_type":"Bearer","expires_in":3600}""");
            }
            if (request.RequestUri.AbsolutePath == "/account")
            {
                events.Add("account");
                Assert.StartsWith("private-access-", request.Headers.Authorization?.Parameter);
                return Json(this.AccountResponse);
            }
            return new(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class MemoryStore(List<string> events) : IPlatformSecretStore
    {
        private readonly Dictionary<string, SecureString> values = [];
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal int Deletes { get; private set; }
        internal bool FailWrite { get; set; }
        public Task<SecureString?> ReadAsync(string name, CancellationToken cancellationToken)
        {
            this.Reads++;
            return Task.FromResult(this.values.TryGetValue(name, out var value) ? value.Copy() : null);
        }
        public Task WriteAsync(string name, SecureString value, CancellationToken cancellationToken)
        {
            this.Writes++;
            if (this.FailWrite) { throw new IOException("private-token private-path"); }
            if (this.values.Remove(name, out var old)) { old.Dispose(); }
            this.values[name] = value.Copy();
            events.Add("store");
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            this.Deletes++;
            if (this.values.Remove(name, out var value)) { value.Dispose(); }
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(this.values.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray());
    }

    private sealed class SecretSource : IStoredSecretAccessSource
    {
        internal int Reads { get; private set; }
        public ValueTask<bool> ExistsAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask<char[]?> ReadAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken)
        {
            this.Reads++;
            return ValueTask.FromResult<char[]?>("private-client-secret".ToCharArray());
        }
    }

    private sealed class CycleExecutor(OAuthCredentialAccessRequest original) : ICredentialAccessExecutor<StoredSecretAccessRequest, StoredSecretAccessResult>
    {
        public CredentialExecutorKind Kind => CredentialExecutorKind.StoredSecret;
        public ValueTask<CredentialInspection> InspectAsync(StoredSecretAccessRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = request.Credential,
                Sources = request.Sources.Select(source => source with { Usability = CredentialSourceUsability.Usable }).ToArray(),
            });
        public async ValueTask<CredentialExecutionOutcome<StoredSecretAccessResult>> ExecuteAsync(
            StoredSecretAccessRequest request, CredentialInspection inspection, ReleaseAuthorization authorization,
            ProviderInteractionPermit? interactionPermit, ICredentialExecutionContext context, CancellationToken cancellationToken)
        {
            var failure = Assert.IsType<AccessFailed<OAuthCredentialAccessResult>>(await context.AcquireDependencyAsync(original, cancellationToken));
            return new CredentialExecutionFailed<StoredSecretAccessResult> { ErrorCode = failure.ErrorCode, CanRetry = false };
        }
    }

    private sealed record DerivedRequest : CredentialAccessRequest<DerivedResult>
    {
        internal required OAuthCredentialAccessRequest Identity { get; init; }
    }
    private sealed class DerivedResult : ICredentialAccessResult
    {
        internal required int Value { get; init; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class DerivedExecutor(IOAuthPrivateCredentialProvider identity) : ICredentialAccessExecutor<DerivedRequest, DerivedResult>
    {
        internal Func<CancellationToken, ValueTask<OAuthGenerationUseOutcome<int>>>? LateUse { get; private set; }
        public CredentialExecutorKind Kind => CredentialExecutorKind.DevTunnelConnect;
        internal DerivedRequest Request(OAuthCredentialAccessRequest oauth) => new()
        {
            Identity = oauth,
            Credential = oauth.Credential with
            {
                Provider = CredentialProviderKind.DevTunnel,
                Kind = CredentialKind.ConnectToken,
                LogicalIdentifier = new() { Digest = new string('c', 64) },
            },
            Requestor = oauth.Requestor,
            Use = oauth.Use,
            PresentationScope = oauth.PresentationScope,
            Sources = oauth.Sources.Select(source => source with { Provider = CredentialProviderKind.DevTunnel }).ToArray(),
        };
        public ValueTask<CredentialInspection> InspectAsync(DerivedRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = request.Credential,
                Sources = request.Sources,
                AcquisitionKeys = new Dictionary<CredentialSourceIdentifier, ProviderAcquisitionKey>
                {
                    [request.Sources[0].Identifier] = Key(request),
                },
            });
        public async ValueTask<CredentialExecutionOutcome<DerivedResult>> ExecuteAsync(
            DerivedRequest request, CredentialInspection inspection, ReleaseAuthorization authorization,
            ProviderInteractionPermit? interactionPermit, ICredentialExecutionContext context, CancellationToken cancellationToken)
        {
            if (interactionPermit is null) { return new CredentialExecutionInteractionRequired<DerivedResult>(); }
            CredentialAccountIdentifier? account = null;
            var count = 0;
            var acquired = await context.ExecuteProviderAcquisitionAsync(new Operation(context, Key(request), async (commit, token) =>
            {
                var receipt = Assert.IsType<ProviderAcquisitionSucceeded>(
                    await identity.AcquirePrivateAsync(request.Identity, context, commit.InteractionPermit, token));
                var metadata = await identity.InspectGenerationAsync(request.Identity, token);
                account = metadata!.AccountIdentifier;
                Assert.IsType<OAuthGenerationUseUnavailable<int>>(await identity.UseGenerationAsync<int>(
                    request.Identity, new() { Value = Guid.NewGuid() }, account!, context, commit.InteractionPermit,
                    (_, _) => throw new InvalidOperationException("A stale generation must not invoke the callback."), token));
                Assert.IsType<OAuthGenerationUseUnavailable<int>>(await identity.UseGenerationAsync<int>(
                    request.Identity, receipt.CommittedGeneration, new() { Digest = new string('f', 64) }, context, commit.InteractionPermit,
                    (_, _) => throw new InvalidOperationException("A different account must not invoke the callback."), token));
                this.LateUse = cancellation => identity.UseGenerationAsync(
                    request.Identity, receipt.CommittedGeneration, account!, context, commit.InteractionPermit,
                    (_, _) => ValueTask.FromResult(1), cancellation);
                var used = await identity.UseGenerationAsync(
                    request.Identity, receipt.CommittedGeneration, account!, context, commit.InteractionPermit,
                    (value, _) =>
                    {
                        Assert.StartsWith("private-access-", value);
                        return ValueTask.FromResult(42);
                    }, token);
                var actual = Assert.IsType<OAuthGenerationUseSucceeded<int>>(used);
                return await commit.CommitAsync(new() { Value = Guid.NewGuid() }, _ =>
                {
                    count = actual.Result;
                    return ValueTask.CompletedTask;
                });
            }), interactionPermit, cancellationToken);
            Assert.IsType<ProviderAcquisitionSucceeded>(acquired);
            return new CredentialExecutionReady<DerivedResult>
            {
                ResolvedIdentity = new()
                {
                    LogicalIdentifier = request.Credential.LogicalIdentifier,
                    SourceIdentifier = inspection.SelectedSource!,
                    AccountIdentifier = account!,
                    RequestIdentifier = context.PolicyRequest!.RequestIdentifier,
                    RequestGeneration = context.PolicyRequest.RequestGeneration,
                },
                Materializer = new DerivedMaterializer(count),
            };
        }
        private static ProviderAcquisitionKey Key(DerivedRequest request) => new()
        {
            Configuration = request.Credential.LogicalIdentifier,
            Source = request.Sources[0].Identifier,
            Protocol = ProviderProtocolKind.TunnelConnect,
        };
    }
    private sealed class DerivedMaterializer(int value) : IAccessResultMaterializer<DerivedResult>
    {
        public ValueTask<DerivedResult> MaterializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new DerivedResult { Value = value });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Operation(
        ICredentialExecutionContext context,
        ProviderAcquisitionKey key,
        Func<ProviderAcquisitionCommitContext, CancellationToken, ValueTask<ProviderAcquisitionOutcome>> execute) : IProviderAcquisitionOperation
    {
        public ProviderAcquisitionKey CompatibilityKey => key;
        public UserAuthorizationRequestIdentifier RequestIdentifier => context.RequestIdentifier;
        public long Revision => context.Revision;
        public CredentialExecutorKind ExecutorKind => CredentialExecutorKind.DevTunnelConnect;
        public Guid AcquisitionGeneration => context.AcquisitionGeneration;
        public ValueTask<ProviderAcquisitionOutcome> ExecuteAsync(ProviderAcquisitionCommitContext commitContext, CancellationToken cancellationToken) =>
            execute(commitContext, cancellationToken);
    }
    private sealed class RecordingPolicy(List<string> events) : IReleaseAuthorizationPolicy, IAsyncDisposable
    {
        private readonly ReleaseAuthorizationPolicy policy = new(new ReleaseMemoryStore());
        public ValueTask<ReleasePolicyEvaluation> EvaluateAsync(ReleasePolicyRequest request, CancellationToken cancellationToken) =>
            this.policy.EvaluateAsync(request, cancellationToken);
        public async ValueTask<ReleaseAuthorization> CommitAsync(ReleasePolicyDecision decision, CancellationToken cancellationToken)
        {
            var result = await this.policy.CommitAsync(decision, cancellationToken);
            events.Add("release");
            return result;
        }
        public ValueTask<ReleaseAuthorizationValidation> ValidateAsync(ReleaseAuthorizationIdentifier identifier, ReleasePolicyRequest request, CancellationToken cancellationToken) =>
            this.policy.ValidateAsync(identifier, request, cancellationToken);
        public async ValueTask FinalizeCredentialIdentityAsync(ReleaseAuthorizationIdentifier identifier, ResolvedCredentialIdentity identity, CancellationToken cancellationToken)
        {
            await this.policy.FinalizeCredentialIdentityAsync(identifier, identity, cancellationToken);
            events.Add("finalize");
        }
        public ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken) => this.policy.ListAsync(cancellationToken);
        public ValueTask ForgetAsync(ReleaseMemoryIdentifier identifier, CancellationToken cancellationToken) => this.policy.ForgetAsync(identifier, cancellationToken);
        public ValueTask DisposeAsync() => this.policy.DisposeAsync();
    }
}
