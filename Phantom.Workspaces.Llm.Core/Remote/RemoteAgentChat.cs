using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Remote;

public sealed record RemoteAgentChatAttachOptions
{
    public required RemoteAgentSessionClient Client { get; init; }
    public required AgentSessionOpenRequest OpenRequest { get; init; }
    public required TaskScheduler ForegroundScheduler { get; init; }
}

public sealed class RemoteAgentChat : IAgentChat
{
    private readonly RemoteAgentSessionClient client;
    private readonly TaskScheduler foregroundScheduler;
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ObservableCollection<IRunningSubAgent> subagents = [];
    private readonly ObservableCollection<AgentChatModal> modals = [];
    private readonly List<AgentChatToolItem> tools = [];
    private readonly Dictionary<string, AgentChatRunningItem> runningById = new(StringComparer.Ordinal);
    private readonly SlashCommandRegistry slashCommands = new();
    private readonly RemoteInputQueues inputQueues;
    private readonly object frameApplicationLock = new();
    private Task frameApplication = Task.CompletedTask;
    private Task? disposalTask;
    private bool disposed;
    private bool detached;
    private bool isConnected = true;
    private bool isTerminal;
    private AgentInformation information;
    private Usage usage;

    private RemoteAgentChat(RemoteAgentSessionClient client, TaskScheduler foregroundScheduler)
    {
        this.client = client;
        this.foregroundScheduler = foregroundScheduler;
        this.inputQueues = new RemoteInputQueues(client, this.QueueForeground);
        this.SubAgents = new(this.subagents);
        this.Modals = new(this.modals);
        this.client.FrameReceived += this.OnFrameReceived;
        this.client.UnexpectedlyDisconnected += this.OnUnexpectedlyDisconnected;
    }

    public static async Task<RemoteAgentChat> AttachAsync(
        RemoteAgentChatAttachOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Client);
        ArgumentNullException.ThrowIfNull(options.OpenRequest);
        ArgumentNullException.ThrowIfNull(options.ForegroundScheduler);
        var chat = new RemoteAgentChat(options.Client, options.ForegroundScheduler);
        try
        {
            await options.Client.ConnectAsync(options.OpenRequest, ct).ConfigureAwait(false);
            await chat.initialized.Task.WaitAsync(ct).ConfigureAwait(false);
            return chat;
        }
        catch
        {
            await chat.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public AgentInformation Information
    {
        get { this.ThrowIfDisposed(); return this.information; }
    }
    public Usage Usage
    {
        get { this.ThrowIfDisposed(); return this.usage; }
    }
    public bool IsBusy { get; private set; }
    public AgentChatHistoryCollection History { get; } = new();
    public Task HistoryPopulated => this.initialized.Task;
    public AgentChatRunningItemCollection RunningItems { get; } = new();
    public IAgentInputQueues InputQueues => this.inputQueues;
    public ReadOnlyObservableCollection<IRunningSubAgent> SubAgents { get; }
    public ReadOnlyObservableCollection<AgentChatModal> Modals { get; }
    public ISlashCommandRegistry SlashCommands => this.slashCommands;
    public bool ContinueInBackground { get; private set; }
    public int ViewerCount { get; private set; }
    internal bool IsConnected => this.isConnected;
    internal bool IsTerminal => this.isTerminal;

    public event EventHandler? InformationChanged;
    public event EventHandler? ToolsChanged;
    public event EventHandler? UsageChanged;
    public event EventHandler<AgentChatHistoryItem>? TurnCompleted;
    public event EventHandler? RetentionChanged;
    internal event EventHandler? RuntimeStateChanged;

    public IReadOnlyList<AgentChatToolItem> GetToolSnapshot()
    {
        this.ThrowIfDisposed();
        return this.tools.ToArray();
    }

    public async Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs args)
        {
            if (this.tools.Any(tool => tool.Id == toolId && tool.IsEnabled == enabled))
                applied.TrySetResult();
        }
        this.ToolsChanged += OnChanged;
        try
        {
            await this.client.SetToolEnabledAsync(new SetAgentToolEnabledRequest
            {
                ToolId = toolId, Enabled = enabled, CommandId = Guid.NewGuid(),
            }, ct).ConfigureAwait(false);
            OnChanged(this, EventArgs.Empty);
            await applied.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            this.ToolsChanged -= OnChanged;
        }
    }

    public Task RespondToModalAsync(string modalId, JsonElement response, CancellationToken ct = default)
        => this.client.RespondToModalAsync(new RespondToAgentModalRequest
        {
            ModalId = modalId, Response = response.Clone(), CommandId = Guid.NewGuid(),
        }, ct);

    public void EnqueueSystemNote(string text) => this.EnqueueLocalNote(text, AgentChatHistoryItem.DiagnosticChatRole);
    public void EnqueueHelpNote(string text) => this.EnqueueLocalNote(text, AgentChatHistoryItem.HelpChatRole);
    public void EnqueueTransientDiagnostic(string text) => this.EnqueueLocalNote(text, AgentChatHistoryItem.DiagnosticChatRole);

    public void Interrupt()
    {
        this.ThrowIfDisposed();
        _ = this.client.InterruptAsync(Guid.NewGuid());
    }

    public async Task TerminateAsync(CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs args)
        {
            if (this.IsTerminal) applied.TrySetResult();
        }

        this.RuntimeStateChanged += OnChanged;
        try
        {
            await this.client.TerminateAsync(new TerminateAgentSessionRequest
            {
                Reason = "user-requested", CommandId = Guid.NewGuid(),
            }, ct).ConfigureAwait(false);
            if (this.IsTerminal) applied.TrySetResult();
            await applied.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            this.RuntimeStateChanged -= OnChanged;
        }
    }

    public async Task SetContinueInBackgroundAsync(bool continueInBackground, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs args)
        {
            if (this.ContinueInBackground == continueInBackground) applied.TrySetResult();
        }
        this.RetentionChanged += OnChanged;
        try
        {
            await this.client.SetContinueInBackgroundAsync(new SetAgentSessionRetentionRequest
            {
                ContinueInBackground = continueInBackground,
                CommandId = Guid.NewGuid(),
            }, ct).ConfigureAwait(false);
            if (this.ContinueInBackground == continueInBackground) applied.TrySetResult();
            await applied.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            this.RetentionChanged -= OnChanged;
        }
    }

    public async Task DetachAsync(CancellationToken ct = default)
    {
        if (this.detached) return;
        this.detached = true;
        Exception? primaryFailure = null;
        try
        {
            await this.client.DetachAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        try
        {
            await this.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = primaryFailure is null
                ? exception
                : new AggregateException(primaryFailure, exception);
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    public object? GetService(Type serviceType) => null;

    public ValueTask DisposeAsync()
    {
        lock (this.frameApplicationLock)
        {
            this.disposalTask ??= this.BeginDisposeLocked();
            return new ValueTask(this.disposalTask);
        }
    }

    private Task BeginDisposeLocked()
    {
        this.disposed = true;
        this.SetConnected(false);
        this.inputQueues.StopAccepting(new ObjectDisposedException(nameof(RemoteAgentChat)));
        this.client.FrameReceived -= this.OnFrameReceived;
        this.client.UnexpectedlyDisconnected -= this.OnUnexpectedlyDisconnected;
        return this.DisposeCoreAsync(this.frameApplication);
    }

    private async Task DisposeCoreAsync(Task pendingFrameApplication)
    {
        Exception? primaryFailure = null;
        try
        {
            await this.client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        try
        {
            await this.inputQueues.DrainAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure ??= exception;
        }

        try
        {
            await pendingFrameApplication.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure ??= exception;
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    private void OnFrameReceived(object? sender, AgentSessionServerFrame frame)
    {
        var application = this.QueueForeground(() => this.ApplyFrame(frame));
        _ = application.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                        this.initialized.TrySetException(task.Exception!.InnerExceptions);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private void OnUnexpectedlyDisconnected(object? sender, EventArgs e)
    {
        this.SetConnected(false);
        _ = this.ReconnectAsync();
    }

    private async Task ReconnectAsync()
    {
        foreach (var delay in new[] { 250, 500, 1000, 1000, 1000, 1000 })
        {
            if (this.disposed || this.detached) return;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(delay));
            if (!await timer.WaitForNextTickAsync().ConfigureAwait(false)) return;
            try
            {
                await this.client.ReconnectAsync().ConfigureAwait(false);
                await this.QueueForeground(() => this.SetConnected(true)).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (RemoteAgentSessionException error)
                when (error.Code is "not-found" or "runtime-changed" or "unauthorized")
            {
                return;
            }
            catch when (!this.disposed && !this.detached) { }
        }
    }

    private void ApplyFrame(AgentSessionServerFrame frame)
    {
        if (this.disposed) return;
        switch (AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(frame))
        {
            case SessionSnapshotEvent e:
                this.ApplySnapshot(e.Snapshot);
                this.initialized.TrySetResult();
                break;
            case HistoryAppendedEvent e:
                var item = Deserialize<AgentChatHistoryItem>(e.Item);
                this.History.Add(item);
                this.TurnCompleted?.Invoke(this, item);
                break;
            case UsageChangedEvent e:
                ProtocolValueValidator.Validate(e.Usage);
                this.usage = e.Usage;
                this.UsageChanged?.Invoke(this, EventArgs.Empty);
                break;
            case AgentInformationChangedEvent e:
                ProtocolValueValidator.Validate(e.Information);
                this.information = CloneInformation(e.Information);
                this.InformationChanged?.Invoke(this, EventArgs.Empty);
                break;
            case QueueChangedEvent e:
                this.inputQueues.ApplyDelta(e);
                break;
            case StreamingStartedEvent e:
                var running = new AgentChatRunningItem();
                running.Items.Add(Deserialize<AgentChatHistoryItem>(e.Item));
                this.runningById.Add(e.RunId, running);
                this.RunningItems.Add(running);
                break;
            case StreamingUpdatedEvent e:
                if (!this.runningById.TryGetValue(e.RunId, out var updated))
                    throw new RemoteAgentProtocolException("A streaming update referenced an unknown run.");
                updated.Items.Clear();
                foreach (var update in DeserializeStreamingItems(e.Update)) updated.Items.Add(update);
                break;
            case StreamingCompletedEvent e:
                if (!this.runningById.Remove(e.RunId, out var completed))
                    throw new RemoteAgentProtocolException("A streaming completion referenced an unknown run.");
                this.RunningItems.Remove(completed);
                var completedItem = Deserialize<AgentChatHistoryItem>(e.Item);
                this.History.Add(completedItem);
                this.TurnCompleted?.Invoke(this, completedItem);
                break;
            case BusyChangedEvent e:
                this.IsBusy = e.IsBusy;
                this.RuntimeStateChanged?.Invoke(this, EventArgs.Empty);
                break;
            case ToolsSnapshotEvent e:
                this.ReplaceTools(e.Tools);
                break;
            case ToolsChangedEvent e:
                this.ReplaceTools(e.Tools);
                break;
            case SubagentsSnapshotEvent e:
                this.ReplaceSubagents(e.Subagents);
                break;
            case SubagentsChangedEvent e:
                this.ReplaceSubagents(e.Subagents);
                break;
            case ModalRaisedEvent e:
                this.modals.Add(e.Modal);
                break;
            case ModalUpdatedEvent e:
                var index = this.modals.ToList().FindIndex(m => m.Id == e.Modal.Id);
                if (index >= 0) this.modals[index] = e.Modal;
                break;
            case ModalDismissedEvent e:
                var modal = this.modals.FirstOrDefault(m => m.Id == e.ModalId);
                if (modal is not null) this.modals.Remove(modal);
                break;
            case SessionRetentionChangedEvent e:
                if (e.ViewerCount < 0) throw new RemoteAgentProtocolException("Viewer count cannot be negative.");
                this.ContinueInBackground = e.ContinueInBackground;
                this.ViewerCount = e.ViewerCount;
                this.RetentionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case SessionTerminalEvent:
                this.IsBusy = false;
                this.RunningItems.Clear();
                this.runningById.Clear();
                this.modals.Clear();
                this.isTerminal = true;
                this.RuntimeStateChanged?.Invoke(this, EventArgs.Empty);
                this.inputQueues.StopAccepting(
                    new RemoteAgentProtocolException("The remote session is terminal."));
                break;
        }
    }

    private void ApplySnapshot(AgentSessionSnapshot snapshot)
    {
        ProtocolValueValidator.Validate(snapshot.Information);
        ProtocolValueValidator.Validate(snapshot.Usage);
        if (snapshot.ViewerCount < 0) throw new RemoteAgentProtocolException("Viewer count cannot be negative.");
        this.information = CloneInformation(snapshot.Information);
        this.usage = snapshot.Usage;
        this.IsBusy = snapshot.IsBusy;
        this.isTerminal = false;
        this.SetConnected(true);
        this.ContinueInBackground = snapshot.ContinueInBackground;
        this.ViewerCount = snapshot.ViewerCount;
        this.History.Clear();
        foreach (var value in snapshot.History) this.History.Add(Deserialize<AgentChatHistoryItem>(value));
        this.RunningItems.Clear();
        this.runningById.Clear();
        foreach (var value in snapshot.RunningItems)
        {
            var state = Deserialize<RemoteRunningItemState>(value);
            var running = new AgentChatRunningItem();
            foreach (var item in state.Items) running.Items.Add(item);
            this.runningById.Add(state.RunId, running);
            this.RunningItems.Add(running);
        }
        this.inputQueues.Replace(snapshot.InputQueues);
        this.ReplaceTools(snapshot.Tools);
        this.ReplaceSubagents(snapshot.Subagents);
        this.ReconcileModals(snapshot.Modals);
    }

    private void SetConnected(bool value)
    {
        if (this.isConnected == value)
        {
            return;
        }

        this.isConnected = value;
        this.RuntimeStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReconcileModals(IReadOnlyList<AgentChatModal> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var modal = desired[index];
            var currentIndex = this.modals
                .Select((value, valueIndex) => (value, valueIndex))
                .FirstOrDefault(item => string.Equals(item.value.Id, modal.Id, StringComparison.Ordinal))
                .valueIndex;
            if (currentIndex < index
                || currentIndex >= this.modals.Count
                || !string.Equals(this.modals[currentIndex].Id, modal.Id, StringComparison.Ordinal))
            {
                this.modals.Insert(index, modal);
                continue;
            }

            if (currentIndex != index)
            {
                this.modals.Move(currentIndex, index);
            }
            if (!Equals(this.modals[index], modal))
            {
                this.modals[index] = modal;
            }
        }

        while (this.modals.Count > desired.Count)
        {
            this.modals.RemoveAt(this.modals.Count - 1);
        }
    }

    private void ReplaceTools(IReadOnlyList<JsonElement> values)
    {
        this.tools.Clear();
        this.tools.AddRange(values.Select(Deserialize<AgentChatToolItem>));
        this.ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReplaceSubagents(IReadOnlyList<JsonElement> values)
    {
        this.subagents.Clear();
        foreach (var value in values) this.subagents.Add(RemoteRunningSubagent.FromJson(value));
    }

    private void EnqueueLocalNote(string text, ChatRole role)
    {
        this.ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(text)) return;
        _ = this.QueueForeground(
            () => this.History.Add(new AgentChatHistoryItem
            {
                Role = role, Timestamp = DateTimeOffset.UtcNow, Contents = [new TextContent(text)],
            }));
    }

    private Task QueueForeground(Action action)
    {
        lock (this.frameApplicationLock)
        {
            if (this.disposed)
                return Task.CompletedTask;
            return this.frameApplication = this.frameApplication.ContinueWith(
                _ => action(),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                this.foregroundScheduler);
        }
    }

    private static T Deserialize<T>(JsonElement value)
        => JsonSerializer.Deserialize<T>(value.GetRawText(), AIJsonUtilities.DefaultOptions)
           ?? throw new RemoteAgentProtocolException($"A {typeof(T).Name} payload was null.");

    private static IReadOnlyList<AgentChatHistoryItem> DeserializeStreamingItems(JsonElement value)
        => value.ValueKind == JsonValueKind.Array
            ? Deserialize<AgentChatHistoryItem[]>(value)
            : [Deserialize<AgentChatHistoryItem>(value)];

    private static AgentInformation CloneInformation(AgentInformation value)
        => new()
        {
            AgentSessionId = value.AgentSessionId, AgentId = value.AgentId, Name = value.Name,
            DisplayName = value.DisplayName, Description = value.Description,
            AcceptsUserInput = value.AcceptsUserInput, CurrentModelId = value.CurrentModelId,
            AgentDefinition = value.AgentDefinition,
        };

    private void ThrowIfDisposed()
    {
        if (this.disposed) throw new ObjectDisposedException(nameof(RemoteAgentChat));
    }

    private sealed class RemoteInputQueues(
        RemoteAgentSessionClient client,
        Func<Action, Task> queueForeground) : IAgentInputQueues
    {
        private readonly object sync = new();
        private readonly Dictionary<string, RemoteInputQueue> queues = new(StringComparer.Ordinal);
        private readonly List<(long Revision, TaskCompletionSource Completion)> revisionWaiters = [];
        private readonly HashSet<IOwnedOperation> operations = [];
        private AgentInputQueuesSnapshot snapshot;
        private Exception? stoppedFailure;
        private bool accepting = true;

        public AgentInputQueuesSnapshot Snapshot => this.snapshot;
        public IReadOnlyList<IAgentInputQueue> Queues => this.snapshot.Queues.Select(q => (IAgentInputQueue)this.queues[q.QueueId]).ToArray();
        public IAgentInputQueue DefaultQueue => this.Queues.Single(q => q.Snapshot.IsDefault);
        public IAgentInputQueue ImmediateQueue => this.Queues.Single(q => q.Snapshot.IsImmediate);
        public event EventHandler? Changed;

        public Task<AgentInputQueueCommandResult> CreateQueueAsync(CreateAgentInputQueueRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => client.CreateQueueAsync(request, ct), ct);
        public Task<AgentInputQueueCommandResult> DeleteQueueAsync(DeleteAgentInputQueueRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => client.DeleteQueueAsync(request, ct), ct);
        public Task<AgentInputQueueCommandResult> EnqueueAsync(EnqueueAgentInputRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => client.EnqueueAsync(request, ct), ct);
        public Task<AgentInputQueueCommandResult> EditAsync(EditAgentInputQueueItemRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => client.EditAsync(request, ct), ct);
        public Task<AgentInputQueueCommandResult> RemoveAsync(RemoveAgentInputQueueItemRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => client.RemoveAsync(request, ct), ct);
        public Task<AgentInputQueueCommandResult> MoveAsync(MoveAgentInputQueueItemRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => client.MoveAsync(request, ct), ct);
        public Task<AgentInputQueueCommandResult> ConfigureAsync(ConfigureAgentInputQueueRequest request, CancellationToken ct = default)
            => this.ExecuteAsync(() => client.ConfigureAsync(request, ct), ct);
        public AgentInputQueueCommandResult CreateQueue(CreateAgentInputQueueRequest request) => throw RemoteOnly();
        public AgentInputQueueCommandResult DeleteQueue(DeleteAgentInputQueueRequest request) => throw RemoteOnly();
        public AgentInputQueueCommandResult Enqueue(EnqueueAgentInputRequest request) => throw RemoteOnly();
        public AgentInputQueueCommandResult Edit(EditAgentInputQueueItemRequest request) => throw RemoteOnly();
        public AgentInputQueueCommandResult Remove(RemoveAgentInputQueueItemRequest request) => throw RemoteOnly();
        public AgentInputQueueCommandResult Move(MoveAgentInputQueueItemRequest request) => throw RemoteOnly();
        public AgentInputQueueCommandResult Configure(ConfigureAgentInputQueueRequest request) => throw RemoteOnly();

        internal void Replace(AgentInputQueuesSnapshot value)
        {
            AgentInputQueueSnapshotValidator.Validate(value);
            List<TaskCompletionSource> completed;
            lock (this.sync)
            {
                this.snapshot = value with { Queues = value.Queues.Select(RemoteAgentChatProxy.RoundTripQueueSnapshotForTransport).ToImmutableArray() };
                var live = this.snapshot.Queues.Select(q => q.QueueId).ToHashSet(StringComparer.Ordinal);
                foreach (var queue in this.snapshot.Queues)
                {
                    if (this.queues.TryGetValue(queue.QueueId, out var existing)) existing.Replace(queue);
                    else this.queues.Add(queue.QueueId, new RemoteInputQueue(queue));
                }
                foreach (var id in this.queues.Keys.Where(id => !live.Contains(id)).ToArray()) this.queues.Remove(id);
                completed = this.revisionWaiters.Where(w => w.Revision <= value.Revision).Select(w => w.Completion).ToList();
                this.revisionWaiters.RemoveAll(w => w.Revision <= value.Revision);
            }
            this.Changed?.Invoke(this, EventArgs.Empty);
            foreach (var completion in completed) completion.TrySetResult();
        }

        internal void ApplyDelta(QueueChangedEvent value)
        {
            if (value.Revision <= this.snapshot.Revision)
                throw new RemoteAgentProtocolException("Queue revision did not advance.");
            var next = this.snapshot.Queues.ToDictionary(q => q.QueueId, StringComparer.Ordinal);
            foreach (var id in value.RemovedQueueIds) next.Remove(id);
            foreach (var queue in value.Queues) next[queue.QueueId] = queue;
            this.Replace(new AgentInputQueuesSnapshot
            {
                Revision = value.Revision,
                Queues = next.Values.OrderBy(q => q.IsImmediate ? 0 : q.IsDefault ? 1 : 2).ThenBy(q => q.QueueId).ToImmutableArray(),
            });
        }

        internal void StopAccepting(Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            TaskCompletionSource[] waiters;
            lock (this.sync)
            {
                if (!this.accepting) return;
                this.accepting = false;
                this.stoppedFailure = failure;
                waiters = this.revisionWaiters.Select(waiter => waiter.Completion).ToArray();
                this.revisionWaiters.Clear();
            }

            foreach (var waiter in waiters)
                waiter.TrySetException(failure);
        }

        internal Task DrainAsync()
        {
            lock (this.sync)
                return Task.WhenAll(this.operations.Select(operation => operation.Drained));
        }

        private static NotSupportedException RemoteOnly()
            => new("Remote queue commands are asynchronous.");

        private Task<AgentInputQueueCommandResult> ExecuteAsync(
            Func<Task<AgentInputQueueCommandResult>> operation, CancellationToken ct)
        {
            OwnedOperation<AgentInputQueueCommandResult> owned;
            lock (this.sync)
            {
                if (!this.accepting)
                    return CreateObservedFailure<AgentInputQueueCommandResult>(
                        this.stoppedFailure ?? new ObjectDisposedException(nameof(RemoteAgentChat)));

                owned = new OwnedOperation<AgentInputQueueCommandResult>(
                    () => this.ExecuteCoreAsync(operation, ct),
                    this.CompleteOperation);
                this.operations.Add(owned);
            }

            owned.Start();
            return owned.Completion;
        }

        private async Task<AgentInputQueueCommandResult> ExecuteCoreAsync(
            Func<Task<AgentInputQueueCommandResult>> operation, CancellationToken ct)
        {
            var result = await operation().ConfigureAwait(false);
            if (result.CurrentSnapshot is { } current)
            {
                await queueForeground(() =>
                {
                    if (current.Revision >= this.snapshot.Revision)
                        this.Replace(current);
                }).ConfigureAwait(false);
                return result;
            }
            if (result.Status is not (AgentInputQueueCommandStatus.Applied or AgentInputQueueCommandStatus.Duplicate))
                return result;

            Task wait;
            TaskCompletionSource completion;
            lock (this.sync)
            {
                if (!this.accepting)
                    throw this.stoppedFailure ?? new ObjectDisposedException(nameof(RemoteAgentChat));
                if (this.snapshot.Revision >= result.Revision) return result;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.revisionWaiters.Add((result.Revision, completion));
                wait = completion.Task;
            }
            try
            {
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                lock (this.sync)
                    this.revisionWaiters.RemoveAll(waiter => ReferenceEquals(waiter.Completion, completion));
            }
            return result;
        }

        private void CompleteOperation(IOwnedOperation operation)
        {
            lock (this.sync)
                this.operations.Remove(operation);
        }

        private static Task<T> CreateObservedFailure<T>(Exception failure)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetException(failure);
            _ = completion.Task.Exception;
            return completion.Task;
        }

        private interface IOwnedOperation
        {
            Task Drained { get; }
        }

        private sealed class OwnedOperation<T> : IOwnedOperation
        {
            private readonly Func<Task<T>> operation;
            private readonly Action<IOwnedOperation> completed;
            private readonly TaskCompletionSource start =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<T> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource drained =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Task driver;

            internal OwnedOperation(Func<Task<T>> operation, Action<IOwnedOperation> completed)
            {
                this.operation = operation;
                this.completed = completed;
                this.driver = this.RunAsync();
            }

            internal Task<T> Completion => this.completion.Task;
            public Task Drained => this.drained.Task;

            internal void Start() => this.start.SetResult();

            private async Task RunAsync()
            {
                await this.start.Task.ConfigureAwait(false);
                try
                {
                    this.completion.TrySetResult(await this.operation().ConfigureAwait(false));
                }
                catch (OperationCanceledException exception)
                {
                    this.completion.TrySetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    this.completion.TrySetException(exception);
                }
                finally
                {
                    if (this.completion.Task.IsFaulted)
                        _ = this.completion.Task.Exception;
                    this.drained.TrySetResult();
                    this.completed(this);
                }
            }
        }
    }

    private sealed class RemoteInputQueue(AgentInputQueueSnapshot snapshot) : IAgentInputQueue
    {
        public AgentInputQueueSnapshot Snapshot { get; private set; } = snapshot;
        public event EventHandler? Changed;
        internal void Replace(AgentInputQueueSnapshot value)
        {
            this.Snapshot = value;
            this.Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed record RemoteRunningSubagent : IRunningSubAgent
    {
        public required string AgentId { get; init; }
        public required string DisplayName { get; init; }
        public required string Description { get; init; }
        public string Name { get; init; } = string.Empty;
        public AgentChatCompletionState CompletionState { get; init; }
        public DateTime LastUpdatedAt { get; init; }
        public IReadOnlyList<IRunningSubAgent> SubAgents { get; init; } = [];

        internal static RemoteRunningSubagent FromJson(JsonElement value)
            => JsonSerializer.Deserialize<RemoteRunningSubagent>(value.GetRawText(), AIJsonUtilities.DefaultOptions)
               ?? throw new RemoteAgentProtocolException("Subagent payload was null.");
    }

    private sealed record RemoteRunningItemState
    {
        public required string RunId { get; init; }
        public required IReadOnlyList<AgentChatHistoryItem> Items { get; init; }
    }
}

internal static class ProtocolValueValidator
{
    internal static void Validate(Usage value)
    {
        if (value.TotalInputTokenCount < 0 || value.TotalOutputTokenCount < 0
            || value.TotalCacheReadTokenCount < 0 || value.TotalCacheWriteTokenCount < 0
            || value.TotalReasoningTokenCount < 0 || value.TotalSessionCostUsd < 0
            || double.IsNaN(value.TotalSessionCostUsd ?? 0)
            || double.IsInfinity(value.TotalSessionCostUsd ?? 0))
            throw new RemoteAgentProtocolException("Usage values must be finite and nonnegative.");
    }

    internal static void Validate(AgentInformation value)
    {
        if (string.IsNullOrWhiteSpace(value.AgentSessionId)
            || string.IsNullOrWhiteSpace(value.AgentId)
            || string.IsNullOrWhiteSpace(value.Name)
            || string.IsNullOrWhiteSpace(value.DisplayName)
            || string.IsNullOrWhiteSpace(value.Description)
            || (value.CurrentModelId is not null && string.IsNullOrWhiteSpace(value.CurrentModelId))
            || value.AgentDefinition is null)
            throw new RemoteAgentProtocolException("Agent information is incomplete.");
    }
}
