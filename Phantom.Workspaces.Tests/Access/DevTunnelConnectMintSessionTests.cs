using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Moq;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests.Access;

public sealed class DevTunnelConnectMintSessionTests
{
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
                new DevTunnelConnectMintSession(this.GitHub, this.entra, managementApi), this.Registry);
            var executors = new CredentialAccessExecutorRegistry();
            executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(this.GitHub);
            executors.Add<EntraCredentialAccessRequest, EntraCredentialAccessResult>(this.entra);
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
