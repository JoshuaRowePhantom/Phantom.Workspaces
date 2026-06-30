using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Interfaces;
using System.Reflection;

namespace Phantom.Workspaces.Llm.Core.Tests;

public class IncrementalPersistenceChatHistoryProviderTests
{
    [Fact]
    public void SetAgentSessionId_WhenSessionProvided_UpdatesExtractedValue()
    {
        var provider = CreateProvider();
        var session = new TestAgentSession();

        provider.SetAgentSessionId(session, "session-123");

        var extractedAgentSessionId = provider.ExtractAgentSessionId(session);
        Assert.Equal("session-123", extractedAgentSessionId);
    }

    [Fact]
    public void ExtractAgentSessionId_WhenCalledTwiceForSameSession_ReturnsSameValue()
    {
        var provider = CreateProvider();
        var session = new TestAgentSession();

        var firstExtractedAgentSessionId = provider.ExtractAgentSessionId(session);
        var secondExtractedAgentSessionId = provider.ExtractAgentSessionId(session);

        Assert.Equal(firstExtractedAgentSessionId, secondExtractedAgentSessionId);
    }

    [Fact]
    public void ExtractAgentSessionId_WhenSessionStateIsNotInitialized_GeneratesSessionId()
    {
        var provider = CreateProvider();
        var session = new TestAgentSession();

        var extractedAgentSessionId = provider.ExtractAgentSessionId(session);

        Assert.True(Guid.TryParseExact(extractedAgentSessionId, "N", out _));
    }

    [Fact]
    public void ExtractAgentSessionId_WhenSessionIsNull_GeneratesSessionId()
    {
        var provider = CreateProvider();

        var extractedAgentSessionId = provider.ExtractAgentSessionId(null);

        Assert.True(Guid.TryParseExact(extractedAgentSessionId, "N", out _));
    }

    [Fact]
    public void SetAgentSessionId_WhenAgentSessionIdIsWhitespace_Throws()
    {
        var provider = CreateProvider();
        var session = new TestAgentSession();

        Assert.Throws<ArgumentException>(() =>
            provider.SetAgentSessionId(session, " "));
    }

    [Fact]
    public void SetAgentSessionId_WhenSessionStateIsAlreadyInitialized_UpdatesExtractedValue()
    {
        var provider = CreateProvider();
        var session = new TestAgentSession();
        _ = provider.ExtractAgentSessionId(session);

        provider.SetAgentSessionId(session, "session-updated");
        var extractedAgentSessionId = provider.ExtractAgentSessionId(session);

        Assert.Equal("session-updated", extractedAgentSessionId);
    }

    [Fact]
    public async Task StoreChatHistoryAsync_IsNoOp_DoesNotWriteToStore()
    {
        var spyStore = new SpyAgentPersistenceStore();
        var provider = new IncrementalPersistenceChatHistoryProvider(agentDefinition: null, store: spyStore);

        // Call StoreChatHistoryAsync via reflection (it is protected).
        var method = typeof(ChatHistoryProvider).GetMethod(
            "StoreChatHistoryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(ChatHistoryProvider.InvokedContext), typeof(CancellationToken)])
            ?? throw new InvalidOperationException("StoreChatHistoryAsync method not found.");

        var result = method.Invoke(provider, [null!, CancellationToken.None]);
        if (result is ValueTask task)
        {
            await task;
        }

        Assert.Equal(0, spyStore.StoreCallCount);
    }

    private sealed class TestAgentSession : Microsoft.Agents.AI.AgentSession
    {
    }

    private static IncrementalPersistenceChatHistoryProvider CreateProvider()
    {
        return new IncrementalPersistenceChatHistoryProvider(
            agentDefinition: null,
            store: new InMemoryAgentPersistenceStore());
    }

    private sealed class SpyAgentPersistenceStore : IAgentPersistenceStore
    {
        public int StoreCallCount { get; private set; }

        public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        {
            StoreCallCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<PersistedAgent?> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PersistedAgent?>(null);

        public ValueTask<ChatMessage[]> ReadMessagesAsync(ReadMessagesRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Array.Empty<ChatMessage>());
    }
}
