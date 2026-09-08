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

        var definition = await ResolveDefinitionIfNeededAsync(request, isRunning, ct).ConfigureAwait(false);

        return await _factory.GetOrCreateAsync(
            sessionId,
            definition,
            services,
            request.EntityDisplayName,
            request.EntityDescription,
            ct: ct);
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

        throw new NotSupportedException(
            "Remote agent-chat transport is introduced by the next implementation commit.");
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
                _runningSessions.Add(new RunningAgentChatWithEntityInfo(added, name, id, workspaceId));
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
        // Commit 2 (#1485) defines the surface without adding transport behaviour. The local flag
        // is stored on the entity-info row so UI observers pick it up via existing property-change
        // paths; the remote plumbing is added in a later commit.
        foreach (var entry in _runningSessions)
        {
            if (entry.SessionId == sessionId)
            {
                entry.SetContinueInBackground(continueInBackground);
                break;
            }
        }
        return Task.CompletedTask;
    }
}

