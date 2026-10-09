using System.Collections.Specialized;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed partial class RemoteAgentChatTests
{
    [Fact]
    public async Task SdkNotification_CoalescedRemoteStreamingPromotionAndSnapshot_PreserveEncounterOrderAndSubagentContext()
    {
        var source = Channel.CreateUnbounded<SessionEvent>();
        source.Writer.TryWrite(new ToolExecutionStartEvent
        {
            AgentId = string.Empty,
            Data = new ToolExecutionStartData { ToolCallId = "parent", ToolName = "task" },
        });
        source.Writer.TryWrite(new SystemNotificationEvent
        {
            AgentId = string.Empty,
            Data = new SystemNotificationData
            {
                Content = "<system_notification>child idle</system_notification>",
                Kind = new SystemNotificationAgentIdle
                {
                    AgentId = "child", AgentType = "background", Description = "idle",
                },
            },
        });
        source.Writer.TryWrite(new ToolExecutionCompleteEvent
        {
            AgentId = string.Empty,
            Data = new ToolExecutionCompleteData
            {
                ToolCallId = "parent", Success = true,
                Result = new ToolExecutionCompleteResult { Content = "done" },
            },
        });
        source.Writer.Complete();

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents(
            source.Reader, CancellationToken.None))
        {
            updates.Add(new AgentResponseUpdate { Role = update.Role, Contents = update.Contents });
        }
        var items = AgentResponseUpdateCoalescer.Coalesce(updates.ToArray(), TimeProvider.System)
            .Select(item => item with { AssistantRunId = "ordered-run" }).ToArray();
        Assert.Equal(new[] { ChatRole.Assistant, ChatRole.System, ChatRole.Tool },
            items.Select(item => item.Role));

        static JsonElement Json(object value)
            => JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions);
        var child = Json(new
        {
            AgentId = "child", Name = "background", DisplayName = "Background agent",
            Description = "Parent task child", CompletionState = AgentChatCompletionState.Running,
            LastUpdatedAt = DateTime.UnixEpoch, SubAgents = Array.Empty<object>(),
        });
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var subagentReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((INotifyCollectionChanged)chat.SubAgents).CollectionChanged += (_, _) =>
            {
                if (chat.SubAgents.Count == 1) subagentReady.TrySetResult();
            };
            await transport.SendAsync(Frame(2, new SubagentsChangedEvent { Subagents = [child] }));
            await subagentReady.Task;

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += (_, _) =>
            {
                if (chat.RunningItems.Count == 1) started.TrySetResult();
            };
            await transport.SendAsync(Frame(3, new StreamingStartedEvent
            {
                RunId = "stream", Item = Json(items[0]),
            }));
            await started.Task;
            var running = Assert.Single(chat.RunningItems);
            Assert.Equal("ordered-run", running.AssistantRunId);
            Assert.Equal("parent", Assert.IsType<FunctionCallContent>(Assert.Single(running.Items[0].Contents)).CallId);
            Assert.Equal("child", Assert.Single(chat.SubAgents).AgentId);

            var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            running.Items.CollectionChanged += (_, _) =>
            {
                if (running.Items.Count == items.Length) updated.TrySetResult();
            };
            await transport.SendAsync(Frame(4, new StreamingUpdatedEvent
            {
                RunId = "stream", Update = Json(items),
            }));
            await updated.Task;
            Assert.Equal(ChatRole.System, running.Items[1].Role);
            Assert.Equal("child idle", Assert.IsType<TextContent>(Assert.Single(running.Items[1].Contents)).Text);

            var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((INotifyCollectionChanged)chat.History).CollectionChanged += (_, _) =>
            {
                if (chat.History.Count == items.Length) promoted.TrySetResult();
            };
            await transport.SendAsync(Frame(5, new StreamingCompletedEvent
            {
                RunId = "stream", Item = Json(items[^1]), Items = items.Select(Json).ToArray(),
            }));
            await promoted.Task;
            Assert.Empty(chat.RunningItems);
            Assert.Equal(items.Select(item => item.Role), chat.History.Select(item => item.Role));
            Assert.Equal("child", Assert.Single(chat.SubAgents).AgentId);
        }

        var snapshot = AgentSessionProtocolCodecTests.Snapshot() with
        {
            History = items.Select(Json).ToArray(),
            Subagents = [child],
        };
        var reloadTransport = new TestTransport();
        var attaching = RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = new RemoteAgentSessionClient(reloadTransport),
            OpenRequest = AgentSessionProtocolCodecTests.Open(),
            ForegroundScheduler = TaskScheduler.Default,
        });
        await reloadTransport.SendAsync(Frame(1, new SessionSnapshotEvent { Snapshot = snapshot }));
        await using var reloaded = await attaching;
        Assert.Equal(items.Select(item => item.Role), reloaded.History.Select(item => item.Role));
        Assert.Equal("parent", Assert.IsType<FunctionCallContent>(Assert.Single(reloaded.History[0].Contents)).CallId);
        Assert.Equal("child idle", Assert.IsType<TextContent>(Assert.Single(reloaded.History[1].Contents)).Text);
        Assert.Equal("parent", Assert.IsType<FunctionResultContent>(Assert.Single(reloaded.History[2].Contents)).CallId);
        Assert.Equal("child", Assert.Single(reloaded.SubAgents).AgentId);
    }
}
