using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Llm.Interfaces.Access;

public enum DevTunnelAccessKind { Private, Anonymous }

public sealed record DevTunnelAccessDescriptor
{
    public required DevTunnelAccessKind Access { get; init; }
    public string? Name { get; init; }
    public Uri? Endpoint { get; init; }
}

public sealed record DevTunnelConnectCredentialAccessRequest : CredentialAccessRequest<DevTunnelConnectCredentialAccessResult>
{
    public required DevTunnelAccessDescriptor Tunnel { get; init; }
    public required DevTunnelIdentityRoute IdentityRoute { get; init; }

    public static DevTunnelConnectCredentialAccessRequest Create(
        DevTunnelAccessDescriptor tunnel,
        DevTunnelIdentityRoute identityRoute,
        AccessRequestor requestor,
        CredentialUseDescriptor use,
        AccessPresentationScope presentationScope = AccessPresentationScope.Runtime) => new()
    {
        Tunnel = tunnel,
        IdentityRoute = identityRoute,
        Requestor = requestor,
        Use = use,
        PresentationScope = presentationScope,
        Credential = new CredentialReference
        {
            Provider = CredentialProviderKind.DevTunnel,
            Kind = tunnel.Access == DevTunnelAccessKind.Anonymous ? CredentialKind.Anonymous : CredentialKind.ConnectToken,
            LogicalIdentifier = CreateLogicalIdentifier(tunnel, identityRoute),
            AccountIdentifier = tunnel.Access == DevTunnelAccessKind.Anonymous ? null : identityRoute.Reference?.AccountIdentifier,
        },
        Sources = tunnel.Access == DevTunnelAccessKind.Anonymous
            ? [CreateSource(null)]
            : identityRoute.Sources.Select(CreateSource).ToArray(),
    };

    internal static LogicalCredentialIdentifier CreateLogicalIdentifier(
        DevTunnelAccessDescriptor tunnel,
        DevTunnelIdentityRoute identityRoute) => CredentialIdentityFactory.CreateLogicalIdentifier(new CredentialIdentityConfiguration
    {
        Provider = CredentialProviderKind.DevTunnel,
        Kind = tunnel.Access == DevTunnelAccessKind.Anonymous ? CredentialKind.Anonymous : CredentialKind.ConnectToken,
        CredentialPartition = tunnel.Name ?? "auto",
        SourceCompatibility = tunnel.Access == DevTunnelAccessKind.Anonymous
            ? "anonymous"
            : DevTunnelIdentityCredentialAccessRequest.CreateLogicalIdentifier(identityRoute).Digest,
        Authority = "devtunnels-management",
        Audience = tunnel.Endpoint?.AbsoluteUri ?? "discovered-relay",
        ClientPartition = tunnel.Access.ToString(),
        Scopes = ["connect"],
    });

    internal static CredentialSourceChoiceDescriptor CreateSource(
        CredentialSourceChoiceDescriptor? identitySource) => new()
    {
        Identifier = CredentialIdentityFactory.CreateSourceIdentifier(
            CredentialProviderKind.DevTunnel,
            $"connect:{identitySource?.Identifier.Digest ?? "anonymous"}"),
        Provider = CredentialProviderKind.DevTunnel,
        Kind = identitySource?.Kind ?? CredentialSourceKind.Provider,
        Usability = identitySource is null ? CredentialSourceUsability.Usable : CredentialSourceUsability.AcquisitionRequired,
        SafeDisplayName = identitySource is null ? "Anonymous relay access" : "Connect credential",
    };
}

public sealed class DevTunnelConnectCredentialAccessResult : ICredentialAccessResult
{
    private readonly ICredentialGenerationCache? cache;
    private readonly CredentialMaterialGenerationIdentifier? generation;

    internal DevTunnelConnectCredentialAccessResult(Uri endpoint)
    {
        this.Endpoint = endpoint;
    }

    internal DevTunnelConnectCredentialAccessResult(
        SensitiveValueLease connectToken,
        Uri endpoint,
        ICredentialGenerationCache cache,
        CredentialMaterialGenerationIdentifier generation)
    {
        this.ConnectToken = connectToken;
        this.Endpoint = endpoint;
        this.cache = cache;
        this.generation = generation;
    }

    public SensitiveValueLease? ConnectToken { get; }
    public Uri Endpoint { get; }

    internal ValueTask<CredentialGenerationInvalidationOutcome> InvalidateRejectedGenerationAsync(
        CancellationToken cancellationToken) => this.cache is not null && this.generation is not null
        ? this.cache.InvalidateGenerationAsync(this.generation, cancellationToken)
        : ValueTask.FromResult(CredentialGenerationInvalidationOutcome.Missing);

    public ValueTask DisposeAsync() => this.ConnectToken?.DisposeAsync() ?? ValueTask.CompletedTask;
}
