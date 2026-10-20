namespace Phantom.Workspaces.Llm.Interfaces.Access;

public enum CredentialSourceKind { SecretStore, Environment, GitHubCli, ProviderCache, Provider, CliOwned }
public enum CredentialSourceUsability { Usable, AcquisitionRequired, Missing }

public sealed record CredentialSourceChoiceDescriptor
{
    public required CredentialSourceIdentifier Identifier { get; init; }
    public required CredentialSourceKind Kind { get; init; }
    public required CredentialProviderKind Provider { get; init; }
    public required CredentialSourceUsability Usability { get; init; }
    public required string SafeDisplayName { get; init; }
}
