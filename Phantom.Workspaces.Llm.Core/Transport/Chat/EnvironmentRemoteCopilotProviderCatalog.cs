using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Llm.Core.Transport.Chat;

/// <summary>
/// Inspects remote Copilot BYOK metadata in worker-local environment variables.
/// Credential values are acquired separately through the host access broker.
/// </summary>
internal sealed class EnvironmentRemoteCopilotProviderCatalog : IRemoteCopilotProviderCatalog
{
    private readonly Func<string, string?> getEnvironmentVariable;

    /// <summary>Creates a catalog over the current process environment.</summary>
    public EnvironmentRemoteCopilotProviderCatalog()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    internal EnvironmentRemoteCopilotProviderCatalog(
        Func<string, string?> getEnvironmentVariable)
    {
        this.getEnvironmentVariable = getEnvironmentVariable
            ?? throw new ArgumentNullException(nameof(getEnvironmentVariable));
    }

    /// <inheritdoc />
    public Task<RemoteCopilotProviderMetadata?> GetMetadataAsync(
        string providerReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(providerReference);
        if (providerReference.Length > 128
            || providerReference.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character)
                  || character is '-' or '_' or '.')))
        {
            return Task.FromResult<RemoteCopilotProviderMetadata?>(null);
        }

        var prefix = "PHANTOM_COPILOT_PROVIDER_"
            + providerReference
                .ToUpperInvariant()
                .Replace('-', '_')
                .Replace('.', '_')
            + "_";
        var providerType = this.getEnvironmentVariable(prefix + "TYPE");
        var baseUrl = this.getEnvironmentVariable(prefix + "BASE_URL");
        if (string.IsNullOrWhiteSpace(providerType)
            || providerType is not ("openai" or "azure")
            || string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            return Task.FromResult<RemoteCopilotProviderMetadata?>(null);
        }

        var provider = new RemoteCopilotProviderMetadata
        {
            Type = providerType,
            BaseUrl = baseUrl,
            EnvironmentKeyName = prefix + "API_KEY",
            WireApi = NullIfWhiteSpace(this.getEnvironmentVariable(prefix + "WIRE_API"))
                ?? "chat-completions",
            WireModel = NullIfWhiteSpace(this.getEnvironmentVariable(prefix + "WIRE_MODEL")),
        };
        return Task.FromResult<RemoteCopilotProviderMetadata?>(provider);
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
