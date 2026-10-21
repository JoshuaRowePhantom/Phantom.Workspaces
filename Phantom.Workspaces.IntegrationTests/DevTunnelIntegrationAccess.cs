using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.DevTunnels.Management;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.IntegrationTests;

// Live tests explicitly opt in to fixture-local release decisions, never production UI consent.
internal sealed class DevTunnelIntegrationAccess : IAsyncDisposable
{
    internal static bool HasConfiguredCredential => IntegrationGitHubSource.Exists();
    private readonly UserAuthorizationRegistry registry = new();
    private readonly string policyDirectory = Path.Combine(
        Environment.CurrentDirectory, ".access-tunnel-integration-tests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentBag<Task> decisions = [];
    private readonly ConcurrentBag<HttpMessageHandler> handlers = [];
    private readonly HashSet<(UserAuthorizationRequestIdentifier, long)> acted = [];
    private readonly AccessRequestor requestor = new()
    {
        Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Application, "devtunnel-integration"),
        Kind = AccessRequestorKind.Application,
        SafeDisplayName = "Integration test",
    };
    private readonly CredentialUseDescriptor use;
    internal DevTunnelIntegrationAccess()
    {
        var lineage = new CredentialUseLineage();
        this.use = new CredentialUseDescriptor
        {
            Kind = CredentialUseKind.Tunnel,
            Lineage = lineage,
            Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                CredentialUseKind.Tunnel, lineage, this.requestor.Identifier, "live integration"),
            SafeDisplayName = "Integration tunnel",
            MemoryScopes = [ReleaseMemoryScopeKind.AllUses],
        };
        var github = new GitHubCredentialAccessExecutor(new IntegrationGitHubSource());
        var entra = new EntraCredentialAccessExecutor();
        var oauth = new OAuthCredentialAccessExecutor(
            new OAuthProviderRegistry(new Phantom.Workspaces.Llm.Secrets.NullPlatformSecretStore(),
                () => new Uri("http://localhost:51234/")), new NoBrowser());
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(github);
        executors.Add<EntraCredentialAccessRequest, EntraCredentialAccessResult>(entra);
        executors.Add<OAuthCredentialAccessRequest, OAuthCredentialAccessResult>(oauth);
        executors.Add<DevTunnelIdentityCredentialAccessRequest, DevTunnelIdentityCredentialAccessResult>(
            new DevTunnelIdentityCredentialAccessExecutor(github, entra, oauth));
        executors.Add<DevTunnelConnectCredentialAccessRequest, DevTunnelConnectCredentialAccessResult>(
            new DevTunnelConnectCredentialAccessExecutor(
                new DevTunnelConnectMintSession(github, entra, oauth, new DevTunnelConnectManagementApi()), this.registry));
        this.Broker = new AccessBroker(new ReleaseAuthorizationPolicy(
            new ReleaseMemoryStore(Path.Combine(this.policyDirectory, "release-memory.json"))), executors,
            new ProviderAcquisitionCoordinator(), this.registry);
        this.IdentityRoute = new GitHubDevTunnelIdentityRoute
        {
            Credential = GitHubCredentialAccessRequest.Create(GitHubCredentialPurpose.DevTunnelIdentity,
                this.requestor, lineage, "Management integration", source: CredentialSourceKind.Environment),
        };
        this.IdentityRequest = DevTunnelIdentityCredentialAccessRequest.Create(this.IdentityRoute, this.requestor, this.use);
        this.registry.Changed += this.ApproveTestDecision;
    }

    internal IAccessBroker Broker { get; }
    internal DevTunnelIdentityRoute IdentityRoute { get; }
    internal DevTunnelIdentityCredentialAccessRequest IdentityRequest { get; }
    internal IDevTunnelHostService CreateHostService() =>
        new DevTunnelServiceFactory(this.Broker, this.IdentityRequest, () => AccessPresentationScope.Runtime).CreateHostService();
    internal DevTunnelConnectCredentialAccessRequest ConnectRequest(string name, Uri endpoint, bool anonymous = false) =>
        DevTunnelConnectCredentialAccessRequest.Create(new DevTunnelAccessDescriptor
        {
            Access = anonymous ? DevTunnelAccessKind.Anonymous : DevTunnelAccessKind.Private,
            Name = name,
            Endpoint = endpoint,
        }, anonymous ? new AnonymousDevTunnelIdentityRoute() : this.IdentityRoute, this.requestor, this.use);

    internal TunnelManagementClient CreateManagementClient()
    {
        var handler = new DevTunnelManagementAuthenticationHandler(this.Broker, this.IdentityRequest,
            new HttpClientHandler { AllowAutoRedirect = false }, () => AccessPresentationScope.Runtime);
        this.handlers.Add(handler);
        return new(new ProductInfoHeaderValue("Phantom.Workspaces.IntegrationTests", "1.0"), null, null,
            handler, ManagementApiVersions.Version20230927Preview);
    }

    private void ApproveTestDecision(object? sender, UserAuthorizationChangedEventArgs arguments)
    {
        foreach (var row in this.registry.GetSnapshot().Where(row =>
                     row.State is UserAuthorizationState.Pending or UserAuthorizationState.RenewalNeeded))
        {
            lock (this.acted)
            {
                if (!this.acted.Add((row.Identifier, row.Revision)))
                {
                    continue;
                }
            }
            this.decisions.Add(this.registry.ActAsync(new ApplyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
                Memory = row.SelectedMemory,
                Source = row.SelectedSource,
            }, CancellationToken.None));
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.registry.Changed -= this.ApproveTestDecision;
        try
        {
            await this.Broker.DisposeAsync();
            await Task.WhenAll(this.decisions);
        }
        finally
        {
            try
            {
                foreach (var handler in this.handlers)
                {
                    handler.Dispose();
                }
            }
            finally
            {
                if (Directory.Exists(this.policyDirectory))
                {
                    Directory.Delete(this.policyDirectory, recursive: true);
                }
            }
        }
    }

    private sealed class NoBrowser : ISystemBrowserLauncher
    {
        public void Open(Uri uri) => throw new InvalidOperationException("Integration fixtures do not open browsers.");
    }

    private sealed class IntegrationGitHubSource : IGitHubCredentialAccessSource
    {
        private const string Variable = "PHANTOM_INTEGRATION_GITHUB_TOKEN";
        internal static bool Exists() => GetEnvironmentVariable(Variable, IntPtr.Zero, 0) > 0;
        public bool EnvironmentExists() => Exists();
        public async ValueTask<GitHubCredentialMaterial?> AcquireAsync(
            CredentialSourceKind source,
            ProviderInteractionPermit permit,
            ProviderAcquisitionKey key,
            CancellationToken cancellationToken)
        {
            if (source != CredentialSourceKind.Environment || !permit.AuthorizesProvider(CredentialExecutorKind.GitHub, key))
            {
                return null;
            }
            var material = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(material))
            {
                return null;
            }
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", material);
            request.Headers.UserAgent.ParseAdd("Phantom.Workspaces.IntegrationTests");
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var account = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return new GitHubCredentialMaterial
            {
                Material = material.ToCharArray(),
                Account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub,
                    account.RootElement.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture)),
            };
        }

        [DllImport("kernel32.dll", EntryPoint = "GetEnvironmentVariableW", CharSet = CharSet.Unicode)]
        private static extern uint GetEnvironmentVariable(string name, IntPtr buffer, uint size);
    }
}
