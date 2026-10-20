namespace Phantom.Workspaces.Llm.Interfaces.Access;

public enum DevTunnelIdentityScheme { Anonymous, GitHub, Entra, OAuth }

public abstract record DevTunnelIdentityRoute
{
    private protected DevTunnelIdentityRoute() { }

    internal abstract DevTunnelIdentityScheme Scheme { get; }
    internal abstract CredentialReference? Reference { get; }
    internal abstract IReadOnlyList<CredentialSourceChoiceDescriptor> Sources { get; }
}

public sealed record GitHubDevTunnelIdentityRoute : DevTunnelIdentityRoute
{
    public required GitHubCredentialAccessRequest Credential { get; init; }
    internal override DevTunnelIdentityScheme Scheme => DevTunnelIdentityScheme.GitHub;
    internal override CredentialReference Reference => this.Credential.Credential;
    internal override IReadOnlyList<CredentialSourceChoiceDescriptor> Sources => this.Credential.Sources;
}

public sealed record EntraDevTunnelIdentityRoute : DevTunnelIdentityRoute
{
    public required EntraCredentialAccessRequest Credential { get; init; }
    internal override DevTunnelIdentityScheme Scheme => DevTunnelIdentityScheme.Entra;
    internal override CredentialReference Reference => this.Credential.Credential;
    internal override IReadOnlyList<CredentialSourceChoiceDescriptor> Sources => this.Credential.Sources;
}

public sealed record AnonymousDevTunnelIdentityRoute : DevTunnelIdentityRoute
{
    internal override DevTunnelIdentityScheme Scheme => DevTunnelIdentityScheme.Anonymous;
    internal override CredentialReference? Reference => null;
    internal override IReadOnlyList<CredentialSourceChoiceDescriptor> Sources => [];
}

public sealed record DevTunnelIdentityCredentialAccessRequest : CredentialAccessRequest<DevTunnelIdentityCredentialAccessResult>
{
    public required DevTunnelIdentityRoute Route { get; init; }

    public static DevTunnelIdentityCredentialAccessRequest Create(
        DevTunnelIdentityRoute route,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope = AccessPresentationScope.Runtime) => new()
    {
        Route = route,
        Requestor = requestor,
        Use = use,
        PresentationScope = presentationScope,
        Credential = new CredentialReference
        {
            Provider = CredentialProviderKind.DevTunnel,
            Kind = route.Scheme == DevTunnelIdentityScheme.Anonymous ? CredentialKind.Anonymous : CredentialKind.IdentityToken,
            LogicalIdentifier = CreateLogicalIdentifier(route),
            AccountIdentifier = route.Reference?.AccountIdentifier,
        },
        Sources = route.Scheme == DevTunnelIdentityScheme.Anonymous
            ? [CreateSource(route, null)]
            : route.Sources.Select(source => CreateSource(route, source)).ToArray(),
    };

    internal static LogicalCredentialIdentifier CreateLogicalIdentifier(DevTunnelIdentityRoute route) =>
        CredentialIdentityFactory.CreateLogicalIdentifier(new CredentialIdentityConfiguration
        {
            Provider = CredentialProviderKind.DevTunnel,
            Kind = route.Scheme == DevTunnelIdentityScheme.Anonymous ? CredentialKind.Anonymous : CredentialKind.IdentityToken,
            CredentialPartition = "management-identity",
            SourceCompatibility = route.Reference?.LogicalIdentifier.Digest ?? "anonymous",
            Authority = route.Scheme.ToString(),
            Audience = "devtunnels-management",
            ClientPartition = string.Empty,
            Scopes = [],
        });

    internal static CredentialSourceChoiceDescriptor CreateSource(
        DevTunnelIdentityRoute route,
        CredentialSourceChoiceDescriptor? dependencySource) => new()
    {
        Identifier = CredentialIdentityFactory.CreateSourceIdentifier(
            CredentialProviderKind.DevTunnel,
            $"identity:{route.Scheme}:{dependencySource?.Identifier.Digest ?? "anonymous"}"),
        Provider = CredentialProviderKind.DevTunnel,
        Kind = dependencySource?.Kind ?? CredentialSourceKind.Provider,
        Usability = dependencySource?.Usability ?? CredentialSourceUsability.Usable,
        SafeDisplayName = route.Scheme switch
        {
            DevTunnelIdentityScheme.GitHub => "GitHub management identity",
            DevTunnelIdentityScheme.Entra => "Microsoft management identity",
            DevTunnelIdentityScheme.OAuth => "OAuth management identity",
            _ => "Anonymous access",
        },
    };
}

public sealed class DevTunnelIdentityCredentialAccessResult : ICredentialAccessResult
{
    internal DevTunnelIdentityCredentialAccessResult(
        SensitiveValueLease? accessToken,
        DevTunnelIdentityScheme scheme)
    {
        this.AccessToken = accessToken;
        this.Scheme = scheme;
    }

    public SensitiveValueLease? AccessToken { get; }
    public DevTunnelIdentityScheme Scheme { get; }

    public ValueTask DisposeAsync() => this.AccessToken?.DisposeAsync() ?? ValueTask.CompletedTask;
}
