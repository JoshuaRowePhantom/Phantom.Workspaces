using AgentSchema;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.ObjectModel;
using System.Linq;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Reference-counted table of running <see cref="AgentChat"/> sessions.
/// On first <see cref="GetAsync"/> for a session ID the factory loads the session from
/// <see cref="IAgentPersistenceStore"/>, creates the chat, and adds it to
/// <see cref="RunningSessions"/>. On the last lease being released the chat is removed
/// from <see cref="RunningSessions"/> and disposed.
/// All <see cref="RunningSessions"/> mutations are dispatched on the foreground scheduler.
/// </summary>
internal sealed class AgentChatFactory : IRunningAgentChatFactory, IRestartableRunningAgentChatFactory, IAsyncDisposable
{
    private sealed class Entry
    {
        public required AgentChat AgentChat { get; init; }
        public int RefCount { get; set; }
        public TaskCompletionSource? DisposalCompletion { get; set; }
        public AgentRuntimeQueueCheckpoint? RestartCheckpoint { get; set; }
        public bool ShutdownConfirmed { get; set; }
        public Task? PendingRestartCleanup { get; set; }
    }

    private readonly IAgentPersistenceStore _store;
    private readonly AgentServices _services;
    private readonly TaskScheduler _foregroundScheduler;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<AgentSessionId, Entry> _entries = new();
    private readonly ObservableCollection<RunningAgentChat> _runningSessions = new();
    private bool _disposed;

    public ObservableCollection<RunningAgentChat> RunningSessions => _runningSessions;

    public AgentChatFactory(
        IAgentPersistenceStore store,
        AgentServices services,
        TaskScheduler foregroundScheduler)
    {
        _store = store;
        _services = services;
        _foregroundScheduler = foregroundScheduler;
    }

    public Task<RunningAgentChatLease> GetOrCreateAsync(
        AgentSessionId sessionId,
        AgentDefinition? definition = null,
        AgentServices? services = null,
        string? displayNameOverride = null,
        string? descriptionOverride = null,
        bool registerAsRunningAgent = true,
        CancellationToken ct = default)
        => this.GetOrCreateAsync(
            sessionId,
            definition,
            services,
            displayNameOverride,
            descriptionOverride,
            registerAsRunningAgent,
            ct,
            null);

    internal async Task<RunningAgentChatLease> GetOrCreateAsync(
        AgentSessionId sessionId,
        AgentDefinition? definition,
        AgentServices? services,
        string? displayNameOverride,
        string? descriptionOverride,
        bool registerAsRunningAgent,
        CancellationToken ct,
        AgentRuntimeQueueCheckpoint? runtimeQueueCheckpoint)
    {
        while (true)
        {
            Task? drain = null;
            await _gate.WaitAsync(ct);

            AgentChat? newChat = null;
            RunningAgentChatLease? existingLease = null;

            try
            {
                if (_entries.TryGetValue(sessionId, out var existing))
                {
                    if (existing.DisposalCompletion is not null)
                    {
                        drain = existing.DisposalCompletion.Task;
                    }
                    else
                    {
                        existing.RefCount++;
                        existingLease = MakeLease(sessionId, existing.AgentChat);
                    }
                }
                else
                {
                    var effectiveServices = WithSelfAsFactory(services ?? _services);
                    var executionTrustContext = effectiveServices.RemoteAgentSessionRuntimeIntent is not null
                        ? effectiveServices.AgentExecutionTrustContext
                            as Trust.AgentExecutionTrustContext
                        : await AgentFactory.ResolveExecutionTrustContextAsync(
                            definition,
                            effectiveServices.TrustProfileResolver as Trust.ITrustProfileProvider,
                            effectiveServices,
                            ct).ConfigureAwait(false);
                    if (executionTrustContext is not null)
                    {
                        effectiveServices = effectiveServices with
                        {
                            AgentExecutionTrustContext = executionTrustContext,
                            ExecutionTrustContext = executionTrustContext,
                        };
                    }

                    if (definition is not null)
                    {
                        var definitionJson = MongoDB.Bson.BsonDocument.Parse(definition.ToJson());
                        await _store.StoreAsync(new StoreRequestAgent
                        {
                            Agent = new PersistedAgent
                            {
                                AgentSessionId = sessionId.Value!,
                                AgentDefinitionJson = definitionJson,
                            }
                        }, ct);
                    }

                    var chat = await CreateChatOnForegroundAsync(new InternalCreateAgentChatRequest
                    {
                        AgentDefinition = definition,
                        AgentSessionId = sessionId.Value,
                        AgentServices = effectiveServices,
                        ExecutionTrustContext = executionTrustContext,
                        ConfiguredStore = _store,
                        ClientOverride = effectiveServices.ChatClientOverride,
                        DisplayNameOverride = displayNameOverride,
                        DescriptionOverride = descriptionOverride,
                        ForegroundScheduler = _foregroundScheduler,
                        CancellationToken = ct,
                        RuntimeQueueCheckpoint = runtimeQueueCheckpoint,
                    }, ct);
                    PopulateAgentChatRef(effectiveServices, chat);
                    _entries[sessionId] = new Entry { AgentChat = chat, RefCount = 1 };
                    newChat = chat;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (drain is not null)
            {
                await drain.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (existingLease is not null)
                return existingLease;

            if (registerAsRunningAgent)
            {
                await PostToForegroundAsync(() => _runningSessions.Add(
                    new RunningAgentChat(sessionId, this, newChat!)));
            }
            return MakeLease(sessionId, newChat!);
        }
    }

    public async Task<RunningAgentChatLease> GetAsync(
        AgentSessionId sessionId,
        bool registerAsRunningAgent = true,
        CancellationToken ct = default)
    {
        while (true)
        {
            Task? drain = null;
            await _gate.WaitAsync(ct);

            AgentChat? newChat = null;
            RunningAgentChatLease? existingLease = null;

            try
            {
                if (_entries.TryGetValue(sessionId, out var existing))
                {
                    if (existing.DisposalCompletion is not null)
                    {
                        drain = existing.DisposalCompletion.Task;
                    }
                    else
                    {
                        existing.RefCount++;
                        existingLease = MakeLease(sessionId, existing.AgentChat);
                    }
                }
                else
                {
                    var effectiveServices = WithSelfAsFactory(_services);
                    var chat = await CreateChatOnForegroundAsync(new InternalCreateAgentChatRequest
                    {
                        AgentDefinition = null,
                        AgentSessionId = sessionId.Value,
                        AgentServices = effectiveServices,
                        ConfiguredStore = _store,
                        ClientOverride = effectiveServices.ChatClientOverride,
                        ForegroundScheduler = _foregroundScheduler,
                        CancellationToken = ct,
                    }, ct);
                    PopulateAgentChatRef(effectiveServices, chat);
                    _entries[sessionId] = new Entry { AgentChat = chat, RefCount = 1 };
                    newChat = chat;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (drain is not null)
            {
                await drain.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (existingLease is not null)
                return existingLease;

            if (registerAsRunningAgent)
            {
                await PostToForegroundAsync(() => _runningSessions.Add(
                    new RunningAgentChat(sessionId, this, newChat!)));
            }
            return MakeLease(sessionId, newChat!);
        }
    }

    public async Task<RunningAgentChatLease> CreateAsync(
        AgentDefinition definition,
        AgentSessionId sessionId,
        AgentServices? services = null,
        string? displayNameOverride = null,
        string? descriptionOverride = null,
        string? nameOverride = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);

        AgentChat? newChat = null;

        try
        {
            if (_entries.ContainsKey(sessionId))
                throw new InvalidOperationException($"A session with ID '{sessionId}' is already running.");

            var definitionJson = BsonDocument.Parse(definition.ToJson());
            await _store.StoreAsync(new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = sessionId.Value!,
                    AgentDefinitionJson = definitionJson,
                }
            }, ct);

            var effectiveServices = WithSelfAsFactory(services ?? _services);
            var chat = await CreateChatOnForegroundAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = definition,
                AgentSessionId = sessionId.Value,
                AgentServices = effectiveServices,
                ConfiguredStore = _store,
                ClientOverride = effectiveServices.ChatClientOverride,
                DisplayNameOverride = displayNameOverride,
                DescriptionOverride = descriptionOverride,
                NameOverride = nameOverride,
                ForegroundScheduler = _foregroundScheduler,
                CancellationToken = ct,
            }, ct);
            PopulateAgentChatRef(effectiveServices, chat);
            _entries[sessionId] = new Entry { AgentChat = chat, RefCount = 1 };
            newChat = chat;
        }
        finally
        {
            _gate.Release();
        }

        await PostToForegroundAsync(() => _runningSessions.Add(
            new RunningAgentChat(sessionId, this, newChat!)));
        return MakeLease(sessionId, newChat!);
    }

    private RunningAgentChatLease MakeLease(AgentSessionId sessionId, AgentChat agentChat)
    {
        var released = false;
        var leaseGate = new SemaphoreSlim(1, 1);
        return new RunningAgentChatLease(sessionId, agentChat, async () =>
        {
            await leaseGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!released)
                {
                    await ReleaseAsync(sessionId, agentChat).ConfigureAwait(false);
                    released = true;
                }
            }
            finally { leaseGate.Release(); }
        }, prepareIdleClose: async ct =>
        {
            await leaseGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (released) return true;
                released = await ReleaseAsync(sessionId, agentChat, idleOnly: true).ConfigureAwait(false);
                return released;
            }
            finally { leaseGate.Release(); }
        });
    }

    public async Task<bool> TerminateAsync(
        AgentSessionId sessionId,
        CancellationToken ct = default)
        => await this.TerminateCoreAsync(sessionId, null, ct).ConfigureAwait(false);

    public Task<bool> TerminateExactAsync(AgentSessionId sessionId, IAgentChat expected, CancellationToken ct = default)
        => this.TerminateCoreAsync(sessionId, expected, ct);

    public Task<RunningAgentChatLease> RestartExactAsync(
        AgentSessionId sessionId,
        IAgentChat expected,
        AgentServices? services,
        Func<AgentRuntimeQueueCheckpoint, CancellationToken, ValueTask> persistCheckpointAsync,
        CancellationToken cancellationToken = default)
        => this.RestartExactAsync(
            sessionId,
            expected,
            services,
            persistCheckpointAsync,
            cancellationToken,
            _ => ValueTask.CompletedTask,
            CancellationToken.None,
            false);

    internal async Task<RunningAgentChatLease> RestartExactAsync(
        AgentSessionId sessionId,
        IAgentChat expected,
        AgentServices? services,
        Func<AgentRuntimeQueueCheckpoint, CancellationToken, ValueTask> persistCheckpointAsync,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask> persistStoppedAsync,
        CancellationToken runtimeLifetimeCancellationToken,
        bool durableOwnerCommit = true,
        TimeProvider? cleanupTimeProvider = null)
    {
        Entry original;
        AgentRuntimeQueueCheckpoint checkpoint;
        TaskCompletionSource completion;
        await this._gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);
            if (!this._entries.TryGetValue(sessionId, out original!)
                || !ReferenceEquals(original.AgentChat, expected)
                || original.DisposalCompletion is not null
                    && !(original.ShutdownConfirmed && original.DisposalCompletion.Task.IsFaulted))
                throw new InvalidOperationException("The expected agent runtime changed or is stopping.");
            checkpoint = original.RestartCheckpoint ?? original.AgentChat.BeginRuntimeHandoff();
            try
            {
                await persistCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                if (!durableOwnerCommit)
                    cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                if (!original.ShutdownConfirmed)
                    original.AgentChat.RollbackRuntimeHandoff();
                throw;
            }
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            original.DisposalCompletion = completion;
            original.RefCount = 0;
            original.RestartCheckpoint = checkpoint;
        }
        finally
        {
            this._gate.Release();
        }

        AgentChat? replacement = null;
        var shutdownAttempted = false;
        try
        {
            await this.PostRestartForegroundAsync(() =>
            {
                var running = this._runningSessions.FirstOrDefault(item =>
                    ReferenceEquals(item.AgentChat, original.AgentChat));
                if (running is not null)
                    this._runningSessions.Remove(running);
            }, runtimeLifetimeCancellationToken, cleanupTimeProvider ?? TimeProvider.System).ConfigureAwait(false);
            if (!original.ShutdownConfirmed)
            {
                using var deadline = new CancellationTokenSource(
                    TimeSpan.FromSeconds(15), cleanupTimeProvider ?? TimeProvider.System);
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(
                    deadline.Token, runtimeLifetimeCancellationToken);
                shutdownAttempted = true;
                var terminating = original.AgentChat.TerminateAsync(CancellationToken.None);
                original.PendingRestartCleanup = terminating;
                ObserveCleanup(terminating);
                await terminating.WaitAsync(cleanup.Token).ConfigureAwait(false);
                cleanup.Token.ThrowIfCancellationRequested();
                var persisting = original.AgentChat.PersistFinalRuntimeStateAsync(cleanup.Token).AsTask();
                original.PendingRestartCleanup = persisting;
                ObserveCleanup(persisting);
                await persisting.WaitAsync(cleanup.Token).ConfigureAwait(false);
                cleanup.Token.ThrowIfCancellationRequested();
                original.ShutdownConfirmed = true;
            }

            using (var deadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(15), cleanupTimeProvider ?? TimeProvider.System))
            using (var cleanup = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, runtimeLifetimeCancellationToken))
            {
                var transitioning = persistStoppedAsync(cleanup.Token).AsTask();
                original.PendingRestartCleanup = transitioning;
                ObserveCleanup(transitioning);
                await transitioning.WaitAsync(cleanup.Token).ConfigureAwait(false);
                cleanup.Token.ThrowIfCancellationRequested();
            }
            runtimeLifetimeCancellationToken.ThrowIfCancellationRequested();
            var effectiveServices = this.WithSelfAsFactory(
                services ?? original.AgentChat.RuntimeServices ?? this._services);
            using var startupDeadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(15), cleanupTimeProvider ?? TimeProvider.System);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(
                startupDeadline.Token, runtimeLifetimeCancellationToken);
            var creating = this.CreateChatOnForegroundAsync(
                original.AgentChat.CreateRuntimeReplacementRequest(effectiveServices, checkpoint) with
                {
                    CancellationToken = startup.Token,
                },
                startup.Token);
            try
            {
                replacement = await creating.WaitAsync(startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!creating.IsCompleted)
            {
                original.ShutdownConfirmed = false;
                var lateCleanup = CleanupLateReplacementAsync(creating);
                original.PendingRestartCleanup = lateCleanup;
                ObserveCleanup(lateCleanup);
                throw new AgentRuntimeRestartCleanupUnconfirmedException(error);
            }
            runtimeLifetimeCancellationToken.ThrowIfCancellationRequested();
            PopulateAgentChatRef(effectiveServices, replacement);
            await this._gate.WaitAsync(startup.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(this._disposed, this);
                if (!this._entries.TryGetValue(sessionId, out var current)
                    || !ReferenceEquals(current, original))
                    throw new InvalidOperationException("The restarting runtime registration changed.");
                this._entries[sessionId] = new Entry
                {
                    AgentChat = replacement,
                    RefCount = 1,
                    DisposalCompletion = completion,
                };
            }
            finally
            {
                this._gate.Release();
            }
            await this.PostRestartForegroundAsync(() =>
                this._runningSessions.Add(new RunningAgentChat(sessionId, this, replacement)),
                startup.Token, cleanupTimeProvider ?? TimeProvider.System)
                .ConfigureAwait(false);
            await this._gate.WaitAsync(startup.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(this._disposed, this);
                this._entries[sessionId].DisposalCompletion = null;
            }
            finally
            {
                this._gate.Release();
            }
            completion.TrySetResult();
            return this.MakeLease(sessionId, replacement);
        }
        catch (Exception error)
        {
            Exception failure = error;
            if (!shutdownAttempted && !original.ShutdownConfirmed)
            {
                var stopping = original.AgentChat.TerminateAsync(CancellationToken.None);
                original.PendingRestartCleanup = stopping;
                ObserveCleanup(stopping);
            }
            if (replacement is not null)
            {
                ObserveCleanup(this.PostToForegroundAsync(() =>
                {
                    for (var index = this._runningSessions.Count - 1; index >= 0; index--)
                        if (ReferenceEquals(this._runningSessions[index].AgentChat, replacement))
                            this._runningSessions.RemoveAt(index);
                }));
                if (error is OperationCanceledException)
                {
                    original.ShutdownConfirmed = false;
                    failure = new AgentRuntimeRestartCleanupUnconfirmedException(error);
                }
                try
                {
                    using var deadline = new CancellationTokenSource(
                        TimeSpan.FromSeconds(15), cleanupTimeProvider ?? TimeProvider.System);
                    var cleaning = replacement.DisposeAsync().AsTask();
                    original.PendingRestartCleanup = cleaning;
                    ObserveCleanup(cleaning);
                    await cleaning.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    failure = new AggregateException(error, cleanupError);
                }
            }
            completion.TrySetException(failure);
            // Observe the shared failure while retaining the stopped entry as an acquisition fence.
            await completion.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!ReferenceEquals(failure, error))
                throw failure;
            throw;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> outstandingRestartCleanup = new();

    private async Task PostRestartForegroundAsync(
        Action action,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        var token = lifetime.Token;
        var scheduled = Task.Factory.StartNew(() =>
        {
            token.ThrowIfCancellationRequested();
            action();
        }, token, TaskCreationOptions.None, this._foregroundScheduler);
        ObserveCleanup(scheduled);
        await scheduled.WaitAsync(token).ConfigureAwait(false);
    }

    private static void ObserveCleanup(Task cleanup)
    {
        outstandingRestartCleanup.TryAdd(cleanup, 0);
        _ = cleanup.ContinueWith(task =>
        {
            _ = task.Exception;
            outstandingRestartCleanup.TryRemove(task, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static async Task CleanupLateReplacementAsync(Task<AgentChat> creating)
    {
        var lateReplacement = await creating.ConfigureAwait(false);
        await lateReplacement.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> TerminateCoreAsync(AgentSessionId sessionId, IAgentChat? expected, CancellationToken ct)
    {
        AgentChat? toDispose;
        TaskCompletionSource disposalCompletion;
        bool ownsDisposal;

        await _gate.WaitAsync(ct);
        try
        {
            if (!_entries.TryGetValue(sessionId, out var entry)
                || expected is not null && !ReferenceEquals(entry.AgentChat, expected))
            {
                return false;
            }

            entry.RefCount = 0;
            ownsDisposal = entry.DisposalCompletion is null;
            entry.DisposalCompletion ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            disposalCompletion = entry.DisposalCompletion;
            toDispose = entry.AgentChat;
        }
        finally
        {
            _gate.Release();
        }

        if (!ownsDisposal)
        {
            if (disposalCompletion.Task.IsFaulted)
                await disposalCompletion.Task.WaitAsync(ct).ConfigureAwait(false);
            await toDispose.TerminateAsync(ct).ConfigureAwait(false);
            await disposalCompletion.Task.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }

        await DisposeEntryAsync(sessionId, toDispose, disposalCompletion, terminate: true).ConfigureAwait(false);
        return true;
    }

    private async Task DisposeEntryAsync(
        AgentSessionId sessionId, AgentChat chat, TaskCompletionSource completion, bool terminate)
    {
        Exception? failure = null;
        try
        {
            await PostToForegroundAsync(() =>
            {
                var item = _runningSessions.FirstOrDefault(r => r.SessionId == sessionId
                    && ReferenceEquals(r.AgentChat, chat));
                if (item is not null)
                    _runningSessions.Remove(item);
            });
            if (terminate)
                await chat.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            else
                await chat.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }

        if (failure is null)
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (_entries.TryGetValue(sessionId, out var current)
                    && ReferenceEquals(current.AgentChat, chat))
                    _entries.Remove(sessionId);
            }
            finally
            {
                _gate.Release();
            }
            completion.TrySetResult();
        }
        else
        {
            // A failed teardown is not a reusable chat or permission to start its replacement.
            completion.TrySetException(failure);
        }
        await completion.Task.ConfigureAwait(false);
    }

    // Fix #1109: every chat this factory creates MUST reach back to the factory so restore
    // (AgentChat.RestoreSubAgentsAsync) and live sub-agent creation work. The factory *is* the
    // IRunningAgentChatFactory. Always inject unconditionally — the old
    // "preserve intentional override" branch is gone because a null override was the same silent
    // misroute (issue #1110) as no factory at all, and an intentional non-null override that
    // wasn't the outer factory would be wired past our sub-agent lifecycle bookkeeping.
    //
    // #1306: Also inject a fresh <see cref="AgentChatRef"/> as
    // <see cref="AgentServices.CurrentAgentChatRef"/> so the named
    // <c>agent-session</c> toolset registered via
    // <see cref="ToolsetFactory.CreateAgentSessionToolsetFactory(IToolsetFactory?)"/> can resolve
    // the parent chat when its tools are invoked. The caller populates <c>chatRef.Chat</c>
    // after <see cref="AgentChat.CreateAsync"/> returns.
    private AgentServices WithSelfAsFactory(AgentServices baseServices)
        => baseServices with { RunningAgentChatFactory = this, CurrentAgentChatRef = new AgentChatRef() };

    private static void PopulateAgentChatRef(AgentServices effectiveServices, AgentChat chat)
    {
        if (effectiveServices.CurrentAgentChatRef is AgentChatRef chatRef)
        {
            chatRef.Chat = chat;
        }
    }

    private async ValueTask<bool> ReleaseAsync(AgentSessionId sessionId, AgentChat expected, bool idleOnly = false)
    {
        AgentChat? toDispose = null;
        TaskCompletionSource? disposalCompletion = null;

        await _gate.WaitAsync();
        try
        {
            if (_entries.TryGetValue(sessionId, out var entry)
                && ReferenceEquals(entry.AgentChat, expected)
                && entry.DisposalCompletion is null)
            {
                if (idleOnly && !expected.TryCommitIdleViewerRelease(entry.RefCount == 1, CommitRelease))
                    return false;
                if (!idleOnly)
                    CommitRelease();

                void CommitRelease()
                {
                    entry.RefCount--;
                    if (entry.RefCount <= 0)
                    {
                        entry.RefCount = 0;
                        entry.DisposalCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        disposalCompletion = entry.DisposalCompletion;
                        toDispose = entry.AgentChat;
                    }
                }
            }
            else if (idleOnly)
                return false;
        }
        finally
        {
            _gate.Release();
        }

        if (toDispose is not null)
        {
            await DisposeEntryAsync(sessionId, toDispose, disposalCompletion!, terminate: false).ConfigureAwait(false);
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> toDispose;

        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            toDispose = _entries.Values.ToList();
            _entries.Clear();
        }
        finally
        {
            _gate.Release();
        }

        await Task.WhenAll(toDispose.Select(async entry =>
        {
            if (entry.PendingRestartCleanup is { } pending)
                await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await entry.AgentChat.DisposeAsync().ConfigureAwait(false);
        }));
    }

    private Task PostToForegroundAsync(Action action)
        => Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.None,
            _foregroundScheduler);

    // AgentChat construction and initialization must happen on the foreground context (issue
    // #909). The factory is invoked both from the GUI (already on the UI thread) and from
    // thread-agnostic contexts such as agent tools creating sub-sessions; since the factory owns
    // the foreground scheduler, it schedules creation onto it so the invariant holds structurally
    // for every caller.
    //
    // #1405: the secret-materialization/consent gate runs here, on the foreground scheduler, so the
    // GUI open-session path materializes ${SECRET:...} placeholders (and can surface the consent
    // dialog on the UI thread) before the MCP transport is built. AgentFactory.CreateAgentChatAsync
    // materializes on its own path; MaterializeSecretsIfNeededAsync is idempotent so a definition
    // already carrying opaque handles is not re-scanned or re-prompted.
    private Task<AgentChat> CreateChatOnForegroundAsync(
        InternalCreateAgentChatRequest request,
        CancellationToken ct)
        => Task.Factory.StartNew(
            async () =>
            {
                var (definition, services) = await AgentFactory.MaterializeSecretsIfNeededAsync(
                    request.AgentDefinition,
                    request.AgentServices,
                    manifest: null,
                    agentSessionId: request.AgentSessionId,
                    ct).ConfigureAwait(true);
                var effectiveRequest = request with
                {
                    AgentDefinition = definition,
                    AgentServices = services,
                    OwnedResources = !ReferenceEquals(request.AgentServices?.SecretPlaceholderResolver, services?.SecretPlaceholderResolver)
                        && services?.SecretPlaceholderResolver is Secrets.ISecretPlaceholderResolver resolver
                            ? (request.OwnedResources ?? []).Append(resolver).ToArray()
                            : request.OwnedResources,
                };
                try
                {
                    return await AgentChat.CreateAsync(effectiveRequest).ConfigureAwait(true);
                }
                catch
                {
                    if (!ReferenceEquals(request.AgentServices?.SecretPlaceholderResolver, services?.SecretPlaceholderResolver)
                        && services?.SecretPlaceholderResolver is Secrets.ISecretPlaceholderResolver ownedResolver)
                    {
                        await ownedResolver.DisposeAsync().ConfigureAwait(true);
                    }
                    throw;
                }
            },
            ct,
            TaskCreationOptions.DenyChildAttach,
            _foregroundScheduler).Unwrap();
}
