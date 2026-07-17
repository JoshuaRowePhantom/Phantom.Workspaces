using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class DispatchedSubAgentTests
{
    private static RunningAgentChatLease CreateLease()
    {
        return new RunningAgentChatLease(new AgentSessionId("session-1"), null!, () => ValueTask.CompletedTask);
    }

    [Fact]
    public void Construction_RetainsRequiredMembers()
    {
        var entityId = new EntityId(Guid.NewGuid());
        var lease = CreateLease();
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };

        var subAgent = new DispatchedSubAgent
        {
            Id = "foo-bar",
            Description = "A dispatched sub-agent",
            DescriptionEmbedding = embedding,
            EntityId = entityId,
            Lease = lease,
        };

        Assert.Equal("foo-bar", subAgent.Id);
        Assert.Equal("A dispatched sub-agent", subAgent.Description);
        Assert.Same(embedding, subAgent.DescriptionEmbedding);
        Assert.Equal(entityId, subAgent.EntityId);
        Assert.Same(lease, subAgent.Lease);
    }

    [Fact]
    public void LastUpdated_IsMutable()
    {
        var subAgent = new DispatchedSubAgent
        {
            Id = "foo-bar",
            Description = "A dispatched sub-agent",
            DescriptionEmbedding = [],
            EntityId = new EntityId(Guid.NewGuid()),
            Lease = CreateLease(),
        };

        var timestamp = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        subAgent.LastUpdated = timestamp;

        Assert.Equal(timestamp, subAgent.LastUpdated);
    }

    [Fact]
    public void DispatchHistoryIndex_IsMutable()
    {
        var subAgent = new DispatchedSubAgent
        {
            Id = "foo-bar",
            Description = "A dispatched sub-agent",
            DescriptionEmbedding = [],
            EntityId = new EntityId(Guid.NewGuid()),
            Lease = CreateLease(),
        };

        subAgent.DispatchHistoryIndex = 7;

        Assert.Equal(7, subAgent.DispatchHistoryIndex);
    }
}
