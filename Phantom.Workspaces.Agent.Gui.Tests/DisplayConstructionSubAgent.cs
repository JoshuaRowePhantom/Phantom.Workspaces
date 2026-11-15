using System;
using System.Collections.Generic;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Tests;

internal sealed class DisplayConstructionSubAgent : IRunningSubAgent
{
    private EventHandler? completionStateChanged;

    public string AgentId { get; } = Guid.NewGuid().ToString();
    public string DisplayName => this.AgentId;
    public string Description => "";
    public string Name => "";
    public DateTime LastUpdatedAt => DateTime.MinValue;
    public AgentChatCompletionState CompletionState => AgentChatCompletionState.Running;
    public IReadOnlyList<IRunningSubAgent> Children { get; init; } = [];
    public Exception? ConstructionFailure { get; set; }
    public Exception? CleanupFailure { get; set; }
    public Exception? SubscriptionFailure { get; set; }
    public Action? BeforeReadingChildren { get; set; }
    public IReadOnlyList<IRunningSubAgent> SubAgents
    {
        get
        {
            this.BeforeReadingChildren?.Invoke();
            if (this.ConstructionFailure is { } failure) throw failure;
            return this.Children;
        }
    }

    public int SubscriberCount => this.completionStateChanged?.GetInvocationList().Length ?? 0;
    public event EventHandler? CompletionStateChanged
    {
        add
        {
            this.completionStateChanged += value;
            if (this.SubscriptionFailure is { } failure) throw failure;
        }
        remove
        {
            this.completionStateChanged -= value;
            if (this.CleanupFailure is { } failure) throw failure;
        }
    }

    public void NotifyChanged() => this.completionStateChanged?.Invoke(this, EventArgs.Empty);
}
