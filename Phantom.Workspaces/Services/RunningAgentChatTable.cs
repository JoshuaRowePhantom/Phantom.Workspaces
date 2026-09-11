using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AgentSchema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;
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
    private readonly ILogger<RunningAgentChatTable> logger;
    private readonly Dictionary<AgentSessionId, (string EntityName, string? EntityId, string? WorkspaceId)> _entityInfo = new();
    private readonly Dictionary<AgentSessionId, RunningAgentChatLease> _continueInBackgroundLeases = new();
    private readonly Dictionary<RemoteRuntimeKey, RemoteAcquisitionFlight> remoteSessions = new();
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
        IAgentSessionRuntimeContextFactory? runtimeContextFactory = null,
        ILogger<RunningAgentChatTable>? logger = null)
    {
        _factory = factory;
        this.runtimeContextFactory = runtimeContextFactory
            ?? new AgentSessionRuntimeContextFactory(null);
        this.logger = logger ?? NullLogger<RunningAgentChatTable>.Instance;
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
        try
        {
            ValidateAcquisitionRequest(request);
        }
        catch (Exception error) when (
            request.AcquisitionMode != AgentChatAcquisitionMode.Local
            && request.OwningProfileTransport is not null)
        {
            try
            {
                await request.OwningProfileTransport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(error, cleanupError);
            }
            throw;
        }
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
            return await this.AcquireLocalAsync(request, localGate, ct).ConfigureAwait(false);
        }
        finally
        {
            localGate.Release();
        }
    }

    private async Task<RunningAgentChatLease> AcquireLocalAsync(
        AcquireAgentChatRequest request,
        SemaphoreSlim localGate,
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
            var executionTrustContext = CreateExecutionTrustContext(
                services,
                runtimeContext.Intent);
            services = (services ?? new AgentServices()) with
            {
                ExecutorBindings = runtimeContext.Intent.ExecutorBindings,
                ExecutorTransportFactoryRegistry = runtimeContext.TransportFactoryRegistry,
                RemoteAgentSessionRuntimeIntent = runtimeContext.Intent,
                AgentExecutionTrustContext = executionTrustContext,
                ExecutionTrustContext = executionTrustContext,
                CurrentSessionContext = CreateCurrentSessionContext(
                    services?.CurrentSessionContext as CurrentSessionContext,
                    sessionId,
                    runtimeContext.Intent),
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
            onDispose: async () =>
            {
                await localGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    localGate.Release();
                }
            },
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

    private static AgentExecutionTrustContext? CreateExecutionTrustContext(
        AgentServices? services,
        PersistedAgentSessionRuntimeIntent intent)
    {
        if (intent.TrustProfileReference is null
            || intent.ExpectedTrustProfileRevision is null)
        {
            return null;
        }

        var reference = new AgentExecutionTrustProfileReference(
            "trust-profile",
            intent.TrustProfileReference,
            intent.ExpectedTrustProfileRevision);
        if (services?.TrustProfileResolver is IRemoteTrustProfileResolver resolver
            && services.TrustProfilePolicyCompiler is ITrustProfileProcessPolicyCompiler compiler)
        {
            return new AgentExecutionTrustContext(reference, resolver, compiler);
        }

        return new AgentExecutionTrustContext(reference);
    }

    private async Task<RunningAgentChatLease> AcquireRemoteAsync(
        AcquireAgentChatRequest request,
        CancellationToken ct)
    {
        var entity = request.AgentSessionEntity!.Value;
        AgentSessionRuntimeContext context;
        try
        {
            context = this.runtimeContextFactory.Create(entity);
        }
        catch (Exception error)
        {
            try
            {
                await request.OwningProfileTransport!.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(error, cleanupError);
            }
            throw;
        }
        var key = new RemoteRuntimeKey(
            request.AgentSessionId,
            context.Intent.OwningProfileEntityId,
            context.Intent.OwnershipGeneration);
        RemoteAcquisitionFlight flight;
        ITransport? redundantTransport = null;
        lock (this._entityInfoLock)
        {
            if (!this.remoteSessions.TryGetValue(key, out flight!))
            {
                flight = new RemoteAcquisitionFlight(
                    request.OwningProfileTransport!,
                    cancellationToken => this.CreateRemoteSessionAsync(
                        key,
                        request,
                        context,
                        cancellationToken));
                this.remoteSessions.Add(key, flight);
            }
            else if (!ReferenceEquals(flight.Transport, request.OwningProfileTransport))
            {
                redundantTransport = request.OwningProfileTransport;
            }
            flight.AddWaiter();
        }

        var cancelledWaiter = false;
        RemoteRunningSession? abandonedSession = null;
        try
        {
            if (redundantTransport is not null)
                await redundantTransport.DisposeAsync().ConfigureAwait(false);

            var session = await flight.Creation.WaitAsync(ct).ConfigureAwait(false);
            return await session.AcquireAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelledWaiter = true;
            throw;
        }
        catch
        {
            this.RemoveFailedRemoteFlight(key, flight);
            throw;
        }
        finally
        {
            var cleanUpCancelledFlight = false;
            lock (this._entityInfoLock)
            {
                if (flight.RemoveWaiter() == 0 && cancelledWaiter)
                {
                    cleanUpCancelledFlight = true;
                    if (this.remoteSessions.TryGetValue(key, out var currentFlight)
                        && ReferenceEquals(currentFlight, flight))
                    {
                        this.remoteSessions.Remove(key);
                    }
                }
            }

            if (cleanUpCancelledFlight)
            {
                flight.Cancel();
                try
                {
                    var completedSession = await flight.Creation.ConfigureAwait(false);
                    if (completedSession.CanRemoveWithoutViewer)
                        abandonedSession = completedSession;
                }
                catch (OperationCanceledException)
                {
                }
                flight.Dispose();
            }

            if (abandonedSession is not null)
            {
                await this.UnpublishRemoteAsync(abandonedSession).ConfigureAwait(false);
                await abandonedSession.CloseAsync(detach: false).ConfigureAwait(false);
            }
        }
    }

    private void RemoveFailedRemoteFlight(
        RemoteRuntimeKey key,
        RemoteAcquisitionFlight flight)
    {
        if (!flight.Creation.IsCompletedSuccessfully)
        {
            var removed = false;
            lock (this._entityInfoLock)
            {
                if (this.remoteSessions.TryGetValue(key, out var current)
                    && ReferenceEquals(current, flight))
                {
                    this.remoteSessions.Remove(key);
                    removed = true;
                }
            }
            if (removed)
                flight.Dispose();
        }
    }

    private static CurrentSessionContext CreateCurrentSessionContext(
        CurrentSessionContext? existing,
        AgentSessionId sessionId,
        PersistedAgentSessionRuntimeIntent intent)
        => existing is null
            ? new CurrentSessionContext
            {
                AgentSessionId = sessionId.Value,
                OwningProfileEntityId = intent.OwningProfileEntityId,
                OwnershipGeneration = intent.OwnershipGeneration,
            }
            : existing with
            {
                AgentSessionId = sessionId.Value,
                OwningProfileEntityId = intent.OwningProfileEntityId,
                OwnershipGeneration = intent.OwnershipGeneration,
            };

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
                request.OwningProfileTransport!,
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
                    row.SetCanSetContinueInBackground(true);
                    row.SetContinueInBackground(chat.ContinueInBackground);
                    row.SetViewerCount(chat.ViewerCount);
                    row.SetIsInterruptible(chat.RunningItems.Count > 0);
                    row.SetIsConnected(chat.IsConnected);
                    row.SetIsTerminal(chat.IsTerminal);
                    session.Row = row;
                    lock (this._entityInfoLock)
                        this.publishedRemoteSessions[key.SessionId] = session;
                    this._runningSessions.Add(row);
                },
                ct).ConfigureAwait(false);
            return session;
        }

        catch (Exception error)
        {
            Exception failure = error;
            try
            {
                if (chat is not null)
                    await chat.DisposeAsync().ConfigureAwait(false);
                else
                    await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                failure = new AggregateException(failure, cleanupError);
            }

            try
            {
                await request.OwningProfileTransport!.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                failure = new AggregateException(failure, cleanupError);
            }
            throw failure;
        }
    }

    private static Task RunOnSchedulerAsync(
        TaskScheduler? scheduler,
        Action action,
        CancellationToken ct)
    {
        scheduler ??= TaskScheduler.Current;
        if (TaskScheduler.Current == scheduler)
        {
            action();
            return Task.CompletedTask;
        }

        return Task.Factory.StartNew(
            action,
            ct,
            TaskCreationOptions.DenyChildAttach,
            scheduler);
    }

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
                var row = new RunningAgentChatWithEntityInfo(added, name, id, workspaceId)
                {
                    ViewerCount = 0,
                };
                lock (this._entityInfoLock)
                {
                    row.SetCanSetContinueInBackground(
                        this.localRuntimeRegistry is not null
                        && this.localRuntimeEntities.ContainsKey(added.SessionId));
                }
                _runningSessions.Add(row);
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
                        _runningSessions[i].Dispose();
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
            entry = this.FindEntry(sessionId)
                ?? throw new ArgumentException("The agent session is not running.", nameof(sessionId));
            RunningAgentChatLease? newlyPinnedLease = null;
            if (continueInBackground)
            {
                lock (this._entityInfoLock)
                {
                    this._continueInBackgroundLeases.TryGetValue(sessionId, out newlyPinnedLease);
                }
                if (newlyPinnedLease is null)
                {
                    newlyPinnedLease = await _factory.GetAsync(
                        sessionId,
                        registerAsRunningAgent: false,
                        ct).ConfigureAwait(false);
                    lock (this._entityInfoLock)
                        this._continueInBackgroundLeases[sessionId] = newlyPinnedLease;
                }
                else
                {
                    newlyPinnedLease = null;
                }
            }

            try
            {
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
            }
            catch
            {
                if (newlyPinnedLease is not null)
                {
                    lock (this._entityInfoLock)
                        this._continueInBackgroundLeases.Remove(sessionId);
                    await newlyPinnedLease.DisposeAsync().ConfigureAwait(false);
                }
                throw;
            }

            if (continueInBackground)
            {
                entry.SetContinueInBackground(true);
                return;
            }

            RunningAgentChatLease? pinnedLease = null;
            lock (_entityInfoLock)
            {
                if (_continueInBackgroundLeases.Remove(sessionId, out var lease))
                    pinnedLease = lease;
            }

            if (pinnedLease is not null)
            {
                await pinnedLease.DisposeAsync().ConfigureAwait(false);
            }
            entry.SetContinueInBackground(false);
        }
        finally
        {
            localGate.Release();
        }
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
        await this.UnpublishRemoteAsync(session).ConfigureAwait(false);
        await session.CloseAsync(detach: !session.Chat.IsTerminal).ConfigureAwait(false);
    }

    private void UnregisterRemoteFlight(RemoteRunningSession session)
    {
        lock (this._entityInfoLock)
        {
            if (this.remoteSessions.TryGetValue(session.Key, out var flight)
                && flight.Creation.IsCompletedSuccessfully
                && ReferenceEquals(flight.Creation.Result, session))
            {
                this.remoteSessions.Remove(session.Key);
                flight.Dispose();
            }
        }
    }

    private Task UnpublishRemoteAsync(RemoteRunningSession session)
    {
        if (!session.TryBeginUnpublish())
            return Task.CompletedTask;

        session.Unsubscribe();
        lock (this._entityInfoLock)
        {
            if (this.remoteSessions.TryGetValue(session.Key, out var flight)
                && flight.Creation.IsCompletedSuccessfully
                && ReferenceEquals(flight.Creation.Result, session))
            {
                this.remoteSessions.Remove(session.Key);
                flight.Dispose();
            }
            if (this.publishedRemoteSessions.TryGetValue(session.Key.SessionId, out var published)
                && ReferenceEquals(published, session))
                this.publishedRemoteSessions.Remove(session.Key.SessionId);
        }
        if (session.Row is { } row)
        {
            return RunOnSchedulerAsync(
                session.ForegroundScheduler,
                () => this._runningSessions.Remove(row),
                CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    private readonly record struct RemoteRuntimeKey(AgentSessionId SessionId, string Owner, long Generation);

    private sealed class RemoteAcquisitionFlight : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private int waiterCount;

        internal RemoteAcquisitionFlight(
            ITransport transport,
            Func<CancellationToken, Task<RemoteRunningSession>> create)
        {
            this.Transport = transport;
            this.Creation = create(this.cancellation.Token);
        }

        internal ITransport Transport { get; }
        internal Task<RemoteRunningSession> Creation { get; }

        internal void AddWaiter() => this.waiterCount++;

        internal int RemoveWaiter() => --this.waiterCount;

        internal void Cancel() => this.cancellation.Cancel();

        public void Dispose() => this.cancellation.Dispose();
    }

    private sealed class RemoteRunningSession
    {
        private readonly RunningAgentChatTable table;
        private readonly ITransport ownedTransport;
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);
        private int viewerCount;
        private int closing;
        private int closed;
        private bool continueInBackground;
        private int unpublished;

        internal RemoteRunningSession(
            RunningAgentChatTable table,
            RemoteRuntimeKey key,
            RemoteAgentChat chat,
            ITransport ownedTransport,
            bool continueInBackground,
            TaskScheduler foregroundScheduler)
        {
            this.table = table;
            this.Key = key;
            this.Chat = chat;
            this.ownedTransport = ownedTransport;
            this.continueInBackground = continueInBackground;
            this.ForegroundScheduler = foregroundScheduler;
            this.Chat.RetentionChanged += this.OnRetentionChanged;
            ((INotifyCollectionChanged)this.Chat.RunningItems).CollectionChanged += this.OnRunningItemsChanged;
            this.Chat.RuntimeStateChanged += this.OnRuntimeStateChanged;
        }

        internal RemoteRuntimeKey Key { get; }
        internal RemoteAgentChat Chat { get; }
        internal TaskScheduler ForegroundScheduler { get; }
        internal RunningAgentChatWithEntityInfo? Row { get; set; }
        internal bool CanRemoveWithoutViewer =>
            Volatile.Read(ref this.viewerCount) == 0
            && !Volatile.Read(ref this.continueInBackground);

        internal async Task<RunningAgentChatLease> AcquireAsync(CancellationToken ct)
        {
            await this.lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref this.closing) != 0
                    || Volatile.Read(ref this.closed) != 0
                    || this.Chat.IsTerminal)
                {
                    throw new ObjectDisposedException(nameof(RemoteRunningSession));
                }
                Interlocked.Increment(ref this.viewerCount);
                return new RunningAgentChatLease(
                    this.Key.SessionId,
                    this.Chat,
                    this.ReleaseAsync);
            }
            finally
            {
                this.lifecycleGate.Release();
            }
        }

        internal async ValueTask ReleaseAsync()
        {
            Interlocked.Decrement(ref this.viewerCount);
            var remove = false;
            await this.lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                remove = Volatile.Read(ref this.viewerCount) == 0
                    && (!Volatile.Read(ref this.continueInBackground) || this.Chat.IsTerminal);
                if (remove)
                {
                    Volatile.Write(ref this.closing, 1);
                    this.table.UnregisterRemoteFlight(this);
                }
            }
            finally
            {
                this.lifecycleGate.Release();
            }
            if (remove)
                await this.table.RemoveRemoteAsync(this).ConfigureAwait(false);
        }

        internal async Task TerminateAsync(CancellationToken ct)
        {
            await this.lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await this.Chat.TerminateAsync(ct).ConfigureAwait(false);
                Volatile.Write(ref this.closing, 1);
                this.table.UnregisterRemoteFlight(this);
            }
            finally
            {
                this.lifecycleGate.Release();
            }
            await this.table.RemoveRemoteAsync(this).ConfigureAwait(false);
        }

        internal async Task SetContinueInBackgroundAsync(bool value, CancellationToken ct)
        {
            var remove = false;
            await this.lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await this.Chat.SetContinueInBackgroundAsync(value, ct).ConfigureAwait(false);
                Volatile.Write(ref this.continueInBackground, this.Chat.ContinueInBackground);
                remove = !Volatile.Read(ref this.continueInBackground)
                    && Volatile.Read(ref this.viewerCount) == 0;
                if (remove)
                {
                    Volatile.Write(ref this.closing, 1);
                    this.table.UnregisterRemoteFlight(this);
                }
            }
            finally
            {
                this.lifecycleGate.Release();
            }
            if (remove)
                await this.table.RemoveRemoteAsync(this).ConfigureAwait(false);
        }

        internal async Task CloseAsync(bool detach)
        {
            if (Interlocked.Exchange(ref this.closed, 1) != 0)
                return;
            Volatile.Write(ref this.closing, 1);

            Exception? failure = null;
            try
            {
                if (detach)
                    await this.Chat.DetachAsync().ConfigureAwait(false);
                else
                    await this.Chat.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                await this.ownedTransport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }

            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        internal void Unsubscribe()
        {
            this.Chat.RetentionChanged -= this.OnRetentionChanged;
            ((INotifyCollectionChanged)this.Chat.RunningItems).CollectionChanged -= this.OnRunningItemsChanged;
            this.Chat.RuntimeStateChanged -= this.OnRuntimeStateChanged;
        }

        internal bool TryBeginUnpublish() =>
            Interlocked.Exchange(ref this.unpublished, 1) == 0;

        private void OnRetentionChanged(object? sender, EventArgs args)
        {
            // RemoteAgentChat applies every protocol frame on ForegroundScheduler before raising
            // this event, so publishing here preserves ordering without orphaning another task.
            Volatile.Write(ref this.continueInBackground, this.Chat.ContinueInBackground);
            this.Row?.SetContinueInBackground(this.Chat.ContinueInBackground);
            this.Row?.SetViewerCount(this.Chat.ViewerCount);
        }

        private void OnRunningItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
            => this.Row?.SetIsInterruptible(this.Chat.RunningItems.Count > 0);

        private void OnRuntimeStateChanged(object? sender, EventArgs args)
        {
            void Publish()
            {
                this.Row?.SetIsConnected(this.Chat.IsConnected);
                this.Row?.SetIsTerminal(this.Chat.IsTerminal);
                this.Row?.SetIsInterruptible(
                    this.Chat.IsConnected
                    && !this.Chat.IsTerminal
                    && this.Chat.RunningItems.Count > 0);
                if (this.Chat.IsTerminal)
                {
                    this.ObserveTerminalCleanup();
                }
            }

            if (TaskScheduler.Current == this.ForegroundScheduler)
            {
                Publish();
            }
            else
            {
                _ = RunOnSchedulerAsync(
                    this.ForegroundScheduler,
                    Publish,
                    CancellationToken.None);
            }
        }

        private void ObserveTerminalCleanup()
        {
            _ = CleanupAsync();

            async Task CleanupAsync()
            {
                try
                {
                    if (Volatile.Read(ref this.viewerCount) == 0)
                        await this.table.RemoveRemoteAsync(this).ConfigureAwait(false);
                    else
                        await this.table.UnpublishRemoteAsync(this).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    this.table.logger.LogError(
                        exception,
                        "Failed to clean up terminal remote agent session {SessionId}.",
                        this.Key.SessionId);
                }
            }
        }
    }
}
