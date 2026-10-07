using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// A currently-running item with model payload for GUI data templates.
/// </summary>
public sealed class AgentChatRunningItem
{
    public string? AssistantRunId { get; set; }

    public ObservableCollection<AgentChatHistoryItem> Items { get; } = [];
}
