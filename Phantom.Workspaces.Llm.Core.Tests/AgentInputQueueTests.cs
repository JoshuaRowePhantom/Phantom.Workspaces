using System.Collections.Immutable;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentInputQueueTests
{
    [Fact]
    public void Enqueue_AppendsItems()
    {
        var queue = new AgentInputQueue();
        LlmEvent[] items =
        [
            new LlmEvent
            {
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.User,
                Content = "first",
            },
            new LlmEvent
            {
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.System,
                Content = "second",
            },
        ];

        var updatedItems = queue.Enqueue(items);

        Assert.Equal(2, updatedItems.Count);
        Assert.Equal("first", updatedItems[0].Content);
        Assert.Equal("second", updatedItems[1].Content);
        Assert.Equal(updatedItems, queue.Items);
    }

    [Fact]
    public void Update_WhenExpectedItemsMatch_ReplacesItems()
    {
        var queue = new AgentInputQueue();
        var originalItems = queue.Enqueue(
        [
            new LlmEvent
            {
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.User,
                Content = "original",
            },
        ]);
        var newItems = ImmutableList.Create(
            new LlmEvent
            {
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.User,
                Content = "replacement",
            });

        var updated = queue.Update(originalItems, newItems);

        Assert.True(updated);
        Assert.Equal("replacement", queue.Items[0].Content);
    }

    [Fact]
    public void Update_WhenExpectedItemsDoNotMatch_ReturnsFalse()
    {
        var queue = new AgentInputQueue();
        var originalItems = queue.Enqueue(
        [
            new LlmEvent
            {
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.User,
                Content = "original",
            },
        ]);
        var staleSnapshot = ImmutableList<LlmEvent>.Empty;
        var replacementItems = ImmutableList.Create(
            new LlmEvent
            {
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.User,
                Content = "replacement",
            });

        var updated = queue.Update(staleSnapshot, replacementItems);

        Assert.False(updated);
        Assert.Equal(originalItems, queue.Items);
    }
}
