namespace Phantom.Workspaces.Llm.Interfaces.Access;

public enum CredentialUseKind { Application, Agent, Manifest, Mcp, Repository, Tunnel, Completion }

public enum ReleaseMemoryScopeKind
{
    AllowOnce,
    ExactUse,
    SessionIdentity,
    KeyInSession,
    ManifestIdentity,
    ManifestContent,
    KeyInManifestContent,
    McpProviderOnManifest,
    KeyInAnyManifest,
    AnyManifest,
    AllUses,
}

public sealed record CredentialUseLineage
{
    public CredentialLineageIdentifier? Session { get; init; }
    public CredentialLineageIdentifier? Manifest { get; init; }
    public CredentialLineageIdentifier? ManifestContent { get; init; }
    public CredentialLineageIdentifier? Key { get; init; }
    public CredentialLineageIdentifier? ProviderConfiguration { get; init; }
}

public sealed record CredentialUseDescriptor
{
    public required CredentialUseFingerprint Fingerprint { get; init; }
    public required CredentialUseKind Kind { get; init; }
    public required CredentialUseLineage Lineage { get; init; }
    public required string SafeDisplayName { get; init; }
    public required IReadOnlyList<ReleaseMemoryScopeKind> MemoryScopes { get; init; }
}
