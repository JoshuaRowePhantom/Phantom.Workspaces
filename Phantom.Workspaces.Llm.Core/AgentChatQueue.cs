using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// The default queue abstraction for the chat UI.
/// </summary>
public sealed class AgentChatQueue
{
    internal AgentChatQueue(AgentInputQueue queue, string name, bool isDefault, bool isImmediate = false)
    {
        this.Queue = queue;
        this.Name = name;
        this.IsDefault = isDefault;
        this.IsImmediate = isImmediate;
        this.Queue.Changed += this.OnQueueChanged;
    }

    internal AgentInputQueue Queue { get; }

    public string Name { get; }

    public bool IsDefault { get; }

    public bool IsImmediate { get; }

    public bool IsHeld => this.Queue.Immediacy == AgentInputQueueImmediacy.Held;

    public AgentInputQueueImmediacy Immediacy => this.Queue.Immediacy;

    public IReadOnlyList<AgentInputItem> Items => this.Queue.Items;

    public event EventHandler? Changed;

    private void OnQueueChanged(object? sender, EventArgs e) => this.Changed?.Invoke(this, EventArgs.Empty);
}
