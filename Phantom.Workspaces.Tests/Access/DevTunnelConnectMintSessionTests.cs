using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Moq;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests.Access;

public sealed class DevTunnelConnectMintSessionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExportedManagementIdentity_401InvalidatesExactGeneration_AndReplaysOnlyOnce(
        bool rejectAgain, bool cancelAfterReplay)
    {
        await using var fixture = new Fixture();
        var presentationScope = AccessPresentationScope.Bootstrap;
        var identityRequest = DevTunnelIdentityCredentialAccessRequest.Create(fixture.Request.IdentityRoute,
            fixture.Request.Requestor, fixture.Request.Use with
            {
                MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce, ReleaseMemoryScopeKind.AllUses],
            }, AccessPresentationScope.Bootstrap);
        var initial = fixture.Broker.AcquireAsync(identityRequest, TestContext.Current.CancellationToken);
        await fixture.ApproveUntilCompletedAsync(initial);
        await using var first = Assert.IsType<AccessSucceeded<DevTunnelIdentityCredentialAccessResult>>(await initial).Result;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var replayContent = new TrackedContent();
        var sends = 0;
        using var client = new HttpClient(new DevTunnelManagementAuthenticationHandler(
            fixture.Broker, identityRequest, new ManagementHandler(async (message, token) =>
            {
                Assert.Equal("github", message.Headers.Authorization!.Scheme);
                Assert.Equal("management identity only", message.Headers.Authorization.Parameter);
                Assert.Equal("buffered request", await message.Content!.ReadAsStringAsync(token));
                var response = new HttpResponseMessage(++sends == 1 || rejectAgain ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
                presentationScope = AccessPresentationScope.Runtime;
                if (sends == 2)
                {
                    response.Content = replayContent;
                    if (cancelAfterReplay)
                    {
                        cancellation.Cancel();
                    }
                }
                return response;
            }), () => presentationScope));
        var sending = client.PostAsync("https://global.rel.tunnels.api.visualstudio.com/tunnels",
            new StringContent("buffered request"), cancellation.Token);
        var renewal = await fixture.WaitForActionAsync(TestContext.Current.CancellationToken);
        Assert.False(sending.IsCompleted);
        Assert.False(renewal.IsBootstrapBlocking);
        Assert.Equal(1, sends);
        Assert.Empty(await fixture.GitHub.InspectCachedGenerationsAsync(TestContext.Current.CancellationToken));
        await fixture.ApplyRememberedAsync(renewal);
        if (cancelAfterReplay)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ApproveUntilCompletedAsync(sending));
            Assert.Equal(2, sends);
            Assert.True(replayContent.Disposed);
            return;
        }
        await fixture.ApproveUntilCompletedAsync(sending);
        using var response = await sending;
        Assert.Equal(rejectAgain ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, sends);
        Assert.Equal(2, fixture.Source.Acquisitions);
        Assert.Equal(rejectAgain ? CredentialGenerationInvalidationOutcome.Missing :
                CredentialGenerationInvalidationOutcome.NoLongerCurrent,
            await first.InvalidateRejectedGenerationAsync(TestContext.Current.CancellationToken));
    }

    private sealed class ManagementHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }

    private sealed class TrackedContent() : StringContent("response")
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            this.Disposed |= disposing;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task ConnectMint_RealPrivateGitHubSessionWaitsForFinalRelease_AndPublishesOnlyConnectMaterial()
    {
        await using var fixture = new Fixture();
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Source.Acquisitions);
        Assert.Equal(0, fixture.ManagementCalls);
        await fixture.PerformAsync();
        await using var result = Assert.IsType<AccessSucceeded<DevTunnelConnectCredentialAccessResult>>(await pending).Result;

        Assert.Equal(1, fixture.Source.Acquisitions);
        Assert.Equal(1, fixture.ManagementCalls);
        Assert.Single(fixture.Registry.GetSnapshot());
        Assert.All(fixture.Source.LastMaterial!, character => Assert.Equal('\0', character));
        Assert.Equal(fixture.ConnectToken, Assert.IsType<SensitiveValueUseSucceeded<string>>(
            await result.ConnectToken!.UseAsync((material, _) => ValueTask.FromResult(material),
                TestContext.Current.CancellationToken)).Result);
        Assert.Equal(fixture.Source.Account, result.ConnectToken.AccountIdentifier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectMint_Management401InvalidatesExactPrivateIdentity_AndRequiresExplicitRetry(bool rejectAgain)
    {
        await using var fixture = new Fixture();
        fixture.RejectionsRemaining = rejectAgain ? 2 : 1;
        var pending = fixture.Broker.AcquireAsync(fixture.Request, TestContext.Current.CancellationToken);
        var renewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Registry.Changed += (_, _) =>
        {
            if (fixture.Registry.GetSnapshot().Any(row => row.State == UserAuthorizationState.RenewalNeeded))
            {
                renewal.TrySetResult();
            }
        };
        await fixture.PerformAsync();
        await renewal.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, fixture.Source.Acquisitions);
        Assert.Equal(1, fixture.ManagementCalls);
        Assert.Empty(await fixture.GitHub.InspectCachedGenerationsAsync(TestContext.Current.CancellationToken));
        Assert.Single(fixture.Registry.GetSnapshot());

        await fixture.PerformAsync();
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
        Assert.Equal(2, fixture.Source.Acquisitions);
        Assert.Equal(2, fixture.ManagementCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly EntraCredentialAccessExecutor entra = new();
        private readonly OAuthCredentialAccessExecutor oauth = new(
            new OAuthProviderRegistry(new Phantom.Workspaces.Llm.Secrets.NullPlatformSecretStore(),
                () => new Uri("http://localhost:51234/")),
            new Mock<ISystemBrowserLauncher>().Object);
        public Fixture()
        {
            this.ConnectToken = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{{\"exp\":{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}"))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
            this.GitHub = new GitHubCredentialAccessExecutor(this.Source);
            var managementApi = new DevTunnelConnectManagementApi(header =>
            {
                Assert.Equal("github", header.Scheme);
                Assert.Equal("management identity only", header.Parameter);
                var tunnel = new Tunnel
                {
                    TunnelId = "selected",
                    ClusterId = "euw",
                    Labels = [DevTunnelNaming.WorkspacesMarkerLabel, "selected tunnel"],
                    Ports = [new TunnelPort { PortNumber = 8080 }],
                    AccessTokens = new Dictionary<string, string> { [TunnelAccessScopes.Connect] = this.ConnectToken },
                };
                var client = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
                client.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
                client.Setup(value => value.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true,
                    It.IsAny<CancellationToken>())).ReturnsAsync([tunnel]);
                client.Setup(value => value.GetTunnelAsync(tunnel, It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
                    .Returns(() =>
                    {
                        this.ManagementCalls++;
                        if (this.RejectionsRemaining-- > 0)
                        {
                            throw new HttpRequestException("unsafe service response", null, HttpStatusCode.Unauthorized);
                        }
                        return Task.FromResult<Tunnel?>(tunnel);
                    });
                return client.Object;
            });
            var executor = new DevTunnelConnectCredentialAccessExecutor(
                new DevTunnelConnectMintSession(this.GitHub, this.entra, this.oauth, managementApi), this.Registry);
            var executors = new CredentialAccessExecutorRegistry();
            executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(this.GitHub);
            executors.Add<EntraCredentialAccessRequest, EntraCredentialAccessResult>(this.entra);
            executors.Add<OAuthCredentialAccessRequest, OAuthCredentialAccessResult>(this.oauth);
            executors.Add<DevTunnelIdentityCredentialAccessRequest, DevTunnelIdentityCredentialAccessResult>(
                new DevTunnelIdentityCredentialAccessExecutor(this.GitHub, this.entra, this.oauth));
            executors.Add<DevTunnelConnectCredentialAccessRequest, DevTunnelConnectCredentialAccessResult>(executor);
            this.Broker = new AccessBroker(new ReleaseAuthorizationPolicy(new ReleaseMemoryStore()),
                executors, new ProviderAcquisitionCoordinator(), this.Registry);
            var template = ReleaseAuthorizationContractTests.Request();
            var requestor = new AccessRequestor
            {
                Identifier = template.RequestorIdentifier,
                Kind = AccessRequestorKind.Application,
                SafeDisplayName = "Tunnel",
            };
            var identity = GitHubCredentialAccessRequest.Create(GitHubCredentialPurpose.DevTunnelIdentity,
                requestor, template.Use.Lineage, "management", source: CredentialSourceKind.GitHubCli);
            this.Request = DevTunnelConnectCredentialAccessRequest.Create(new DevTunnelAccessDescriptor
            {
                Access = DevTunnelAccessKind.Private,
                Name = "selected tunnel",
                Endpoint = new Uri("https://selected-8080.euw.devtunnels.ms/"),
            }, new GitHubDevTunnelIdentityRoute { Credential = identity }, requestor,
                template.Use with { Kind = CredentialUseKind.Tunnel, MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce] });
        }

        public string ConnectToken { get; }
        public Source Source { get; } = new();
        public GitHubCredentialAccessExecutor GitHub { get; }
        public UserAuthorizationRegistry Registry { get; } = new();
        public AccessBroker Broker { get; }
        public DevTunnelConnectCredentialAccessRequest Request { get; }
        public int ManagementCalls { get; private set; }
        public int RejectionsRemaining { get; set; }

        public async Task<UserAuthorizationSnapshot> WaitForActionAsync(CancellationToken cancellationToken)
        {
            var ready = new TaskCompletionSource<UserAuthorizationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Find()
            {
                var row = this.Registry.GetSnapshot().LastOrDefault(row =>
                    row.State is UserAuthorizationState.Pending or UserAuthorizationState.RenewalNeeded);
                if (row is not null)
                {
                    ready.TrySetResult(row);
                }
            }
            EventHandler changed = (_, _) => Find();
            this.Registry.Changed += changed;
            try
            {
                Find();
                return await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            finally
            {
                this.Registry.Changed -= changed;
            }
        }

        public Task ApplyRememberedAsync(UserAuthorizationSnapshot row) =>
            this.Registry.ActAsync(new ApplyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
                Memory = row.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllUses).Identifier,
                Source = row.SelectedSource,
            }, TestContext.Current.CancellationToken);

        public async Task ApproveUntilCompletedAsync(Task operation)
        {
            for (var approvals = 0; approvals < 4 && !operation.IsCompleted; approvals++)
            {
                using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                var next = this.WaitForActionAsync(stopWaiting.Token);
                if (await Task.WhenAny(operation, next).WaitAsync(TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken) == operation)
                {
                    await stopWaiting.CancelAsync();
                    try { await next; } catch (OperationCanceledException) { }
                    break;
                }
                await this.ApplyRememberedAsync(await next);
            }
            await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        public Task PerformAsync()
        {
            var row = Assert.Single(this.Registry.GetSnapshot());
            return this.Registry.ActAsync(new ApplyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
                Memory = row.SelectedMemory,
                Source = row.SelectedSource,
            }, TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync() => this.Broker.DisposeAsync();
    }

    private sealed class Source : IGitHubCredentialAccessSource
    {
        public int Acquisitions { get; private set; }
        public char[]? LastMaterial { get; private set; }
        public CredentialAccountIdentifier Account { get; } =
            CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "authoritative principal");
        public bool EnvironmentExists() => false;
        public ValueTask<GitHubCredentialMaterial?> AcquireAsync(
            CredentialSourceKind source,
            ProviderInteractionPermit permit,
            ProviderAcquisitionKey key,
            CancellationToken cancellationToken)
        {
            Assert.True(permit.AuthorizesProvider(CredentialExecutorKind.GitHub, key));
            this.Acquisitions++;
            this.LastMaterial = "management identity only".ToCharArray();
            return ValueTask.FromResult<GitHubCredentialMaterial?>(new GitHubCredentialMaterial
            {
                Material = this.LastMaterial,
                Account = this.Account,
            });
        }
    }
}
