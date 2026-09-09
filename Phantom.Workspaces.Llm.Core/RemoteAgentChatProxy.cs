using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// In-process proxy over an <see cref="IAgentChat"/> that exposes the same common surface without
/// relying on concrete <see cref="AgentChat"/> members. Commit 2 uses this as the transport-free
/// parity seam for common-surface tests and UI construction.
/// </summary>
public sealed class RemoteAgentChatProxy : IAgentChat
{
    private readonly IAgentChat source;
    private readonly RemoteAgentInputQueuesProxy inputQueues;

    public RemoteAgentChatProxy(IAgentChat source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.inputQueues = new RemoteAgentInputQueuesProxy(source.InputQueues);
        source.InformationChanged += this.ForwardInformationChanged;
        source.ToolsChanged += this.ForwardToolsChanged;
        source.UsageChanged += this.ForwardUsageChanged;
        source.TurnCompleted += this.ForwardTurnCompleted;
    }

    public AgentInformation Information => Clone(this.source.Information);
    public Usage Usage => this.source.Usage;
    public bool IsBusy => this.source.IsBusy;
    public AgentChatHistoryCollection History => this.source.History;
    public Task HistoryPopulated => this.source.HistoryPopulated;
    public AgentChatRunningItemCollection RunningItems => this.source.RunningItems;
    public IAgentInputQueues InputQueues => this.inputQueues;
    public ReadOnlyObservableCollection<IRunningSubAgent> SubAgents => this.source.SubAgents;
    public ReadOnlyObservableCollection<AgentChatModal> Modals => this.source.Modals;
    public ISlashCommandRegistry SlashCommands => this.source.SlashCommands;

    public event EventHandler? InformationChanged;
    public event EventHandler? ToolsChanged;
    public event EventHandler? UsageChanged;
    public event EventHandler<AgentChatHistoryItem>? TurnCompleted;

    public IReadOnlyList<AgentChatToolItem> GetToolSnapshot() => this.source.GetToolSnapshot();

    public Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct = default)
        => this.source.SetToolEnabledAsync(toolId, enabled, ct);

    public Task RespondToModalAsync(string modalId, JsonElement response, CancellationToken ct = default)
        => this.source.RespondToModalAsync(modalId, response, ct);

    public void EnqueueSystemNote(string text) => this.source.EnqueueSystemNote(text);
    public void EnqueueHelpNote(string text) => this.source.EnqueueHelpNote(text);
    public void EnqueueTransientDiagnostic(string text) => this.source.EnqueueTransientDiagnostic(text);
    public void Interrupt() => this.source.Interrupt();
    public object? GetService(Type serviceType) => this.source.GetService(serviceType);

    public ValueTask DisposeAsync()
    {
        this.source.InformationChanged -= this.ForwardInformationChanged;
        this.source.ToolsChanged -= this.ForwardToolsChanged;
        this.source.UsageChanged -= this.ForwardUsageChanged;
        this.source.TurnCompleted -= this.ForwardTurnCompleted;
        this.inputQueues.Dispose();
        return ValueTask.CompletedTask;
    }

    private static AgentInformation Clone(AgentInformation information)
        => new()
        {
            AgentSessionId = information.AgentSessionId,
            AgentId = information.AgentId,
            Name = information.Name,
            DisplayName = information.DisplayName,
            Description = information.Description,
            AcceptsUserInput = information.AcceptsUserInput,
            CurrentModelId = information.CurrentModelId,
            AgentDefinition = information.AgentDefinition,
        };

    private void ForwardInformationChanged(object? sender, EventArgs e)
        => this.InformationChanged?.Invoke(this, EventArgs.Empty);

    private void ForwardToolsChanged(object? sender, EventArgs e)
        => this.ToolsChanged?.Invoke(this, EventArgs.Empty);

    private void ForwardUsageChanged(object? sender, EventArgs e)
        => this.UsageChanged?.Invoke(this, EventArgs.Empty);

    private void ForwardTurnCompleted(object? sender, AgentChatHistoryItem e)
        => this.TurnCompleted?.Invoke(this, e);

    private sealed class RemoteAgentInputQueuesProxy : IAgentInputQueues, IDisposable
    {
        private readonly IAgentInputQueues source;
        private readonly Dictionary<string, RemoteAgentInputQueueProxy> queuesById = new(StringComparer.Ordinal);
        private AgentInputQueuesSnapshot snapshot;

        public RemoteAgentInputQueuesProxy(IAgentInputQueues source)
        {
            this.source = source;
            this.snapshot = CloneSnapshot(source.Snapshot);
            foreach (var queue in source.Queues)
            {
                this.queuesById[queue.Snapshot.QueueId] = new RemoteAgentInputQueueProxy(queue);
            }

            this.source.Changed += this.OnChanged;
            this.RebuildQueues();
        }

        public AgentInputQueuesSnapshot Snapshot => this.snapshot;
        public IReadOnlyList<IAgentInputQueue> Queues => this.queuesById.Values.Cast<IAgentInputQueue>().ToArray();
        public IAgentInputQueue DefaultQueue => this.queuesById[this.source.DefaultQueue.Snapshot.QueueId];
        public IAgentInputQueue ImmediateQueue => this.queuesById[this.source.ImmediateQueue.Snapshot.QueueId];
        public event EventHandler? Changed;

        public Task<AgentInputQueueCommandResult> CreateQueueAsync(CreateAgentInputQueueRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => this.source.CreateQueueAsync(request, ct));

        public Task<AgentInputQueueCommandResult> DeleteQueueAsync(DeleteAgentInputQueueRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => this.source.DeleteQueueAsync(request, ct));

        public Task<AgentInputQueueCommandResult> EnqueueAsync(EnqueueAgentInputRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => this.source.EnqueueAsync(request, ct));

        public Task<AgentInputQueueCommandResult> EditAsync(EditAgentInputQueueItemRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => this.source.EditAsync(request, ct));

        public Task<AgentInputQueueCommandResult> RemoveAsync(RemoveAgentInputQueueItemRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => this.source.RemoveAsync(request, ct));

        public Task<AgentInputQueueCommandResult> MoveAsync(MoveAgentInputQueueItemRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => this.source.MoveAsync(request, ct));

        public Task<AgentInputQueueCommandResult> ConfigureAsync(ConfigureAgentInputQueueRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => this.source.ConfigureAsync(request, ct));

        public AgentInputQueueCommandResult CreateQueue(CreateAgentInputQueueRequest request)
            => this.ExecuteSync(() => this.source.CreateQueue(request));

        public AgentInputQueueCommandResult DeleteQueue(DeleteAgentInputQueueRequest request)
            => this.ExecuteSync(() => this.source.DeleteQueue(request));

        public AgentInputQueueCommandResult Enqueue(EnqueueAgentInputRequest request)
            => this.ExecuteSync(() => this.source.Enqueue(request));

        public AgentInputQueueCommandResult Edit(EditAgentInputQueueItemRequest request)
            => this.ExecuteSync(() => this.source.Edit(request));

        public AgentInputQueueCommandResult Remove(RemoveAgentInputQueueItemRequest request)
            => this.ExecuteSync(() => this.source.Remove(request));

        public AgentInputQueueCommandResult Move(MoveAgentInputQueueItemRequest request)
            => this.ExecuteSync(() => this.source.Move(request));

        public AgentInputQueueCommandResult Configure(ConfigureAgentInputQueueRequest request)
            => this.ExecuteSync(() => this.source.Configure(request));

        private AgentInputQueueCommandResult ExecuteSync(Func<AgentInputQueueCommandResult> operation)
        {
            var result = operation();
            this.RebuildQueues();
            return result.CurrentSnapshot is { } current
                ? result with { CurrentSnapshot = CloneSnapshot(current) }
                : result;
        }

        public void Dispose()
        {
            this.source.Changed -= this.OnChanged;
            foreach (var queue in this.queuesById.Values)
            {
                queue.Dispose();
            }
        }

        private async Task<AgentInputQueueCommandResult> ExecuteAsync(Func<Task<AgentInputQueueCommandResult>> operation)
        {
            var result = await operation().ConfigureAwait(false);
            this.RebuildQueues();
            return result.CurrentSnapshot is { } current
                ? result with { CurrentSnapshot = CloneSnapshot(current) }
                : result;
        }

        private void OnChanged(object? sender, EventArgs e)
        {
            this.RebuildQueues();
            this.Changed?.Invoke(this, EventArgs.Empty);
        }

        private void RebuildQueues()
        {
            this.snapshot = CloneSnapshot(this.source.Snapshot);
            var queueIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var queue in this.source.Queues)
            {
                var id = queue.Snapshot.QueueId;
                queueIds.Add(id);
                if (this.queuesById.TryGetValue(id, out var existing))
                {
                    existing.Update(queue.Snapshot);
                }
                else
                {
                    this.queuesById[id] = new RemoteAgentInputQueueProxy(queue);
                }
            }

            foreach (var stale in this.queuesById.Keys.Where(id => !queueIds.Contains(id)).ToArray())
            {
                    this.queuesById[stale].Dispose();
                    this.queuesById.Remove(stale);
            }
        }

        private static AgentInputQueuesSnapshot CloneSnapshot(AgentInputQueuesSnapshot snapshot)
        {
            AgentInputQueueSnapshotValidator.Validate(snapshot);
            var json = JsonSerializer.Serialize(snapshot, AIJsonUtilities.DefaultOptions);
            return JsonSerializer.Deserialize<AgentInputQueuesSnapshot>(json, AIJsonUtilities.DefaultOptions);
        }
    }

    private sealed class RemoteAgentInputQueueProxy : IAgentInputQueue, IDisposable
    {
        public RemoteAgentInputQueueProxy(IAgentInputQueue source)
        {
            this.Update(source.Snapshot);
            source.Changed += this.OnChanged;
            this.source = source;
        }

        private readonly IAgentInputQueue source;
        private AgentInputQueueSnapshot snapshot;

        public AgentInputQueueSnapshot Snapshot => this.snapshot;
        public event EventHandler? Changed;

        public void Update(AgentInputQueueSnapshot snapshot)
            => this.snapshot = CloneSnapshot(snapshot);

        private void OnChanged(object? sender, EventArgs e)
        {
            this.Update(this.source.Snapshot);
            this.Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
            => this.source.Changed -= this.OnChanged;

        private static AgentInputQueueSnapshot CloneSnapshot(AgentInputQueueSnapshot snapshot)
        {
            var json = JsonSerializer.Serialize(snapshot, AIJsonUtilities.DefaultOptions);
            return JsonSerializer.Deserialize<AgentInputQueueSnapshot>(json, AIJsonUtilities.DefaultOptions);
        }
    }
}
