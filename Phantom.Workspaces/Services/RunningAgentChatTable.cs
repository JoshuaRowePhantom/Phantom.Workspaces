using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AgentSchema;
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
            ?? new AgentSessionRuntimeContextFactory(new TransportFactoryRegistryProvider());
        factory.RunningSessions.CollectionChanged += OnFactorySessionsChanged;
    }

    /// <inheritdoc/>
    public async Task<RunningAgentChatLease> AcquireAsync(
        AcquireAgentChatRequest request,
        CancellationToken ct = default)
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
        if (!isRunning && request.AgentSessionEntity is { } entity)
        {
            var runtimeContext = this.runtimeContextFactory.Create(entity);
            services = (services ?? new AgentServices()) with
            {
                ExecutorBindings = runtimeContext.ExecutorBindings,
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
}

