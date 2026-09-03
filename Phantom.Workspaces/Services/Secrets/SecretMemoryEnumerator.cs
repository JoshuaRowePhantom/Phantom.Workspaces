using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Services.Secrets;

public sealed class SecretMemoryEnumerator
{
    private readonly IAllowedSecretsStore allowedSecretsStore;
    private readonly IPlatformSecretStore platformSecretStore;

    public SecretMemoryEnumerator(IAllowedSecretsStore allowedSecretsStore, IPlatformSecretStore platformSecretStore)
    {
        ArgumentNullException.ThrowIfNull(allowedSecretsStore);
        ArgumentNullException.ThrowIfNull(platformSecretStore);
        this.allowedSecretsStore = allowedSecretsStore;
        this.platformSecretStore = platformSecretStore;
    }

    public async Task<SecretMemorySnapshot> EnumerateAsync(CancellationToken ct)
    {
        var all = await this.allowedSecretsStore.LoadAllAsync(ct).ConfigureAwait(false);
        var groups = all
            .Select(pair => new SecretMemoryUse(pair.Key, pair.Value.Memory, pair.Value.Source))
            .GroupBy(use => use.Source)
            .Select(group => new SecretMemoryGroup(group.Key, group.OrderBy(use => use.Memory.DisplayString, StringComparer.Ordinal).ToArray()))
            .OrderBy(group => SecretSourceDisplay.GetLabel(group.Source), StringComparer.Ordinal)
            .ToArray();

        var savedNames = await this.platformSecretStore.EnumerateNamesAsync(string.Empty, ct).ConfigureAwait(false);
        var usedCredentialNames = all.Values
            .Select(static record => record.Source)
            .OfType<CredentialStoreSecretSource>()
            .Select(static source => source.CredentialName)
            .ToHashSet(StringComparer.Ordinal);

        var unused = savedNames
            .Where(name => !usedCredentialNames.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        return new SecretMemorySnapshot(groups, unused);
    }
}

public sealed record SecretMemorySnapshot(
    IReadOnlyList<SecretMemoryGroup> Groups,
    IReadOnlyList<string> UnusedSavedCredentialNames);

public sealed record SecretMemoryGroup(SecretSource Source, IReadOnlyList<SecretMemoryUse> UsePlaces);

public sealed record SecretMemoryUse(string Hash, SecretUseMemory Memory, SecretSource Source);

public static class SecretSourceDisplay
{
    public static string GetLabel(SecretSource source)
        => source switch
        {
            GitHubLoginSecretSource => "GitHub login token",
            CredentialStoreSecretSource credential => $"Saved credential '{credential.CredentialName}'",
            AwsLoginSecretSource => "AWS login (not yet implemented)",
            AzureLoginSecretSource => "Azure login (not yet implemented)",
            OAuthSecretSource => "OAuth sign-in",
            _ => source.ToString() ?? "Unknown secret source",
        };
}
