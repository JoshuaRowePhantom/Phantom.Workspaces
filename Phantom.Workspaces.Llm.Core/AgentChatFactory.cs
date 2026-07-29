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
internal sealed class AgentChatFactory : IRunningAgentChatFactory, IAsyncDisposable
{
    private sealed class Entry
    {
        public required AgentChat AgentChat { get; init; }
        public int RefCount { get; set; }
        public TaskCompletionSource? DisposalCompletion { get; set; }
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

    public async Task<RunningAgentChatLease> GetOrCreateAsync(
        AgentSessionId sessionId,
        AgentDefinition? definition = null,
        AgentServices? services = null,
        string? displayNameOverride = null,
        string? descriptionOverride = null,
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
                    var effectiveServices = WithSelfAsFactory(services ?? _services);

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
                        ConfiguredStore = _store,
                        ClientOverride = effectiveServices.ChatClientOverride,
                        DisplayNameOverride = displayNameOverride,
                        DescriptionOverride = descriptionOverride,
                        ForegroundScheduler = _foregroundScheduler,
                        CancellationToken = ct,
                    }, ct);
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

            await PostToForegroundAsync(() => _runningSessions.Add(new RunningAgentChat(sessionId, this)));
            return MakeLease(sessionId, newChat!);
        }
    }

    public async Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
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

            await PostToForegroundAsync(() => _runningSessions.Add(new RunningAgentChat(sessionId, this)));
            return MakeLease(sessionId, newChat!);
        }
    }

    public async Task<RunningAgentChatLease> CreateAsync(
        AgentDefinition definition,
        AgentSessionId sessionId,
        AgentServices? services = null,
        string? displayNameOverride = null,
        string? descriptionOverride = null,
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
                ForegroundScheduler = _foregroundScheduler,
                CancellationToken = ct,
            }, ct);
            _entries[sessionId] = new Entry { AgentChat = chat, RefCount = 1 };
            newChat = chat;
        }
        finally
        {
            _gate.Release();
        }

        await PostToForegroundAsync(() => _runningSessions.Add(new RunningAgentChat(sessionId, this)));
        return MakeLease(sessionId, newChat!);
    }

    private RunningAgentChatLease MakeLease(AgentSessionId sessionId, AgentChat agentChat)
        => new RunningAgentChatLease(sessionId, agentChat, () => ReleaseAsync(sessionId));

    // Fix #1109: every chat this factory creates MUST reach back to the factory so restore
    // (AgentChat.RestoreSubAgentsAsync) and live sub-agent creation work. The factory *is* the
    // IRunningAgentChatFactory. Always inject unconditionally — the old
    // "preserve intentional override" branch is gone because a null override was the same silent
    // misroute (issue #1110) as no factory at all, and an intentional non-null override that
    // wasn't the outer factory would be wired past our sub-agent lifecycle bookkeeping.
    private AgentServices WithSelfAsFactory(AgentServices baseServices)
        => baseServices with { RunningAgentChatFactory = this };

    private async ValueTask ReleaseAsync(AgentSessionId sessionId)
    {
        AgentChat? toDispose = null;
        TaskCompletionSource? disposalCompletion = null;

        await _gate.WaitAsync();
        try
        {
            if (_entries.TryGetValue(sessionId, out var entry))
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
        finally
        {
            _gate.Release();
        }

        if (toDispose is not null)
        {
            try
            {
                await PostToForegroundAsync(() =>
                {
                    var item = _runningSessions.FirstOrDefault(r => r.SessionId == sessionId);
                    if (item is not null)
                        _runningSessions.Remove(item);
                });

                await toDispose.DisposeAsync();
            }
            finally
            {
                await _gate.WaitAsync();
                try
                {
                    _entries.Remove(sessionId);
                }
                finally
                {
                    _gate.Release();
                }

                disposalCompletion?.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<AgentChat> toDispose;

        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            toDispose = _entries.Values.Select(e => e.AgentChat).ToList();
            _entries.Clear();
        }
        finally
        {
            _gate.Release();
        }

        await Task.WhenAll(toDispose.Select(c => c.DisposeAsync().AsTask()));
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
    private Task<AgentChat> CreateChatOnForegroundAsync(
        InternalCreateAgentChatRequest request,
        CancellationToken ct)
        => Task.Factory.StartNew(
            () => AgentChat.CreateAsync(request),
            ct,
            TaskCreationOptions.DenyChildAttach,
            _foregroundScheduler).Unwrap();
}
