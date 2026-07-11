using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatOutputHtmlTransformerLookupTests
{
    private sealed class RecordingSink : IChatOutputHtmlSink
    {
        public List<string> Operations { get; } = [];

        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
            => this.Operations.Add($"update:{path}");

        public void RemoveContent(string path)
            => this.Operations.Add($"remove:{path}");

        public void ScrollToBottom() => this.Operations.Add("scroll");
        public void BeginBatch() { }
        public void EndBatch() { }
        public void Clear() => this.Operations.Clear();
    }

    private static AgentChatHistoryItem ToolCallMessage(string toolName, string callId)
        => new()
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent(callId, toolName)],
        };

    private static AgentChatHistoryItem ToolResultMessage(string callId, object result)
        => new()
        {
            Role = ChatRole.Tool,
            Contents = [new FunctionResultContent(callId, result)],
        };

    private static AgentChatHistoryItem MultiToolCallMessage(params (string toolName, string callId)[] calls)
        => new()
        {
            Role = ChatRole.Assistant,
            Contents = calls.Select(c => new FunctionCallContent(c.callId, c.toolName) as AIContent).ToList(),
        };

    private static AgentChatHistoryItem TextMessage(ChatRole role, string text)
        => new() { Role = role, Contents = [new TextContent(text)] };

    private static ChatMessageHtmlTransformer MakeTransformer(
        ObservableCollection<AgentChatHistoryItem> source,
        List<RenderSlot> target,
        RecordingSink sink,
        Dictionary<string, RenderSlot>? sharedSlotByCallId = null)
        => new(
            source,
            target,
            sink,
            () => true,
            containerPath: "container",
            elementIdForSourceIndex: ChatOutputHtmlRenderer.MessageId,
            groupIdForSourceIndex: ChatOutputHtmlRenderer.ToolGroupId,
            sharedSlotByCallId: sharedSlotByCallId ?? new(System.StringComparer.Ordinal));

    [Fact]
    public void FindSlotWithCallId_SingleCall_ReturnsMatchingSlot()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("test_tool", "call-123"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        var slot = transformer.FindSlotWithCallId("call-123");

        Assert.NotNull(slot);
        Assert.True(slot.Model.HasCallWithId("call-123"));
    }

    [Fact]
    public void FindSlotWithCallId_MultipleCallsInMessage_AllIndexed()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            MultiToolCallMessage(
                ("tool_a", "call-1"),
                ("tool_b", "call-2"),
                ("tool_c", "call-3")),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        var slot1 = transformer.FindSlotWithCallId("call-1");
        var slot2 = transformer.FindSlotWithCallId("call-2");
        var slot3 = transformer.FindSlotWithCallId("call-3");

        Assert.NotNull(slot1);
        Assert.NotNull(slot2);
        Assert.NotNull(slot3);
        Assert.Same(slot1, slot2);
        Assert.Same(slot2, slot3);
    }

    [Fact]
    public void FindSlotWithCallId_CallIdNull_ReturnsNull()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("test_tool", "call-123"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        var slot = transformer.FindSlotWithCallId(null);

        Assert.Null(slot);
    }

    [Fact]
    public void FindSlotWithCallId_NotFound_ReturnsNull()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("test_tool", "call-123"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        var slot = transformer.FindSlotWithCallId("non-existent-id");

        Assert.Null(slot);
    }

    [Fact]
    public void FindSlotWithCallId_AfterRemove_NotFound()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("test_tool", "call-123"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        var slotBefore = transformer.FindSlotWithCallId("call-123");
        Assert.NotNull(slotBefore);

        source.RemoveAt(0);

        var slotAfter = transformer.FindSlotWithCallId("call-123");
        Assert.Null(slotAfter);
    }

    [Fact]
    public void FindSlotWithCallId_SharedMap_MatchesCallRegisteredByOtherTransformer()
    {
        // Two transformers (e.g. history and a running item) share one call-id map: a call
        // registered by the first transformer is visible through the second.
        var sharedMap = new Dictionary<string, RenderSlot>(System.StringComparer.Ordinal);
        var sink = new RecordingSink();

        var historySource = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("history_tool", "shared-call"),
        };
        using var historyTransformer = MakeTransformer(historySource, [], sink, sharedMap);

        var runningSource = new ObservableCollection<AgentChatHistoryItem>();
        using var runningTransformer = MakeTransformer(runningSource, [], sink, sharedMap);

        var slot = runningTransformer.FindSlotWithCallId("shared-call");

        Assert.NotNull(slot);
        Assert.True(slot.Model.HasCallWithId("shared-call"));
    }

    [Fact]
    public void FindSlotWithCallId_TransformerDispose_RemovesItsEntriesFromSharedMap()
    {
        var sharedMap = new Dictionary<string, RenderSlot>(System.StringComparer.Ordinal);
        var sink = new RecordingSink();

        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("test_tool", "disposed-call"),
        };
        var transformer = MakeTransformer(source, [], sink, sharedMap);
        Assert.True(sharedMap.ContainsKey("disposed-call"));

        transformer.Dispose();

        Assert.False(sharedMap.ContainsKey("disposed-call"));
    }

    [Fact]
    public void FindGroupablePredecessor_PreviousToolCall_ReturnsItsSlot()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "call-1"),
            ToolCallMessage("tool_b", "call-2"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        var predecessor = transformer.FindGroupablePredecessor(1);

        Assert.Same(target[0], predecessor);
        // The two calls were promoted into a group during insertion.
        Assert.NotNull(target[0].Group);
        Assert.Same(target[0].Group, target[1].Group);
    }

    [Fact]
    public void FindGroupablePredecessor_NoToolCallBefore_ReturnsNull()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "hello"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        var predecessor = transformer.FindGroupablePredecessor(0);

        Assert.Null(predecessor);
    }

    [Fact]
    public void FindGroupablePredecessor_SkipsToolResultOnlyMessages_FindsToolCall()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "call-1"),
            ToolResultMessage("call-1", "result"),
            ToolCallMessage("tool_b", "call-2"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        var predecessor = transformer.FindGroupablePredecessor(2);

        Assert.Same(target[0], predecessor);
    }

    [Fact]
    public void FindGroupablePredecessor_TextMessageBetween_ReturnsNull()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "call-1"),
            TextMessage(ChatRole.Assistant, "thinking"),
            ToolCallMessage("tool_b", "call-2"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        var predecessor = transformer.FindGroupablePredecessor(2);

        Assert.Null(predecessor);
        Assert.Null(target[2].Group);
    }

    [Fact]
    public void ChatMessageHtmlTransformer_IncrementalUpdates_DictionaryConsistent()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>();
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        for (var i = 0; i < 10; i++)
        {
            var callId = $"call-{i}";
            source.Add(ToolCallMessage($"tool_{i}", callId));
            source.Add(ToolResultMessage(callId, $"result-{i}"));

            var slot = transformer.FindSlotWithCallId(callId);
            Assert.NotNull(slot);
            Assert.True(slot.Model.HasCallWithId(callId));
        }

        for (var i = 0; i < 10; i++)
        {
            var callId = $"call-{i}";
            var slot = transformer.FindSlotWithCallId(callId);
            Assert.NotNull(slot);
        }
    }

    [Fact]
    public void ChatMessageHtmlTransformer_NonConsecutiveToolCalls_AllIndexed()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>();
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        source.Add(ToolCallMessage("tool_0", "call-0"));
        source.Add(ToolResultMessage("call-0", "result-0"));

        var slot0 = transformer.FindSlotWithCallId("call-0");
        Assert.NotNull(slot0);

        source.Add(TextMessage(ChatRole.User, "user message"));

        source.Add(ToolCallMessage("tool_1", "call-1"));
        source.Add(ToolResultMessage("call-1", "result-1"));

        var slot1 = transformer.FindSlotWithCallId("call-1");
        Assert.NotNull(slot1);

        var slot0Again = transformer.FindSlotWithCallId("call-0");
        Assert.NotNull(slot0Again);
    }

    [Fact]
    public void ChatMessageHtmlTransformer_ConsecutiveToolCalls_AllIndexed()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>();
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        using var transformer = MakeTransformer(source, target, sink);

        source.Add(ToolCallMessage("tool_0", "call-0"));
        var slot0 = transformer.FindSlotWithCallId("call-0");
        Assert.NotNull(slot0);

        source.Add(ToolCallMessage("tool_1", "call-1"));
        var slot1 = transformer.FindSlotWithCallId("call-1");
        Assert.NotNull(slot1);

        source.Add(ToolCallMessage("tool_2", "call-2"));
        var slot2 = transformer.FindSlotWithCallId("call-2");
        Assert.NotNull(slot2);

        var slot0Again = transformer.FindSlotWithCallId("call-0");
        var slot1Again = transformer.FindSlotWithCallId("call-1");
        Assert.NotNull(slot0Again);
        Assert.NotNull(slot1Again);
    }
}
