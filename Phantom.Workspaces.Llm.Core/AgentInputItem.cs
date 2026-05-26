using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public sealed record AgentInputItem
{
    public required ChatMessage[] Messages { get; init; }

    public AgentChatSession? ResetSession { get; init; }

    public IReadOnlyList<AIContent> Contents => this.Messages?.SelectMany(m => m.Contents).ToArray() ?? Array.Empty<AIContent>();

    public string Text => string.Concat(
        this.Messages.SelectMany(message => message.Contents).Select(FormatContentAsText));

    private static string FormatContentAsText(AIContent content) => content switch
    {
        TextContent textContent => textContent.Text,
        DataContent dataContent when !string.IsNullOrWhiteSpace(dataContent.MediaType) => $"[{dataContent.MediaType}]",
        DataContent => "[data]",
        UriContent uriContent when !string.IsNullOrWhiteSpace(uriContent.MediaType) => $"[{uriContent.MediaType}] {uriContent.Uri}",
        UriContent uriContent => uriContent.Uri.ToString(),
        _ => $"[{content.GetType().Name}]",
    };
}
