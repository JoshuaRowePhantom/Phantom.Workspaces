using GitHub.Copilot;

namespace Phantom.Workspaces.Llm.Core.Transport.Chat;

/// <summary>
/// Resolves remote Copilot BYOK providers from worker-local environment variables. The caller sends
/// only an opaque reference; endpoint and credential values remain in the worker process.
/// </summary>
public sealed class EnvironmentRemoteCopilotProviderResolver : IRemoteCopilotProviderResolver
{
    private readonly Func<string, string?> getEnvironmentVariable;

    /// <summary>Creates a resolver over the current process environment.</summary>
    public EnvironmentRemoteCopilotProviderResolver()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    internal EnvironmentRemoteCopilotProviderResolver(
        Func<string, string?> getEnvironmentVariable)
    {
        this.getEnvironmentVariable = getEnvironmentVariable
            ?? throw new ArgumentNullException(nameof(getEnvironmentVariable));
    }

    /// <inheritdoc />
    public Task<ProviderConfig?> ResolveAsync(
        string providerReference,
        string modelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(providerReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (providerReference.Length > 128
            || providerReference.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character)
                  || character is '-' or '_' or '.')))
        {
            return Task.FromResult<ProviderConfig?>(null);
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
            return Task.FromResult<ProviderConfig?>(null);
        }

        var provider = new ProviderConfig
        {
            Type = providerType,
            BaseUrl = baseUrl,
            ApiKey = NullIfWhiteSpace(this.getEnvironmentVariable(prefix + "API_KEY")),
            WireApi = NullIfWhiteSpace(this.getEnvironmentVariable(prefix + "WIRE_API"))
                ?? "chat-completions",
            WireModel = NullIfWhiteSpace(this.getEnvironmentVariable(prefix + "WIRE_MODEL")),
            ModelId = modelId,
        };
        return Task.FromResult<ProviderConfig?>(provider);
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
