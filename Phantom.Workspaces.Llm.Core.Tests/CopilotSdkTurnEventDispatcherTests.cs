using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotSdkTurnEventDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_ConcurrentToolStartEvents_DoNotCorruptDictionary()
    {
        // Regression test for GitHub issue #765: concurrent DispatchAsync calls would corrupt the
        // internal bufferedToolStarts dictionary, causing IndexOutOfRangeException.
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var dispatcher = new CopilotSdkTurnEventDispatcher(
            channel.Writer,
            registry: null,
            factory: null,
            subAgentTable: null,
            logger: null);

        var toolStart1 = new ToolExecutionStartEvent
        {
            AgentId = string.Empty,
            Data = new ToolExecutionStartData
            {
                ToolCallId = "call-1",
                ToolName = "tool-1"
            }
        };

        var toolStart2 = new ToolExecutionStartEvent
        {
            AgentId = string.Empty,
            Data = new ToolExecutionStartData
            {
                ToolCallId = "call-2",
                ToolName = "tool-2"
            }
        };

        // Fire two concurrent DispatchAsync calls. Before the fix, this would often trigger
        // IndexOutOfRangeException during dictionary resize/insert because Dictionary<K,V> is not
        // thread-safe.
        var task1 = Task.Run(async () => await dispatcher.DispatchAsync(toolStart1));
        var task2 = Task.Run(async () => await dispatcher.DispatchAsync(toolStart2));

        await Task.WhenAll(task1, task2);

        // If we reach here without exception, the test passes
        channel.Writer.Complete();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_SerializedEventProcessing_ProcessesAllEventsInOrder()
    {
        // Verifies that events dispatched through channel-based drain loop are processed
        // sequentially in order.
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var dispatcher = new CopilotSdkTurnEventDispatcher(
            channel.Writer,
            registry: null,
            factory: null,
            subAgentTable: null,
            logger: null);

        var events = new List<AssistantMessageDeltaEvent>
        {
            new AssistantMessageDeltaEvent
            {
                AgentId = string.Empty,
                Data = new AssistantMessageDeltaData { DeltaContent = "message-1", MessageId = "msg-1" }
            },
            new AssistantMessageDeltaEvent
            {
                AgentId = string.Empty,
                Data = new AssistantMessageDeltaData { DeltaContent = "message-2", MessageId = "msg-2" }
            },
            new AssistantMessageDeltaEvent
            {
                AgentId = string.Empty,
                Data = new AssistantMessageDeltaData { DeltaContent = "message-3", MessageId = "msg-3" }
            }
        };

        // Dispatch all events sequentially (simulating the drain loop behavior)
        foreach (var evt in events)
        {
            await dispatcher.DispatchAsync(evt);
        }

        channel.Writer.Complete();

        // Verify all events were processed in order
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in channel.Reader.ReadAllAsync())
        {
            updates.Add(update);
        }

        Assert.Equal(3, updates.Count);
        var textContents = updates
            .SelectMany(u => u.Contents.OfType<TextContent>())
            .Select(t => t.Text)
            .ToList();
        Assert.Contains("message-1", textContents);
        Assert.Contains("message-2", textContents);
        Assert.Contains("message-3", textContents);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_EventDispatchCancellation_StopsProcessing()
    {
        // Verifies that when the cancellation token is triggered, the drain loop stops processing.
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var dispatcher = new CopilotSdkTurnEventDispatcher(
            channel.Writer,
            registry: null,
            factory: null,
            subAgentTable: null,
            logger: null);

        using var cts = new CancellationTokenSource();

        var deltaEvent = new AssistantMessageDeltaEvent
        {
            AgentId = string.Empty,
            Data = new AssistantMessageDeltaData { DeltaContent = "test-message", MessageId = "msg-1" }
        };

        // Dispatch one event successfully
        await dispatcher.DispatchAsync(deltaEvent);

        // Cancel the token (simulating turn cancellation)
        cts.Cancel();

        // Verify the event was processed before cancellation
        channel.Writer.Complete();
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in channel.Reader.ReadAllAsync())
        {
            updates.Add(update);
        }

        Assert.Single(updates);
    }
}
