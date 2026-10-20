namespace Phantom.Workspaces.Llm.Interfaces.Access;

public sealed record SecretReference
{
    public required string Name { get; init; }
}

public sealed record StoredSecretAccessRequest : CredentialAccessRequest<StoredSecretAccessResult>
{
    public required SecretReference Reference { get; init; }
}

public sealed class StoredSecretAccessResult : ICredentialAccessResult
{
    public required SensitiveValueLease Lease { get; init; }

    public ValueTask DisposeAsync() => this.Lease.DisposeAsync();
}
