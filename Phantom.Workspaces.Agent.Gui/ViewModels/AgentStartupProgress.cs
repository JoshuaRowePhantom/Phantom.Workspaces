using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentStartupProgress
{
    public ObservableCollection<AgentChatHistoryItem> History { get; } = [];
    public ObservableCollection<AgentChatRunningItem> RunningItems { get; } = [];

    public void Report(string sessionName, string stage)
    {
        var description = stage switch
        {
            "service-initialization" => $"Preparing session: {sessionName}",
            "owner-profile-lookup" or "transport-choice" => $"Connecting to owner: {sessionName}",
            "owner-decision" => $"Waiting for your owner selection: {sessionName}",
            "remote-flight-chat-acquisition" => $"Loading session and history: {sessionName}",
            "ready-publication" => $"Opening chat: {sessionName}",
            _ => $"Loading {stage}: {sessionName}",
        };
        this.RunningItems.Clear();
        var item = new AgentChatRunningItem();
        item.Items.Add(new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents = [new TextContent(description)],
        });
        this.RunningItems.Add(item);
    }
}
