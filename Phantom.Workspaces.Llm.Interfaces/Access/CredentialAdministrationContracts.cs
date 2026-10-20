namespace Phantom.Workspaces.Llm.Interfaces.Access;

public sealed record CredentialCacheIdentifier
{
    public required Guid Value { get; init; }
}

public sealed record CredentialCacheDescriptor
{
    public required CredentialCacheIdentifier Identifier { get; init; }
    public required CredentialProviderKind Provider { get; init; }
    public required CredentialKind Kind { get; init; }
    public required CredentialMaterialGenerationIdentifier Generation { get; init; }
    public required CredentialSourceIdentifier SourceIdentifier { get; init; }
    public DateTimeOffset? ExpiresOn { get; init; }
    public string SafeDisplayName => $"{this.Provider} {this.Kind}";
}

public enum CredentialAdministrationFailure { Unavailable, NoLongerCurrent, Denied, Failed }

public sealed class CredentialAdministrationException : Exception
{
    public CredentialAdministrationException(CredentialAdministrationFailure failure)
        : base("Credential administration could not complete.") => this.Failure = failure;
    public CredentialAdministrationFailure Failure { get; }
}

public interface ICredentialCacheAdministration
{
    // Identifiers name exact generations, not mutable cache slots. Enumeration must not read material.
    ValueTask<IReadOnlyList<CredentialCacheDescriptor>> ListAsync(CancellationToken cancellationToken = default);
    // Reject a replaced generation with NoLongerCurrent; invalidate its leases without forgetting policy.
    ValueTask DeleteAsync(CredentialCacheIdentifier identifier, CancellationToken cancellationToken = default);
}

public sealed record ProviderGrantIdentifier
{
    public required Guid Value { get; init; }
}

public sealed record ProviderGrantDescriptor
{
    public required ProviderGrantIdentifier Identifier { get; init; }
    public required CredentialProviderKind Provider { get; init; }
    public required CredentialKind Kind { get; init; }
    public required CredentialSourceIdentifier SourceIdentifier { get; init; }
    public string SafeDisplayName => $"{this.Provider} {this.Kind}";
}

public interface IProviderGrantAdministration
{
    // Enumerate supported revocable grants only; never expose provider responses or account names.
    ValueTask<IReadOnlyList<ProviderGrantDescriptor>> ListAsync(CancellationToken cancellationToken = default);
    // Revoke at the provider, then invalidate affected local generations. Release policy is independent.
    ValueTask RevokeAsync(ProviderGrantIdentifier identifier, CancellationToken cancellationToken = default);
}
