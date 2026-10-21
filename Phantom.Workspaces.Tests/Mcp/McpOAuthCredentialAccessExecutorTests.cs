using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Headers;
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

namespace Phantom.Workspaces.Tests.Mcp;

public sealed partial class McpOAuthCredentialAccessExecutorTests
{
    [Fact]
    public async Task McpOAuthExecutor_FirstUse_CommitsReleaseBeforeBrowser()
    {
        await using var fixture = new Fixture();
        var request = fixture.Request();
        var inspection = await fixture.Executor.InspectAsync(request, TestContext.Current.CancellationToken);
        Assert.Single(inspection.Sources);
        Assert.Equal(CredentialSourceUsability.AcquisitionRequired, inspection.Sources[0].Usability);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Store.Reads);
        var acquisition = fixture.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Events);
        await fixture.AllowAsync();
        await using var result = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await acquisition.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        Assert.True(fixture.Events.IndexOf("release") < fixture.Events.IndexOf("browser"));
        Assert.True(fixture.Events.IndexOf("release") < fixture.Events.IndexOf("http"));
        Assert.True(fixture.Events.IndexOf("store") < fixture.Events.IndexOf("finalize"));
        Assert.Equal(1, fixture.Browser.OpenCount);
        Assert.Equal(1, fixture.Store.Writes);
        Assert.Equal(1, fixture.Server.TokenRequests);
        Assert.True(fixture.Server.SawVerifier);
        await fixture.Browser.Callback!;
    }

    [Fact]
    public async Task McpOAuthExecutor_FirstUse_CompletesWithoutSecondReleasePrompt()
    {
        await using var fixture = new Fixture();
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var result = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await acquisition.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        var row = Assert.Single(fixture.Registry.GetSnapshot());
        Assert.Equal(UserAuthorizationState.Completed, row.State);
        Assert.Equal(1, fixture.Events.Count(value => value == "release"));
        Assert.Equal(1, fixture.Events.Count(value => value == "finalize"));
        Assert.IsType<CredentialUseSucceeded<int>>(await result.Use.UseAsync(_ => ValueTask.FromResult(42), TestContext.Current.CancellationToken));
        using var unbounded = new HttpRequestMessage(HttpMethod.Post, fixture.Configuration.Endpoint);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await result.Session.SendAsync(unbounded, TestContext.Current.CancellationToken));
        var use = await result.Use.UseAsync(async cancellation =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Configuration.Endpoint)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":7,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
            };
            using var response = await result.Session.SendAsync(request, cancellation);
            return response.StatusCode;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, Assert.IsType<CredentialUseSucceeded<HttpStatusCode>>(use).Result);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(fixture.Registry.GetSnapshot()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpOAuthExecutor_ConfidentialClientSecret_UsesTypedDependencyAndRejectsCycle(bool cycle)
    {
        await using var fixture = new Fixture(confidential: true, cycle: cycle);
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        var nested = await fixture.WaitForPendingAsync(acquisition);
        Assert.Equal(0, fixture.Browser.OpenCount);
        Assert.Equal(0, fixture.SecretSource.Reads);
        await fixture.AllowAsync(nested);
        var outcome = await acquisition.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        if (cycle)
        {
            Assert.Equal(AccessErrorCode.DependencyCycle, Assert.IsType<AccessFailed<McpCredentialUseResult>>(outcome).ErrorCode);
            Assert.Equal(0, fixture.Browser.OpenCount);
            Assert.Equal(0, fixture.SecretSource.Reads);
        }
        else
        {
            await using var result = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(outcome).Result;
            Assert.Equal(1, fixture.SecretSource.Reads);
            Assert.True(fixture.Server.SawClientSecret);
            Assert.Equal(1, fixture.Browser.OpenCount);
        }
        Assert.Equal(2, fixture.Registry.GetSnapshot().Count);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(fixture.Registry.GetSnapshot()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpOAuthExecutor_ApprovedFollowers_KeepIndependentReleaseAndUseCommittedCache()
    {
        await using var fixture = new Fixture();
        fixture.Browser.Block = true;
        var first = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await fixture.Browser.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var second = fixture.Broker.AcquireAsync(fixture.Request("second"), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await fixture.Acquisitions.Joined.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Registry.GetSnapshot().Count);
        Assert.False(second.IsCompleted);
        fixture.Browser.Continue.TrySetResult();
        await using var firstResult = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        await using var secondResult = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await second.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        Assert.Equal(1, fixture.Browser.OpenCount);
        Assert.Equal(1, fixture.Store.Writes);
        Assert.Equal(2, fixture.Events.Count(value => value == "release"));
        await firstResult.DisposeAsync();
        Assert.IsType<CredentialUseSucceeded<int>>(await secondResult.Use.UseAsync(_ => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        var cache = fixture.Executor.GetGenerationCache(fixture.Configuration.Identifier);
        var generation = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
            await cache.InvalidateGenerationAsync(generation.Generation, TestContext.Current.CancellationToken));
        Assert.IsType<CredentialUseReacquisitionRequired<int>>(
            await secondResult.Use.UseAsync(_ => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Browser.OpenCount);
    }

    [Fact]
    public async Task McpOAuthExecutor_LeadingRowCancelled_ApprovedFollowerKeepsPrivateAttemptAlive()
    {
        await using var fixture = new Fixture();
        fixture.Browser.Block = true;
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Broker.AcquireAsync(fixture.Request(), cancellation.Token);
        await fixture.AllowAsync();
        await fixture.Browser.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var second = fixture.Broker.AcquireAsync(fixture.Request("second"), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await fixture.Acquisitions.Joined.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.IsType<AccessCancelled<McpCredentialUseResult>>(await first);
        fixture.Browser.Continue.TrySetResult();
        await using var result = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await second.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        Assert.Equal(1, fixture.Browser.OpenCount);
        Assert.Equal(1, fixture.Store.Writes);
        Assert.IsType<CredentialUseSucceeded<int>>(await result.Use.UseAsync(_ => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task McpOAuthExecutor_ShutdownDuringBrowserWait_ClosesAttemptAndDoesNotCommit()
    {
        await using var fixture = new Fixture();
        fixture.Browser.Block = true;
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await fixture.Browser.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await fixture.Broker.DisposeAsync();
        Assert.IsType<AccessShuttingDown<McpCredentialUseResult>>(await acquisition);
        fixture.Browser.Continue.TrySetResult();
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Browser.Callback!);
        Assert.Equal(0, fixture.Store.Writes);
        Assert.Empty(fixture.Registry.GetSnapshot());
    }

    [Fact]
    public async Task McpOAuthExecutor_FixedUse_RejectsOtherOriginsAndRotatedGenerations()
    {
        await using var fixture = new Fixture();
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var result = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await acquisition.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await result.Use.UseAsync(async cancellation =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://other.test/mcp");
            using var response = await result.Session.SendAsync(request, cancellation);
            return 1;
        }, TestContext.Current.CancellationToken));
        await fixture.Configuration.Cache.StoreTokensAsync(new ModelContextProtocol.Authentication.TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "private-rotated",
            ExpiresIn = 3600,
            ObtainedAt = DateTimeOffset.UtcNow,
        }, TestContext.Current.CancellationToken);
        Assert.IsType<CredentialUseReacquisitionRequired<int>>(await result.Use.UseAsync<int>(
            _ => throw new InvalidOperationException("A rotated generation cannot run the old use."), TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Browser.OpenCount);
    }

    [Fact]
    public async Task McpOAuthExecutor_CachedUse_StillRequiresIndependentAllowWithoutBrowser()
    {
        await using var fixture = new Fixture();
        var first = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var original = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        var reads = fixture.Store.Reads;
        var second = fixture.Broker.AcquireAsync(fixture.Request("second"), TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        Assert.Equal(reads, fixture.Store.Reads);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Allow, (await fixture.WaitForPendingAsync(second)).PrimaryAction);
        await fixture.AllowAsync();
        await using var cached = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await second.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        Assert.Equal(1, fixture.Browser.OpenCount);
        Assert.Equal(1, fixture.Server.TokenRequests);
    }

    [Fact]
    public async Task McpOAuthExecutor_WriteFailureAndAccountMismatch_NeverPublishCallerUse()
    {
        foreach (var writeFailure in new[] { true, false })
        {
            await using var fixture = new Fixture();
            fixture.Store.FailWrite = writeFailure;
            var request = fixture.Request();
            if (!writeFailure)
            {
                request = request with
                {
                    Credential = request.Credential with { AccountIdentifier = new() { Digest = new string('e', 64) } },
                };
            }
            var task = fixture.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
            await fixture.AllowAsync();
            var failure = Assert.IsType<AccessFailed<McpCredentialUseResult>>(
                await task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.DoesNotContain("private-", failure.SafeMessage, StringComparison.Ordinal);
            Assert.Equal(writeFailure ? AccessErrorCode.PersistenceFailure : AccessErrorCode.IdentityMismatch, failure.ErrorCode);
            Assert.NotEqual(UserAuthorizationState.Completed, Assert.Single(fixture.Registry.GetSnapshot()).State);
        }
    }

    [Fact]
    public async Task McpOAuthExecutor_AccountSwitchAfterRenewal_RejectsRememberedAccountAndOldGeneration()
    {
        await using var fixture = new Fixture();
        fixture.Server.Subject = "principal-A";
        var request = fixture.Request() with
        {
            Use = fixture.Request().Use with { MemoryScopes = [ReleaseMemoryScopeKind.AllUses] },
        };
        var first = fixture.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var original = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        var accountA = CredentialIdentityFactory.CreateAccountIdentifier(
            CredentialProviderKind.McpOAuth, $"{fixture.Configuration.AuthorizationServer.AbsoluteUri}\nprincipal-A");
        var cache = fixture.Executor.GetGenerationCache(fixture.Configuration.Identifier);
        var originalGeneration = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation;
        Assert.Equal(CredentialGenerationInvalidationOutcome.Invalidated,
            await cache.InvalidateGenerationAsync(originalGeneration, TestContext.Current.CancellationToken));

        fixture.Server.Subject = "principal-B";
        fixture.Server.AccessToken = "private-renewed-access";
        var pinnedRequest = request with { Credential = request.Credential with { AccountIdentifier = accountA } };
        var renewal = fixture.Broker.AcquireAsync(pinnedRequest, TestContext.Current.CancellationToken);
        var renewalRow = await fixture.WaitForPendingAsync(renewal);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, renewalRow.PrimaryAction);
        Assert.Equal(1, fixture.Browser.OpenCount);
        await fixture.AllowAsync(renewalRow);
        var switched = Assert.IsType<AccessFailed<McpCredentialUseResult>>(
            await renewal.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        Assert.Equal(AccessErrorCode.IdentityMismatch, switched.ErrorCode);
        var rotatedGeneration = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation;
        Assert.NotEqual(originalGeneration, rotatedGeneration);
        Assert.IsType<CredentialUseReacquisitionRequired<int>>(await original.Use.UseAsync<int>(
            _ => throw new InvalidOperationException("An old account/generation must not execute."),
            TestContext.Current.CancellationToken));

        var accountB = CredentialIdentityFactory.CreateAccountIdentifier(
            CredentialProviderKind.McpOAuth, $"{fixture.Configuration.AuthorizationServer.AbsoluteUri}\nprincipal-B");
        var existingRows = fixture.Registry.GetSnapshot().Select(row => row.Identifier).ToHashSet();
        var otherAccount = fixture.Broker.AcquireAsync(request with
        {
            Credential = request.Credential with { AccountIdentifier = accountB },
        }, TestContext.Current.CancellationToken);
        var accountBRow = await fixture.WaitForPendingAsync(otherAccount, existingRows);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Allow, accountBRow.PrimaryAction);
        Assert.False(otherAccount.IsCompleted);
        await fixture.AllowAsync(accountBRow);
        await using var allowedB = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await otherAccount.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        Assert.IsType<CredentialUseSucceeded<int>>(await allowedB.Use.UseAsync(
            _ => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        Assert.Equal(2, fixture.Browser.OpenCount);
        Assert.Equal(2, fixture.Server.TokenRequests);
    }

    [Fact]
    public async Task McpOAuthExecutor_SamePrincipalRenewal_RotatesMaterialWithoutChangingAccount()
    {
        await using var fixture = new Fixture();
        fixture.Server.Subject = "principal-A";
        var request = fixture.Request();
        var first = fixture.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var original = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        var inspection = await fixture.Executor.InspectAsync(request, TestContext.Current.CancellationToken);
        var account = inspection.Credential.AccountIdentifier;
        Assert.NotNull(account);
        var cache = fixture.Executor.GetGenerationCache(fixture.Configuration.Identifier);
        var oldGeneration = (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation;
        await cache.InvalidateGenerationAsync(oldGeneration, TestContext.Current.CancellationToken);
        fixture.Server.AccessToken = "private-same-account-renewed";
        var renewal = fixture.Broker.AcquireAsync(request with
        {
            Credential = request.Credential with { AccountIdentifier = account },
        }, TestContext.Current.CancellationToken);
        var row = await fixture.WaitForPendingAsync(renewal);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
        Assert.Equal(1, fixture.Browser.OpenCount);
        await fixture.AllowAsync(row);
        await using var renewed = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await renewal.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        var after = await fixture.Executor.InspectAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(account, after.Credential.AccountIdentifier);
        Assert.NotEqual(oldGeneration, (await cache.InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation);
        Assert.IsType<CredentialUseReacquisitionRequired<int>>(await original.Use.UseAsync<int>(
            _ => throw new InvalidOperationException("A renewed token cannot be substituted into an old lease."),
            TestContext.Current.CancellationToken));
        Assert.IsType<CredentialUseSucceeded<int>>(await renewed.Use.UseAsync(
            _ => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task McpOAuthExecutor_MissingStablePrincipal_ReturnsTypedFailureWithoutFinalization()
    {
        await using var fixture = new Fixture();
        fixture.Server.Subject = null;
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        var outcome = Assert.IsType<AccessFailed<McpCredentialUseResult>>(
            await acquisition.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        Assert.Equal(AccessErrorCode.ProviderFailure, outcome.ErrorCode);
        Assert.DoesNotContain("finalize", fixture.Events);
        Assert.Equal(UserAuthorizationState.Failed, Assert.Single(fixture.Registry.GetSnapshot()).State);
        Assert.Equal(1, fixture.Server.TokenRequests);
    }

    [Fact]
    public async Task McpOAuthExecutor_DeniedRow_PerformsNoMaterialOrProviderWork()
    {
        await using var fixture = new Fixture();
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        var row = await fixture.WaitForPendingAsync(acquisition);
        await fixture.Registry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);
        Assert.IsType<AccessDenied<McpCredentialUseResult>>(await acquisition);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Store.Reads);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly string releaseMemoryDirectory = Path.Combine(
            Environment.CurrentDirectory, ".mcp-oauth-executor-tests", Guid.NewGuid().ToString("N"));
        internal List<string> Events { get; } = [];
        internal Store Store { get; }
        internal Server Server { get; }
        internal Browser Browser { get; }
        internal SecretSource SecretSource { get; } = new();
        internal McpOAuthProviderConfiguration Configuration { get; }
        internal McpOAuthCredentialRequestCatalog Catalog { get; }
        internal McpOAuthCredentialAccessExecutor Executor { get; }
        internal AccessBroker Broker { get; }
        internal UserAuthorizationRegistry Registry { get; } = new();
        internal ObservedAcquisitions Acquisitions { get; } = new();

        internal Fixture(
            bool confidential = false,
            bool cycle = false,
            Func<Server, HttpMessageHandler>? createHttpHandler = null,
            Func<ICredentialAccessExecutor<McpOAuthCredentialUseRequest, McpCredentialUseResult>,
                ICredentialAccessExecutor<McpOAuthCredentialUseRequest, McpCredentialUseResult>>? wrapMcpExecutor = null)
        {
            this.Store = new Store(this.Events);
            this.Server = new Server(this.Events);
            this.Browser = new Browser(this.Events);
            this.Configuration = new McpOAuthProviderConfiguration
            {
                Identifier = new() { Digest = new string('a', 64) },
                Endpoint = new Uri("https://resource.test/mcp"),
                AuthorizationServer = new Uri("https://authority.test/"),
                AccountEndpoint = new Uri("https://authority.test/userinfo"),
                ClientAuthentication = confidential
                    ? new ConfidentialOAuthClient
                    {
                        Client = new() { Value = "private-client" },
                        ClientSecret = new() { Name = "private-client-secret-name" },
                    }
                    : new PublicOAuthClient { Client = new() { Value = "private-client" } },
                Scopes = [new() { Value = "read" }],
                Source = new() { Digest = new string('b', 64) },
                Cache = new CredentialManagerTokenCache(this.Store, "mcp-oauth:", "private-cache"),
                CreateHttpHandler = () => createHttpHandler?.Invoke(this.Server) ?? new Handler(this.Server),
            };
            this.Catalog = new McpOAuthCredentialRequestCatalog([this.Configuration]);
            this.Executor = new McpOAuthCredentialAccessExecutor(this.Catalog, this.Browser);
            var executors = new CredentialAccessExecutorRegistry();
            executors.Add<McpOAuthCredentialUseRequest, McpCredentialUseResult>(wrapMcpExecutor?.Invoke(this.Executor) ?? this.Executor);
            if (cycle)
            {
                executors.Add<StoredSecretAccessRequest, StoredSecretAccessResult>(new CycleExecutor(this.Request()));
            }
            else
            {
                executors.Add<StoredSecretAccessRequest, StoredSecretAccessResult>(new StoredSecretAccessExecutor(this.SecretSource));
            }
            this.Broker = new AccessBroker(new RecordingPolicy(this.Events,
                Path.Combine(this.releaseMemoryDirectory, "release-memory.json")), executors, this.Acquisitions, this.Registry);
        }

        internal McpOAuthCredentialUseRequest Request(string purpose = "first")
        {
            var requestor = new AccessRequestor
            {
                Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.McpServer, "test"),
                Kind = AccessRequestorKind.McpServer,
                SafeDisplayName = "MCP server",
            };
            var lineage = new CredentialUseLineage();
            return new McpOAuthCredentialUseRequest
            {
                ProviderConfiguration = this.Configuration.Identifier,
                ClientAuthentication = this.Configuration.ClientAuthentication,
                Scopes = this.Configuration.Scopes,
                Credential = McpOAuthCredentialAccessExecutor.DescribeCredential(this.Configuration),
                Requestor = requestor,
                Use = new CredentialUseDescriptor
                {
                    Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Mcp, lineage, requestor.Identifier, purpose),
                    Kind = CredentialUseKind.Mcp,
                    Lineage = lineage,
                    SafeDisplayName = "Use MCP server",
                    MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
                },
                PresentationScope = AccessPresentationScope.Runtime,
                Sources = [new()
                {
                    Identifier = this.Configuration.Source,
                    Provider = CredentialProviderKind.McpOAuth,
                    Kind = CredentialSourceKind.Provider,
                    Usability = CredentialSourceUsability.AcquisitionRequired,
                    SafeDisplayName = "OAuth provider",
                }],
            };
        }

        internal async Task AllowAsync(UserAuthorizationSnapshot? row = null)
        {
            row ??= await this.WaitForPendingAsync();
            await this.Registry.ActAsync(new ApplyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
                Memory = row.SelectedMemory,
                Source = row.SelectedSource,
            }, TestContext.Current.CancellationToken);
        }

        internal async Task<UserAuthorizationSnapshot> WaitForPendingAsync(
            Task? acquisition = null,
            IReadOnlySet<UserAuthorizationRequestIdentifier>? excluded = null)
        {
            var pending = new TaskCompletionSource<UserAuthorizationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool Matches(UserAuthorizationSnapshot snapshot) =>
                snapshot.State == UserAuthorizationState.Pending &&
                (excluded is null || !excluded.Contains(snapshot.Identifier));
            void Refresh(object? sender, UserAuthorizationChangedEventArgs arguments)
            {
                var row = this.Registry.GetSnapshot().FirstOrDefault(Matches);
                if (row is not null) { pending.TrySetResult(row); }
            }
            this.Registry.Changed += Refresh;
            try
            {
                var row = this.Registry.GetSnapshot().FirstOrDefault(Matches);
                if (row is not null) { return row; }
                if (acquisition is null)
                    return await pending.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                var completed = await Task.WhenAny(pending.Task, acquisition)
                    .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                Assert.Same(pending.Task, completed);
                return await pending.Task;
            }
            finally
            {
                this.Registry.Changed -= Refresh;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await this.Broker.DisposeAsync();
            if (Directory.Exists(this.releaseMemoryDirectory))
            {
                Directory.Delete(this.releaseMemoryDirectory, recursive: true);
            }
        }

    }

    internal sealed class Browser(List<string> events) : ISystemBrowserLauncher
    {
        internal int OpenCount { get; private set; }
        internal bool Block { get; set; }
        internal Task? Callback { get; private set; }
        internal TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open(Uri uri)
        {
            this.OpenCount++;
            events.Add("browser");
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var competing = new TcpListener(IPAddress.Loopback, new Uri(query["redirect_uri"]!).Port);
            try { Assert.Throws<SocketException>(competing.Start); }
            finally { competing.Stop(); }
            this.Callback = this.CallbackAsync(uri);
            this.Opened.TrySetResult();
        }
        private async Task CallbackAsync(Uri uri)
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var redirect = new UriBuilder(query["redirect_uri"]!)
            {
                Query = $"code=private-code&state={Uri.EscapeDataString(query["state"]!)}",
            }.Uri;
            if (this.Block)
            {
                await this.Continue.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            using var client = new HttpClient();
            using var response = await client.GetAsync(redirect, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    private sealed class Handler(Server server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            server.SendAsync(request, cancellationToken);
    }

    internal sealed class ObservedAcquisitions : IProviderAcquisitionCoordinator
    {
        private readonly ProviderAcquisitionCoordinator coordinator = new();
        private int calls;
        internal TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ProviderAcquisitionOutcome> ExecuteOrJoinAsync(
            IProviderAcquisitionOperation operation,
            ProviderInteractionPermit permit,
            CancellationToken cancellationToken)
        {
            var outcome = this.coordinator.ExecuteOrJoinAsync(operation, permit, cancellationToken);
            if (Interlocked.Increment(ref this.calls) == 2) { this.Joined.TrySetResult(); }
            return outcome;
        }
        public ValueTask DisposeAsync() => this.coordinator.DisposeAsync();
    }

    internal sealed class Server(List<string> events)
    {
        internal string AccessToken { get; set; } = "private-access";
        internal string? Subject { get; set; } = "private-stable-account";
        internal bool BlockAccount { get; set; }
        internal TaskCompletionSource AccountRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueAccount { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int TokenRequests { get; private set; }
        internal bool SawVerifier { get; private set; }
        internal bool SawClientSecret { get; private set; }
        internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            events.Add("http");
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.Contains("oauth-protected-resource", StringComparison.Ordinal))
            {
                return Json("""{"resource":"https://resource.test/mcp","authorization_servers":["https://authority.test/"],"scopes_supported":["read"]}""");
            }
            if (uri.Host == "authority.test" && uri.AbsolutePath.Contains(".well-known", StringComparison.Ordinal))
            {
                return Json("""{"issuer":"https://authority.test/","authorization_endpoint":"https://authority.test/authorize","token_endpoint":"https://authority.test/token","response_types_supported":["code"],"grant_types_supported":["authorization_code","refresh_token"],"code_challenge_methods_supported":["S256"],"token_endpoint_auth_methods_supported":["client_secret_post","none"]}""");
            }
            if (uri.AbsolutePath == "/token")
            {
                this.TokenRequests++;
                var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                this.SawVerifier = !string.IsNullOrEmpty(form["code_verifier"]);
                this.SawClientSecret = form["client_secret"] == "private-client-secret";
                return Json(JsonSerializer.Serialize(new
                {
                    access_token = this.AccessToken,
                    refresh_token = "private-refresh",
                    token_type = "Bearer",
                    expires_in = 3600,
                    scope = "read",
                }));
            }
            if (uri.AbsolutePath == "/userinfo")
            {
                Assert.Equal(this.AccessToken, request.Headers.Authorization?.Parameter);
                this.AccountRequested.TrySetResult();
                if (this.BlockAccount)
                {
                    await this.ContinueAccount.Task.WaitAsync(cancellationToken);
                }
                return Json(JsonSerializer.Serialize(new { sub = this.Subject }));
            }
            if (uri.AbsolutePath == "/mcp")
            {
                if (request.Headers.Authorization?.Parameter != this.AccessToken)
                {
                    var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    unauthorized.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer",
                        """resource_metadata="https://resource.test/.well-known/oauth-protected-resource/mcp" """.Trim()));
                    return unauthorized;
                }
                if (request.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                }
                if (request.Method == HttpMethod.Delete)
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("id", out var identifier))
                {
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                }
                return document.RootElement.GetProperty("method").GetString() == "initialize"
                    ? Json($$$$"""{"jsonrpc":"2.0","id":{{{{identifier.GetRawText()}}}},"result":{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"test","version":"1"}}}""")
                    : Json($$$$"""{"jsonrpc":"2.0","id":{{{{identifier.GetRawText()}}}},"result":{"tools":[]}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }

    internal sealed class Store(List<string> events) : IPlatformSecretStore
    {
        private readonly Dictionary<string, SecureString> material = [];
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal bool FailWrite { get; set; }
        public Task<SecureString?> ReadAsync(string name, CancellationToken cancellationToken)
        {
            this.Reads++;
            return Task.FromResult(this.material.TryGetValue(name, out var value) ? value.Copy() : null);
        }
        public Task WriteAsync(string name, SecureString value, CancellationToken cancellationToken)
        {
            this.Writes++;
            if (this.FailWrite) { throw new IOException("private-token unsafe-path"); }
            this.material[name] = value.Copy();
            events.Add("store");
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            this.material.Remove(name);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(this.material.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray());
    }

    internal sealed class SecretSource : IStoredSecretAccessSource
    {
        internal int Reads { get; private set; }
        public ValueTask<bool> ExistsAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask<char[]?> ReadAsync(string name, CredentialSourceKind source, CancellationToken cancellationToken)
        {
            this.Reads++;
            return ValueTask.FromResult<char[]?>("private-client-secret".ToCharArray());
        }
    }

    private sealed class CycleExecutor(McpOAuthCredentialUseRequest original) : ICredentialAccessExecutor<StoredSecretAccessRequest, StoredSecretAccessResult>
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
            var cycle = Assert.IsType<AccessFailed<McpCredentialUseResult>>(
                await context.AcquireDependencyAsync(original, cancellationToken));
            return new CredentialExecutionFailed<StoredSecretAccessResult> { ErrorCode = cycle.ErrorCode, CanRetry = false };
        }
    }

    private sealed class RecordingPolicy : IReleaseAuthorizationPolicy, IAsyncDisposable
    {
        private readonly List<string> events;
        private readonly ReleaseAuthorizationPolicy policy;
        internal RecordingPolicy(List<string> events, string filePath)
        {
            this.events = events;
            this.policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(filePath));
        }
        public async ValueTask<ReleasePolicyEvaluation> EvaluateAsync(ReleasePolicyRequest request, CancellationToken cancellationToken)
        {
            // Exercise asynchronous publication even when the isolated store is empty.
            await Task.Yield();
            return await this.policy.EvaluateAsync(request, cancellationToken);
        }
        public async ValueTask<ReleaseAuthorization> CommitAsync(ReleasePolicyDecision decision, CancellationToken cancellationToken)
        {
            var result = await this.policy.CommitAsync(decision, cancellationToken);
            this.events.Add("release");
            return result;
        }
        public ValueTask<ReleaseAuthorizationValidation> ValidateAsync(ReleaseAuthorizationIdentifier identifier, ReleasePolicyRequest request, CancellationToken cancellationToken) =>
            this.policy.ValidateAsync(identifier, request, cancellationToken);
        public async ValueTask FinalizeCredentialIdentityAsync(ReleaseAuthorizationIdentifier identifier, ResolvedCredentialIdentity identity, CancellationToken cancellationToken)
        {
            await this.policy.FinalizeCredentialIdentityAsync(identifier, identity, cancellationToken);
            this.events.Add("finalize");
        }
        public ValueTask<IReadOnlyList<ReleaseMemoryDescriptor>> ListAsync(CancellationToken cancellationToken) => this.policy.ListAsync(cancellationToken);
        public ValueTask ForgetAsync(ReleaseMemoryIdentifier identifier, CancellationToken cancellationToken) => this.policy.ForgetAsync(identifier, cancellationToken);
        public ValueTask DisposeAsync() => this.policy.DisposeAsync();
    }
}
