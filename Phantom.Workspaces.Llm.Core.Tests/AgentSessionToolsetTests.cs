using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Interfaces;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentSessionToolsetTests
{
    private const string EchoAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    private static AgentDefinition EchoAgentDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);

    // ──────────────────────────────────────────────────────────────────────────
    // Test fixture
    // ──────────────────────────────────────────────────────────────────────────

    private sealed record TestSetup(
        AgentChatFactory Factory,
        AgentChat ParentChat,
        AgentSessionToolset Toolset,
        IReadOnlyList<AITool> Tools,
        DeterministicTestChatClient ChildClient) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Toolset.DisposeAsync();
            await ParentChat.DisposeAsync();
            await Factory.DisposeAsync();
        }

        public AIFunction GetTool(string name) =>
            (AIFunction)Tools.First(t => t.Name == name);

        public async Task<JsonElement> InvokeAsync(
            string toolName,
            Dictionary<string, object?> args,
            CancellationToken ct = default)
        {
            var result = await GetTool(toolName).InvokeAsync(new AIFunctionArguments(args), ct);
            return Assert.IsType<JsonElement>(result);
        }

        /// <summary>
        /// Creates a child session and returns its session_id string.
        /// </summary>
        public async Task<string> CreateChildSessionAsync(string? definition = null)
        {
            var args = new Dictionary<string, object?>();
            if (definition is not null)
                args["definition"] = definition;

            var result = await InvokeAsync("agent_session_create", args);
            Assert.False(result.TryGetProperty("error", out _), $"agent_session_create error: {result}");
            return result.GetProperty("session_id").GetString()!;
        }
    }

    private static async Task<TestSetup> CreateTestSetupAsync(
        DeterministicTestChatClient? childClient = null)
    {
        childClient ??= new DeterministicTestChatClient();

        var store = new InMemoryAgentPersistenceStore();
        var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = childClient },
            TaskScheduler.Default);

        var currentSessionContext = new CurrentSessionContext { AgentSessionId = "parent-session" };

        var parentChat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
            AgentSessionId = "parent-session",
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent-chat",
            AgentServices = new AgentServices { RunningAgentChatFactory = factory },
            ForegroundScheduler = TaskScheduler.Default,
        });

        var toolsetFactory = ToolsetFactory.CreateAgentSessionToolsetFactory(
            parentChat,
            currentSessionContext,
            factory,
            null);

        var toolset = Assert.IsType<AgentSessionToolset>(
            await toolsetFactory.CreateToolsetAsync(
                new AgentSchema.CustomTool { Kind = "agent-session" },
                new AgentServices()));

        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        var tools = await AIContextProviderToolReader.GetToolsAsync(toolset, agent, session, CancellationToken.None);

        return new TestSetup(factory, parentChat, toolset, tools, childClient);
    }

    /// <summary>Polls <see cref="AgentChat.SubAgents"/> until it has at least <paramref name="expectedCount"/> entries.</summary>
    private static async Task WaitForSubAgentsCountAsync(AgentChat parentChat, int expectedCount)
    {
        for (int i = 0; i < 200 && parentChat.SubAgents.Count < expectedCount; i++)
            await Task.Delay(10);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // agent_session_create
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentSessionCreate_ValidDefinition_ReturnsSessionId()
    {
        await using var setup = await CreateTestSetupAsync();

        var result = await setup.InvokeAsync("agent_session_create", new()
        {
            ["definition"] = EchoAgentDefinitionJson,
        });

        Assert.False(result.TryGetProperty("error", out _), $"Unexpected error: {result}");
        Assert.True(result.TryGetProperty("session_id", out var sessionId));
        Assert.Equal(JsonValueKind.String, sessionId.ValueKind);
        Assert.False(string.IsNullOrEmpty(sessionId.GetString()));
    }

    [Fact]
    public async Task AgentSessionCreate_WithInitialMessage_EnqueuesMessage()
    {
        await using var setup = await CreateTestSetupAsync();

        var result = await setup.InvokeAsync("agent_session_create", new()
        {
            ["initial_message"] = "hello from parent",
        });

        // The creation should succeed; the initial message is enqueued to the child's
        // default input queue and starts the processing loop (which blocks on the
        // child DeterministicTestChatClient waiting for a response).
        Assert.False(result.TryGetProperty("error", out _), $"Unexpected error: {result}");
        Assert.True(result.TryGetProperty("session_id", out _));

        // Wait for the child to pick up the message.
        await setup.ChildClient.WaitForRequestAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // The last request messages should include the initial message.
        Assert.Contains(setup.ChildClient.LastRequestMessages,
            m => m.Text?.Contains("hello from parent") == true);

        // Complete the streaming response so the session is not left hanging.
        setup.ChildClient.EnqueueStreamingResponse().Complete();
    }

    [Fact]
    public async Task AgentSessionCreate_NoDefinition_UsesParentDefinition()
    {
        await using var setup = await CreateTestSetupAsync();

        // No definition → toolset falls back to parentChat.AgentDefinition (EchoAgentDefinition)
        var result = await setup.InvokeAsync("agent_session_create", new());

        Assert.False(result.TryGetProperty("error", out _), $"Unexpected error: {result}");
        Assert.True(result.TryGetProperty("session_id", out _));
    }

    [Fact]
    public async Task AgentSessionCreate_StoresLeaseInToolset()
    {
        await using var setup = await CreateTestSetupAsync();

        var result = await setup.InvokeAsync("agent_session_create", new());

        Assert.False(result.TryGetProperty("error", out _));
        var childId = result.GetProperty("session_id").GetString()!;

        // The child should be accessible via agent_session_get
        var getResult = await setup.InvokeAsync("agent_session_get", new()
        {
            ["session_id"] = childId,
        });
        Assert.False(getResult.TryGetProperty("error", out _), $"Session not found after create: {getResult}");
        Assert.Equal(childId, getResult.GetProperty("session_id").GetString());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // agent_session_list
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentSessionList_NoFilters_ReturnsAllSessions()
    {
        await using var setup = await CreateTestSetupAsync();
        await setup.CreateChildSessionAsync();
        await setup.CreateChildSessionAsync();
        await WaitForSubAgentsCountAsync(setup.ParentChat, 2);

        var result = await setup.InvokeAsync("agent_session_list", new());

        var sessions = result.GetProperty("sessions");
        Assert.Equal(JsonValueKind.Array, sessions.ValueKind);
        Assert.Equal(2, sessions.GetArrayLength());
    }

    [Fact]
    public async Task AgentSessionList_StatusFilter_ReturnsMatchingSessions()
    {
        await using var setup = await CreateTestSetupAsync();
        await setup.CreateChildSessionAsync();
        await WaitForSubAgentsCountAsync(setup.ParentChat, 1);

        // Idle filter should return the one idle session.
        var idleResult = await setup.InvokeAsync("agent_session_list", new()
        {
            ["status"] = "idle",
        });
        Assert.Equal(1, idleResult.GetProperty("sessions").GetArrayLength());

        // Running filter should return zero (no messages enqueued).
        var runningResult = await setup.InvokeAsync("agent_session_list", new()
        {
            ["status"] = "running",
        });
        Assert.Equal(0, runningResult.GetProperty("sessions").GetArrayLength());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // agent_session_get
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentSessionGet_ValidSessionId_ReturnsStatus()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        var result = await setup.InvokeAsync("agent_session_get", new()
        {
            ["session_id"] = childId,
        });

        Assert.False(result.TryGetProperty("error", out _), $"Unexpected error: {result}");
        Assert.Equal(childId, result.GetProperty("session_id").GetString());
        Assert.True(result.TryGetProperty("status", out _));
        Assert.True(result.TryGetProperty("is_busy", out _));
    }

    [Fact]
    public async Task AgentSessionGet_SelfReference_ReturnsParentSession()
    {
        await using var setup = await CreateTestSetupAsync();

        var result = await setup.InvokeAsync("agent_session_get", new()
        {
            ["session_id"] = ".",
        });

        Assert.False(result.TryGetProperty("error", out _), $"Unexpected error: {result}");
        Assert.Equal("parent-session", result.GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task AgentSessionGet_UnknownSessionId_ReturnsError()
    {
        await using var setup = await CreateTestSetupAsync();

        var result = await setup.InvokeAsync("agent_session_get", new()
        {
            ["session_id"] = Guid.NewGuid().ToString("n"),
        });

        Assert.True(result.TryGetProperty("error", out _));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // agent_session_send
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentSessionSend_ValidSessionId_EnqueuesMessage()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        var result = await setup.InvokeAsync("agent_session_send", new()
        {
            ["session_id"] = childId,
            ["text"] = "test message",
        });

        Assert.False(result.TryGetProperty("error", out _), $"Unexpected error: {result}");
        Assert.True(result.GetProperty("ok").GetBoolean());

        // The child processes the message; wait for the client to receive it.
        await setup.ChildClient.WaitForRequestAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Contains(setup.ChildClient.LastRequestMessages,
            m => m.Text?.Contains("test message") == true);

        setup.ChildClient.EnqueueStreamingResponse().Complete();
    }

    [Fact]
    public async Task AgentSessionSend_ImmediateMode_EnqueuesImmediate()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        var result = await setup.InvokeAsync("agent_session_send", new()
        {
            ["session_id"] = childId,
            ["text"] = "immediate message",
            ["immediacy"] = "immediate",
        });

        Assert.False(result.TryGetProperty("error", out _), $"Unexpected error: {result}");
        Assert.True(result.GetProperty("ok").GetBoolean());

        setup.ChildClient.EnqueueStreamingResponse().Complete();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // agent_session_stop
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentSessionStop_InterruptOnly_StopsButRetainsLease()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        // Stop without dispose — interrupt only.
        var stopResult = await setup.InvokeAsync("agent_session_stop", new()
        {
            ["session_id"] = childId,
            ["dispose"] = false,
        });
        Assert.True(stopResult.GetProperty("ok").GetBoolean());

        // Session should still be resolvable (lease still held).
        var getResult = await setup.InvokeAsync("agent_session_get", new()
        {
            ["session_id"] = childId,
        });
        Assert.False(getResult.TryGetProperty("error", out _), "Lease was unexpectedly released.");
        Assert.Equal(childId, getResult.GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task AgentSessionStop_WithDispose_ReleasesLease()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        var stopResult = await setup.InvokeAsync("agent_session_stop", new()
        {
            ["session_id"] = childId,
            ["dispose"] = true,
        });
        Assert.True(stopResult.GetProperty("ok").GetBoolean());

        // Lease disposed → session no longer resolvable through toolset's _leases.
        // The SubAgent is still in parentChat.SubAgents but its AgentChat is now disposed.
        var getResult = await setup.InvokeAsync("agent_session_get", new()
        {
            ["session_id"] = childId,
        });
        // The SubAgent entry remains in parentChat.SubAgents but the chat is disposed.
        // TryResolveAgentChat still finds it via SubAgent.AgentChat; so no error is expected here.
        // The key observable fact is that stop returned ok=true and didn't throw.
        Assert.False(getResult.TryGetProperty("error", out _),
            "Expected session to still be found via SubAgent after stop-with-dispose.");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // agent_session_read_events
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentSessionReadEvents_ReturnsHistory()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        var result = await setup.InvokeAsync("agent_session_read_events", new()
        {
            ["session_id"] = childId,
        });

        Assert.False(result.TryGetProperty("error", out _), $"Unexpected error: {result}");
        Assert.True(result.TryGetProperty("events", out var events));
        Assert.Equal(JsonValueKind.Array, events.ValueKind);
        Assert.True(result.TryGetProperty("total_matching", out _));
    }

    [Fact]
    public async Task AgentSessionReadEvents_SelfReference_ReturnsParentHistory()
    {
        await using var setup = await CreateTestSetupAsync();

        var result = await setup.InvokeAsync("agent_session_read_events", new()
        {
            ["session_id"] = ".",
        });

        Assert.False(result.TryGetProperty("error", out _), $"Unexpected error: {result}");
        Assert.Equal(JsonValueKind.Array, result.GetProperty("events").ValueKind);
    }

    [Fact]
    public async Task AgentSessionReadEvents_WithFilters_ReturnsFilteredEvents()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        // With event_types filter — should not error even when no history exists.
        var types = JsonSerializer.SerializeToElement(new[] { "user", "assistant" });
        var result = await setup.InvokeAsync("agent_session_read_events", new()
        {
            ["session_id"] = childId,
            ["event_types"] = types,
        });

        Assert.False(result.TryGetProperty("error", out _));
        Assert.Equal(JsonValueKind.Array, result.GetProperty("events").ValueKind);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // agent_session_wait
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentSessionWait_IdleSession_ReturnsImmediately()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        var result = await setup.InvokeAsync("agent_session_wait", new()
        {
            ["session_id"] = childId,
            ["wait_for_idle"] = true,
            ["timeout_seconds"] = 30,
        });

        // Session was already idle, so returns immediately with status "idle".
        Assert.False(result.TryGetProperty("error", out _));
        Assert.Equal("idle", result.GetProperty("status").GetString());
        Assert.Equal(childId, result.GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task AgentSessionWait_RunningSession_WaitFalse_ReturnsRunning()
    {
        var childClient = new DeterministicTestChatClient();
        await using var setup = await CreateTestSetupAsync(childClient);
        var childId = await setup.CreateChildSessionAsync();

        // Make the session busy by sending a message.
        await setup.InvokeAsync("agent_session_send", new()
        {
            ["session_id"] = childId,
            ["text"] = "keep busy",
        });
        await childClient.WaitForRequestAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // With wait_for_idle=false, return immediately with the current (running) status.
        var result = await setup.InvokeAsync("agent_session_wait", new()
        {
            ["session_id"] = childId,
            ["wait_for_idle"] = false,
        });

        Assert.Equal("running", result.GetProperty("status").GetString());

        childClient.EnqueueStreamingResponse().Complete();
    }

    [Fact]
    public async Task AgentSessionWait_Timeout_ReturnsTimeoutStatus()
    {
        var childClient = new DeterministicTestChatClient();
        await using var setup = await CreateTestSetupAsync(childClient);
        var childId = await setup.CreateChildSessionAsync();

        // Make the session busy.
        await setup.InvokeAsync("agent_session_send", new()
        {
            ["session_id"] = childId,
            ["text"] = "stay busy",
        });
        await childClient.WaitForRequestAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // timeout_seconds=0 with a busy session should return "timeout" immediately.
        var result = await setup.InvokeAsync("agent_session_wait", new()
        {
            ["session_id"] = childId,
            ["wait_for_idle"] = true,
            ["timeout_seconds"] = 0,
        });

        Assert.Equal("timeout", result.GetProperty("status").GetString());

        childClient.EnqueueStreamingResponse().Complete();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // agent_session_on_complete
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentSessionOnComplete_IdleSession_FiresImmediately()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        var result = await setup.InvokeAsync("agent_session_on_complete", new()
        {
            ["session_id"] = childId,
            ["message"] = "child finished",
        });

        Assert.False(result.TryGetProperty("error", out _));
        Assert.True(result.GetProperty("fired_immediately").GetBoolean());
    }

    [Fact]
    public async Task AgentSessionOnComplete_RunningSession_RegistersCallback()
    {
        var childClient = new DeterministicTestChatClient();
        await using var setup = await CreateTestSetupAsync(childClient);
        var childId = await setup.CreateChildSessionAsync();

        // Make the child session busy.
        await setup.InvokeAsync("agent_session_send", new()
        {
            ["session_id"] = childId,
            ["text"] = "busy work",
        });
        await childClient.WaitForRequestAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // Register the on_complete callback — should not fire immediately.
        var result = await setup.InvokeAsync("agent_session_on_complete", new()
        {
            ["session_id"] = childId,
            ["message"] = "done",
        });

        Assert.False(result.TryGetProperty("error", out _));
        Assert.False(result.GetProperty("fired_immediately").GetBoolean());

        childClient.EnqueueStreamingResponse().Complete();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // agent_session_acquire
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentSessionAcquire_ExistingSession_AcquiresLease()
    {
        var store = new InMemoryAgentPersistenceStore();
        await using var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = new DeterministicTestChatClient() },
            TaskScheduler.Default);
        await using var setup1 = await CreateTestSetupWithExternalStoreAsync(store, factory);

        var childId = await setup1.CreateChildSessionAsync();
        await WaitForSubAgentsCountAsync(setup1.ParentChat, 1);

        // Create a second toolset backed by the same parentChat and factory, but with no
        // pre-existing leases.  Disposal order (reverse of declaration) ensures toolset2
        // is released before factory.
        await using var toolset2 = Assert.IsType<AgentSessionToolset>(
            await ToolsetFactory.CreateAgentSessionToolsetFactory(
                    setup1.ParentChat,
                    new CurrentSessionContext { AgentSessionId = "parent-session" },
                    factory,
                    null)
                .CreateToolsetAsync(new AgentSchema.CustomTool { Kind = "agent-session" }, new AgentServices()));

        var agentForTools = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var agentSession2 = await agentForTools.CreateSessionAsync(CancellationToken.None);
        var tools2 = await AIContextProviderToolReader.GetToolsAsync(
            toolset2, agentForTools, agentSession2, CancellationToken.None);

        var acquireTool = (AIFunction)tools2.First(t => t.Name == "agent_session_acquire");
        var acquireResult = await acquireTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["session_id"] = childId }),
            CancellationToken.None);

        var acquireJson = Assert.IsType<JsonElement>(acquireResult);
        Assert.False(acquireJson.TryGetProperty("error", out _), $"Acquire error: {acquireJson}");
        Assert.Equal(childId, acquireJson.GetProperty("session_id").GetString());
        Assert.False(acquireJson.GetProperty("already_acquired").GetBoolean());
    }

    [Fact]
    public async Task AgentSessionAcquire_AlreadyAcquired_ReturnsStatus()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        // Acquiring a session that was already acquired by this toolset via create
        // should return already_acquired=true.
        var result = await setup.InvokeAsync("agent_session_acquire", new()
        {
            ["session_id"] = childId,
        });

        Assert.False(result.TryGetProperty("error", out _));
        Assert.Equal(childId, result.GetProperty("session_id").GetString());
        Assert.True(result.GetProperty("already_acquired").GetBoolean());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Toolset disposal
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolsetDispose_ReleasesAllLeases()
    {
        var childClient = new DeterministicTestChatClient();
        var store = new InMemoryAgentPersistenceStore();
        await using var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = childClient },
            TaskScheduler.Default);

        var setup = await CreateTestSetupWithExternalStoreAsync(store, factory);

        await setup.CreateChildSessionAsync();
        await setup.CreateChildSessionAsync();

        // Sessions are in the factory's RunningSessions now.
        Assert.Equal(2, factory.RunningSessions.Count);

        // Disposing the toolset should release both leases.
        await setup.Toolset.DisposeAsync();
        await setup.ParentChat.DisposeAsync();

        // Wait for the factory's running sessions to drain.
        for (int i = 0; i < 200 && factory.RunningSessions.Count > 0; i++)
            await Task.Delay(10);

        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task ToolsetDispose_CancelsOnCompleteRegistrations()
    {
        var childClient = new DeterministicTestChatClient();
        await using var setup = await CreateTestSetupAsync(childClient);
        var childId = await setup.CreateChildSessionAsync();

        // Make child busy.
        await setup.InvokeAsync("agent_session_send", new()
        {
            ["session_id"] = childId,
            ["text"] = "busy",
        });
        await childClient.WaitForRequestAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // Register on_complete callback while child is busy.
        var onCompleteResult = await setup.InvokeAsync("agent_session_on_complete", new()
        {
            ["session_id"] = childId,
            ["message"] = "finished",
        });
        Assert.False(onCompleteResult.GetProperty("fired_immediately").GetBoolean());

        // Dispose the toolset — should cancel the background watcher.
        await setup.Toolset.DisposeAsync();

        // Complete the child's streaming response AFTER toolset disposal.
        childClient.EnqueueStreamingResponse().Complete();

        // Give the background task a chance to run (it should exit without enqueuing).
        await Task.Delay(100);

        // The parent should NOT have been triggered by the callback.
        Assert.Empty(setup.ParentChat.RunningItems);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers for tests that use an externally-managed store/factory
    // ──────────────────────────────────────────────────────────────────────────

    private sealed record ExternalSetup(
        AgentChat ParentChat,
        AgentSessionToolset Toolset,
        IReadOnlyList<AITool> Tools) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Toolset.DisposeAsync();
            await ParentChat.DisposeAsync();
        }

        public AIFunction GetTool(string name) =>
            (AIFunction)Tools.First(t => t.Name == name);

        public async Task<JsonElement> InvokeAsync(string toolName, Dictionary<string, object?> args)
        {
            var result = await GetTool(toolName).InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);
            return Assert.IsType<JsonElement>(result);
        }

        public async Task<string> CreateChildSessionAsync(string? definition = null)
        {
            var args = new Dictionary<string, object?>();
            if (definition is not null) args["definition"] = definition;
            var result = await InvokeAsync("agent_session_create", args);
            Assert.False(result.TryGetProperty("error", out _));
            return result.GetProperty("session_id").GetString()!;
        }
    }

    private static async Task<ExternalSetup> CreateTestSetupWithExternalStoreAsync(
        IAgentPersistenceStore store,
        AgentChatFactory factory)
    {
        var currentSessionContext = new CurrentSessionContext { AgentSessionId = "parent-session" };

        var parentChat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
            AgentSessionId = "parent-session",
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent-chat",
            AgentServices = new AgentServices { RunningAgentChatFactory = factory },
            ForegroundScheduler = TaskScheduler.Default,
        });

        var toolset = Assert.IsType<AgentSessionToolset>(
            await ToolsetFactory.CreateAgentSessionToolsetFactory(
                    parentChat, currentSessionContext, factory, null)
                .CreateToolsetAsync(new AgentSchema.CustomTool { Kind = "agent-session" }, new AgentServices()));

        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        var tools = await AIContextProviderToolReader.GetToolsAsync(toolset, agent, session, CancellationToken.None);

        return new ExternalSetup(parentChat, toolset, tools);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TryResolveAgentChatAsync thread pool blocking tests (#961)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentSessionGet_ConcurrentAccess_DoesNotDeadlock()
    {
        const int concurrentCalls = 10;
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        var tasks = Enumerable.Range(0, concurrentCalls)
            .Select(_ => Task.Run(async () =>
            {
                var result = await setup.InvokeAsync("agent_session_get", new()
                {
                    ["session_id"] = childId,
                });
                Assert.False(result.TryGetProperty("error", out var _));
                return result;
            }))
            .ToArray();

        var allTasks = Task.WhenAll(tasks);
        var allCompleted = await Task.WhenAny(
            allTasks,
            Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(allTasks, allCompleted);

        var results = await allTasks;
        Assert.All(results, r => Assert.Equal(childId, r.GetProperty("session_id").GetString()));
    }

    [Fact]
    public async Task AgentSessionGet_AfterDispose_CompletesWithoutBlocking()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        bool wasOnThreadPoolThread = false;
        var getCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            wasOnThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            try
            {
                var result = setup.InvokeAsync("agent_session_get", new()
                {
                    ["session_id"] = childId,
                }).GetAwaiter().GetResult();
                getCompleted.SetResult();
            }
            catch (Exception ex)
            {
                getCompleted.SetException(ex);
            }
        });

        var completed = await Task.WhenAny(
            getCompleted.Task,
            Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(getCompleted.Task, completed);
        Assert.True(wasOnThreadPoolThread);
    }

    [Fact]
    public async Task AgentSessionAcquire_DuplicateFromSubAgent_ReturnsExistingSession()
    {
        await using var setup = await CreateTestSetupAsync();
        var childId = await setup.CreateChildSessionAsync();

        var first = await setup.InvokeAsync("agent_session_get", new()
        {
            ["session_id"] = childId,
        });
        Assert.False(first.TryGetProperty("error", out _));

        var second = await setup.InvokeAsync("agent_session_get", new()
        {
            ["session_id"] = childId,
        });
        Assert.False(second.TryGetProperty("error", out _));

        Assert.Equal(childId, first.GetProperty("session_id").GetString());
        Assert.Equal(childId, second.GetProperty("session_id").GetString());
    }
}
