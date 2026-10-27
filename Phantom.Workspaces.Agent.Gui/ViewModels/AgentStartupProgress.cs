using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentStartupProgress : IDisposable
{
    private IAgentChat? observed;
    private readonly HashSet<AgentChatHistoryItem> observedHistory = new(ReferenceEqualityComparer.Instance);
    private bool disposed;
    public IAgentChat? ObservedChat => this.observed;
    public ObservableCollection<AgentChatHistoryItem> History { get; } = [];
    public ObservableCollection<AgentChatRunningItem> RunningItems { get; } = [];
    public void ReportFailure(string safeMessage)
    {
        if (!this.disposed)
            this.History.Add(new AgentChatHistoryItem
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = [new TextContent(safeMessage)],
            });
    }

    public void Observe(IAgentChat chat)
    {
        if (this.disposed)
            return;
        this.Unsubscribe();
        this.observed = chat;
        ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += this.OnRunningItemsChanged;
        ((INotifyCollectionChanged)chat.History).CollectionChanged += this.OnHistoryChanged;
        this.SynchronizeRunningItems();
        this.SynchronizeHistory();
    }

    private void OnRunningItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => this.SynchronizeRunningItems();

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => this.SynchronizeHistory();

    private void SynchronizeHistory()
    {
        if (this.observed is not { } chat)
            return;
        foreach (var removed in this.observedHistory.Where(item => !chat.History.Contains(item)).ToArray())
        {
            this.History.Remove(removed);
            this.observedHistory.Remove(removed);
        }
        foreach (var added in chat.History)
            if (this.observedHistory.Add(added))
                this.History.Add(added);
    }

    private void SynchronizeRunningItems()
    {
        if (this.observed is not { } chat)
            return;
        foreach (var removed in this.RunningItems.Except(chat.RunningItems).ToArray())
            this.RunningItems.Remove(removed);
        foreach (var added in chat.RunningItems.Except(this.RunningItems))
            this.RunningItems.Add(added);
    }

    private void Unsubscribe()
    {
        if (this.observed is { } chat)
        {
            ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged -= this.OnRunningItemsChanged;
            ((INotifyCollectionChanged)chat.History).CollectionChanged -= this.OnHistoryChanged;
        }
        foreach (var item in this.observedHistory)
            this.History.Remove(item);
        this.observedHistory.Clear();
        this.observed = null;
    }

    public void Dispose()
    {
        this.disposed = true;
        this.Unsubscribe();
        this.RunningItems.Clear();
    }

    public void Report(string sessionName, string stage)
    {
        if (this.disposed || this.observed is not null)
            return;
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
