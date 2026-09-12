using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public sealed class AgentChatQueueManager
{
    private readonly AgentInputQueueManager inputQueueManager;
    private readonly ObservableCollection<AgentChatQueue> inputQueues = [];
    private readonly Dictionary<string, AgentChatQueue> queuesById = new(StringComparer.Ordinal);
    private int nextUserQueuePriority = 10;
    private int userQueueSequence = 1;

    public AgentChatQueueManager(AgentInputQueueManager inputQueueManager)
    {
        ArgumentNullException.ThrowIfNull(inputQueueManager);
        this.inputQueueManager = inputQueueManager;

        var defaultQueue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = int.MaxValue - 1,
                Immediacy = AgentInputQueueImmediacy.Immediate,
            });
        this.inputQueueManager.RegisterInputQueue(defaultQueue);
        this.DefaultInputQueue = new AgentChatQueue(defaultQueue, "Default Queue", isDefault: true);
        this.ImmediateInputQueue = new AgentChatQueue(
            inputQueueManager.ImmediateQueue,
            "Immediate Queue",
            isDefault: false,
            isImmediate: true);
        this.queuesById[this.DefaultInputQueue.Queue.QueueId] = this.DefaultInputQueue;
        this.queuesById[this.ImmediateInputQueue.Queue.QueueId] = this.ImmediateInputQueue;
        this.inputQueues.Add(this.DefaultInputQueue);
        this.InputQueues = new ReadOnlyObservableCollection<AgentChatQueue>(this.inputQueues);
        this.inputQueueManager.QueueRegistered += this.OnQueueRegistered;
        this.inputQueueManager.QueueUnregistered += this.OnQueueUnregistered;
    }

    public ReadOnlyObservableCollection<AgentChatQueue> InputQueues { get; }

    public AgentChatQueue DefaultInputQueue { get; }

    public AgentChatQueue ImmediateInputQueue { get; }

    public AgentChatQueue CreateInputQueue(
        string? name = null,
        AgentInputQueueImmediacy immediacy = AgentInputQueueImmediacy.Queue)
    {
        var queue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = this.nextUserQueuePriority++,
                Immediacy = immediacy,
            });
        var queueName = string.IsNullOrWhiteSpace(name)
            ? $"Queue {this.userQueueSequence++}"
            : name;
        this.inputQueueManager.SetQueueName(queue.QueueId, queueName);
        this.inputQueueManager.RegisterInputQueue(queue);
        return this.queuesById[queue.QueueId];
    }

    public bool RemoveInputQueue(AgentChatQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (queue.IsDefault)
        {
            return false;
        }

        var removedFromManager = this.inputQueueManager.UnregisterInputQueue(queue.Queue);
        return removedFromManager;
    }

    private void OnQueueRegistered(object? sender, AgentInputQueueManager.QueueRegistrationChangedEventArgs e)
    {
        if (ReferenceEquals(e.Queue, this.inputQueueManager.ImmediateQueue)
            || ReferenceEquals(e.Queue, this.DefaultInputQueue.Queue)
            || this.queuesById.ContainsKey(e.Queue.QueueId))
        {
            return;
        }

        var name = this.inputQueueManager.GetQueueName(e.Queue.QueueId);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Queue {this.userQueueSequence++}";
            this.inputQueueManager.SetQueueName(e.Queue.QueueId, name);
        }

        var wrapped = new AgentChatQueue(e.Queue, name, isDefault: false);
        this.queuesById[e.Queue.QueueId] = wrapped;
        this.inputQueues.Add(wrapped);
    }

    private void OnQueueUnregistered(object? sender, AgentInputQueueManager.QueueRegistrationChangedEventArgs e)
    {
        if (!this.queuesById.Remove(e.Queue.QueueId, out var wrapped)
            || ReferenceEquals(wrapped, this.DefaultInputQueue)
            || ReferenceEquals(wrapped, this.ImmediateInputQueue))
        {
            return;
        }

        this.inputQueues.Remove(wrapped);
    }

    public void SetQueueHeld(AgentChatQueue queue, bool held)
        => this.SetQueueImmediacy(
            queue,
            held
                ? AgentInputQueueImmediacy.Held
                : queue.IsImmediate
                    ? AgentInputQueueImmediacy.Immediate
                    : AgentInputQueueImmediacy.Queue);

    public void SetQueueImmediacy(AgentChatQueue queue, AgentInputQueueImmediacy immediacy)
    {
        ArgumentNullException.ThrowIfNull(queue);
        queue.Queue.Configure(new AgentInputQueue.Parameters
        {
            Priority = queue.Queue.Priority,
            Immediacy = immediacy,
            CoalescingKey = queue.Queue.CoalescingKey,
        });
    }

    public void ClearQueue(AgentChatQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        while (true)
        {
            var expected = queue.Queue.Items;
            if (expected.Count == 0)
            {
                return;
            }

            if (queue.Queue.Clear(ref expected))
            {
                return;
            }
        }
    }

    public bool RemoveQueueItem(AgentChatQueue queue, int index)
        => this.RemoveQueueItem(queue, this.GetItemAt(queue, index));

    public bool RemoveQueueItem(AgentChatQueue queue, AgentInputItem item)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(item);

        while (true)
        {
            var expected = queue.Queue.Items;
            if (!expected.Contains(item))
            {
                return false;
            }

            if (queue.Queue.TryRemove(ref expected, item))
            {
                return true;
            }
        }
    }

    public bool UpdateQueueItem(AgentChatQueue queue, int index, string text)
        => this.UpdateQueueItem(queue, this.GetItemAt(queue, index), text);

    public bool UpdateQueueItem(AgentChatQueue queue, AgentInputItem item, string text)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        while (true)
        {
            var expected = queue.Queue.Items;
            if (!expected.Contains(item))
            {
                return false;
            }

            if (this.TryUpdateQueueItem(queue.Queue, ref expected, item, text))
            {
                return true;
            }
        }
    }

    public bool RemoveQueueItemContent(AgentChatQueue queue, int index, int contentIndex)
        => this.RemoveQueueItemContent(queue, this.GetItemAt(queue, index), contentIndex);

    public bool RemoveQueueItemContent(AgentChatQueue queue, AgentInputItem item, int contentIndex)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(item);

        while (true)
        {
            var expected = queue.Queue.Items;
            if (!expected.Contains(item))
            {
                return false;
            }

            if (this.TryRemoveQueueItemContent(queue.Queue, ref expected, item, contentIndex))
            {
                return true;
            }
        }
    }

    private AgentInputItem GetItemAt(AgentChatQueue queue, int index)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var items = queue.Queue.Items;
        if (index < 0 || index >= items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return items[index];
    }

    private bool TryUpdateQueueItem(
        AgentInputQueue queue,
        ref ImmutableList<AgentInputItem> expected,
        AgentInputItem item,
        string text)
    {
        var existingMessage = item.Messages.Length > 0
            ? item.Messages[0]
            : new ChatMessage(ChatRole.User, []);
        var contents = existingMessage.Contents.ToList();
        var textContentIndex = contents.FindIndex(static content => content is TextContent);
        if (textContentIndex >= 0)
        {
            contents[textContentIndex] = new TextContent(text);
        }
        else
        {
            contents.Insert(0, new TextContent(text));
        }

        bool applied;
        AgentInputQueueManager.QueueStateChangeKind kind;
        if (contents.Count == 0)
        {
            applied = queue.TryRemove(ref expected, item);
            kind = AgentInputQueueManager.QueueStateChangeKind.ItemRemoved;
        }
        else
        {
            var updatedMessages = item.Messages.ToArray();
            if (updatedMessages.Length == 0)
            {
                updatedMessages = [new ChatMessage(ChatRole.User, contents)];
            }
            else
            {
                updatedMessages[0] = new ChatMessage(ChatRole.User, contents);
            }

            applied = queue.TryUpdate(
                ref expected,
                item,
                item with
                {
                    Messages = updatedMessages,
                });
            kind = AgentInputQueueManager.QueueStateChangeKind.ItemAdded;
        }

        if (applied)
        {
            // #1485: keep the common queue aggregate in lock-step with legacy edits.
            this.inputQueueManager.NotifyLegacyMutationApplied(queue, kind);
        }
        return applied;
    }

    private bool TryRemoveQueueItemContent(
        AgentInputQueue queue,
        ref ImmutableList<AgentInputItem> expected,
        AgentInputItem item,
        int contentIndex)
    {
        var existingMessage = item.Messages.Length > 0
            ? item.Messages[0]
            : new ChatMessage(ChatRole.User, []);
        var contents = existingMessage.Contents.ToList();
        if (contentIndex < 0 || contentIndex >= contents.Count)
        {
            return false;
        }

        contents.RemoveAt(contentIndex);
        bool applied;
        AgentInputQueueManager.QueueStateChangeKind kind;
        if (contents.Count == 0)
        {
            applied = queue.TryRemove(ref expected, item);
            kind = AgentInputQueueManager.QueueStateChangeKind.ItemRemoved;
        }
        else
        {
            var updatedMessages = item.Messages.ToArray();
            if (updatedMessages.Length == 0)
            {
                updatedMessages = [new ChatMessage(ChatRole.User, contents)];
            }
            else
            {
                updatedMessages[0] = new ChatMessage(ChatRole.User, contents);
            }

            applied = queue.TryUpdate(
                ref expected,
                item,
                item with
                {
                    Messages = updatedMessages,
                });
            kind = AgentInputQueueManager.QueueStateChangeKind.ItemAdded;
        }

        if (applied)
        {
            this.inputQueueManager.NotifyLegacyMutationApplied(queue, kind);
        }
        return applied;
    }
}
