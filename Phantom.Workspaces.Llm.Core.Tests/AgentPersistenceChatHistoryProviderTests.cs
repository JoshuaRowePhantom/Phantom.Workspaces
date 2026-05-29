using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Echo;

namespace Phantom.Workspaces.Llm.Core.Tests;

public class AgentPersistenceChatHistoryProviderTests
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

    private sealed class TestAgentSession : Microsoft.Agents.AI.AgentSession
    {
    }

    private static AgentPersistenceChatHistoryProvider CreateProvider()
    {
        return new AgentPersistenceChatHistoryProvider(
            agentDefinition: null,
            store: new InMemoryAgentPersistenceStore());
    }
}
