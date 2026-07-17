using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Vector;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// An <see cref="IChatClient"/> that dispatches messages to sub-agents based on parsed routing
/// prefixes. Sub-agents are created on demand from <see cref="SubAgentDispatcherOptions.AgentDefinitionTools"/>
/// and routed to either by exact id match or fuzzy embedding-based matching.
/// </summary>
public sealed class SubAgentDispatcherChatClient : IChatClient, ISubAgentDispatcherCommandClient
{
    private const int MaxTruncatedPromptLength = 40;

    private readonly IRunningAgentChatFactory _runningAgentChatFactory;
    private readonly IEmbeddingsProvider _embeddingsProvider;
    private readonly IDataAccessLayer _dataAccessLayer;
    private readonly EntityName _dispatcherEntityName;
    private readonly SubAgentDispatcherOptions _options;
    private readonly AgentServices? _subAgentServices;
    private readonly TimeProvider _timeProvider;
    private readonly SubAgentMessageParser _parser;
    private readonly SubAgentFuzzyRouter _fuzzyRouter;

    private readonly Dictionary<string, DispatchedSubAgent> _subAgents = new(StringComparer.Ordinal);
    private string? _mostRecentlyDispatchedId;
    private EntityId? _dispatcherEntityId;
    private bool _disposed;

    public SubAgentDispatcherChatClient(
        IRunningAgentChatFactory runningAgentChatFactory,
        IEmbeddingsProvider embeddingsProvider,
        IDataAccessLayer dataAccessLayer,
        EntityName dispatcherEntityName,
        SubAgentDispatcherOptions options,
        AgentServices? subAgentServices = null,
        TimeProvider? timeProvider = null,
        ISlashCommandRegistry? slashCommandRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(runningAgentChatFactory);
        ArgumentNullException.ThrowIfNull(embeddingsProvider);
        ArgumentNullException.ThrowIfNull(dataAccessLayer);
        ArgumentNullException.ThrowIfNull(options);

        _runningAgentChatFactory = runningAgentChatFactory;
        _embeddingsProvider = embeddingsProvider;
        _dataAccessLayer = dataAccessLayer;
        _dispatcherEntityName = dispatcherEntityName;
        _options = options;
        _subAgentServices = subAgentServices;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _parser = new SubAgentMessageParser(options);
        _fuzzyRouter = new SubAgentFuzzyRouter(embeddingsProvider, options, _timeProvider);

        if (slashCommandRegistry is { } registry)
        {
            registry.Register(new AvailableSubAgentsSlashCommandHandler(this));
            registry.Register(new NewSubAgentSlashCommandHandler(this));
            registry.Register(new SubAgentSlashCommandHandler(this));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentDefinitionTool> AvailableDefinitions => _options.AgentDefinitionTools;

    /// <inheritdoc />
    public IReadOnlyList<SubAgentDescriptor> ActiveSubAgents =>
        _subAgents.Values
            .Select(static subAgent => new SubAgentDescriptor(subAgent.Id, subAgent.Description))
            .ToArray();

    internal IReadOnlyList<(string Id, string Description, DateTimeOffset LastUpdated)> GetSubAgentSnapshotsForTest() =>
        _subAgents.Values
            .Select(static subAgent => (subAgent.Id, subAgent.Description, subAgent.LastUpdated))
            .ToArray();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var collectedMessages = new List<ChatMessage>();
        var responseText = string.Empty;

        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (update.Text is not null)
            {
                responseText += update.Text;
            }

            // Collect complete messages from updates that carry contents
            if (update.Contents is { Count: > 0 })
            {
                collectedMessages.Add(new ChatMessage(update.Role ?? ChatRole.Assistant, update.Contents.ToList()));
            }
        }

        if (collectedMessages.Count > 0)
        {
            return new ChatResponse(collectedMessages);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var lastUserMessage = messageList
            .Where(m => m.Role == ChatRole.User)
            .LastOrDefault();

        if (lastUserMessage is null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "No user message found.");
            yield break;
        }

        var text = lastUserMessage.Text ?? string.Empty;
        var parseResult = _parser.Parse(text, _mostRecentlyDispatchedId);

        switch (parseResult)
        {
            case CreateSubAgentInstruction create:
                await foreach (var update in HandleCreateSubAgentAsync(create, cancellationToken))
                {
                    yield return update;
                }
                break;

            case RouteToSubAgentInstruction route:
                await foreach (var update in HandleRouteToSubAgentAsync(route.Id, route.Message, cancellationToken))
                {
                    yield return update;
                }
                break;

            case RouteToMostRecentInstruction mostRecent:
                await foreach (var update in HandleRouteToSubAgentAsync(mostRecent.Id, mostRecent.Message, cancellationToken))
                {
                    yield return update;
                }
                break;

            case ParseErrorInstruction error:
                yield return new ChatResponseUpdate(ChatRole.Assistant, error.Message);
                break;
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> HandleCreateSubAgentAsync(
        CreateSubAgentInstruction create,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Compute the id
        var id = ComputeSubAgentId(create);

        // Compute entity name
        var entityName = SubAgentEntityNaming.AppendSubAgentId(_dispatcherEntityName, id);

        // Acquire lease
        var sessionId = new AgentSessionId(Guid.NewGuid().ToString("n"));
        var lease = await _runningAgentChatFactory.GetOrCreateAsync(
            sessionId,
            create.Definition.Definition,
            _subAgentServices,
            displayNameOverride: id,
            descriptionOverride: Truncate(create.Prompt, MaxTruncatedPromptLength),
            ct: cancellationToken);

        // Compute description embedding
        var entityId = new EntityId(Guid.NewGuid());
        var embeddings = await _embeddingsProvider.ComputeAsync(
            [new EmbeddingInput { EntityId = entityId, Text = create.Prompt }],
            cancellationToken);
        var descriptionEmbedding = embeddings[0].Values;

        // Create the dispatched sub-agent record
        var dispatched = new DispatchedSubAgent
        {
            Id = id,
            Description = create.Prompt,
            DescriptionEmbedding = descriptionEmbedding,
            EntityId = entityId,
            Lease = lease,
            LastUpdated = _timeProvider.GetUtcNow(),
        };

        // Subscribe to events for idle detection
        var idleSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wasCancelled = false;

        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            CheckIdleCondition();
        }

        void OnTurnCompleted(object? sender, AgentChatHistoryItem item)
        {
            CheckIdleCondition();
        }

        void CheckIdleCondition()
        {
            // Idle when: history has grown past DispatchHistoryIndex AND RunningItems is empty
            if (lease.AgentChat.History.Count > dispatched.DispatchHistoryIndex
                && lease.AgentChat.RunningItems.Count == 0)
            {
                idleSignal.TrySetResult();
            }
        }

        ((INotifyCollectionChanged)lease.AgentChat.RunningItems).CollectionChanged += OnCollectionChanged;
        lease.AgentChat.TurnCompleted += OnTurnCompleted;

        try
        {
            // Yield ack update
            var truncatedPrompt = Truncate(create.Prompt, MaxTruncatedPromptLength);
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"Sending \"{truncatedPrompt}\" to {id}.\n");

            // Record dispatch history index before enqueue
            dispatched.DispatchHistoryIndex = lease.AgentChat.History.Count;

            // Enqueue the user message
            lease.AgentChat.EnqueueUserMessage(create.Prompt);

            // Store the dispatched sub-agent
            _subAgents[id] = dispatched;
            _mostRecentlyDispatchedId = id;

            // Persist the sub-agent as a child entity of the dispatcher so it survives restart.
            await PersistSubAgentAsync(dispatched, lease.SessionId, cancellationToken);

            yield return new ChatResponseUpdate(ChatRole.Assistant, $"Created sub-agent \"{id}\".\n");

            // Handle cancellation
            using var registration = cancellationToken.Register(() =>
            {
                if (lease.AgentChat.RunningItems.Count > 0)
                {
                    lease.AgentChat.Interrupt();
                }
                idleSignal.TrySetCanceled(cancellationToken);
            });

            // Wait for idle - capture cancellation state without catching
            CheckIdleCondition();
            var task = idleSignal.Task;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
            }
        }
        finally
        {
            ((INotifyCollectionChanged)lease.AgentChat.RunningItems).CollectionChanged -= OnCollectionChanged;
            lease.AgentChat.TurnCompleted -= OnTurnCompleted;
        }

        // Handle cancellation result - yields outside of catch clause
        if (wasCancelled)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Interrupted.\n");
            yield break;
        }

        // Emit new history items
        dispatched.LastUpdated = _timeProvider.GetUtcNow();
        await PersistSubAgentAsync(dispatched, lease.SessionId, cancellationToken);
        for (var i = dispatched.DispatchHistoryIndex; i < lease.AgentChat.History.Count; i++)
        {
            var historyItem = lease.AgentChat.History[i];
            yield return new ChatResponseUpdate(historyItem.Role, historyItem.Contents.ToList());
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> HandleRouteToSubAgentAsync(
        string id,
        string message,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DispatchedSubAgent? targetAgent = null;

        // Check for exact match
        if (_subAgents.TryGetValue(id, out targetAgent))
        {
            // Exact match found
        }
        else
        {
            // Use fuzzy routing
            var candidates = _subAgents.Values
                .Select(sa => new FuzzyRouteCandidate
                {
                    Id = sa.Id,
                    Description = sa.Description,
                    DescriptionEmbedding = sa.DescriptionEmbedding,
                    LastUpdated = sa.LastUpdated,
                })
                .ToList();

            var routeResult = await _fuzzyRouter.RouteAsync(id, candidates, cancellationToken);

            switch (routeResult)
            {
                case FuzzyRouteMatch match:
                    targetAgent = _subAgents[match.Id];
                    break;

                case FuzzyRouteAmbiguous ambiguous:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, ambiguous.Message);
                    yield break;
            }
        }

        if (targetAgent is null)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"Sub-agent \"{id}\" not found.\n");
            yield break;
        }

        // Route to the target agent
        var lease = targetAgent.Lease;
        var idleSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wasCancelled = false;

        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            CheckIdleCondition();
        }

        void OnTurnCompleted(object? sender, AgentChatHistoryItem item)
        {
            CheckIdleCondition();
        }

        void CheckIdleCondition()
        {
            if (lease.AgentChat.History.Count > targetAgent.DispatchHistoryIndex
                && lease.AgentChat.RunningItems.Count == 0)
            {
                idleSignal.TrySetResult();
            }
        }

        ((INotifyCollectionChanged)lease.AgentChat.RunningItems).CollectionChanged += OnCollectionChanged;
        lease.AgentChat.TurnCompleted += OnTurnCompleted;

        try
        {
            // Record dispatch history index before enqueue
            targetAgent.DispatchHistoryIndex = lease.AgentChat.History.Count;

            // Enqueue the message
            lease.AgentChat.EnqueueUserMessage(message);

            // Update most recently dispatched
            _mostRecentlyDispatchedId = targetAgent.Id;

            // Handle cancellation
            using var registration = cancellationToken.Register(() =>
            {
                if (lease.AgentChat.RunningItems.Count > 0)
                {
                    lease.AgentChat.Interrupt();
                }
                idleSignal.TrySetCanceled(cancellationToken);
            });

            // Wait for idle - capture cancellation state without catching
            CheckIdleCondition();
            try
            {
                await idleSignal.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
            }
        }
        finally
        {
            ((INotifyCollectionChanged)lease.AgentChat.RunningItems).CollectionChanged -= OnCollectionChanged;
            lease.AgentChat.TurnCompleted -= OnTurnCompleted;
        }

        // Handle cancellation result - yields outside of catch clause
        if (wasCancelled)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Interrupted.\n");
            yield break;
        }

        // Emit new history items
        targetAgent.LastUpdated = _timeProvider.GetUtcNow();
        await PersistSubAgentAsync(targetAgent, lease.SessionId, cancellationToken);
        for (var i = targetAgent.DispatchHistoryIndex; i < lease.AgentChat.History.Count; i++)
        {
            var historyItem = lease.AgentChat.History[i];
            yield return new ChatResponseUpdate(historyItem.Role, historyItem.Contents.ToList());
        }
    }

    /// <summary>
    /// Reconstructs the in-memory <see cref="_subAgents"/> dictionary after a process restart by
    /// querying the data access layer for all persisted child entities of the dispatcher's name
    /// prefix and re-leasing each sub-agent session from the running-agent-chat factory. The
    /// sub-agent <see cref="DispatchedSubAgent.Description"/> and
    /// <see cref="DispatchedSubAgent.LastUpdated"/> are restored from the persisted entity.
    /// </summary>
    public async Task RestoreSubAgentsAsync(CancellationToken cancellationToken = default)
    {
        var getResult = await _dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = _dispatcherEntityName,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
            },
            cancellationToken);

        foreach (var batch in getResult.Batches)
        {
            foreach (var snapshot in batch.Entities)
            {
                if (snapshot.Data is not { } data
                    || !TryReadPersistedSubAgent(data, out var id, out var description, out var sessionId))
                {
                    continue;
                }

                if (_subAgents.ContainsKey(id))
                {
                    continue;
                }

                var lease = await _runningAgentChatFactory.GetAsync(new AgentSessionId(sessionId), cancellationToken);

                var embeddings = await _embeddingsProvider.ComputeAsync(
                    [new EmbeddingInput { EntityId = snapshot.EntityId, Text = description }],
                    cancellationToken);

                var lastUpdated = snapshot.ModifiedTime.DateTime;
                _subAgents[id] = new DispatchedSubAgent
                {
                    Id = id,
                    Description = description,
                    DescriptionEmbedding = embeddings[0].Values,
                    EntityId = snapshot.EntityId,
                    Lease = lease,
                    LastUpdated = lastUpdated,
                    DispatchHistoryIndex = lease.AgentChat.History.Count,
                };

                if (_mostRecentlyDispatchedId is null
                    || lastUpdated >= _subAgents[_mostRecentlyDispatchedId].LastUpdated)
                {
                    _mostRecentlyDispatchedId = id;
                }
            }
        }
    }

    private static bool TryReadPersistedSubAgent(
        JsonElement data,
        out string id,
        out string description,
        out string sessionId)
    {
        id = string.Empty;
        description = string.Empty;
        sessionId = string.Empty;

        if (!data.TryGetProperty("agent-session-id", out var sessionIdElement)
            || sessionIdElement.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("sub-agent-description", out var descriptionElement)
            || descriptionElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        sessionId = sessionIdElement.GetString() ?? string.Empty;
        description = descriptionElement.GetString() ?? string.Empty;

        if (data.TryGetProperty("display-name", out var displayName)
            && displayName.ValueKind == JsonValueKind.Object
            && displayName.TryGetProperty("default", out var displayDefault)
            && displayDefault.ValueKind == JsonValueKind.String)
        {
            id = displayDefault.GetString() ?? string.Empty;
        }

        if (string.IsNullOrEmpty(id)
            && data.TryGetProperty("names", out var names)
            && names.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in names.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.Array && name.GetArrayLength() > 0)
                {
                    id = name[name.GetArrayLength() - 1].GetString() ?? string.Empty;
                    break;
                }
            }
        }

        return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(sessionId);
    }

    private async Task PersistSubAgentAsync(
        DispatchedSubAgent dispatched,
        AgentSessionId sessionId,
        CancellationToken cancellationToken)
    {
        var entityName = SubAgentEntityNaming.AppendSubAgentId(_dispatcherEntityName, dispatched.Id);
        var dispatcherEntityId = await ResolveDispatcherEntityIdAsync(cancellationToken);

        ConcurrencyTag? currentTag = null;
        var currentResult = await _dataAccessLayer.GetAsync(
            new GetRequest { Entities = [new GetEntityRequest { EntityId = dispatched.EntityId }] },
            cancellationToken);
        foreach (var batch in currentResult.Batches)
        {
            foreach (var entity in batch.Entities)
            {
                currentTag = entity.ConcurrencyTag;
            }
        }

        var data = new Dictionary<string, object?>
        {
            ["entity-id"] = dispatched.EntityId.ToString(),
            ["entity-types"] = new[] { "entity", "agent-session" },
            ["names"] = new[] { entityName.Components },
            ["display-name"] = new Dictionary<string, object?> { ["default"] = dispatched.Id },
            ["agent-session-id"] = sessionId.Value,
            ["sub-agent-description"] = dispatched.Description,
            ["parent-agent-session-ids"] = new[] { (dispatcherEntityId ?? new EntityId(Guid.Empty)).ToString() },
        };

        var element = JsonSerializer.SerializeToElement(data);

        await _dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = $"Persist sub-agent '{dispatched.Id}' under dispatcher session (issue #1027).",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = dispatched.EntityId,
                        ConcurrencyTag = currentTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = element,
                    },
                ],
            },
            cancellationToken);
    }

    private async Task<EntityId?> ResolveDispatcherEntityIdAsync(CancellationToken cancellationToken)
    {
        if (_dispatcherEntityId is { } cached)
        {
            return cached;
        }

        var getResult = await _dataAccessLayer.GetAsync(
            new GetRequest { Entities = [new GetEntityRequest { EntityName = _dispatcherEntityName }] },
            cancellationToken);

        foreach (var batch in getResult.Batches)
        {
            foreach (var entity in batch.Entities)
            {
                _dispatcherEntityId = entity.EntityId;
            }
        }

        return _dispatcherEntityId;
    }

    private string ComputeSubAgentId(CreateSubAgentInstruction create)
    {
        if (create.ExplicitId is not null)
        {
            return DeduplicateId(create.ExplicitId);
        }

        var slug = SubAgentSlugGenerator.GenerateSlug(create.Prompt, _subAgents.Keys);

        if (create.PrefixSlugWithDefinitionName)
        {
            var prefixedId = $"{create.Definition.Name}-{slug}";
            return DeduplicateId(prefixedId);
        }

        return slug;
    }

    private string DeduplicateId(string id)
    {
        if (!_subAgents.ContainsKey(id))
        {
            return id;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{id}-{suffix}";
            if (!_subAgents.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + "...";
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceType == typeof(IChatClient) ? this : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Unsubscribe from events but do NOT dispose leases - they are managed by the factory
        // and disposing them here would kill running sub-agents
    }
}
