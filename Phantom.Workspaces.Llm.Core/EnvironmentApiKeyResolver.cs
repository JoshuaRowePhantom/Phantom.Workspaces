namespace Phantom.Workspaces.Llm;

/// <summary>
/// Production implementation of <see cref="IApiKeyResolver"/> that delegates to
/// <see cref="AgentFactory.ResolveApiKeyAsync"/>, which expands <c>${VAR}</c> references from
/// the process environment and falls back to the GitHub CLI for <c>GITHUB_TOKEN</c>.
/// </summary>
public sealed class EnvironmentApiKeyResolver : IApiKeyResolver
{
    /// <summary>The shared singleton instance.</summary>
    public static readonly EnvironmentApiKeyResolver Instance = new();

    /// <inheritdoc />
    public Task<string> ResolveApiKeyAsync(string? apiKeyValue, string? serverName, CancellationToken cancellationToken = default)
        => AgentFactory.ResolveApiKeyAsync(apiKeyValue, serverName, cancellationToken);
}
