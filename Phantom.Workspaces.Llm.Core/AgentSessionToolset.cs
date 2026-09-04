using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using System.Text.Json;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Exposes nine <c>agent_session_*</c> tools that allow a parent agent to create, send
/// messages to, monitor, read events from, and stop subordinate agent sessions (subagents).
/// Manages a dictionary of <see cref="RunningAgentChatLease"/> instances; all leases are
/// disposed when this toolset is disposed.
/// </summary>
public sealed class AgentSessionToolset : AIContextProvider, IAsyncDisposable
{
    private readonly AgentChatRef _parentChatRef;
    private readonly CurrentSessionContext _currentSessionContext;
    private readonly IRunningAgentChatFactory _factory;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<AgentSessionId, RunningAgentChatLease> _leases = new();
    private readonly object _leasesLock = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly string _stateKey = $"agent-session:{Guid.NewGuid():n}";
    private readonly AITool[] _tools;
    private int _disposed;

    internal AgentSessionToolset(
        AgentChatRef parentChatRef,
        CurrentSessionContext currentSessionContext,
        IRunningAgentChatFactory factory,
        TimeProvider? timeProvider = null)
        : base(null, null, null)
    {
        _parentChatRef = parentChatRef;
        _currentSessionContext = currentSessionContext;
        _factory = factory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tools =
        [
            new AgentSessionCreateTool(this),
            new AgentSessionListTool(this),
            new AgentSessionGetTool(this),
            new AgentSessionSendTool(this),
            new AgentSessionStopTool(this),
            new AgentSessionReadEventsTool(this),
            new AgentSessionWaitTool(this),
            new AgentSessionOnCompleteTool(this),
            new AgentSessionAcquireTool(this),
        ];
    }

    public override IReadOnlyList<string> StateKeys => [_stateKey];

    internal AgentChat ParentChat =>
        _parentChatRef.Chat
        ?? throw new InvalidOperationException("AgentChat parent is not yet initialised.");

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        _ = cancellationToken;
        return ValueTask.FromResult(new AIContext { Tools = _tools });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        _disposeCts.Cancel();

        List<RunningAgentChatLease> toDispose;
        lock (_leasesLock)
        {
            toDispose = [.. _leases.Values];
            _leases.Clear();
        }

        foreach (var lease in toDispose)
            await lease.DisposeAsync();

        _disposeCts.Dispose();
    }

    // ── Session resolution ──────────────────────────────────────────────────────

    private async ValueTask<AgentChat?> TryResolveAgentChatAsync(string? sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId == ".")
            return _parentChatRef.Chat;

        var id = new AgentSessionId(sessionId);
        lock (_leasesLock)
        {
            if (_leases.TryGetValue(id, out var lease))
                return lease.AgentChat;
        }

        // Resolve via the authoritative sub-agent table map first (issue #1386). It is populated
        // synchronously when a sub-agent is registered, whereas the SubAgents observable collection
        // is filled asynchronously on the foreground scheduler and can lag under load, intermittently
        // making a just-registered (or stop-with-disposed) session appear "not found".
        if (ParentChat.TryGetRegisteredSubAgent(sessionId) is { } registeredSubAgent)
        {
            var lease = await registeredSubAgent.AcquireLeaseAsync(cancellationToken);

            RunningAgentChatLease? duplicateLease = null;
            AgentChat? existingChat = null;
            lock (_leasesLock)
            {
                if (!_leases.TryAdd(id, lease))
                {
                    duplicateLease = lease;
                    existingChat = _leases[id].AgentChat;
                }
            }

            if (duplicateLease is not null)
            {
                await duplicateLease.DisposeAsync();
                return existingChat;
            }
            return lease.AgentChat;
        }

        // Fallback: directly-added AgentChat children are present only in the SubAgents observable
        // collection (they are not tracked in the sub-agent table map), so scan for those.
        foreach (var subAgent in ParentChat.SubAgents)
        {
            if (subAgent is AgentChat ac && ac.AgentSessionId == sessionId)
                return ac;
        }

        return null;
    }

    private SubAgent? TryFindSubAgent(string sessionId)
        => ParentChat.TryGetRegisteredSubAgent(sessionId);

    // ── Shared helpers ──────────────────────────────────────────────────────────

    private static string GetStatus(AgentChat chat)
    {
        return chat.CompletionState switch
        {
            AgentChatCompletionState.Succeeded => "stopped",
            AgentChatCompletionState.Failed => "error",
            _ => chat.RunningItems.Count > 0 ? "running" : "idle",
        };
    }

    private static string? GetString(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw is null)
            return null;
        return raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            _ => raw.ToString(),
        };
    }

    private static bool GetBool(AIFunctionArguments arguments, string name, bool defaultValue = false)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw is null)
            return defaultValue;
        return raw switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            string s when bool.TryParse(s, out var v) => v,
            _ => defaultValue,
        };
    }

    private static int GetInt(AIFunctionArguments arguments, string name, int defaultValue, int min, int max)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw is null)
            return defaultValue;
        int value = raw switch
        {
            int i => i,
            long l => (int)l,
            JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var n) => n,
            _ => defaultValue,
        };
        return Math.Clamp(value, min, max);
    }

    private static JsonElement Serialize(object? value) => JsonSerializer.SerializeToElement(value);

    private static string EventType(AgentChatHistoryItem item)
    {
        if (item.Role == ChatRole.User) return "user";
        if (item.Role == ChatRole.Tool) return "tool_result";
        if (item.Role == AgentChatHistoryItem.DiagnosticChatRole) return "diagnostic";
        // Assistant — distinguish tool_call vs text
        foreach (var content in item.Contents)
        {
            if (content is FunctionCallContent) return "tool_call";
        }

        return "assistant";
    }

    private static string ContentPreview(AgentChatHistoryItem item, int maxLength = 500)
    {
        var text = string.Concat(item.Contents.Select(static c => c switch
        {
            TextContent tc => tc.Text,
            FunctionCallContent fc => $"[call:{fc.Name}]",
            FunctionResultContent fr => $"[result:{fr.CallId}]",
            _ => $"[{c.GetType().Name}]",
        }));
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    // ── Tool implementations ────────────────────────────────────────────────────

    private sealed class AgentSessionCreateTool : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "definition": {
                  "type": "object",
                  "description": "Inline agent definition (kind, model, instructions, tools). Omit to clone the parent agent's definition."
                },
                "initial_message": {
                  "type": "string",
                  "description": "First user message to enqueue after the session is created."
                }
              },
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly AgentSessionToolset _toolset;

        public AgentSessionCreateTool(AgentSessionToolset toolset) { _toolset = toolset; }

        public override string Name => "agent_session_create";
        public override string Description => "Create and start a new subagent session. Returns a session_id for use with other agent_session_* tools.";
        public override JsonElement JsonSchema => Schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            // Resolve agent definition
            AgentDefinition? definition = null;
            if (arguments.TryGetValue("definition", out var rawDef) && rawDef is not null)
            {
                var defJson = rawDef switch
                {
                    JsonElement e => e.GetRawText(),
                    string s => s,
                    _ => null,
                };
                if (defJson is not null)
                {
                    definition = PhantomAgentSchema.AgentDefinitionFromJson(defJson);
                    if (definition is null)
                        return Serialize(new { error = "Could not parse the supplied definition." });
                }
            }

            definition ??= _toolset._parentChatRef.Chat?.AgentDefinition;
            if (definition is null)
                return Serialize(new { error = "No agent definition available; provide a definition or ensure the parent's definition is accessible." });

            var sessionId = new AgentSessionId(Guid.NewGuid().ToString("n"));
            RunningAgentChatLease lease;
            try
            {
                lease = await _toolset._factory.CreateAsync(definition, sessionId, null, ct: cancellationToken);
            }
            catch (Exception ex)
            {
                return Serialize(new { error = $"Failed to create subagent session: {ex.Message}" });
            }

            // Register with parent's sub-agent table
            var parentChat = _toolset.ParentChat;
            await ((ISubAgentTable)parentChat).Add(lease.AgentChat);

            lock (_toolset._leasesLock)
            {
                _toolset._leases[sessionId] = lease;
            }

            var initialMessage = GetString(arguments, "initial_message");
            if (!string.IsNullOrEmpty(initialMessage))
                lease.AgentChat.EnqueueUserMessage(initialMessage);

            return Serialize(new
            {
                session_id = sessionId.Value,
                status = GetStatus(lease.AgentChat),
                created_at = _toolset._timeProvider.GetUtcNow().ToString("O"),
            });
        }
    }

    private sealed class AgentSessionListTool : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "status": {
                  "type": "string",
                  "description": "Filter by status: running, idle, stopped, error.",
                  "enum": ["running", "idle", "stopped", "error"]
                }
              },
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly AgentSessionToolset _toolset;

        public AgentSessionListTool(AgentSessionToolset toolset) { _toolset = toolset; }

        public override string Name => "agent_session_list";
        public override string Description => "List all subagent sessions owned by the current agent, optionally filtered by status.";
        public override JsonElement JsonSchema => Schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            var statusFilter = GetString(arguments, "status");
            var parentChat = _toolset.ParentChat;

            var sessions = parentChat.SubAgents
                .Select(sub =>
                {
                    var chat = sub as AgentChat;
                    var sessionId = sub is SubAgent subAgent ? subAgent.SessionId.Value
                        : chat?.AgentSessionId ?? string.Empty;
                    
                    string status;
                    DateTime? lastActivity;
                    
                    if (chat is not null)
                    {
                        status = GetStatus(chat);
                        lastActivity = chat.LastUpdatedAt;
                    }
                    else if (sub is SubAgent)
                    {
                        var completionState = sub.CompletionState;
                        status = completionState switch
                        {
                            AgentChatCompletionState.Succeeded => "stopped",
                            AgentChatCompletionState.Failed => "error",
                            AgentChatCompletionState.Unknown => "unknown",
                            _ => "idle",
                        };
                        lastActivity = sub.LastUpdatedAt == DateTime.MinValue ? null : sub.LastUpdatedAt;
                    }
                    else
                    {
                        status = "unknown";
                        lastActivity = null;
                    }
                    
                    return new
                    {
                        session_id = sessionId,
                        status,
                        last_activity_at = lastActivity?.ToString("O"),
                    };
                })
                .Where(s => statusFilter is null || s.status == statusFilter)
                .ToArray();

            return ValueTask.FromResult<object?>(Serialize(new { sessions }));
        }
    }

    private sealed class AgentSessionGetTool : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "session_id": {
                  "type": "string",
                  "description": "Session ID. Use \".\" for the current (parent) session."
                }
              },
              "required": ["session_id"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly AgentSessionToolset _toolset;

        public AgentSessionGetTool(AgentSessionToolset toolset) { _toolset = toolset; }

        public override string Name => "agent_session_get";
        public override string Description => "Get the current status and running items of a session.";
        public override JsonElement JsonSchema => Schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var sessionId = GetString(arguments, "session_id");
            var chat = await _toolset.TryResolveAgentChatAsync(sessionId, cancellationToken);

            if (chat is null)
                return Serialize(new { error = $"Unknown session_id: '{sessionId}'." });

            var runningItems = chat.RunningItems
                .SelectMany(static item => item.Items)
                .Take(10)
                .Select(static item => new
                {
                    role = item.Role.Value,
                    preview = ContentPreview(item, 200),
                })
                .ToArray();

            return Serialize(new
            {
                session_id = chat.AgentSessionId,
                status = GetStatus(chat),
                is_busy = chat.IsBusy,
                running_items = runningItems,
                last_activity_at = chat.LastUpdatedAt.ToString("O"),
            });
        }
    }

    private sealed class AgentSessionSendTool : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "session_id": {
                  "type": "string",
                  "description": "Target session ID."
                },
                "text": {
                  "type": "string",
                  "description": "Text to enqueue as a user message."
                },
                "immediacy": {
                  "type": "string",
                  "description": "Queue type: \"immediate\" or \"queue\" (default: \"queue\").",
                  "enum": ["immediate", "queue"]
                }
              },
              "required": ["session_id", "text"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly AgentSessionToolset _toolset;

        public AgentSessionSendTool(AgentSessionToolset toolset) { _toolset = toolset; }

        public override string Name => "agent_session_send";
        public override string Description => "Inject a text message into a subagent session's input queue.";
        public override JsonElement JsonSchema => Schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var sessionId = GetString(arguments, "session_id");
            var text = GetString(arguments, "text");
            var immediacy = GetString(arguments, "immediacy");

            if (string.IsNullOrEmpty(text))
                return Serialize(new { error = "text is required." });

            var chat = await _toolset.TryResolveAgentChatAsync(sessionId, cancellationToken);
            if (chat is null)
                return Serialize(new { error = $"Unknown session_id: '{sessionId}'." });

            var queue = string.Equals(immediacy, "immediate", StringComparison.OrdinalIgnoreCase)
                ? chat.ImmediateInputQueue
                : chat.DefaultInputQueue;

            chat.EnqueueUserMessage(text, queue);

            return Serialize(new { ok = true });
        }
    }

    private sealed class AgentSessionStopTool : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "session_id": {
                  "type": "string",
                  "description": "Session to stop."
                },
                "dispose": {
                  "type": "boolean",
                  "description": "If true, dispose and remove the session lease. Default: false (interrupt only)."
                }
              },
              "required": ["session_id"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly AgentSessionToolset _toolset;

        public AgentSessionStopTool(AgentSessionToolset toolset) { _toolset = toolset; }

        public override string Name => "agent_session_stop";
        public override string Description => "Interrupt a running session, optionally disposing it.";
        public override JsonElement JsonSchema => Schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var sessionId = GetString(arguments, "session_id");
            var dispose = GetBool(arguments, "dispose");

            var chat = await _toolset.TryResolveAgentChatAsync(sessionId, cancellationToken);
            if (chat is null)
                return Serialize(new { error = $"Unknown session_id: '{sessionId}'." });

            chat.Interrupt();

            if (dispose && sessionId is not null && sessionId != ".")
            {
                var id = new AgentSessionId(sessionId);
                RunningAgentChatLease? lease = null;
                lock (_toolset._leasesLock)
                {
                    if (_toolset._leases.TryGetValue(id, out lease))
                        _toolset._leases.Remove(id);
                }

                if (lease is not null)
                    await lease.DisposeAsync();
            }

            return Serialize(new { ok = true });
        }
    }

    private sealed class AgentSessionReadEventsTool : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "session_id": {
                  "type": "string",
                  "description": "Session to read from. Use \".\" for the current session."
                },
                "after_timestamp": {
                  "type": "string",
                  "description": "ISO 8601 cursor; only events after this timestamp."
                },
                "event_types": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Filter: user, assistant, tool_call, tool_result, diagnostic."
                },
                "search": {
                  "type": "string",
                  "description": "Substring match against event content."
                },
                "limit": {
                  "type": "integer",
                  "description": "Max events to return (default 20, max 200)."
                }
              },
              "required": ["session_id"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly AgentSessionToolset _toolset;

        public AgentSessionReadEventsTool(AgentSessionToolset toolset) { _toolset = toolset; }

        public override string Name => "agent_session_read_events";
        public override string Description => "Read event history from a session with optional type and content filters.";
        public override JsonElement JsonSchema => Schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var sessionId = GetString(arguments, "session_id");
            var afterTimestamp = GetString(arguments, "after_timestamp");
            var search = GetString(arguments, "search");
            var limit = GetInt(arguments, "limit", 20, 1, 200);

            HashSet<string>? eventTypeFilter = null;
            if (arguments.TryGetValue("event_types", out var rawTypes) && rawTypes is not null)
            {
                var typeList = rawTypes switch
                {
                    JsonElement e when e.ValueKind == JsonValueKind.Array =>
                        e.EnumerateArray()
                            .Where(static x => x.ValueKind == JsonValueKind.String)
                            .Select(static x => x.GetString()!)
                            .ToArray(),
                    _ => [],
                };
                if (typeList.Length > 0)
                    eventTypeFilter = new HashSet<string>(typeList, StringComparer.OrdinalIgnoreCase);
            }

            var chat = await _toolset.TryResolveAgentChatAsync(sessionId, cancellationToken);
            if (chat is null)
                return Serialize(new { error = $"Unknown session_id: '{sessionId}'." });

            DateTimeOffset? afterCursor = null;
            if (!string.IsNullOrEmpty(afterTimestamp)
                && DateTimeOffset.TryParse(afterTimestamp, out var parsed))
            {
                afterCursor = parsed;
            }

            var allItems = chat.History.AsEnumerable();

            if (afterCursor.HasValue)
                allItems = allItems.Where(item => item.Timestamp > afterCursor);

            if (eventTypeFilter is { Count: > 0 })
                allItems = allItems.Where(item => eventTypeFilter.Contains(EventType(item)));

            if (!string.IsNullOrEmpty(search))
                allItems = allItems.Where(item => ContentPreview(item).Contains(search, StringComparison.OrdinalIgnoreCase));

            var matched = allItems.ToArray();
            var page = matched.Take(limit).ToArray();

            var events = page.Select(static item => new
            {
                timestamp = item.Timestamp?.ToString("O"),
                event_type = EventType(item),
                role = item.Role.Value,
                content_preview = ContentPreview(item),
                has_more_content = ContentPreview(item).Length >= 500,
            }).ToArray();

            var nextCursor = page.Length > 0 && page.Length == limit
                ? page[^1].Timestamp?.ToString("O")
                : null;

            return Serialize(new
            {
                events,
                total_matching = matched.Length,
                next_cursor = nextCursor,
            });
        }
    }

    private sealed class AgentSessionWaitTool : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "session_id": {
                  "type": "string",
                  "description": "Session to wait on."
                },
                "timeout_seconds": {
                  "type": "integer",
                  "description": "Max wait time in seconds (default 30, max 300)."
                },
                "wait_for_idle": {
                  "type": "boolean",
                  "description": "If true, returns only when the session becomes idle."
                }
              },
              "required": ["session_id"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly AgentSessionToolset _toolset;

        public AgentSessionWaitTool(AgentSessionToolset toolset) { _toolset = toolset; }

        public override string Name => "agent_session_wait";
        public override string Description => "Wait until a session produces output or a timeout elapses.";
        public override JsonElement JsonSchema => Schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var sessionId = GetString(arguments, "session_id");
            var timeoutSeconds = GetInt(arguments, "timeout_seconds", 30, 0, 300);
            var waitForIdle = GetBool(arguments, "wait_for_idle");

            var chat = await _toolset.TryResolveAgentChatAsync(sessionId, cancellationToken);
            if (chat is null)
                return Serialize(new { error = $"Unknown session_id: '{sessionId}'." });

            // Return immediately when not waiting, session is already idle, or timeout is zero.
            if (!waitForIdle || chat.RunningItems.Count == 0 || timeoutSeconds == 0)
            {
                return Serialize(new
                {
                    session_id = chat.AgentSessionId,
                    status = chat.RunningItems.Count > 0 && timeoutSeconds == 0 ? "timeout" : GetStatus(chat),
                    new_events = Array.Empty<object>(),
                });
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _toolset._disposeCts.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var beforeHistoryCount = chat.History.Count;

            try
            {
                while (chat.RunningItems.Count > 0 && !linked.Token.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromMilliseconds(50), _toolset._timeProvider, linked.Token);
            }
            catch (OperationCanceledException)
            {
                // timeout or dispose
            }

            if (chat.RunningItems.Count > 0)
            {
                return Serialize(new
                {
                    session_id = chat.AgentSessionId,
                    status = "timeout",
                    new_events = Array.Empty<object>(),
                });
            }

            var newItems = chat.History.Skip(beforeHistoryCount).Take(20).Select(static item => new
            {
                timestamp = item.Timestamp?.ToString("O"),
                event_type = EventType(item),
                content_preview = ContentPreview(item),
            }).ToArray();

            return Serialize(new
            {
                session_id = chat.AgentSessionId,
                status = GetStatus(chat),
                new_events = newItems,
            });
        }
    }

    private sealed class AgentSessionOnCompleteTool : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "session_id": {
                  "type": "string",
                  "description": "Session to monitor."
                },
                "message": {
                  "type": "string",
                  "description": "Message to enqueue on the parent's immediate queue when the session becomes idle or reaches a terminal state."
                }
              },
              "required": ["session_id", "message"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly AgentSessionToolset _toolset;

        public AgentSessionOnCompleteTool(AgentSessionToolset toolset) { _toolset = toolset; }

        public override string Name => "agent_session_on_complete";
        public override string Description => "Register a callback: when the session becomes idle or reaches a terminal state, enqueue a message on the parent agent's immediate queue.";
        public override JsonElement JsonSchema => Schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var sessionId = GetString(arguments, "session_id");
            var message = GetString(arguments, "message");

            if (string.IsNullOrEmpty(message))
                return Serialize(new { error = "message is required." });

            var chat = await _toolset.TryResolveAgentChatAsync(sessionId, cancellationToken);
            if (chat is null)
                return Serialize(new { error = $"Unknown session_id: '{sessionId}'." });

            var parentChat = _toolset.ParentChat;

            // If already idle/terminal, enqueue immediately.
            if (chat.RunningItems.Count == 0 || chat.CompletionState != AgentChatCompletionState.Running)
            {
                parentChat.EnqueueUserMessage(message, parentChat.ImmediateInputQueue);
                return Serialize(new { registered = true, fired_immediately = true });
            }

            // Register a background watcher that fires when the session finishes its current turn.
            var disposeCts = _toolset._disposeCts;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (chat.RunningItems.Count > 0 && chat.CompletionState == AgentChatCompletionState.Running)
                    {
                        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                        EventHandler<AgentChatHistoryItem>? handler = null;
                        handler = (_, _) => tcs.TrySetResult();
                        chat.TurnCompleted += handler;

                        // Double-check after subscribing to avoid missing a concurrent completion.
                        if (chat.RunningItems.Count == 0)
                        {
                            chat.TurnCompleted -= handler;
                            break;
                        }

                        try
                        {
                            await tcs.Task.WaitAsync(disposeCts.Token);
                        }
                        finally
                        {
                            chat.TurnCompleted -= handler;
                        }

                        // Brief yield to allow the processing loop to drain RunningItems.
                        await Task.Delay(TimeSpan.FromMilliseconds(10), _toolset._timeProvider, disposeCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!disposeCts.IsCancellationRequested)
                    parentChat.EnqueueUserMessage(message, parentChat.ImmediateInputQueue);
            });

            return Serialize(new { registered = true, fired_immediately = false });
        }
    }

    private sealed class AgentSessionAcquireTool : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "session_id": {
                  "type": "string",
                  "description": "Session ID of the existing subagent session to acquire."
                }
              },
              "required": ["session_id"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly AgentSessionToolset _toolset;

        public AgentSessionAcquireTool(AgentSessionToolset toolset) { _toolset = toolset; }

        public override string Name => "agent_session_acquire";
        public override string Description => "Acquire a lease on an existing subagent session by session ID, enabling resume or query after a restart.";
        public override JsonElement JsonSchema => Schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var sessionId = GetString(arguments, "session_id");
            if (string.IsNullOrEmpty(sessionId) || sessionId == ".")
                return Serialize(new { error = "session_id must be a specific session UUID." });

            var id = new AgentSessionId(sessionId);

            // If already acquired, return current status without acquiring a second lease.
            lock (_toolset._leasesLock)
            {
                if (_toolset._leases.TryGetValue(id, out var existing))
                {
                    return Serialize(new
                    {
                        session_id = sessionId,
                        status = GetStatus(existing.AgentChat),
                        already_acquired = true,
                    });
                }
            }

            // Look up in parent's sub-agents.
            var subAgent = _toolset.TryFindSubAgent(sessionId);
            if (subAgent is null)
                return Serialize(new { error = $"Unknown session_id: '{sessionId}'." });

            RunningAgentChatLease lease;
            try
            {
                lease = await subAgent.AcquireLeaseAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                return Serialize(new { error = $"Failed to acquire lease: {ex.Message}" });
            }

            lock (_toolset._leasesLock)
            {
                if (_toolset._leases.ContainsKey(id))
                {
                    // Another concurrent acquire beat us — dispose the redundant lease.
                    _ = lease.DisposeAsync();
                    return Serialize(new
                    {
                        session_id = sessionId,
                        status = GetStatus(_toolset._leases[id].AgentChat),
                        already_acquired = true,
                    });
                }

                _toolset._leases[id] = lease;
            }

            return Serialize(new
            {
                session_id = sessionId,
                status = GetStatus(lease.AgentChat),
                already_acquired = false,
            });
        }
    }
}
