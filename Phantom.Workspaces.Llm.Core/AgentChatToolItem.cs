namespace Phantom.Workspaces.Llm;

public sealed record AgentChatToolItem(
    string Id,
    string Name,
    string Description,
    string Instructions,
    string Kind,
    bool IsEnabled,
    IReadOnlyList<AgentChatToolItem> Children,
    string? Status = null);
