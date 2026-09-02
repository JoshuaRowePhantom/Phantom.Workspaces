namespace Phantom.Workspaces.Llm;

/// <summary>
/// Resolves an API key value, expanding environment-variable references of the form
/// <c>${VAR_NAME}</c> as needed.
/// </summary>
public interface IApiKeyResolver
{
    /// <summary>
    /// Asynchronously resolves the key.
    /// </summary>
    /// <param name="apiKeyValue">
    /// The raw key value from the agent definition (may be a literal or a <c>${VAR}</c> reference).
    /// </param>
    /// <param name="serverName">
    /// The MCP server / provider name used in error messages.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the resolution operation.</param>
    /// <returns>The resolved API key string.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the key cannot be resolved (missing environment variable, etc.).
    /// </exception>
    Task<string> ResolveApiKeyAsync(string? apiKeyValue, string? serverName, CancellationToken cancellationToken = default);
}
