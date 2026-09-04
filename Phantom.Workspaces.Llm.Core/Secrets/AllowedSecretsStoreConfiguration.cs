namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Configuration for <see cref="AllowedSecretsStore"/>, carrying the (optional) path to the
/// allowed-secrets JSON file.
/// </summary>
public sealed record AllowedSecretsStoreConfiguration
{
    /// <summary>
    /// Absolute path to the allowed-secrets JSON file. When <see langword="null"/>, defaults to
    /// <c>allowed-secrets.json</c> next to the primary <c>config.json</c> (i.e. under
    /// <c>%APPDATA%\Phantom.Workspaces\</c>), keeping the two files siblings by default.
    /// </summary>
    public string? Path { get; init; }
}
