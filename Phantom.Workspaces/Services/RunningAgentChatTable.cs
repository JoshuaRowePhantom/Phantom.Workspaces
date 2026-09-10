using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services.AgentSessions;
using IRunningAgentChatFactory = Phantom.Workspaces.Llm.IRunningAgentChatFactory;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Wraps <see cref="IRunningAgentChatFactory"/> and maintains a parallel
/// <see cref="ObservableCollection{T}"/> of <see cref="RunningAgentChatWithEntityInfo"/> by
/// mirroring <see cref="IRunningAgentChatFactory.RunningSessions"/> and enriching each entry
/// with workspace entity display information supplied at <see cref="AcquireAsync"/> time.
///
/// Threading: <see cref="IRunningAgentChatFactory.RunningSessions"/> mutations are already
/// dispatched on the foreground scheduler (established in the factory implementation). The
/// <see cref="System.Collections.Specialized.INotifyCollectionChanged.CollectionChanged"/>
/// handler therefore runs on the foreground scheduler automatically; all mutations to
/// <see cref="RunningSessions"/> happen on the foreground scheduler with no additional marshalling.
/// </summary>
public sealed class RunningAgentChatTable : IRunningAgentChatTable
{
    private readonly IRunningAgentChatFactory _factory;
    private readonly IAgentSessionRuntimeContextFactory runtimeContextFactory;
    private readonly Dictionary<AgentSessionId, (string EntityName, string? EntityId, string? WorkspaceId)> _entityInfo = new();
    private readonly Dictionary<AgentSessionId, RunningAgentChatLease> _continueInBackgroundLeases = new();
    private readonly Dictionary<RemoteRuntimeKey, Task<RemoteRunningSession>> remoteSessions = new();
    private readonly Dictionary<AgentSessionId, RemoteRunningSession> publishedRemoteSessions = new();
    private readonly Dictionary<AgentSessionId, JsonElement> localRuntimeEntities = new();
    private readonly Dictionary<AgentSessionId, SemaphoreSlim> localAcquisitionGates = new();
    private readonly object _entityInfoLock = new();
    private readonly ObservableCollection<RunningAgentChatWithEntityInfo> _runningSessions = new();
    private ILocalAgentSessionRuntimeRegistry? localRuntimeRegistry;

    /// <inheritdoc/>
    public ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions => _runningSessions;

    public RunningAgentChatTable(
        IRunningAgentChatFactory factory,
        IAgentSessionRuntimeContextFactory? runtimeContextFactory = null)
    {
        _factory = factory;
        this.runtimeContextFactory = runtimeContextFactory
            ?? new AgentSessionRuntimeContextFactory(null);
        factory.RunningSessions.CollectionChanged += OnFactorySessionsChanged;
    }

    internal void ConfigureLocalRuntimeRegistry(ILocalAgentSessionRuntimeRegistry registry)
        => this.localRuntimeRegistry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc/>
    public async Task<RunningAgentChatLease> AcquireAsync(
        AcquireAgentChatRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAcquisitionRequest(request);
        var sessionId = request.AgentSessionId;
        if (request.AcquisitionMode != AgentChatAcquisitionMode.Local)
        {
            return await this.AcquireRemoteAsync(request, ct).ConfigureAwait(false);
        }
        SemaphoreSlim localGate;
        lock (this._entityInfoLock)
        {
            if (!this.localAcquisitionGates.TryGetValue(sessionId, out localGate!))
            {
                localGate = new SemaphoreSlim(1, 1);
                this.localAcquisitionGates.Add(sessionId, localGate);
            }
        }
        await localGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await this.AcquireLocalAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            localGate.Release();
        }
    }

    private async Task<RunningAgentChatLease> AcquireLocalAsync(
        AcquireAgentChatRequest request,
        CancellationToken ct)
    {
        var sessionId = request.AgentSessionId;
        // Store entity info before calling the factory so the CollectionChanged handler can read it
        // when the factory posts the Add mutation on the foreground scheduler.
        lock (_entityInfoLock)
        {
            _entityInfo.TryAdd(sessionId, (request.EntityName, request.EntityId, request.WorkspaceId));
        }

        var isRunning = IsRunning(sessionId);
        // Hydrate persisted runtime context BEFORE resolving the definition or invoking the
        // running-chat factory. Persisted executor bindings must be reconstructed and validated at
        // the acquisition boundary so definition resolution and chat creation see the effective
        // services; invalid or nonlocal-without-registry bindings must fail acquisition here rather
        // than silently downgrade to local execution.
        var services = request.AgentServices;
        var continueInBackground = false;
        if (!isRunning && request.AgentSessionEntity is { } entity)
        {
            var localProfileEntityId = (services?.CurrentSessionContext as CurrentSessionContext)
                ?.UserComputerProfile?.EntityId;
            var runtimeContext = this.runtimeContextFactory.Create(entity, localProfileEntityId);
            if (entity.TryGetProperty("ownership-generation", out _)
                && localProfileEntityId is { } localOwner
                && !string.Equals(
                    runtimeContext.Intent.OwningProfileEntityId,
                    localOwner.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The persisted agent session is owned by another profile and requires remote acquisition.");
            }
            continueInBackground = runtimeContext.Intent.ContinueInBackground;
            services = (services ?? new AgentServices()) with
            {
                ExecutorBindings = runtimeContext.Intent.ExecutorBindings,
                ExecutorTransportFactoryRegistry = runtimeContext.TransportFactoryRegistry,
                RemoteAgentSessionRuntimeIntent = runtimeContext.Intent,
            };
            lock (this._entityInfoLock)
                this.localRuntimeEntities[sessionId] = entity.Clone();
        }

        var definition = await ResolveDefinitionIfNeededAsync(request, isRunning, ct).ConfigureAwait(false);

        var lease = await _factory.GetOrCreateAsync(
            sessionId,
            definition,
            services,
            request.EntityDisplayName,
            request.EntityDescription,
            ct: ct);

        var entry = this.FindEntry(sessionId);
        if (entry is not null)
        {
            entry.IncrementViewerCount();
            if (!isRunning && continueInBackground)
            {
                var retainedLease = await _factory.GetAsync(
                    sessionId,
                    registerAsRunningAgent: false,
                    ct).ConfigureAwait(false);
                lock (this._entityInfoLock)
                    this._continueInBackgroundLeases[sessionId] = retainedLease;
                entry.SetContinueInBackground(true);
            }
        }

        return new RunningAgentChatLease(
            lease.SessionId,
            lease.AgentChat,
            onDispose: lease.DisposeAsync,
            afterDispose: () =>
            {
                var disposedEntry = this.FindEntry(sessionId);
                if (disposedEntry is not null)
                {
                    disposedEntry.DecrementViewerCount();
                }
                return ValueTask.CompletedTask;
            });
    }

    private async Task<RunningAgentChatLease> AcquireRemoteAsync(
        AcquireAgentChatRequest request,
        CancellationToken ct)
    {
        var entity = request.AgentSessionEntity!.Value;
        var context = this.runtimeContextFactory.Create(entity);
        var key = new RemoteRuntimeKey(
            request.AgentSessionId,
            context.Intent.OwningProfileEntityId,
            context.Intent.OwnershipGeneration);
        Task<RemoteRunningSession> creation;
        lock (this._entityInfoLock)
        {
            if (!this.remoteSessions.TryGetValue(key, out creation!))
            {
                creation = this.CreateRemoteSessionAsync(key, request, context, ct);
                this.remoteSessions.Add(key, creation);
            }
        }

        RemoteRunningSession session;
        try
        {
            session = await creation.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (this._entityInfoLock)
            {
                if (this.remoteSessions.TryGetValue(key, out var current)
                    && ReferenceEquals(current, creation))
                {
                    this.remoteSessions.Remove(key);
                }
            }
            throw;
        }

        return await session.AcquireAsync(ct).ConfigureAwait(false);
    }

    private async Task<RemoteRunningSession> CreateRemoteSessionAsync(
        RemoteRuntimeKey key,
        AcquireAgentChatRequest request,
        AgentSessionRuntimeContext context,
        CancellationToken ct)
    {
        var client = new RemoteAgentSessionClient(request.OwningProfileTransport!);
        RemoteAgentChat? chat = null;
        try
        {
            chat = await RemoteAgentChat.AttachAsync(
                new RemoteAgentChatAttachOptions
                {
                    Client = client,
                    OpenRequest = new AgentSessionOpenRequest
                    {
                        ProtocolVersion = 1,
                        AgentSessionId = key.SessionId.Value,
                        ExpectedOwningProfileEntityId = key.Owner,
                        ExpectedOwnershipGeneration = key.Generation,
                        OpenIntent = request.AcquisitionMode == AgentChatAcquisitionMode.AttachRemote
                            ? AgentSessionOpenIntent.Attach
                            : AgentSessionOpenIntent.StartOrAttach,
                        AttachmentToken = Guid.NewGuid().ToString("N"),
                        ReplayCursor = request.ReplayCursor,
                        Capabilities = [],
                    },
                    ForegroundScheduler = request.ForegroundScheduler ?? TaskScheduler.Current,
                },
                ct).ConfigureAwait(false);

            var scheduler = request.ForegroundScheduler ?? TaskScheduler.Current;
            var session = new RemoteRunningSession(
                this,
                key,
                chat,
                chat.ContinueInBackground,
                scheduler);
            await RunOnSchedulerAsync(
                scheduler,
                () =>
                {
                    var row = new RunningAgentChatWithEntityInfo(
                        key.SessionId,
                        false,
                        session.AcquireAsync,
                        request.EntityName,
                        request.EntityId,
                        request.WorkspaceId);
                    row.SetIsRemote(true);
                    row.SetContinueInBackground(chat.ContinueInBackground);
                    row.SetViewerCount(chat.ViewerCount);
                    session.Row = row;
                    lock (this._entityInfoLock)
                        this.publishedRemoteSessions[key.SessionId] = session;
                    this._runningSessions.Add(row);
                },
                ct).ConfigureAwait(false);
            return session;
        }

        catch
        {
            if (chat is not null)
                await chat.DisposeAsync().ConfigureAwait(false);
            else
                await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static Task RunOnSchedulerAsync(
        TaskScheduler? scheduler,
        Action action,
        CancellationToken ct)
        => Task.Factory.StartNew(
            action,
            ct,
            TaskCreationOptions.DenyChildAttach,
            scheduler ?? TaskScheduler.Current);

    private static void ValidateAcquisitionRequest(AcquireAgentChatRequest request)
    {
        if (!Enum.IsDefined(request.AcquisitionMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.AcquisitionMode,
                "Unknown agent-chat acquisition mode.");
        }

        if (request.AcquisitionMode == AgentChatAcquisitionMode.Local)
        {
            if (request.OwningProfileTransport is not null)
            {
                throw new ArgumentException(
                    "Local acquisition must not specify an owning profile transport.",
                    nameof(request));
            }
            return;
        }

        if (request.OwningProfileTransport is null || request.AgentSessionEntity is not { } entity)
        {
            throw new ArgumentException(
                "Remote acquisition requires an owning profile transport and persisted session entity.",
                nameof(request));
        }
        if (!entity.TryGetProperty(AgentSessionExecutorBindings.HostProfileKey, out var owner)
            || owner.ValueKind != System.Text.Json.JsonValueKind.String
            || !Guid.TryParse(owner.GetString(), out _)
            || !entity.TryGetProperty("ownership-generation", out var generation)
            || !generation.TryGetInt64(out var generationValue)
            || generationValue < 0)
        {
            throw new ArgumentException(
                "Remote acquisition requires valid persisted owner and ownership generation.",
                nameof(request));
        }

    }

    private async Task<AgentDefinition?> ResolveDefinitionIfNeededAsync(
        AcquireAgentChatRequest request,
        bool isRunning,
        CancellationToken ct)
    {
        if (isRunning)
        {
            return null;
        }

        if (request.AgentDefinitionResolver is not null)
        {
            var resolved = await request.AgentDefinitionResolver.ResolveAsync(
                new AgentDefinitionResolveRequest
                {
                    AgentDefinition = request.AgentDefinition,
                    AgentManifest = request.AgentManifest,
                    AgentSessionEntity = request.AgentSessionEntity,
                    ToolResourceFactory = request.ToolResourceFactory ?? request.AgentServices?.ToolResourceFactory,
                    Parameters = request.Parameters,
                },
                ct).ConfigureAwait(false);
            return resolved?.Definition;
        }

        if (request.AgentDefinition is not null)
        {
            return request.AgentDefinition;
        }

        if (request.AgentManifest is not null)
        {
            return await AgentFactory.CreateAgentDefinitionAsync(
                new CreateAgentDefinitionRequest
                {
                    AgentManifest = request.AgentManifest,
                    ToolResourceFactory = request.ToolResourceFactory ?? request.AgentServices?.ToolResourceFactory,
                    Parameters = request.Parameters,
                },
                ct).ConfigureAwait(false);
        }

        return null;
    }

    private bool IsRunning(AgentSessionId sessionId)
    {
        lock (_entityInfoLock)
        {
            return _runningSessions.Any(session => session.SessionId == sessionId);
        }
    }

    private void OnFactorySessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (RunningAgentChat added in e.NewItems)
            {
                var (name, id, workspaceId) = GetEntityInfo(added.SessionId);
                _runningSessions.Add(new RunningAgentChatWithEntityInfo(added, name, id, workspaceId)
                {
                    ViewerCount = 0,
                });
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (RunningAgentChat removed in e.OldItems)
            {
                for (var i = _runningSessions.Count - 1; i >= 0; i--)
                {
                    if (_runningSessions[i].SessionId == removed.SessionId)
                    {
                        _runningSessions.RemoveAt(i);
                        lock (_entityInfoLock)
                        {
                            _entityInfo.Remove(removed.SessionId);
                            localAcquisitionGates.Remove(removed.SessionId);
                            localRuntimeEntities.Remove(removed.SessionId);
                        }
                        break;
                    }
                }
            }
        }
    }

    private (string EntityName, string? EntityId, string? WorkspaceId) GetEntityInfo(AgentSessionId sessionId)
    {
        lock (_entityInfoLock)
        {
            return _entityInfo.TryGetValue(sessionId, out var info) ? info : ("", null, null);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> TerminateAsync(
        AgentSessionId sessionId, CancellationToken ct = default)
    {
        var remote = this.FindRemoteSession(sessionId);
        if (remote is not null)
        {
            await remote.TerminateAsync(ct).ConfigureAwait(false);
            return true;
        }
        return await _factory.TerminateAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task SetContinueInBackgroundAsync(
        AgentSessionId sessionId, bool continueInBackground,
        CancellationToken ct = default)
    {
        return this.SetContinueInBackgroundCoreAsync(sessionId, continueInBackground, ct);
    }

    private async Task SetContinueInBackgroundCoreAsync(
        AgentSessionId sessionId,
        bool continueInBackground,
        CancellationToken ct)
    {
        var entry = this.FindEntry(sessionId)
            ?? throw new ArgumentException("The agent session is not running.", nameof(sessionId));
        if (entry.IsSubAgent)
            throw new ArgumentException("Subagent retention cannot be changed directly.", nameof(sessionId));

        var remote = this.FindRemoteSession(sessionId);
        if (remote is not null)
        {
            await remote.SetContinueInBackgroundAsync(continueInBackground, ct).ConfigureAwait(false);
            return;
        }

        JsonElement? persistedEntity = null;
        lock (this._entityInfoLock)
        {
            if (this.localRuntimeEntities.TryGetValue(sessionId, out var entity))
                persistedEntity = entity;
        }
        if (persistedEntity is { } entityToPersist)
        {
            var registry = this.localRuntimeRegistry
                ?? throw new InvalidOperationException("The local runtime registry is unavailable.");
            await registry.SetContinueInBackgroundAsync(
                sessionId, entityToPersist, continueInBackground, ct).ConfigureAwait(false);
        }
        else
        {
            ct.ThrowIfCancellationRequested();
        }

        if (continueInBackground)
        {
            lock (_entityInfoLock)
            {
                if (_continueInBackgroundLeases.ContainsKey(sessionId))
                {
                    entry.SetContinueInBackground(true);
                    return;
                }
            }

            var lease = await _factory.GetAsync(sessionId, registerAsRunningAgent: false, ct).ConfigureAwait(false);
            lock (_entityInfoLock)
            {
                if (_continueInBackgroundLeases.ContainsKey(sessionId))
                {
                    _ = lease.DisposeAsync();
                    return;
                }

                _continueInBackgroundLeases[sessionId] = lease;
            }

            entry.SetContinueInBackground(true);
            return;
        }

        RunningAgentChatLease? pinnedLease = null;
        lock (_entityInfoLock)
        {
            if (_continueInBackgroundLeases.Remove(sessionId, out var lease))
            {
                pinnedLease = lease;
            }
        }

        if (pinnedLease is not null)
        {
            await pinnedLease.DisposeAsync().ConfigureAwait(false);
        }
        entry.SetContinueInBackground(false);
    }

    private RunningAgentChatWithEntityInfo? FindEntry(AgentSessionId sessionId)
    {
        foreach (var entry in _runningSessions)
        {
            if (entry.SessionId == sessionId)
            {
                return entry;
            }
        }

        return null;
    }

    private RemoteRunningSession? FindRemoteSession(AgentSessionId sessionId)
    {
        lock (this._entityInfoLock)
            return this.publishedRemoteSessions.GetValueOrDefault(sessionId);
    }

    private async Task RemoveRemoteAsync(RemoteRunningSession session)
    {
        session.Unsubscribe();
        lock (this._entityInfoLock)
        {
            this.remoteSessions.Remove(session.Key);
            if (this.publishedRemoteSessions.TryGetValue(session.Key.SessionId, out var published)
                && ReferenceEquals(published, session))
                this.publishedRemoteSessions.Remove(session.Key.SessionId);
        }
        if (session.Row is { } row)
        {
            await RunOnSchedulerAsync(
                session.ForegroundScheduler,
                () => this._runningSessions.Remove(row),
                CancellationToken.None).ConfigureAwait(false);
        }
        await session.Chat.DetachAsync().ConfigureAwait(false);
    }

    private readonly record struct RemoteRuntimeKey(AgentSessionId SessionId, string Owner, long Generation);

    private sealed class RemoteRunningSession
    {
        private readonly RunningAgentChatTable table;
        private int viewerCount;
        private bool continueInBackground;

        internal RemoteRunningSession(
            RunningAgentChatTable table,
            RemoteRuntimeKey key,
            RemoteAgentChat chat,
            bool continueInBackground,
            TaskScheduler foregroundScheduler)
        {
            this.table = table;
            this.Key = key;
            this.Chat = chat;
            this.continueInBackground = continueInBackground;
            this.ForegroundScheduler = foregroundScheduler;
            this.Chat.RetentionChanged += this.OnRetentionChanged;
        }

        internal RemoteRuntimeKey Key { get; }
        internal RemoteAgentChat Chat { get; }
        internal TaskScheduler ForegroundScheduler { get; }
        internal RunningAgentChatWithEntityInfo? Row { get; set; }

        internal async Task<RunningAgentChatLease> AcquireAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref this.viewerCount);
            return new RunningAgentChatLease(
                this.Key.SessionId,
                this.Chat,
                this.ReleaseAsync);
        }

        internal async ValueTask ReleaseAsync()
        {
            var remaining = 0;
            remaining = Interlocked.Decrement(ref this.viewerCount);
            if (remaining == 0 && !this.continueInBackground)
                await this.table.RemoveRemoteAsync(this).ConfigureAwait(false);
        }

        internal async Task TerminateAsync(CancellationToken ct)
        {
            await this.Chat.TerminateAsync(ct).ConfigureAwait(false);
            await this.table.RemoveRemoteAsync(this).ConfigureAwait(false);
        }

        internal async Task SetContinueInBackgroundAsync(bool value, CancellationToken ct)
        {
            await this.Chat.SetContinueInBackgroundAsync(value, ct).ConfigureAwait(false);
            this.continueInBackground = value;
            await this.PublishMetadataAsync(ct).ConfigureAwait(false);
            if (!value && this.viewerCount == 0)
                await this.table.RemoveRemoteAsync(this).ConfigureAwait(false);
        }

        internal void Unsubscribe() => this.Chat.RetentionChanged -= this.OnRetentionChanged;

        private void OnRetentionChanged(object? sender, EventArgs args)
        {
            this.continueInBackground = this.Chat.ContinueInBackground;
            _ = this.PublishMetadataAsync(CancellationToken.None);
        }

        private Task PublishMetadataAsync(CancellationToken ct)
            => RunOnSchedulerAsync(
                this.ForegroundScheduler,
                () =>
                {
                    this.Row?.SetContinueInBackground(this.Chat.ContinueInBackground);
                    this.Row?.SetViewerCount(this.Chat.ViewerCount);
                },
                ct);
    }
}
