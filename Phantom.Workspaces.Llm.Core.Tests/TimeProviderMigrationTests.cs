using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class TimeProviderMigrationTests
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

    [Fact]
    public void SubAgentChatClient_Construction_StampsLastUpdatedFromTimeProvider()
    {
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var client = new SubAgentChatClient("agent-1", "Agent One", timeProvider: timeProvider);

        Assert.Equal(timeProvider.GetUtcNow().UtcDateTime, client.LastUpdatedAt);
    }

    [Fact]
    public void SubAgentChatClient_Complete_UpdatesLastUpdatedFromTimeProvider()
    {
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var client = new SubAgentChatClient("agent-1", "Agent One", timeProvider: timeProvider);

        timeProvider.Advance(TimeSpan.FromSeconds(42));
        client.Complete();

        Assert.Equal(timeProvider.GetUtcNow().UtcDateTime, client.LastUpdatedAt);
    }

    [Fact]
    public void SubAgentChatClient_Fail_UpdatesLastUpdatedFromTimeProvider()
    {
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var client = new SubAgentChatClient("agent-1", "Agent One", timeProvider: timeProvider);

        timeProvider.Advance(TimeSpan.FromSeconds(7));
        client.Fail(new InvalidOperationException("boom"));

        Assert.Equal(timeProvider.GetUtcNow().UtcDateTime, client.LastUpdatedAt);
    }

    [Fact]
    public async Task AgentChat_EnqueueSystemNote_StampsHistoryTimestampFromTimeProvider()
    {
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(new DateTimeOffset(2030, 6, 7, 8, 9, 10, TimeSpan.Zero));

        var store = new InMemoryAgentPersistenceStore();
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "chat",
            ForegroundScheduler = TaskScheduler.Default,
            TimeProvider = timeProvider,
        });

        var added = new TaskCompletionSource<AgentChatHistoryItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 }
                && e.NewItems[0] is AgentChatHistoryItem item)
            {
                added.TrySetResult(item);
            }
        }

        ((INotifyCollectionChanged)chat.History).CollectionChanged += OnChanged;
        try
        {
            chat.EnqueueSystemNote("hello");
            var item = await added.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal(timeProvider.GetUtcNow(), item.Timestamp);
        }
        finally
        {
            ((INotifyCollectionChanged)chat.History).CollectionChanged -= OnChanged;
        }
    }

    [Fact]
    public async Task AgentSessionToolset_CreatedAt_StampedFromTimeProvider()
    {
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero));

        var store = new InMemoryAgentPersistenceStore();
        await using var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = new DeterministicTestChatClient() },
            TaskScheduler.Default);

        var currentSessionContext = new CurrentSessionContext { AgentSessionId = "parent-session" };

        await using var parentChat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
            AgentSessionId = "parent-session",
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent-chat",
            AgentServices = new AgentServices { RunningAgentChatFactory = factory },
            ForegroundScheduler = TaskScheduler.Default,
            TimeProvider = timeProvider,
        });

        await using var toolset = new AgentSessionToolset(
            new AgentChatRef(parentChat),
            currentSessionContext,
            factory,
            timeProvider);

        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        var tools = await AIContextProviderToolReader.GetToolsAsync(
            toolset, agent, session, CancellationToken.None);

        var createTool = (AIFunction)tools.First(t => t.Name == "agent_session_create");
        var result = await createTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>()),
            CancellationToken.None);
        var element = Assert.IsType<JsonElement>(result);

        Assert.False(element.TryGetProperty("error", out _), $"agent_session_create error: {element}");
        var createdAt = DateTimeOffset.Parse(element.GetProperty("created_at").GetString()!);
        Assert.Equal(timeProvider.GetUtcNow(), createdAt);
    }
}
