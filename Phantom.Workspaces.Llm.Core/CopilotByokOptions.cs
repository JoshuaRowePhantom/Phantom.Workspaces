using System.Collections.Generic;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Bring-your-own-key (BYOK) configuration for the GitHub Copilot provider. When supplied, the
/// Copilot session is pointed at a custom OpenAI-compatible API endpoint instead of GitHub's
/// hosted models, allowing the provider to be exercised against any compatible server (including
/// a local test chat provider).
/// </summary>
/// <remarks>
/// When adding BYOK fields, update the workspace documentation entity:
/// <c>["documentation", "agent-options", "connections"]</c>.
/// </remarks>
public sealed record CopilotByokOptions
{
    /// <summary>Absolute base URL of the OpenAI-compatible API endpoint.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>API key for the custom endpoint, if it requires one.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Bearer token for the custom endpoint, if it uses bearer auth.</summary>
    public string? BearerToken { get; init; }

    /// <summary>Provider type understood by the Copilot runtime (defaults to <c>openai</c>).</summary>
    public string ProviderType { get; init; } = "openai";

    /// <summary>Wire API the endpoint speaks (defaults to <c>chat-completions</c>).</summary>
    public string WireApi { get; init; } = "chat-completions";

    /// <summary>Wire model name when it differs from the model id.</summary>
    public string? WireModel { get; init; }

    /// <summary>Extra request headers to send to the custom endpoint.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
