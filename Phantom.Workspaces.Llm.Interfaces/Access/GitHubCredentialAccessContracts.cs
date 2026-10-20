namespace Phantom.Workspaces.Llm.Interfaces.Access;

public enum GitHubCredentialPurpose { Repository, Persistence, CopilotUsage, ActionsUsage, Agent, Model, Mcp, DevTunnelIdentity }

public sealed record GitHubCredentialAccessRequest : CredentialAccessRequest<GitHubCredentialAccessResult>
{
    public required GitHubCredentialPurpose Purpose { get; init; }

    public static GitHubCredentialAccessRequest Create(
        GitHubCredentialPurpose purpose,
        AccessRequestor requestor,
        CredentialUseLineage lineage,
        string operationIdentity,
        AccessPresentationScope presentationScope = AccessPresentationScope.Runtime,
        CredentialSourceKind? source = null)
    {
        var kind = purpose == GitHubCredentialPurpose.Repository ? CredentialUseKind.Repository : CredentialUseKind.Application;
        return new GitHubCredentialAccessRequest
        {
            Purpose = purpose,
            Requestor = requestor,
            PresentationScope = presentationScope,
            Use = new CredentialUseDescriptor
            {
                Kind = kind,
                Lineage = lineage,
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(kind, lineage, requestor.Identifier, $"{purpose}:{operationIdentity}"),
                SafeDisplayName = purpose switch
                {
                    GitHubCredentialPurpose.Repository => "Access repository",
                    GitHubCredentialPurpose.Persistence => "Access persistence store",
                    GitHubCredentialPurpose.CopilotUsage => "Refresh Copilot usage",
                    GitHubCredentialPurpose.ActionsUsage => "Refresh Actions usage",
                    _ => "Use GitHub credential",
                },
                MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce, ReleaseMemoryScopeKind.ExactUse, ReleaseMemoryScopeKind.AllUses],
            },
            Credential = new CredentialReference
            {
                LogicalIdentifier = CreateLogicalIdentifier(),
                Provider = CredentialProviderKind.GitHub,
                Kind = CredentialKind.AccessToken,
            },
            Sources = (source is { } selected ? new[] { selected } : new[] { CredentialSourceKind.Environment, CredentialSourceKind.GitHubCli })
                .Select(CreateSource).ToArray(),
        };
    }

    public static LogicalCredentialIdentifier CreateLogicalIdentifier() =>
        CredentialIdentityFactory.CreateLogicalIdentifier(new CredentialIdentityConfiguration
        {
            Provider = CredentialProviderKind.GitHub,
            Kind = CredentialKind.AccessToken,
            CredentialPartition = "github.com",
            SourceCompatibility = "GitHubIdentity",
            Authority = "https://github.com",
            Audience = "https://api.github.com",
            ClientPartition = "Phantom.Workspaces",
            Scopes = [],
        });

    public static CredentialSourceChoiceDescriptor CreateSource(CredentialSourceKind kind)
    {
        if (kind is not (CredentialSourceKind.Environment or CredentialSourceKind.GitHubCli))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return new CredentialSourceChoiceDescriptor
        {
            Identifier = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.GitHub, $"{kind}:github.com"),
            Kind = kind,
            Provider = CredentialProviderKind.GitHub,
            Usability = CredentialSourceUsability.AcquisitionRequired,
            SafeDisplayName = kind == CredentialSourceKind.Environment ? "GitHub environment" : "GitHub CLI",
        };
    }
}

public sealed class GitHubCredentialAccessResult : ICredentialAccessResult
{
    public required SensitiveValueLease Lease { get; init; }

    public ValueTask DisposeAsync() => this.Lease.DisposeAsync();
}
