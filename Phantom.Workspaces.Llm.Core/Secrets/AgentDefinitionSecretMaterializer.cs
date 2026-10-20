using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Llm.Secrets;

public sealed class AgentDefinitionSecretMaterializer
{
    private readonly SecretUsageScanner scanner = new();

    public async Task<MaterializedAgentDefinition> MaterializeAsync(
        AgentDefinition definition,
        IAccessBroker accessBroker,
        CredentialUseLineage lineage,
        CancellationToken cancellationToken)
    {
        var resolver = new SecretPlaceholderResolver();
        var handles = new Dictionary<SecretUsage, string>(ReferenceEqualityComparer.Instance);
        try
        {
            var usages = this.scanner.Scan(definition);
            for (var occurrence = 0; occurrence < usages.Count; occurrence++)
            {
                var usage = usages[occurrence];
                var outcome = await accessBroker.AcquireAsync(CreateRequest(usage, lineage, occurrence), cancellationToken).ConfigureAwait(false);
                if (outcome is AccessDenied<StoredSecretAccessResult> or AccessCancelled<StoredSecretAccessResult>)
                {
                    throw new SecretMaterializationRefusedException("Credential use was not authorized.");
                }
                if (outcome is not AccessSucceeded<StoredSecretAccessResult> succeeded)
                {
                    throw new SecretMaterializationFailedException(
                        "A required credential is unavailable.",
                        [new SecretRequestFailure(usage.SecretName, "Credential access is required.", SecretRequestFailureReason.Other)]);
                }
                var handle = Guid.NewGuid().ToString("N");
                resolver.Register($"${{SECRET:{handle}}}", succeeded.Result);
                handles.Add(usage, handle);
            }
            this.scanner.RewritePlaceholders(definition, handles);
            return new MaterializedAgentDefinition { Definition = definition, Resolver = resolver };
        }
        catch
        {
            await resolver.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static StoredSecretAccessRequest CreateRequest(
        SecretUsage usage,
        CredentialUseLineage lineage,
        int occurrence)
    {
        var requestor = new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Manifest, lineage.Manifest?.Digest ?? "manifest"),
            Kind = AccessRequestorKind.Manifest,
            SafeDisplayName = "Manifest",
        };
        var useLineage = lineage with { Key = CredentialIdentityFactory.CreateLineageIdentifier("secret-key", usage.SecretName) };
        var scopes = new List<ReleaseMemoryScopeKind> { ReleaseMemoryScopeKind.AllowOnce, ReleaseMemoryScopeKind.ExactUse, ReleaseMemoryScopeKind.AllUses };
        if (lineage.Session is not null)
        {
            scopes.AddRange([ReleaseMemoryScopeKind.SessionIdentity, ReleaseMemoryScopeKind.KeyInSession]);
        }
        if (lineage.Manifest is not null)
        {
            scopes.Add(ReleaseMemoryScopeKind.ManifestIdentity);
        }
        if (lineage.ManifestContent is not null)
        {
            scopes.AddRange([ReleaseMemoryScopeKind.ManifestContent, ReleaseMemoryScopeKind.KeyInManifestContent]);
        }
        return new StoredSecretAccessRequest
        {
            Reference = new SecretReference { Name = usage.SecretName },
            Credential = new CredentialReference
            {
                LogicalIdentifier = CredentialIdentityFactory.CreateLogicalIdentifier(new CredentialIdentityConfiguration
                {
                    Provider = CredentialProviderKind.StoredSecret,
                    Kind = CredentialKind.Secret,
                    CredentialPartition = usage.SecretName,
                    SourceCompatibility = "platform-or-environment",
                    Authority = "",
                    Audience = "",
                    ClientPartition = "manifest",
                    Scopes = [],
                }),
                Provider = CredentialProviderKind.StoredSecret,
                Kind = CredentialKind.Secret,
            },
            Requestor = requestor,
            Use = new CredentialUseDescriptor
            {
                Kind = CredentialUseKind.Manifest,
                Lineage = useLineage,
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(CredentialUseKind.Manifest, useLineage, requestor.Identifier, $"{usage.JsonPath}:{occurrence}"),
                SafeDisplayName = "Use manifest secret",
                MemoryScopes = scopes,
            },
            PresentationScope = AccessPresentationScope.Runtime,
            Sources = new[] { CredentialSourceKind.SecretStore, CredentialSourceKind.Environment }.Select(kind => new CredentialSourceChoiceDescriptor
            {
                Identifier = CredentialIdentityFactory.CreateSourceIdentifier(CredentialProviderKind.StoredSecret, $"{kind}:{usage.SecretName}"),
                Kind = kind,
                Provider = CredentialProviderKind.StoredSecret,
                SafeDisplayName = kind == CredentialSourceKind.SecretStore ? "Secret store" : "Environment",
                Usability = CredentialSourceUsability.Missing,
            }).ToArray(),
        };
    }
}
