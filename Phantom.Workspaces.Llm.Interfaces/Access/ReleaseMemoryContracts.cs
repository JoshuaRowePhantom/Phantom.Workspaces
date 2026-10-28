namespace Phantom.Workspaces.Llm.Interfaces.Access;

public sealed record ReleaseAuthorizationIdentifier
{
    public required Guid Value { get; init; }
}

public sealed record ReleaseMemoryIdentifier
{
    public required Guid Value { get; init; }
}

public sealed record ReleaseMemoryOptionIdentifier
{
    public required Guid Value { get; init; }
}

public sealed record ReleaseMemoryOptionDescriptor
{
    public required ReleaseMemoryOptionIdentifier Identifier { get; init; }
    public required ReleaseMemoryScopeKind Scope { get; init; }
    public required string SafeDisplayName { get; init; }
}

public sealed record ReleasePolicyRequest
{
    public required UserAuthorizationRequestIdentifier RequestIdentifier { get; init; }
    public required long RequestGeneration { get; init; }
    public required CredentialReference Credential { get; init; }
    public required CredentialSourceIdentifier SourceIdentifier { get; init; }
    public CredentialSourceKind? SourceKind { get; init; }
    public required AccessRequestorIdentifier RequestorIdentifier { get; init; }
    public required CredentialUseDescriptor Use { get; init; }
}

public sealed record ReleasePolicyDecision
{
    public required ReleasePolicyRequest Request { get; init; }
    public required ReleaseMemoryScopeKind Scope { get; init; }
}

public sealed record ReleaseAuthorization
{
    public required ReleaseAuthorizationIdentifier Identifier { get; init; }
    public required UserAuthorizationRequestIdentifier RequestIdentifier { get; init; }
    public required long RequestGeneration { get; init; }
    public required ReleaseMemoryScopeKind Scope { get; init; }
    public ReleaseMemoryIdentifier? MemoryIdentifier { get; init; }
}

public abstract record ReleasePolicyEvaluation
{
    private protected ReleasePolicyEvaluation() { }
}

public sealed record ReleasePolicyMatched : ReleasePolicyEvaluation
{
    public required ReleaseAuthorization Authorization { get; init; }
}

public sealed record ReleasePolicyRequired : ReleasePolicyEvaluation;

public enum ReleaseAuthorizationValidation { Valid, AuthorizationRequired, IdentityMismatch }

public sealed record ResolvedCredentialIdentity
{
    public required LogicalCredentialIdentifier LogicalIdentifier { get; init; }
    public required CredentialSourceIdentifier SourceIdentifier { get; init; }
    public required CredentialAccountIdentifier AccountIdentifier { get; init; }
    public required UserAuthorizationRequestIdentifier RequestIdentifier { get; init; }
    public required long RequestGeneration { get; init; }

    public bool Narrows(ReleasePolicyRequest request) =>
        this.LogicalIdentifier == request.Credential.LogicalIdentifier &&
        this.SourceIdentifier == request.SourceIdentifier &&
        this.RequestIdentifier == request.RequestIdentifier &&
        this.RequestGeneration == request.RequestGeneration &&
        (request.Credential.AccountIdentifier is null || this.AccountIdentifier == request.Credential.AccountIdentifier);
}

public sealed record ReleaseMemoryDescriptor
{
    public required ReleaseMemoryIdentifier Identifier { get; init; }
    public required LogicalCredentialIdentifier LogicalIdentifier { get; init; }
    public required CredentialSourceIdentifier SourceIdentifier { get; init; }
    public CredentialAccountIdentifier? AccountIdentifier { get; init; }
    public required ReleaseMemoryScopeKind Scope { get; init; }
    public required string SafeDisplayName { get; init; }
    public SafeApprovalContext? SafeContext { get; init; }
}

public sealed class ReleaseIdentityMismatchException : Exception
{
    public ReleaseIdentityMismatchException() : base("Credential identity does not match the approved use.") { }
}
