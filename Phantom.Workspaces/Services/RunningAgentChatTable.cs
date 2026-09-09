using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
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
    private readonly Dictionary<AgentSessionId, int> _remoteViewerCounts = new();
    private readonly object _entityInfoLock = new();
    private readonly ObservableCollection<RunningAgentChatWithEntityInfo> _runningSessions = new();

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

    /// <inheritdoc/>
    public async Task<RunningAgentChatLease> AcquireAsync(
        AcquireAgentChatRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAcquisitionRequest(request);
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
        if (!isRunning && request.AgentSessionEntity is { } entity)
        {
            var localProfileEntityId = (services?.CurrentSessionContext as CurrentSessionContext)
                ?.UserComputerProfile?.EntityId;
            var runtimeContext = this.runtimeContextFactory.Create(entity, localProfileEntityId);
            services = (services ?? new AgentServices()) with
            {
                ExecutorBindings = runtimeContext.Intent.ExecutorBindings,
                ExecutorTransportFactoryRegistry = runtimeContext.TransportFactoryRegistry,
            };
        }

        if (request.AcquisitionMode != AgentChatAcquisitionMode.Local)
        {
            services = (services ?? new AgentServices()) with
            {
                RemoteRuntimeIntent = new RemoteRuntimeIntent
                {
                    AcquisitionMode = request.AcquisitionMode,
                    OwningProfileTransport = request.OwningProfileTransport
                        ?? throw new ArgumentException(
                            "Remote acquisition requires an owning profile transport.",
                            nameof(request)),
                    ReplayCursor = request.ReplayCursor,
                },
            };
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
        var isRemote = request.AcquisitionMode != AgentChatAcquisitionMode.Local;
        var viewerCount = 0;
        if (entry is not null)
        {
            if (isRemote)
            {
                lock (_entityInfoLock)
                {
                    _remoteViewerCounts.TryGetValue(sessionId, out viewerCount);
                    viewerCount++;
                    _remoteViewerCounts[sessionId] = viewerCount;
                }
            }

            entry.SetIsRemote(viewerCount > 0);
            entry.IncrementViewerCount();
        }

        AgentChat? localAgentChat = null;
        RemoteAgentChatProxy? remoteProxy = null;
        IAgentChat exposedAgentChat = lease.AgentChat;
        if (isRemote)
        {
            localAgentChat = lease.LocalAgentChat;
            if (RemoteAgentChatProxy.TryOpen(localAgentChat, isAuthorized: true, out var proxy)
                && proxy is not null)
            {
                remoteProxy = proxy;
                exposedAgentChat = proxy;
            }
        }

        return new RunningAgentChatLease(
            lease.SessionId,
            exposedAgentChat,
            onDispose: lease.DisposeAsync,
            localAgentChat: localAgentChat,
            afterDispose: () =>
            {
                var disposedEntry = this.FindEntry(sessionId);
                if (disposedEntry is not null)
                {
                    if (isRemote)
                    {
                        lock (_entityInfoLock)
                        {
                            if (_remoteViewerCounts.TryGetValue(sessionId, out var remaining) && remaining > 1)
                            {
                                _remoteViewerCounts[sessionId] = remaining - 1;
                                remaining--;
                            }
                            else
                            {
                                _remoteViewerCounts.Remove(sessionId);
                                remaining = 0;
                            }

                            disposedEntry.SetIsRemote(remaining > 0);
                        }
                    }

                    disposedEntry.DecrementViewerCount();
                }

                if (remoteProxy is not null)
                {
                    return remoteProxy.DisposeAsync();
                }

                return ValueTask.CompletedTask;
            });
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
                            _remoteViewerCounts.Remove(removed.SessionId);
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
        return await _factory.TerminateAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task SetContinueInBackgroundAsync(
        AgentSessionId sessionId, bool continueInBackground,
        CancellationToken ct = default)
    {
        foreach (var entry in _runningSessions)
        {
            if (entry.SessionId == sessionId)
            {
                entry.SetContinueInBackground(continueInBackground);
                break;
            }
        }
        return this.SetContinueInBackgroundCoreAsync(sessionId, continueInBackground, ct);
    }

    private async Task SetContinueInBackgroundCoreAsync(
        AgentSessionId sessionId,
        bool continueInBackground,
        CancellationToken ct)
    {
        if (continueInBackground)
        {
            lock (_entityInfoLock)
            {
                if (_continueInBackgroundLeases.ContainsKey(sessionId))
                {
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
}
