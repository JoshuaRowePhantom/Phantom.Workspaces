namespace Phantom.Workspaces.Llm;

/// <summary>
/// The factory-resolved connection facts for a bring-your-own-key (BYOK) Copilot session: which
/// BYOK provider was selected (via the agent definition's provider string) and the endpoint and
/// credential resolved from the model connection. When supplied, the Copilot session is pointed
/// at a custom OpenAI-compatible API endpoint instead of GitHub's hosted models. The remaining
/// wire knobs (<c>wireApi</c>, <c>wireModel</c>, <c>headers</c>) live in the model options and
/// are interpreted by <see cref="CopilotSdkChatClient.CreateProviderConfig"/>, not by the
/// factory (issue #896).
/// </summary>
/// <remarks>
/// When adding BYOK fields, update the workspace documentation entity:
/// <c>["documentation", "agent-options", "connections"]</c>.
/// </remarks>
public sealed record CopilotByokOptions
{
    /// <summary>
    /// The agent-definition provider string that selected BYOK mode: <c>openai</c> or
    /// <c>azure-openai</c>. Mapped to the Copilot SDK provider type by
    /// <see cref="CopilotSdkChatClient.CreateProviderConfig"/>.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>Absolute base URL of the OpenAI-compatible API endpoint.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>API key for the custom endpoint, if it requires one.</summary>
    public string? ApiKey { get; init; }
}
