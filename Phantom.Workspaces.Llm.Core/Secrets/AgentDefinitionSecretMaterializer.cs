using System.Linq;
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
        CancellationToken cancellationToken,
        AccessPresentationScope presentationScope = AccessPresentationScope.Runtime,
        AccessRequestor? requestor = null,
        CredentialUseDescriptor? parentUse = null,
        Func<AccessPresentationScope>? presentationScopeProvider = null)
        => await this.MaterializeScopeAsync(
            definition, accessBroker, lineage, cancellationToken, presentationScope,
            requestor, parentUse, presentationScopeProvider, serverScope: false).ConfigureAwait(false);

    internal Task<MaterializedAgentDefinition> MaterializeServerAsync(
        McpTool tool,
        AgentServices services,
        CancellationToken cancellationToken)
    {
        var definition = PhantomAgentSchema.AgentDefinitionFromJson(
            new PromptAgent { Name = "mcp-server-credentials", Tools = [tool] }.ToJson())!;
        var parentResolver = services.SecretPlaceholderResolver as ISecretPlaceholderResolver;
        foreach (var usage in this.scanner.Scan(definition))
        {
            var reference = $"${{SECRET:{usage.SecretName}}}";
            if (services.DeferredSecretReferences?.Contains(reference, StringComparer.Ordinal) != true
                && parentResolver?.TryResolve(reference, out _) != true)
                throw new InvalidOperationException("MCP credential reference is not registered.");
        }
        return this.MaterializeScopeAsync(
            definition,
            services.AccessBroker,
            services.AccessUse?.Lineage ?? services.AccessLineage ?? new CredentialUseLineage(),
            cancellationToken,
            services.AccessPresentationScope,
            services.AccessRequestor ?? new AccessRequestor
            {
                Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(
                    AccessRequestorKind.McpServer, tool.ServerName ?? tool.Name ?? "mcp-server"),
                Kind = AccessRequestorKind.McpServer,
                SafeDisplayName = tool.ServerName ?? tool.Name ?? "MCP server",
            },
            services.AccessUse,
            () => services.AccessPresentationScope,
            serverScope: true,
            parentResolver,
            services.DeferredSecretReferences);
    }

    private async Task<MaterializedAgentDefinition> MaterializeScopeAsync(
        AgentDefinition definition,
        IAccessBroker? accessBroker,
        CredentialUseLineage lineage,
        CancellationToken cancellationToken,
        AccessPresentationScope presentationScope,
        AccessRequestor? requestor,
        CredentialUseDescriptor? parentUse,
        Func<AccessPresentationScope>? presentationScopeProvider,
        bool serverScope,
        ISecretPlaceholderResolver? parentResolver = null,
        IReadOnlyCollection<string>? deferredReferences = null)
    {
        var resolver = new SecretPlaceholderResolver(parentResolver);
        var handles = new Dictionary<SecretUsage, string>(ReferenceEqualityComparer.Instance);
        try
        {
            // Preflight owns required-core uses. A connection attempt owns only its explicitly
            // deferred references, leaving borrowed core handles and sibling scopes untouched.
            var partition = this.scanner.PartitionByOwner(definition);
            var usages = serverScope
                ? this.scanner.Scan(definition).Where(usage =>
                    deferredReferences?.Contains($"${{SECRET:{usage.SecretName}}}", StringComparer.Ordinal) == true).ToArray()
                : partition.RequiredCore;
            for (var occurrence = 0; occurrence < usages.Count; occurrence++)
            {
                var usage = usages[occurrence];
                var outcome = await (accessBroker ?? throw new InvalidOperationException("MCP credential access is unavailable.")).AcquireAsync(
                    CreateRequest(usage, lineage, occurrence, presentationScopeProvider?.Invoke() ?? presentationScope, requestor, parentUse,
                        serverScope ? CredentialUseKind.Mcp : CredentialUseKind.Manifest),
                    cancellationToken).ConfigureAwait(false);
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
                cancellationToken.ThrowIfCancellationRequested();
                handles.Add(usage, handle);
            }
            if (handles.Count > 0)
            {
                this.scanner.RewritePlaceholders(definition, handles);
            }
            var remainingReferences = (serverScope ? [] : partition.DeferredMcpServer)
                .Select(usage => $"${{SECRET:{usage.SecretName}}}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new MaterializedAgentDefinition
            {
                Definition = definition,
                Resolver = resolver,
                DeferredSecretReferences = remainingReferences,
            };
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
        int occurrence,
        AccessPresentationScope presentationScope,
        AccessRequestor? suppliedRequestor,
        CredentialUseDescriptor? parentUse,
        CredentialUseKind useKind)
    {
        var requestor = suppliedRequestor ?? new AccessRequestor
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
        var purpose = $"{usage.JsonPath}:{occurrence}";
        if (parentUse is not null)
        {
            purpose = $"{parentUse.Fingerprint.Digest}:{purpose}";
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
                Kind = useKind,
                Lineage = useLineage,
                Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                    useKind, useLineage, requestor.Identifier,
                    purpose),
                SafeDisplayName = useKind == CredentialUseKind.Mcp ? $"Connect {requestor.SafeDisplayName}" : "Use manifest secret",
                MemoryScopes = parentUse?.MemoryScopes ?? scopes,
            },
            PresentationScope = presentationScope,
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
