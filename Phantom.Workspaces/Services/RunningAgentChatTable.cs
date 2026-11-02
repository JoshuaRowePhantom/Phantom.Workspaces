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
    private readonly HashSet<AgentSessionId> restartingSessions = [];
    private readonly object _entityInfoLock = new();
    private readonly ObservableCollection<RunningAgentChatWithEntityInfo> _runningSessions = new();
    private ILocalAgentSessionRuntimeRegistry? localRuntimeRegistry;
    private RemoteAgentSessionHost? ownerRuntimeHost;
    private TransportPeerIdentity? localPeer;
    private readonly Dictionary<IAgentChat, HashSet<RunningAgentChatLease>> localViewers =
        new(ReferenceEqualityComparer.Instance);

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

    internal void ConfigureOwnerRuntime(RemoteAgentSessionHost host, TransportPeerIdentity peer)
    {
        this.ownerRuntimeHost = host;
        this.localPeer = peer;
    }

    internal void ApplyOwnerRuntimeRetention(AgentSessionId sessionId, bool continueInBackground)
        => this.FindEntry(sessionId)?.SetContinueInBackground(continueInBackground);

    internal async Task<RunningAgentChatLease> RestartOwnedRuntimeAsync(
        AgentSessionOwnerRestartRequest request,
        IAgentChat expected,
        AgentServices services,
        Func<AgentRuntimeQueueCheckpoint, CancellationToken, ValueTask> persistCheckpointAsync,
        Func<CancellationToken, ValueTask> persistStoppedAsync,
        CancellationToken cancellationToken,
        CancellationToken runtimeLifetimeCancellationToken,
        TimeProvider cleanupTimeProvider)
    {
        var sessionId = new AgentSessionId(request.ExpectedRuntime.AgentSessionId);
        SemaphoreSlim gate;
        lock (this._entityInfoLock)
            gate = this.localAcquisitionGates[sessionId];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (this._entityInfoLock)
                this.restartingSessions.Add(sessionId);
            if (this._factory is not AgentChatFactory factory)
                throw new InvalidOperationException("The owning factory does not support runtime replacement.");
            return await factory.RestartExactAsync(
                sessionId, expected, services, persistCheckpointAsync, cancellationToken,
                persistStoppedAsync, runtimeLifetimeCancellationToken,
                cleanupTimeProvider: cleanupTimeProvider).ConfigureAwait(false);
        }

        finally
        {
            lock (this._entityInfoLock)
                this.restartingSessions.Remove(sessionId);
            gate.Release();
        }
    }

    internal void PublishRestartState(
        IAgentChat original, AgentSessionOwnerRestartRequest request, AgentSessionOwnerRestartResult result)
    {
        lock (this._entityInfoLock)
        {
            foreach (var pair in this.localViewers)
                foreach (var viewer in pair.Value.Where(viewer => ReferenceEquals(pair.Key, original)
                    || viewer.RestartCommandState.Pending?.ExpectedRuntime == request.ExpectedRuntime))
                {
                    viewer.RestartCommandState.Pending = result.State == AgentSessionOwnerRestartState.Succeeded ? null : request;
                    viewer.RestartCommandState.Result = result;
                }
        }
        TabCommands.AgentSessionTabCommandProvider.Instance.NotifyCommandsChanged();
    }

    internal async Task ReattachLocalViewersAsync(
        IAgentChat original,
        AgentSessionRuntimeIdentity replacementIdentity,
        CancellationToken cancellationToken = default)
    {
        RunningAgentChatLease[] viewers;
        lock (this._entityInfoLock)
            viewers = this.localViewers.TryGetValue(original, out var current) ? current.ToArray() : [];
        foreach (var viewer in viewers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (viewer.ReattachAsync is not { } reattach)
                continue;
            var replacement = await this.AcquireAsync(new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId(replacementIdentity.AgentSessionId),
                OwnerRuntimeAcquisition = true,
                IsViewerReattachment = true,
            }, cancellationToken).ConfigureAwait(false);
            replacement.ReattachmentCancellation = cancellationToken;
            replacement.RestartIdentity = replacementIdentity;
            replacement.RestartRuntimeAsync = viewer.RestartRuntimeAsync;
            replacement.RestartStatusAsync = viewer.RestartStatusAsync;
            replacement.RestartCommandState = viewer.RestartCommandState;
            try
            {
                await reattach(replacement).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await replacement.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public Task<RunningAgentChatLease> AcquireAsync(
        AcquireAgentChatRequest request,
        CancellationToken ct = default)
        => this.AcquireCoreAsync(request, ct, false);

    private async Task<RunningAgentChatLease> AcquireCoreAsync(
        AcquireAgentChatRequest request,
        CancellationToken ct,
        bool ownerAuthorized)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var progress = request.ObserveInitializingChat is { } observe
            ? new AgentChatAcquisitionProgress(request.AgentSessionId.Value!, observe, request.CancelStartup,
                request.ProgressCancellation ?? ct)
            : null;
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
        if (!ownerAuthorized && !request.OwnerRuntimeAcquisition
            && request.AgentSessionEntity is { } ownerEntity
            && ownerEntity.TryGetProperty("ownership-generation", out _)
            && this.ownerRuntimeHost is { } host)
            return await host.EnsureLocalRuntimeAsync(
                this.localPeer!, request, () => this.AcquireCoreAsync(request, ct, true), ct).ConfigureAwait(false);
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
        var isViewer = !request.OwnerRuntimeAcquisition || request.IsViewerReattachment;
        // Store entity info before calling the factory so the CollectionChanged handler can read it
        // when the factory posts the Add mutation on the foreground scheduler.
        lock (_entityInfoLock)
        {
            var canonicalName = request.AgentSessionEntity is { } metadata
                ? ReadCanonicalDisplayName(metadata)
                : request.EntityDisplayName;
            var name = !string.IsNullOrWhiteSpace(canonicalName) ? canonicalName
                : request.EntityName == sessionId.Value ? "" : request.EntityName;
            if (!_entityInfo.TryGetValue(sessionId, out var previous)
                || !string.IsNullOrWhiteSpace(name))
                _entityInfo[sessionId] = (
                    name ?? "",
                    request.EntityId ?? previous.EntityId,
                    request.WorkspaceId ?? previous.WorkspaceId);
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

        var definition = request.RuntimeQueueCheckpoint is null
            ? await ResolveDefinitionIfNeededAsync(request, isRunning, ct).ConfigureAwait(false)
            : null;

        var lease = request.RuntimeQueueCheckpoint is { } checkpoint && this._factory is AgentChatFactory checkpointFactory
            ? await checkpointFactory.GetOrCreateAsync(
                sessionId, null, services, request.EntityDisplayName, request.EntityDescription, true, ct, checkpoint)
            : await _factory.GetOrCreateAsync(
            sessionId,
            definition,
            services,
            request.EntityDisplayName,
            request.EntityDescription,
            ct: ct);

        var entry = this.FindEntry(sessionId);
        if (entry is not null)
        {
            var canonicalName = request.AgentSessionEntity is { } metadata
                ? ReadCanonicalDisplayName(metadata)
                : request.EntityDisplayName;
            if (!string.IsNullOrWhiteSpace(canonicalName))
                entry.ApplyAuthoritativeDisplayName(canonicalName);
            else
                entry.ApplyLaunchName(GetEntityInfo(sessionId).EntityName);
            if (isViewer)
                entry.IncrementViewerCount();
            if (isViewer && !isRunning && continueInBackground)
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

        RunningAgentChatLease? viewer = null;
        viewer = new RunningAgentChatLease(
            lease.SessionId,
            lease.AgentChat,
            onDispose: async () =>
            {
                if (request.OwnerRuntimeAcquisition && lease.AgentChat is AgentChat { IsTerminal: true })
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    return;
                }
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
            afterDispose: async () =>
            {
                lock (this._entityInfoLock)
                {
                    if (this.localViewers.TryGetValue(lease.AgentChat, out var viewers))
                    {
                        viewers.Remove(viewer!);
                        if (viewers.Count == 0)
                            this.localViewers.Remove(lease.AgentChat);
                    }
                }
                if (isViewer && ReferenceEquals(entry, this.FindEntry(sessionId)))
                    entry?.DecrementViewerCount();
                if (isViewer && this.ownerRuntimeHost is { } lifecycle)
                    await lifecycle.ReleaseLocalViewerAsync(lease.AgentChat).ConfigureAwait(false);
            },
            prepareIdleClose: async token =>
            {
                await localGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    return await lease.TryPrepareIdleCloseAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    localGate.Release();
                }
            });
        if (isViewer)
        {
            viewer.RestartIdentity = this.ownerRuntimeHost?.GetIdentity(lease.AgentChat);
            viewer.RestartIdentityProvider = () => this.ownerRuntimeHost?.GetIdentity(lease.AgentChat);
            if (this.ownerRuntimeHost is { } runtimeHost)
            {
                viewer.RestartRuntimeAsync = (restart, token) =>
                    runtimeHost.RestartAsync(this.localPeer!, restart, token);
                viewer.RestartStatusAsync = (restart, token) =>
                    runtimeHost.GetRestartStatusAsync(this.localPeer!, restart, token);
            }
            this.ownerRuntimeHost?.AddLocalViewer(lease.AgentChat);
        }
        lock (this._entityInfoLock)
        {
            if (!this.localViewers.TryGetValue(lease.AgentChat, out var viewers))
            {
                viewers = [];
                this.localViewers.Add(lease.AgentChat, viewers);
            }
            if (isViewer)
                viewers.Add(viewer);
        }
        return viewer;
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

        // Each waiter keeps its own attempt correlation; the flight id joins them to one host operation.
        var waiterAttempt = SessionAttachDiagnosticScope.CurrentAttempt;
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
        catch (Exception error)
        {
            this.RemoveFailedRemoteFlight(key, flight);
            try
            {
                this.logger.LogWarning(
                    error,
                    "Remote session acquisition failed; shared-flight {FlightDiagnosticId}; waiter-attempt {WaiterAttempt}; "
                    + "agent-session-id {AgentSessionId}; owner-profile-entity-id {OwnerProfileEntityId}; "
                    + "ownership-generation {OwnershipGeneration}; reason remote-flight-failure.",
                    flight.DiagnosticId,
                    waiterAttempt ?? "none",
                    key.SessionId.Value,
                    key.Owner,
                    key.Generation);
            }
            catch
            {
                // A diagnostics/redaction failure must never replace the original acquisition
                // failure, block this flight's cleanup, or disturb other sessions' leases.
            }

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
                        DiagnosticAttempt = SessionAttachDiagnosticScope.CurrentAttempt,
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
                        request.WorkspaceId,
                        AgentChatInterruptState.For(chat));
                    row.ApplyAuthoritativeDisplayName(request.AgentSessionEntity is { } metadata
                        ? ReadCanonicalDisplayName(metadata)
                        : request.EntityDisplayName);
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
                            if (!this.restartingSessions.Contains(removed.SessionId))
                            {
                                _entityInfo.Remove(removed.SessionId);
                                localRuntimeEntities.Remove(removed.SessionId);
                            }
                            // Keep the session gate across epochs: old waiters and replacement
                            // acquisition must serialize on the same owner.
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

    internal static string? ReadCanonicalDisplayName(JsonElement data)
        => data.TryGetProperty("display-name", out var names)
            && names.ValueKind == JsonValueKind.Object
            && names.TryGetProperty("default", out var name)
            && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;

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
        var local = this._factory.RunningSessions.FirstOrDefault(item => item.SessionId == sessionId)?.AgentChat;
        if (local is not null && this.ownerRuntimeHost?.GetIdentity(local) is { } identity)
            return await this.ownerRuntimeHost.TerminateLocalRuntimeAsync(
                this.localPeer!, identity, ct).ConfigureAwait(false);
        return await this.TerminateLocalAsync(sessionId, null, ct).ConfigureAwait(false);
    }

    internal async Task<bool> TerminateOwnerRuntimeExactAsync(
        AgentSessionId sessionId,
        IAgentChat expected,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider ?? TimeProvider.System);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        return await this.TerminateLocalAsync(sessionId, expected, lifetime.Token, boundCleanup: true).ConfigureAwait(false);
    }

    private async Task<bool> TerminateLocalAsync(
        AgentSessionId sessionId, IAgentChat? expected, CancellationToken ct, bool boundCleanup = false)
    {
        SemaphoreSlim gate;
        lock (this._entityInfoLock)
        {
            if (!this.localAcquisitionGates.TryGetValue(sessionId, out gate!))
                return false;
        }
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var terminating = expected is null
                ? this._factory.TerminateAsync(sessionId, boundCleanup ? ct : CancellationToken.None)
                : this._factory.TerminateExactAsync(sessionId, expected, boundCleanup ? ct : CancellationToken.None);
            if (boundCleanup)
                ObserveOwnerCleanup(terminating);
            var terminated = boundCleanup
                ? await terminating.WaitAsync(ct).ConfigureAwait(false)
                : await terminating.ConfigureAwait(false);
            if (!terminated)
                return false;

            RunningAgentChatLease? retained = null;
            lock (this._entityInfoLock)
            {
                if (this._continueInBackgroundLeases.TryGetValue(sessionId, out var pin)
                    && (expected is null || ReferenceEquals(pin.AgentChat, expected)))
                {
                    this._continueInBackgroundLeases.Remove(sessionId);
                    retained = pin;
                }
            }
            if (retained is not null)
            {
                var disposing = retained.DisposeAsync().AsTask();
                if (boundCleanup)
                {
                    ObserveOwnerCleanup(disposing);
                    await disposing.WaitAsync(ct).ConfigureAwait(false);
                }
                else
                    await disposing.ConfigureAwait(false);
            }
            return true;
        }
        finally { gate.Release(); }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> pendingOwnerCleanup = new();

    private static void ObserveOwnerCleanup(Task task)
    {
        pendingOwnerCleanup.TryAdd(task, 0);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            pendingOwnerCleanup.TryRemove(completed, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <inheritdoc/>
    public Task SetContinueInBackgroundAsync(
        AgentSessionId sessionId, bool continueInBackground,
        CancellationToken ct = default)
    {
        return this.SetContinueInBackgroundCoreAsync(sessionId, continueInBackground, ct);
    }

    public Task RetainForViewerCloseAsync(
        AgentSessionId sessionId,
        CancellationToken ct = default)
        => this.SetContinueInBackgroundCoreAsync(sessionId, true, ct, requirePersistedEntity: true);

    public Task<bool> TerminateExactAsync(AgentSessionId sessionId, IAgentChat expected, CancellationToken ct = default)
    {
        var remote = this.FindRemoteSession(sessionId);
        if (remote is not null)
            return ReferenceEquals(remote.Chat, expected) ? TerminateRemoteAsync() : Task.FromResult(false);
        if (this.ownerRuntimeHost?.GetIdentity(expected) is { } identity)
            return this.ownerRuntimeHost.TerminateLocalRuntimeAsync(this.localPeer!, identity, ct);
        return this.TerminateLocalAsync(sessionId, expected, ct);

        async Task<bool> TerminateRemoteAsync()
        {
            await remote!.TerminateAsync(ct).ConfigureAwait(false);
            return true;
        }
    }

    public Task RetainExactForViewerCloseAsync(AgentSessionId sessionId, IAgentChat expected, CancellationToken ct = default)
        => this.SetContinueInBackgroundCoreAsync(sessionId, true, ct, requirePersistedEntity: true, expected: expected);

    private async Task SetContinueInBackgroundCoreAsync(
        AgentSessionId sessionId,
        bool continueInBackground,
        CancellationToken ct,
        bool requirePersistedEntity = false,
        IAgentChat? expected = null)
    {
        var entry = this.FindEntry(sessionId)
            ?? throw new ArgumentException("The agent session is not running.", nameof(sessionId));
        if (entry.IsSubAgent)
            throw new ArgumentException("Subagent retention cannot be changed directly.", nameof(sessionId));

        var remote = this.FindRemoteSession(sessionId);
        if (remote is not null)
        {
            if (expected is not null && !ReferenceEquals(remote.Chat, expected))
                throw new InvalidOperationException("The owning runtime changed.");
            await remote.SetContinueInBackgroundAsync(continueInBackground, ct).ConfigureAwait(false);
            return;
        }
        var localChat = expected ?? this._factory.RunningSessions.FirstOrDefault(item => item.SessionId == sessionId)?.AgentChat;
        if (localChat is not null && this.ownerRuntimeHost?.GetIdentity(localChat) is { } identity)
        {
            await this.ownerRuntimeHost.SetLocalRetentionAsync(
                this.localPeer!, identity, continueInBackground, ct).ConfigureAwait(false);
            entry.SetContinueInBackground(continueInBackground);
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
            if (expected is not null && !this._factory.RunningSessions.Any(
                item => item.SessionId == sessionId && ReferenceEquals(item.AgentChat, expected)))
                throw new InvalidOperationException("The owning runtime changed.");
            entry = this.FindEntry(sessionId)
                ?? throw new ArgumentException("The agent session is not running.", nameof(sessionId));
            if (requirePersistedEntity)
            {
                lock (this._entityInfoLock)
                {
                    if (!this.localRuntimeEntities.ContainsKey(sessionId))
                        throw new InvalidOperationException("Background retention requires a persisted session.");
                }
            }
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

    /// <inheritdoc/>
    public void ApplyAuthoritativeDisplayName(AgentSessionId sessionId, string? displayName)
    {
        this.FindEntry(sessionId)?.ApplyAuthoritativeDisplayName(displayName);
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
        RemoteAcquisitionFlight? removedFlight = null;
        lock (this._entityInfoLock)
        {
            if (this.remoteSessions.TryGetValue(session.Key, out var flight)
                && flight.WasCreatedFor(session))
            {
                this.remoteSessions.Remove(session.Key);
                removedFlight = flight;
            }
        }
        removedFlight?.Dispose();
    }

    private Task UnpublishRemoteAsync(RemoteRunningSession session)
    {
        if (!session.TryBeginUnpublish())
            return Task.CompletedTask;

        session.Unsubscribe();
        RemoteAcquisitionFlight? removedFlight = null;
        lock (this._entityInfoLock)
        {
            if (this.remoteSessions.TryGetValue(session.Key, out var flight)
                && flight.WasCreatedFor(session))
            {
                this.remoteSessions.Remove(session.Key);
                removedFlight = flight;
            }
            if (this.publishedRemoteSessions.TryGetValue(session.Key.SessionId, out var published)
                && ReferenceEquals(published, session))
                this.publishedRemoteSessions.Remove(session.Key.SessionId);
        }
        removedFlight?.Dispose();
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
        private RemoteRunningSession? createdSession;
        private int waiterCount;

        internal RemoteAcquisitionFlight(
            ITransport transport,
            Func<CancellationToken, Task<RemoteRunningSession>> create)
        {
            this.Transport = transport;
            this.Creation = this.CreateAndCaptureAsync(create);
        }

        internal ITransport Transport { get; }
        internal Task<RemoteRunningSession> Creation { get; }

        // One stable id for the whole shared acquisition so every joined waiter's failure record can
        // be correlated back to the single host operation without confusing their distinct attempts.
        internal string DiagnosticId { get; } = Guid.NewGuid().ToString("D");

        internal bool WasCreatedFor(RemoteRunningSession session) =>
            ReferenceEquals(Volatile.Read(ref this.createdSession), session);

        internal void AddWaiter() => this.waiterCount++;

        internal int RemoveWaiter() => --this.waiterCount;

        internal void Cancel() => this.cancellation.Cancel();

        public void Dispose() => this.cancellation.Dispose();

        private async Task<RemoteRunningSession> CreateAndCaptureAsync(
            Func<CancellationToken, Task<RemoteRunningSession>> create)
        {
            var session = await create(this.cancellation.Token).ConfigureAwait(false);
            Volatile.Write(ref this.createdSession, session);
            return session;
        }
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
                var released = false;
                var leaseGate = new SemaphoreSlim(1, 1);
                return new RunningAgentChatLease(
                    this.Key.SessionId,
                    this.Chat,
                    async () =>
                    {
                        await leaseGate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            if (!released)
                            {
                                released = true;
                                await this.ReleaseAsync().ConfigureAwait(false);
                            }
                        }
                        finally { leaseGate.Release(); }
                    },
                    prepareIdleClose: async token =>
                    {
                        await leaseGate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            if (released) return true;
                            var remove = false;
                            await this.lifecycleGate.WaitAsync(token).ConfigureAwait(false);
                            try
                            {
                                if (this.closing != 0 || this.closed != 0 || this.Chat.IsTerminal
                                    || AgentChatActivity.HasPendingWork(this.Chat))
                                    return false;
                                remove = this.viewerCount == 1 && !this.continueInBackground;
                                if (!await this.Chat.TryDetachIdleAsync(token, keepAttached: !remove).ConfigureAwait(false))
                                    return false;
                                Interlocked.Decrement(ref this.viewerCount);
                                released = true;
                                if (remove)
                                {
                                    Volatile.Write(ref this.closing, 1);
                                    this.table.UnregisterRemoteFlight(this);
                                }
                            }
                            finally { this.lifecycleGate.Release(); }
                            if (remove)
                                await this.table.RemoveRemoteAsync(this).ConfigureAwait(false);
                            return true;
                        }
                        finally { leaseGate.Release(); }
                    })
                {
                    RestartIdentity = this.Chat.RestartClient.RestartIdentity,
                    RestartRuntimeAsync = this.Chat.RestartClient.RestartAsync,
                    RestartStatusAsync = this.Chat.RestartClient.GetRestartStatusAsync,
                    RestartCommandState = this.Chat.RestartClient.RestartCommandState,
                };
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
            TabCommands.AgentSessionTabCommandProvider.Instance.NotifyCommandsChanged();
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
