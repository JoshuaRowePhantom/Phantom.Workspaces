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

    [Fact]
    public void FindSlotWithCallId_SingleCall_ReturnsMatchingSlot()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("test_tool", "call-123"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

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
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

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
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

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
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

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
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

        var slotBefore = transformer.FindSlotWithCallId("call-123");
        Assert.NotNull(slotBefore);

        source.RemoveAt(0);

        var slotAfter = transformer.FindSlotWithCallId("call-123");
        Assert.Null(slotAfter);
    }

    [Fact]
    public void FindPrecedingToolCallSlotIndex_ToolCallExists_ReturnsIndex()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "call-1"),
            ToolCallMessage("tool_b", "call-2"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

        var index = transformer.FindPrecedingToolCallSlotIndex(1);

        Assert.Equal(0, index);
    }

    [Fact]
    public void FindPrecedingToolCallSlotIndex_NoToolCallSlot_ReturnsMinusOne()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "hello"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

        var index = transformer.FindPrecedingToolCallSlotIndex(0);

        Assert.Equal(-1, index);
    }

    [Fact]
    public void FindPrecedingToolCallSlotIndex_AfterGroupedSlot_ReturnsGroupIndex()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "call-1"),
            ToolCallMessage("tool_b", "call-2"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

        var index = transformer.FindPrecedingToolCallSlotIndex(1);

        Assert.Equal(0, index);
        Assert.NotNull(target[0].Group);
    }

    [Fact]
    public void FindPrecedingToolCallSlotIndex_SkipsToolResultOnlyMessages_FindsToolCall()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "call-1"),
            ToolResultMessage("call-1", "result"),
            ToolCallMessage("tool_b", "call-2"),
        };
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

        var index = transformer.FindPrecedingToolCallSlotIndex(2);

        Assert.Equal(0, index);
    }

    [Fact]
    public void ChatMessageHtmlTransformer_IncrementalUpdates_DictionaryConsistent()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>();
        var target = new List<RenderSlot>();
        var sink = new RecordingSink();
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

        for (var i = 0; i < 10; i++)
        {
            var callId = $"call-{i}";
            source.Add(ToolCallMessage($"tool_{i}", callId));
            source.Add(ToolResultMessage(callId, $"result-{i}"));

            var slot = transformer.FindSlotWithCallId(callId);
            if (slot == null)
            {
                throw new System.Exception($"Failed to find slot for {callId} at iteration {i}, target count = {target.Count}");
            }
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
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

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
        var nextIdCounter = 0;
        var transformer = new ChatMessageHtmlTransformer(
            source,
            target,
            sink,
            () => true,
            () => nextIdCounter++,
            "container");

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
