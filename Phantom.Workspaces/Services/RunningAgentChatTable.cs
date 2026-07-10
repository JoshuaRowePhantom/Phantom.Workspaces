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
    private readonly Dictionary<AgentSessionId, (string EntityName, string? EntityId)> _entityInfo = new();
    private readonly object _entityInfoLock = new();
    private readonly ObservableCollection<RunningAgentChatWithEntityInfo> _runningSessions = new();

    /// <inheritdoc/>
    public ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions => _runningSessions;

    public RunningAgentChatTable(IRunningAgentChatFactory factory)
    {
        _factory = factory;
        factory.RunningSessions.CollectionChanged += OnFactorySessionsChanged;
    }

    /// <inheritdoc/>
    public async Task<RunningAgentChatLease> AcquireAsync(
        AgentSessionId sessionId,
        AgentDefinition? definition = null,
        AgentServices? agentServices = null,
        string entityName = "",
        string? entityId = null,
        string? entityDisplayName = null,
        string? entityDescription = null,
        CancellationToken ct = default)
    {
        // Store entity info before calling the factory so the CollectionChanged handler can read it
        // when the factory posts the Add mutation on the foreground scheduler.
        lock (_entityInfoLock)
        {
            _entityInfo.TryAdd(sessionId, (entityName, entityId));
        }

        return await _factory.GetOrCreateAsync(sessionId, definition, agentServices, entityDisplayName, entityDescription, ct);
    }

    private void OnFactorySessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (RunningAgentChat added in e.NewItems)
            {
                var (name, id) = GetEntityInfo(added.SessionId);
                _runningSessions.Add(new RunningAgentChatWithEntityInfo(added, name, id));
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

    private (string EntityName, string? EntityId) GetEntityInfo(AgentSessionId sessionId)
    {
        lock (_entityInfoLock)
        {
            return _entityInfo.TryGetValue(sessionId, out var info) ? info : ("", null);
        }
    }
}

