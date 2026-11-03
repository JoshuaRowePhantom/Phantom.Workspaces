using System.Collections.ObjectModel;
using System.Text.Json;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Agent.Gui.Tests;

internal sealed class ToolProjectionChat(IAgentChat inner) : IAgentChat
{
    private IReadOnlyList<AgentChatToolItem> snapshot = [];
    private EventHandler? toolsChanged;
    public Action? Subscribed { get; set; }
    public Action? SnapshotRead { get; set; }
    public Action? UnsubscribingInformation { get; set; }
    public int SubscriberCount => this.toolsChanged?.GetInvocationList().Length ?? 0;
    public EventHandler? CaptureDelivery() => this.toolsChanged;
    public void Publish(params AgentChatToolItem[] tools)
    {
        Volatile.Write(ref this.snapshot, tools);
        this.toolsChanged?.Invoke(this, EventArgs.Empty);
    }

    public static AgentChatToolItem Tool(string id, bool enabled = true, string? status = null)
        => new(id, id, "", "", "mcp", enabled, [], status);

    public IReadOnlyList<AgentChatToolItem> GetToolSnapshot()
    {
        var result = Volatile.Read(ref this.snapshot);
        this.SnapshotRead?.Invoke();
        return result;
    }

    public event EventHandler? ToolsChanged
    {
        add { this.toolsChanged += value; this.Subscribed?.Invoke(); }
        remove { this.toolsChanged -= value; }
    }
    public AgentInformation Information => inner.Information;
    public Usage Usage => inner.Usage;
    public bool IsBusy => inner.IsBusy;
    public AgentChatHistoryCollection History => inner.History;
    public Task HistoryPopulated => inner.HistoryPopulated;
    public AgentChatRunningItemCollection RunningItems => inner.RunningItems;
    public IAgentInputQueues InputQueues => inner.InputQueues;
    public ReadOnlyObservableCollection<IRunningSubAgent> SubAgents => inner.SubAgents;
    public ReadOnlyObservableCollection<AgentChatModal> Modals => inner.Modals;
    public ISlashCommandRegistry SlashCommands => inner.SlashCommands;
    public event EventHandler? InformationChanged
    {
        add => inner.InformationChanged += value;
        remove { this.UnsubscribingInformation?.Invoke(); inner.InformationChanged -= value; }
    }
    public event EventHandler? UsageChanged { add => inner.UsageChanged += value; remove => inner.UsageChanged -= value; }
    public event EventHandler<AgentChatHistoryItem>? TurnCompleted { add => inner.TurnCompleted += value; remove => inner.TurnCompleted -= value; }
    public Task SetToolEnabledAsync(string id, bool enabled, CancellationToken ct = default) => inner.SetToolEnabledAsync(id, enabled, ct);
    public Task RespondToModalAsync(string id, JsonElement response, CancellationToken ct = default) => inner.RespondToModalAsync(id, response, ct);
    public void EnqueueSystemNote(string text) => inner.EnqueueSystemNote(text);
    public void EnqueueHelpNote(string text) => inner.EnqueueHelpNote(text);
    public void EnqueueTransientDiagnostic(string text) => inner.EnqueueTransientDiagnostic(text);
    public Task InterruptAsync(CancellationToken ct = default) => inner.InterruptAsync(ct);
    public object? GetService(Type type) => inner.GetService(type);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal sealed class ToolProjectionScheduler : TaskScheduler
{
    private readonly Queue<Task> tasks = new();
    public bool RejectNext { get; set; }
    public int Count => this.tasks.Count;
    protected override IEnumerable<Task> GetScheduledTasks() => this.tasks.ToArray();
    protected override void QueueTask(Task task)
    {
        if (this.RejectNext)
        {
            this.RejectNext = false;
            throw new InvalidOperationException("deterministic-scheduler-rejection");
        }
        this.tasks.Enqueue(task);
    }
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    public void Drain()
    {
        while (this.tasks.TryDequeue(out var task))
            this.TryExecuteTask(task);
    }
}
