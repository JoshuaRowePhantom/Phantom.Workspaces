using AgentSchema;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Scans an agent definition for secret placeholders, requests the corresponding secret
/// retrievers, and rewrites the definition to opaque reference tokens only.
/// </summary>
public sealed class AgentDefinitionSecretMaterializer
{
    private readonly SecretUsageScanner scanner;
    private readonly AgentManifestSecretUseMemoryFactory memoryFactory;
    private readonly IPlatformSecretStore? platformSecretStore;

    public AgentDefinitionSecretMaterializer(
        SecretUsageScanner? scanner = null,
        AgentManifestSecretUseMemoryFactory? memoryFactory = null,
        IPlatformSecretStore? platformSecretStore = null)
    {
        this.scanner = scanner ?? new SecretUsageScanner();
        this.memoryFactory = memoryFactory ?? new AgentManifestSecretUseMemoryFactory();
        this.platformSecretStore = platformSecretStore;
    }

    public async Task<MaterializedAgentDefinition> MaterializeAsync(
        AgentDefinition definition,
        ISecretProvider secretProvider,
        CancellationToken ct,
        AgentManifest? manifest = null,
        string? agentSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(secretProvider);

        var usages = this.scanner.Scan(definition);
        if (usages.Count == 0)
        {
            return new MaterializedAgentDefinition(definition, SecretPlaceholderResolver.Empty);
        }

        var lineage = AgentManifestSecretUseMemoryFactory.CreateLineage(manifest, definition, agentSessionId);

        var credentialNames = await this.EnumerateCredentialNamesAsync(ct).ConfigureAwait(false);
        var requests = usages
            .Select(usage => this.BuildRequest(lineage, usage, credentialNames))
            .ToArray();

        var result = await secretProvider.RequestSecretsAsync(requests, ct).ConfigureAwait(false);
        if (result is null)
        {
            throw new SecretMaterializationRefusedException("Secret materialization was refused.");
        }

        var requestedNames = usages.Select(usage => usage.SecretName).ToHashSet(StringComparer.Ordinal);
        var relevantFailures = result.FailedSecrets
            .Where(failure => requestedNames.Contains(failure.SecretName))
            .ToArray();
        if (relevantFailures.Length > 0)
        {
            throw new SecretMaterializationFailedException(
                $"Failed to materialize {relevantFailures.Length} secret(s): "
                + string.Join(", ", relevantFailures.Select(f => f.SecretName).Distinct(StringComparer.Ordinal)),
                relevantFailures);
        }

        var retrieversByName = result.AcquiredSecrets
            .GroupBy(retriever => retriever.SecretName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var resolver = new SecretPlaceholderResolver();
        var usageToHandle = new Dictionary<SecretUsage, string>();
        foreach (var usage in usages)
        {
            if (!retrieversByName.TryGetValue(usage.SecretName, out var retriever))
            {
                throw new SecretMaterializationFailedException(
                    $"Failed to materialize secret '{usage.SecretName}'.",
                    [new SecretRequestFailure(usage.SecretName, "Secret was not acquired", SecretRequestFailureReason.Other)]);
            }

            var handle = Guid.NewGuid().ToString("N");
            var token = $"${{SECRET:{handle}}}";
            resolver.Register(token, retriever);
            usageToHandle[usage] = handle;
        }

        this.scanner.RewritePlaceholders(definition, usageToHandle);
        return new MaterializedAgentDefinition(definition, resolver);
    }

    private async Task<IReadOnlyList<string>> EnumerateCredentialNamesAsync(CancellationToken ct)
    {
        if (this.platformSecretStore is null)
        {
            return [];
        }

        return await this.platformSecretStore.EnumerateNamesAsync(string.Empty, ct).ConfigureAwait(false);
    }

    private SecretRequest BuildRequest(
        AgentManifestSecretUseMemoryFactory.SecretUseLineage lineage,
        SecretUsage usage,
        IReadOnlyList<string> credentialNames)
    {
        var credentialSources = credentialNames
            .Select(name => new CredentialStoreSecretSource(name))
            .Cast<SecretSource>()
            .ToArray();
        var candidateSources = credentialSources
            .Concat([new GitHubLoginSecretSource(), new AwsLoginSecretSource(), new AzureLoginSecretSource()])
            .ToArray();

        var defaultSource = ChooseDefaultSource(usage.SecretName, credentialSources);
        return new SecretRequest(
            usage.SecretName,
            usage.JsonPath,
            this.memoryFactory.Build(lineage, usage.SecretName, usage.JsonPath),
            defaultSource,
            candidateSources);
    }

    private static SecretSource? ChooseDefaultSource(string secretName, IReadOnlyList<SecretSource> credentialSources)
    {
        if (secretName.Contains("Github", StringComparison.OrdinalIgnoreCase)
            || secretName.Contains("GitHub", StringComparison.Ordinal))
        {
            return new GitHubLoginSecretSource();
        }

        if (secretName.Contains("Aws", StringComparison.OrdinalIgnoreCase))
        {
            return new AwsLoginSecretSource();
        }

        if (secretName.Contains("Azure", StringComparison.OrdinalIgnoreCase))
        {
            return new AzureLoginSecretSource();
        }

        return credentialSources
            .OfType<CredentialStoreSecretSource>()
            .FirstOrDefault(source => string.Equals(source.CredentialName, secretName, StringComparison.OrdinalIgnoreCase))
            ?? credentialSources.OfType<CredentialStoreSecretSource>().FirstOrDefault();
    }
}
