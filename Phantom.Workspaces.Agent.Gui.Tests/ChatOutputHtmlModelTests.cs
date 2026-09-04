using Avalonia.Headless.XUnit;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Xunit;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatOutputHtmlModelTests
{
    private sealed record Operation(string Kind, string Path, ChatOutputUpdateLocation Location, string Content);

    private sealed class RecordingSink : IChatOutputHtmlSink
    {
        private bool batchActive;

        public List<Operation> Operations { get; } = [];

        public List<Operation> ContentOperations
            => this.Operations.Where(operation => operation.Kind is "update" or "remove").ToList();

        public int ScrollCount => this.Operations.Count(operation => operation.Kind == "scroll");

        public int BatchCount { get; private set; }

        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
            => this.Operations.Add(new Operation("update", path, location, content));

        public void RemoveContent(string path)
            => this.Operations.Add(new Operation("remove", path, ChatOutputUpdateLocation.Replace, string.Empty));

        public void ScrollToBottom()
            => this.Operations.Add(new Operation("scroll", string.Empty, ChatOutputUpdateLocation.Replace, string.Empty));

        public void BeginBatch() => this.batchActive = true;

        public void EndBatch()
        {
            if (this.batchActive)
            {
                this.batchActive = false;
                this.BatchCount++;
            }
        }

        public void Clear() => this.Operations.Clear();
    }

    private static AgentChatHistoryItem TextMessage(ChatRole role, string text)
        => new() { Role = role, Contents = [new TextContent(text)] };

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InitialHistory_EmitsSinglePrependBlob_IntoHistoryContainer()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "hello"),
            TextMessage(ChatRole.Assistant, "hi there"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.Prepend, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.HistoryContainerId, op.Path);
        Assert.Contains("chat-message", op.Content);
        Assert.Contains(">hello<", op.Content);
        Assert.Contains("chat-user-message", op.Content);
        Assert.Contains("chat-assistant-message", op.Content);
        Assert.True(op.Content.IndexOf(">hello<", StringComparison.Ordinal) < op.Content.IndexOf(">hi there<", StringComparison.Ordinal));
        Assert.True(sink.ScrollCount >= 1);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AddingMessage_InsertsAfterPreviousMessageElement()
    {
        var history = new ObservableCollection<AgentChatHistoryItem> { TextMessage(ChatRole.User, "first") };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(TextMessage(ChatRole.Assistant, "second"));

        var operation = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.After, operation.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), operation.Path);
        Assert.Contains(">second<", operation.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task StreamingUpdate_WhenLeadingContentUnchanged_OnlyEmitsForChangedContent()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("stable")] },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        // Replace the message with one that keeps the leading content and appends a new block.
        history[0] = new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("stable"), new TextContent("appended")],
        };

        var operation = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.Append, operation.Location);
        Assert.Equal(ChatOutputHtmlRenderer.ContentsContainerId(ChatOutputHtmlRenderer.MessageId(0)), operation.Path);
        Assert.Contains(">appended<", operation.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task StreamingUpdate_WhenLastContentChanges_ReplacesThatContentById()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("partial")] },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history[0] = new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("partial complete")],
        };

        var operation = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.Replace, operation.Location);
        Assert.Equal(ChatOutputHtmlRenderer.ContentId(ChatOutputHtmlRenderer.MessageId(0), 0), operation.Path);
        Assert.Contains("partial complete", operation.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RemovingMessage_EmitsRemoveByElementId()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "keep"),
            TextMessage(ChatRole.Assistant, "drop"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.RemoveAt(1);

        var operation = Assert.Single(sink.ContentOperations);
        Assert.Equal("remove", operation.Kind);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(1), operation.Path);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ReasoningHidden_DoesNotRenderReasoningContent_UntilToggledOn()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents = [new TextReasoningContent("thinking"), new TextContent("answer")],
            },
        };
        var sink = new RecordingSink();
        var reasoningVisible = false;
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => reasoningVisible, sink);
        await model.HistoryLoaded;

        var initial = Assert.Single(sink.ContentOperations);
        Assert.DoesNotContain("thinking", initial.Content);
        Assert.Contains("answer", initial.Content);

        sink.Clear();
        reasoningVisible = true;
        model.Refresh();

        Assert.Contains(sink.ContentOperations, operation => operation.Content.Contains("thinking"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_RendersContainerThenAppendsMessagesIntoIt()
    {
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "working"));
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;

        var operations = sink.ContentOperations;
        Assert.Equal(2, operations.Count);

        // First the empty running container appended into the persistent running region.
        Assert.Equal(ChatOutputHtmlRenderer.RunningContainerId, operations[0].Path);
        Assert.Equal(ChatOutputUpdateLocation.Append, operations[0].Location);
        Assert.Contains(ChatOutputHtmlRenderer.RunningItemId(0), operations[0].Content);

        // Then the message appended into the running item's own contents container.
        Assert.Equal(
            ChatOutputHtmlRenderer.RunningItemContentsId(ChatOutputHtmlRenderer.RunningItemId(0)),
            operations[1].Path);
        Assert.Equal(ChatOutputUpdateLocation.Append, operations[1].Location);
        Assert.Contains(">working<", operations[1].Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItems_WithNullEntry_DoesNotThrowAndRendersRealItems()
    {
        var realItem = new AgentChatRunningItem();
        realItem.Items.Add(TextMessage(ChatRole.Assistant, "real running"));

        // A null entry in the running-items source collection previously crashed the renderer with
        // a NullReferenceException in RunningChatItemHtmlModel.Activate().
        var running = new ObservableCollection<AgentChatRunningItem> { null!, realItem };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);

        await model.HistoryLoaded;

        Assert.Contains(sink.ContentOperations, operation => operation.Content.Contains(">real running<"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_ReplacingNullEntryWithRealItem_ActivatesAndRendersMessages()
    {
        // The running item starts as a null placeholder, so its model's Activate() no-ops and no
        // transformer is built. When the entry is later replaced with a real item, Update() must
        // recover by activating, otherwise streamed messages are silently dropped forever.
        var running = new ObservableCollection<AgentChatRunningItem> { null! };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);

        await model.HistoryLoaded;
        sink.Clear();

        var realItem = new AgentChatRunningItem();
        realItem.Items.Add(TextMessage(ChatRole.Assistant, "recovered"));
        running[0] = realItem;

        Assert.Contains(sink.ContentOperations, operation => operation.Content.Contains(">recovered<"));

        sink.Clear();

        // Subsequent streaming into the now-activated item must also render.
        realItem.Items.Add(TextMessage(ChatRole.Assistant, "streamed"));

        Assert.Contains(sink.ContentOperations, operation => operation.Content.Contains(">streamed<"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HtmlEscape_EscapesMarkupInMessageText()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "<script>alert('x')</script>"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var operation = Assert.Single(sink.ContentOperations);
        Assert.DoesNotContain("<script>", operation.Content);
        Assert.Contains("&lt;script&gt;", operation.Content);
    }

    [Fact]
    public void TextContent_RendersMarkdownEmphasisAndInlineCode_AsHtmlElements()
    {
        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new TextContent("This is **bold**, *italic*, and `code`."),
            includeReasoning: true,
            isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("class=\"chat-content chat-text\"", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<em>italic</em>", html);
        Assert.Contains("<code>code</code>", html);
    }

    [Fact]
    public void TextContent_RendersMarkdownHeadingsListsAndFencedCode_AsHtmlElements()
    {
        var markdown = "# Title\n\n- one\n- two\n\n```\nlet x = 1;\n```";

        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new TextContent(markdown),
            includeReasoning: true,
            isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("<h1>Title</h1>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<pre><code>", html);
        Assert.Contains("let x = 1;", html);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Update_EmitsNoOperations_WhenSourceIsReferenceEqual()
    {
        var item = TextMessage(ChatRole.Assistant, "hello");
        var history = new ObservableCollection<AgentChatHistoryItem> { item };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        // Trigger a Replace event with the same reference — ChatMessageHtmlModel.Update must short-circuit.
        history[0] = item;

        Assert.Empty(sink.ContentOperations);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Update_EmitsOperations_WhenSourceDiffers()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.Assistant, "original"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history[0] = TextMessage(ChatRole.Assistant, "updated");

        Assert.NotEmpty(sink.ContentOperations);
    }

    [Fact]
    public void TextContent_RendersMarkdownPipeTable_AsHtmlTable()
    {
        var markdown = "| A | B |\n|---|---|\n| 1 | 2 |";

        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new TextContent(markdown),
            includeReasoning: true,
            isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("<table>", html);
        Assert.Contains("<th>", html);
        Assert.Contains("<td>", html);
        // The raw markdown source is stored in data-details-target; verify the rendered body
        // contains the HTML table elements (above) rather than checking for absent pipe syntax,
        // since the attribute now intentionally embeds the original markdown.
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_StreamingUpdate_EmitsNoHtmlOps_WhenItemsAreReferenceEqual()
    {
        var item = TextMessage(ChatRole.Assistant, "hello");
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(item);
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;
        sink.Clear();

        // Replace the item in the running item's inner collection with the same reference.
        // ChatMessageHtmlModel.Update must short-circuit on ReferenceEquals.
        runningItem.Items[0] = item;

        Assert.Empty(sink.ContentOperations);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_StreamingUpdate_EmitsHtmlOps_WhenItemContentChanges()
    {
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "partial"));
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;
        sink.Clear();

        // Replace the item with new content — HTML ops must be emitted.
        runningItem.Items[0] = TextMessage(ChatRole.Assistant, "partial complete");

        Assert.NotEmpty(sink.ContentOperations);
    }

    [Fact]
    public void TextContent_DisablesRawHtmlInMarkdown_SoItCannotInjectMarkup()
    {
        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new TextContent("before <img src=x onerror=alert(1)> after"),
            includeReasoning: true,
            isDiagnostic: false);

        Assert.NotNull(html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
    }

    private static AgentChatHistoryItem ToolCallMessage(string toolName, string callId = "call-1")
        => new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent(callId, toolName)] };

    [AvaloniaFact(Timeout = 15_000)]
    public async Task SingleToolCallMessage_IsInsertedStandalone_WithoutGroupWrapper()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal("update", op.Kind);
        // Single standalone tool-call message: content-level item present, no message-level group body.
        Assert.Contains("chat-tool-group-item", op.Content);
        Assert.DoesNotContain("chat-tool-group-body", op.Content);
        Assert.Contains("chat-message", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TwoConsecutiveToolCalls_AreGroupedIntoSingleDetailsElement()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(ToolCallMessage("write_file", "c2"));

        var contentOps = sink.ContentOperations;
        Assert.True(contentOps.Count >= 2, "Expected Replace + Append + summary update");

        // First op: replace history-0 with group wrapping it
        var replaceOp = contentOps.First(op => op.Location == ChatOutputUpdateLocation.Replace && op.Content.Contains("chat-tool-group"));
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), replaceOp.Path);
        Assert.Contains(ChatOutputHtmlRenderer.ToolGroupId(0), replaceOp.Content);
        Assert.Contains("read_file", replaceOp.Content);
        Assert.Contains("chat-tool-group-body", replaceOp.Content);
        Assert.Contains(ChatOutputHtmlRenderer.MessageId(0), replaceOp.Content);

        // Second message appended into the group body
        var appendOp = contentOps.First(op => op.Location == ChatOutputUpdateLocation.Append && op.Path.Contains("body"));
        Assert.Contains("write_file", appendOp.Content);

        // Summary updated to count 2
        var summaryOp = contentOps.First(op => op.Location == ChatOutputUpdateLocation.Replace && op.Path.Contains("summary"));
        Assert.Contains("2 calls", summaryOp.Content);
        // Mixed group (read_file and write_file) lists both unique tool names.
        Assert.Contains("tools (", summaryOp.Content);
        Assert.Contains("write_file", summaryOp.Content);
        Assert.Contains("read_file", summaryOp.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ThreeConsecutiveToolCalls_GroupIsExtendedInPlace()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
            ToolCallMessage("write_file", "c2"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(ToolCallMessage("list_files", "c3"));

        var contentOps = sink.ContentOperations;

        // No Replace that creates a new chat-tool-group — group already exists
        Assert.DoesNotContain(contentOps, op =>
            op.Location == ChatOutputUpdateLocation.Replace && op.Content.Contains("chat-tool-group"));

        // Third message appended into group body
        var appendOp = contentOps.First(op => op.Location == ChatOutputUpdateLocation.Append && op.Path.Contains("body"));
        Assert.Contains("list_files", appendOp.Content);

        // Summary updated to count 3
        var summaryOp = contentOps.First(op => op.Location == ChatOutputUpdateLocation.Replace && op.Path.Contains("summary"));
        Assert.Contains("3 calls", summaryOp.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TextThenToolCall_ToolCallIsStandalone_NoGroupWrapper()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.Assistant, "thinking"),
            ToolCallMessage("read_file"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var contentOps = sink.ContentOperations;
        var op = Assert.Single(contentOps);
        // Neither message should be inside a message-level group body.
        Assert.DoesNotContain("chat-tool-group-body", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ToolCallThenText_TextInsertsAfterToolCallMessage()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(TextMessage(ChatRole.Assistant, "done"));

        // Two ops: the insert After, plus the #1222 header-suppression Replace on the new element
        // (same role as the predecessor assistant tool-call group).
        Assert.Equal(2, sink.ContentOperations.Count);
        var op = sink.ContentOperations[0];
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), op.Path);
        Assert.Contains(">done<", op.Content);
        var suppression = sink.ContentOperations[1];
        Assert.Equal(ChatOutputUpdateLocation.Replace, suppression.Location);
        Assert.Equal(ChatOutputHtmlRenderer.HeaderId(ChatOutputHtmlRenderer.MessageId(1)), suppression.Path);
        Assert.Contains("chat-header-suppressed", suppression.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ConsecutiveToolCallsThenText_TextAnchorsAfterGroupElement()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
            ToolCallMessage("write_file", "c2"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(TextMessage(ChatRole.Assistant, "finished"));

        var contentOps = sink.ContentOperations;
        var insertOp = contentOps.First(op => op.Content.Contains("finished"));
        Assert.Equal(ChatOutputUpdateLocation.After, insertOp.Location);
        // Should insert after the group element that wraps both tool calls (group id from the
        // first member's history index).
        Assert.Equal(ChatOutputHtmlRenderer.ToolGroupId(0), insertOp.Path);
    }

    // ── Content-level tool-group tests (issue #154) ────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MessageWithSingleFunctionCall_RendersToolGroupItem_NoOuterWrapper()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent("call-1", "my_tool")],
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations);
        Assert.Contains("chat-tool-group-item", op.Content);
        Assert.DoesNotContain("chat-tool-group-wrapper", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MessageWithMultipleFunctionCalls_RendersOuterToolGroupWrapper()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call-1", "tool_a"),
                    new FunctionCallContent("call-2", "tool_b"),
                ],
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations);
        Assert.Contains("chat-tool-group-wrapper", op.Content);
        Assert.Contains("chat-tool-group-item", op.Content);
        Assert.Contains("2 calls", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MessageWithFunctionCallAndMatchingResult_ResultNestedInsideCallItem()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent("call-1", "my_tool"),
                    new FunctionResultContent("call-1", "result data"),
                ],
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations);
        // Both call and result sections present in a single element.
        Assert.Contains("chat-tool-call", op.Content);
        Assert.Contains("chat-tool-result", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MessageWithFunctionResultOnly_NoMatchingCall_RenderedStandalone()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent("orphan-id", "orphan result")],
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations);
        // Unmatched result: falls back to standalone RenderContent path, no content-level grouping.
        Assert.DoesNotContain("chat-tool-group-item", op.Content);
        Assert.DoesNotContain("chat-tool-group-wrapper", op.Content);
    }

    [Fact]
    public void ToolRoleMessage_RenderHeader_ReturnsEmpty()
    {
        var html = ChatOutputHtmlRenderer.RenderHeader("msg-0", "tool");
        Assert.Empty(html);
    }

    [Fact]
    public void RenderToolCallPair_ProducesCollapsedDetails_NoOpenAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", "\"result\"");

        Assert.Contains("chat-tool-group-item", html);
        Assert.Contains("chat-tool-call", html);
        Assert.Contains("chat-tool-result", html);
        Assert.DoesNotContain("<details open", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderToolGroupWrapper_ProducesOuterDetailsWithCallCount()
    {
        var inner = "<span>item</span>";
        var html = ChatOutputHtmlRenderer.RenderToolGroupWrapper("g0", 3, new[] { "last_tool" }, inner);

        Assert.Contains("chat-tool-group-wrapper", html);
        Assert.Contains("3 calls", html);
        Assert.Contains(inner, html);
        Assert.DoesNotContain("<details open", html, StringComparison.OrdinalIgnoreCase);
    }

    // ── Cross-message tool-result injection tests (issue #154 bug fix) ────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ToolResultMessage_CrossMessage_MatchedByCallId_InjectedIntoCallItem()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent("call-1", "my_tool")],
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Tool,
            Contents = [new FunctionResultContent("call-1", "\"result data\"")],
        });

        var contentOps = sink.ContentOperations;

        // The call message should be updated (Replace) to include the result sub-detail.
        var updateOp = contentOps.FirstOrDefault(op =>
            op.Location == ChatOutputUpdateLocation.Replace &&
            op.Content.Contains("chat-tool-group-item"));
        Assert.NotNull(updateOp);
        Assert.Contains("chat-tool-result", updateOp.Content);

        // No standalone "tool result:" element should appear as a separate DOM operation.
        Assert.DoesNotContain(contentOps, op => op.Content.Contains("tool result:"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ToolResultMessage_CrossMessage_Unmatched_RenderedStandalone()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent("orphan-id", "orphan result")],
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations);
        Assert.DoesNotContain("chat-tool-group-item", op.Content);
        Assert.DoesNotContain("chat-tool-group-wrapper", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ToolResultMessage_CrossMessage_DoesNotTriggerMessageLevelGroup()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent("call-1", "my_tool")],
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Tool,
            Contents = [new FunctionResultContent("call-1", "\"result data\"")],
        });

        var contentOps = sink.ContentOperations;

        // No message-level chat-tool-group (ToolCallGroupHtmlModel) should be created.
        Assert.DoesNotContain(contentOps, op => op.Content.Contains("chat-tool-group-body"));
        Assert.DoesNotContain(contentOps, op => op.Content.Contains("chat-tool-group\""));
    }

    // ── Cross-batch grouping tests (issue #291) ─────────────────────────────────

    private static AgentChatHistoryItem ToolResultMessage(string callId)
        => new() { Role = ChatRole.Tool, Contents = [new FunctionResultContent(callId, "result")] };

    [AvaloniaFact(Timeout = 15_000)]
    public async Task TwoToolCallBatches_SeparatedByResults_AreGroupedTogether()
    {
        // Arrange: batch 1
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        // Tool result for batch 1 (injected — no DOM element)
        history.Add(ToolResultMessage("c1"));

        // batch 2 — should be grouped with batch 1 even though a result message sits between them
        history.Add(ToolCallMessage("write_file", "c2"));

        var contentOps = sink.ContentOperations;

        // A Replace on history-0 must have created the message-level group wrapper (contains group body).
        var groupCreateOp = contentOps.FirstOrDefault(op =>
            op.Location == ChatOutputUpdateLocation.Replace &&
            op.Path == ChatOutputHtmlRenderer.MessageId(0) &&
            op.Content.Contains("chat-tool-group-body"));
        Assert.NotNull(groupCreateOp);
        Assert.Contains("read_file", groupCreateOp!.Content);
        Assert.Contains(ChatOutputHtmlRenderer.ToolGroupId(0), groupCreateOp.Content);

        // Second batch appended into the group body
        var appendOp = contentOps.FirstOrDefault(op =>
            op.Location == ChatOutputUpdateLocation.Append && op.Path.Contains("body"));
        Assert.NotNull(appendOp);
        Assert.Contains("write_file", appendOp!.Content);

        // Summary updated to 2 calls
        var summaryOp = contentOps.FirstOrDefault(op =>
            op.Location == ChatOutputUpdateLocation.Replace && op.Path.Contains("summary"));
        Assert.NotNull(summaryOp);
        Assert.Contains("2 calls", summaryOp!.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ThreeToolCallBatches_SeparatedByResults_AllInSameGroup()
    {
        // Arrange: batch 1 + batch 2 (already grouped)
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
            ToolResultMessage("c1"),
            ToolCallMessage("write_file", "c2"),
            ToolResultMessage("c2"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        // batch 3 — must extend the existing group, not create a new one
        history.Add(ToolCallMessage("list_files", "c3"));

        var contentOps = sink.ContentOperations;

        // No Replace on msg-0 that creates a brand-new chat-tool-group (group must already exist)
        Assert.DoesNotContain(contentOps, op =>
            op.Location == ChatOutputUpdateLocation.Replace &&
            op.Path == ChatOutputHtmlRenderer.MessageId(0) &&
            op.Content.Contains("chat-tool-group-body"));

        // Third batch appended into the existing group body
        var appendOp = contentOps.FirstOrDefault(op =>
            op.Location == ChatOutputUpdateLocation.Append && op.Path.Contains("body"));
        Assert.NotNull(appendOp);
        Assert.Contains("list_files", appendOp!.Content);

        // Summary updated to 3 calls
        var summaryOp = contentOps.FirstOrDefault(op =>
            op.Location == ChatOutputUpdateLocation.Replace && op.Path.Contains("summary"));
        Assert.NotNull(summaryOp);
        Assert.Contains("3 calls", summaryOp!.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ToolCallBatch_AfterNonToolMessage_IsStandalone_EvenWithResults()
    {
        // A text reply followed by results and then a new tool-call batch:
        // the tool-call batch must NOT be grouped with anything before the text reply.
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
            ToolResultMessage("c1"),
            TextMessage(ChatRole.Assistant, "thinking..."),
            ToolCallMessage("write_file", "c2"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var contentOps = sink.ContentOperations;

        // No message-level group should be created (text reply breaks the run)
        Assert.DoesNotContain(contentOps, op => op.Content.Contains("chat-tool-group-body"));
    }

    [Fact]
    public void RenderContent_FunctionCallContent_EmitsDataDetailsTarget()
    {
        var call = new FunctionCallContent("call-1", "my_tool");

        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            call,
            includeReasoning: true,
            isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-details-target=", html);
    }

    [Fact]
    public void RenderContent_TextContent_EmitsDataDetailsTargetWithJsonContent()
    {
        const string markdown = "**bold** text";

        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new TextContent(markdown),
            includeReasoning: true,
            isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-details-target=\"{", html);
    }

    [Fact]
    public void RenderContent_TextReasoningContent_EmitsDataDetailsTarget()
    {
        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new TextReasoningContent("my reasoning"),
            includeReasoning: true,
            isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-details-target=\"{", html);
    }

    // ── Running-item insertion-point reliability tests (issue #222) ───────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_Activate_EmitsContainerInsertBeforeMessageAppend()
    {
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "hello"));
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;

        var ops = sink.ContentOperations;
        Assert.Equal(2, ops.Count);

        // First op must be the outer running-item container appended into the running region.
        Assert.Equal(ChatOutputHtmlRenderer.RunningContainerId, ops[0].Path);
        Assert.Equal(ChatOutputUpdateLocation.Append, ops[0].Location);
        Assert.Contains(ChatOutputHtmlRenderer.RunningItemId(0), ops[0].Content);

        // Second op must be the message appended into the running item's contents container —
        // never a hardcoded anchor outside the running item.
        Assert.Equal(
            ChatOutputHtmlRenderer.RunningItemContentsId(ChatOutputHtmlRenderer.RunningItemId(0)),
            ops[1].Path);
        Assert.Equal(ChatOutputUpdateLocation.Append, ops[1].Location);
        Assert.Contains(">hello<", ops[1].Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_StreamingChunksContinueAfterToolCallResultInsertion()
    {
        var runningItem = new AgentChatRunningItem();
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;

        // Seed: partial text + tool call already streamed.
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "thinking..."));
        runningItem.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("call-1", "my_tool")],
        });
        sink.Clear();

        // Tool result arrives — injected into the call slot, no new top-level DOM element.
        runningItem.Items.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Tool,
            Contents = [new FunctionResultContent("call-1", "\"ok\"")],
        });
        sink.Clear();

        // Next streaming chunk after the tool result.
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "done"));

        var ops = sink.ContentOperations;
        Assert.Contains(ops, op => op.Content.Contains(">done<"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_WhenInsertionFailed_NotifyInsertionFailed_ReInsertsAndRestoresStreaming()
    {
        var runningItem = new AgentChatRunningItem();
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;
        sink.Clear();

        // Simulate: JS reported that the running-item container element was not found
        // (the anchor used to insert it was stale — e.g. after a page reset/reload).
        var runningItemId = ChatOutputHtmlRenderer.RunningItemId(0);
        model.NotifyInsertionFailed(runningItemId);

        // The model must re-insert the container via Append into the running region.
        var reInsertOps = sink.ContentOperations;
        var containerReInsert = reInsertOps.FirstOrDefault(op =>
            op.Content.Contains(runningItemId) &&
            op.Location == ChatOutputUpdateLocation.Append);
        Assert.NotNull(containerReInsert);
        Assert.Equal(ChatOutputHtmlRenderer.RunningContainerId, containerReInsert.Path);

        sink.Clear();

        // Subsequent streaming chunks must now land correctly.
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "recovered stream"));

        var streamOps = sink.ContentOperations;
        Assert.Contains(streamOps, op => op.Content.Contains(">recovered stream<"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_InspectMessage_HandledByBridge()
    {
        // The rendered HTML for each content block must contain data-inspect-target so that the
        // InspectGutter JS component can attach an inspect button.  When the user clicks that
        // button the page posts {type:"inspect", contentId, contentJson} to the C# bridge, which
        // fires AgentChatOutputControl.InspectorRequested and opens AIContentInspectorWindow.
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.Assistant, "inspect me"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            history,
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink);
        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations);
        var expectedContentId = ChatOutputHtmlRenderer.ContentId(ChatOutputHtmlRenderer.MessageId(0), 0);

        // The content block element must carry data-inspect-target (the JS bridge trigger).
        Assert.Contains("data-inspect-target", op.Content);

        // The content block must have the correct id so the bridge can reference it back.
        Assert.Contains($"id=\"{expectedContentId}\"", op.Content);
    }

    // ── GenerateHistoryChunk tests ────────────────────────────────────────────

    [Fact]
    public void HistoryChunkSize_IsExactly200()
    {
        Assert.Equal(200, ChatOutputHtmlModel.HistoryChunkSize);
    }

    private static ChatOutputHtmlModel.HistoryRenderPlan BuildPlan(
        IReadOnlyList<AgentChatHistoryItem> snapshot,
        RecordingSink sink)
        => ChatOutputHtmlModel.BuildHistoryRenderPlan(snapshot, sink, () => true);

    [Fact]
    public void BuildHistoryRenderPlan_EmptyHistory_ReturnsNoSlotsAndNoChunks()
    {
        var sink = new RecordingSink();

        var plan = BuildPlan([], sink);

        Assert.Empty(plan.Slots);
        Assert.Empty(plan.SlotByCallId);
        Assert.Empty(plan.Chunks);
        Assert.Empty(sink.Operations);
    }

    [Fact]
    public void GenerateHistoryChunk_SingleItem_ReturnsHistoryMessageHtml()
    {
        var sink = new RecordingSink();
        var plan = BuildPlan([TextMessage(ChatRole.User, "hello world")], sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, 1);

        Assert.Contains("id=\"history-0\"", html);
        Assert.Contains("chat-message", html);
        Assert.Contains("hello world", html);
    }

    [Fact]
    public void GenerateHistoryChunk_NonZeroStart_UsesHistoryIds()
    {
        var snapshot = Enumerable.Range(0, 250)
            .Select(i => TextMessage(ChatRole.User, $"message {i}"))
            .ToList();
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 200, 250);

        Assert.Contains("id=\"history-200\"", html);
        Assert.Contains("id=\"history-249\"", html);
        Assert.DoesNotContain("id=\"history-0\"", html);
        Assert.DoesNotContain("id=\"history-199\"", html);
    }

    [Fact]
    public void GenerateHistoryChunk_ToolCallRun_GroupedWithFirstHistoryIndex()
    {
        var snapshot = Enumerable.Range(0, 10)
            .Select(i => TextMessage(ChatRole.User, $"text {i}"))
            .Concat(
            [
                ToolCallMessage("tool_a", "c1"),
                ToolCallMessage("tool_b", "c2"),
                ToolCallMessage("tool_c", "c3"),
            ])
            .ToList();
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        // The group id derives from the first member's global history index (10), and all members
        // are nested inside the single group element.
        var groupId = ChatOutputHtmlRenderer.ToolGroupId(10);
        Assert.Contains($"id=\"{groupId}\"", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "chat-tool-group\""));
        Assert.Contains("tool_a", html);
        Assert.Contains("tool_b", html);
        Assert.Contains("tool_c", html);
        Assert.Contains("3 calls", html);
    }

    [Fact]
    public void GenerateHistoryChunk_ToolCallsSeparatedByNonDisplayedItem_GroupedTogether()
    {
        // An empty (non-displayed) message sits between two tool calls; grouping must ignore it.
        var snapshot = new List<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            new() { Role = ChatRole.Assistant, Contents = [] },
            ToolCallMessage("tool_b", "c2"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        // A single group wraps both calls; the empty message emits nothing.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "chat-tool-group\""));
        Assert.Contains("tool_a", html);
        Assert.Contains("tool_b", html);
        Assert.Contains("2 calls", html);
        // The non-displayed intervening slot carries no DOM element.
        Assert.False(plan.Slots[1].HasDomElement);
    }

    [Fact]
    public void BuildHistoryRenderPlan_NonDisplayedInterveningItem_ProducesNoDomElement()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            new() { Role = ChatRole.Assistant, Contents = [] },
            ToolCallMessage("tool_b", "c2"),
        };
        var sink = new RecordingSink();

        var plan = BuildPlan(snapshot, sink);

        Assert.False(plan.Slots[1].HasDomElement);
        Assert.True(plan.Slots[1].Model.ProducesNoVisibleContent);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task LiveTransformer_ToolCallsSeparatedByEmptyMessage_CoalesceIntoOneGroup()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        // A non-displayed empty message, then a second tool call: the two calls must coalesce.
        history.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [] });
        history.Add(ToolCallMessage("tool_b", "c2"));

        var summaryOp = sink.ContentOperations.FirstOrDefault(op => op.Path.Contains("summary"));
        Assert.NotNull(summaryOp);
        Assert.Contains("2 calls", summaryOp!.Content);

        // No standalone empty chat-message bubble was appended for the empty item.
        Assert.DoesNotContain(
            sink.ContentOperations,
            op => op.Location == ChatOutputUpdateLocation.Append
                && op.Path == ChatOutputHtmlRenderer.HistoryContainerId);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task LiveTransformer_NonDisplayedInterveningItem_ProducesNoDomElement()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(new AgentChatHistoryItem { Role = ChatRole.Assistant, Contents = [] });

        // The empty message must not emit any content operation of its own.
        Assert.DoesNotContain(
            sink.ContentOperations,
            op => op.Content.Contains(ChatOutputHtmlRenderer.MessageId(1)));
    }

    [Fact]
    public void GenerateHistoryChunk_GroupedMessageHtml_RemainsNestedForEmitDiff()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, 2);

        // Grouped members no longer emit per-member <div class="chat-message"> frames (issue #1225).
        // The group owns the single message frame; per-content binding element ids remain intact
        // so later EmitDiff operations can still target them.
        var groupId = ChatOutputHtmlRenderer.ToolGroupId(0);
        Assert.Contains($"id=\"{groupId}\"", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "chat-message"));
        Assert.Contains("tool_a", html);
        Assert.Contains("tool_b", html);
        // Per-content binding ids are still present for diff targeting.
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.ContentId(ChatOutputHtmlRenderer.MessageId(0), 0)}\"", html);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.ContentId(ChatOutputHtmlRenderer.MessageId(1), 0)}\"", html);
    }

    [Fact]
    public void GenerateHistoryChunk_DoesNotInvokeOnInsertOrEmitSinkCommands()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "one"),
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
            TextMessage(ChatRole.Assistant, "two"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        Assert.NotEmpty(html);
        // Chunk generation returns a single blob and never emits per-item sink operations.
        Assert.Empty(sink.Operations);
    }

    [Fact]
    public void BuildHistoryRenderPlan_ResultInNewerChunkCallInOlderChunk_InjectedIntoCall()
    {
        // The call sits at index 10 (older chunk) while its result sits at index 300 (newer chunk).
        var snapshot = Enumerable.Range(0, 10)
            .Select(i => TextMessage(ChatRole.User, $"text {i}"))
            .Append(ToolCallMessage("my_tool", "cross-chunk-call"))
            .Concat(Enumerable.Range(11, 289).Select(i => TextMessage(ChatRole.User, $"text {i}")))
            .Append(new AgentChatHistoryItem
            {
                Role = ChatRole.Tool,
                Contents = [new FunctionResultContent("cross-chunk-call", "\"cross-chunk result data\"")],
            })
            .Concat(Enumerable.Range(301, 99).Select(i => TextMessage(ChatRole.User, $"text {i}")))
            .ToList();
        Assert.Equal(400, snapshot.Count);
        var sink = new RecordingSink();

        var plan = BuildPlan(snapshot, sink);

        // The result-only message renders no element of its own.
        Assert.False(plan.Slots[300].HasDomElement);

        // The older chunk containing the call renders the injected result nested under it.
        Assert.True(plan.Chunks.Count >= 2);
        var olderChunk = plan.Chunks.First(chunk => chunk.Start <= 10 && chunk.End > 10);
        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, olderChunk.Start, olderChunk.End);
        Assert.Contains("cross-chunk result data", html);
        Assert.Contains("chat-tool-result", html);

        // The newer chunk contains no standalone rendering of the result message.
        var newerChunk = plan.Chunks.First(chunk => chunk.Start <= 300 && chunk.End > 300);
        var newerHtml = ChatOutputHtmlModel.GenerateHistoryChunk(plan, newerChunk.Start, newerChunk.End);
        Assert.DoesNotContain($"id=\"{ChatOutputHtmlRenderer.MessageId(300)}\"", newerHtml);
    }

    [Fact]
    public async Task GenerateHistoryChunk_CanBeCalledOffUIThread()
    {
        var sink = new RecordingSink();

        var html = await Task.Run(() =>
        {
            var plan = BuildPlan([TextMessage(ChatRole.Assistant, "off-thread")], sink);
            return ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, 1);
        });

        Assert.Contains("off-thread", html);
    }

    // ── ComputeChunkRanges / SnapCutPoint tests ───────────────────────────────

    [Fact]
    public void ComputeChunkRanges_RawCutInsideToolRun_SnapsToRunStart()
    {
        // 400 items with a call/result run straddling the raw cut at 200: the whole tool-related
        // run must land in the newer chunk so grouping and result injection stay chunk-local.
        var items = Enumerable.Range(0, 196).Select(_ => TextMessage(ChatRole.User, "text"))
            .Concat(
            [
                ToolCallMessage("t0", "c0"),
                ToolResultMessage("c0"),
                ToolCallMessage("t1", "c1"),
                ToolResultMessage("c1"),
                ToolCallMessage("t2", "c2"),
                ToolResultMessage("c2"),
            ])
            .Concat(Enumerable.Range(0, 198).Select(_ => TextMessage(ChatRole.User, "text")))
            .ToList();
        Assert.Equal(400, items.Count);

        var ranges = ChatOutputHtmlModel.ComputeChunkRanges(items);

        Assert.Equal(2, ranges.Count);
        Assert.Equal((196, 400), ranges[0]);
        Assert.Equal((0, 196), ranges[1]);
    }

    [Fact]
    public void ComputeChunkRanges_SnapCutBoundaryInsideTool3Run_SnapsToBeforeRun()
    {
        // 400 items: 0-197=text, 198-200=tool-call, 201-399=text.
        // rawStart=200; snapshot[199]=tool-call → k=199, snapshot[198]=tool-call → k=198, snapshot[197]=text → stop.
        var items = Enumerable.Range(0, 198).Select(_ => TextMessage(ChatRole.User, "text"))
            .Concat(Enumerable.Range(0, 3).Select(i => ToolCallMessage($"t{i}", $"c{i}")))
            .Concat(Enumerable.Range(0, 199).Select(_ => TextMessage(ChatRole.User, "text")))
            .ToList();
        Assert.Equal(400, items.Count);

        var ranges = ChatOutputHtmlModel.ComputeChunkRanges(items);

        Assert.Equal(2, ranges.Count);
        Assert.Equal((198, 400), ranges[0]); // all three tool-call items in the same (newer) chunk
        Assert.Equal((0, 198), ranges[1]);
    }

    [Fact]
    public void ComputeChunkRanges_SnapCutBetweenToolCallAndResult_SnapsToBeforeToolCall()
    {
        // 400 items: 0-198=text, 199=tool-call, 200=tool-result, 201-399=text.
        // rawStart=200; snapshot[199]=tool-call-only → snap to 199; snapshot[198]=text → stop.
        var items = Enumerable.Range(0, 199).Select(_ => TextMessage(ChatRole.User, "text"))
            .Append(ToolCallMessage("tool", "c1"))
            .Append(ToolResultMessage("c1"))
            .Concat(Enumerable.Range(0, 199).Select(_ => TextMessage(ChatRole.User, "text")))
            .ToList();
        Assert.Equal(400, items.Count);

        var ranges = ChatOutputHtmlModel.ComputeChunkRanges(items);

        Assert.Equal(2, ranges.Count);
        Assert.Equal((199, 400), ranges[0]); // tool-call and its result are in the same (newer) chunk
        Assert.Equal((0, 199), ranges[1]);
    }

    [Fact]
    public void ComputeChunkRanges_SnapCutImmediatelyAfterToolRun_NoSnap()
    {
        // 399 items: 0-198=text, 199-201=tool-calls, 202-398=text.
        // rawStart=199; snapshot[198]=text → no snap; tool items 199-201 stay in newer chunk.
        var items = Enumerable.Range(0, 199).Select(_ => TextMessage(ChatRole.User, "text"))
            .Concat(Enumerable.Range(0, 3).Select(i => ToolCallMessage($"t{i}", $"c{i}")))
            .Concat(Enumerable.Range(0, 197).Select(_ => TextMessage(ChatRole.User, "text")))
            .ToList();
        Assert.Equal(399, items.Count);

        var ranges = ChatOutputHtmlModel.ComputeChunkRanges(items);

        Assert.Equal(2, ranges.Count);
        Assert.Equal((199, 399), ranges[0]);
        Assert.Equal((0, 199), ranges[1]);
    }

    [Fact]
    public void ComputeChunkRanges_500Items_Produces3Chunks()
    {
        var items = Enumerable.Range(0, 500).Select(_ => TextMessage(ChatRole.User, "text")).ToList();

        var ranges = ChatOutputHtmlModel.ComputeChunkRanges(items);

        Assert.Equal(3, ranges.Count);
        Assert.Equal((300, 500), ranges[0]);
        Assert.Equal((100, 300), ranges[1]);
        Assert.Equal((0, 100), ranges[2]);
    }

    [Fact]
    public void ComputeChunkRanges_AllToolCalls300Items_ProducesOneChunk()
    {
        // All 300 items are tool-call-only. HistoryChunkSize=200: rawStart=100, snap walks back to 0.
        var items = Enumerable.Range(0, 300).Select(i => ToolCallMessage($"t{i}", $"c{i}")).ToList();

        var ranges = ChatOutputHtmlModel.ComputeChunkRanges(items);

        Assert.Single(ranges);
        Assert.Equal((0, 300), ranges[0]);
    }

    [Fact]
    public void ComputeChunkRanges_ToolRunSpanning201Items_ProducesOneChunk()
    {
        // 201 tool-call items: rawStart=1, snap(1): snapshot[0]=tool-call → k=0. One chunk (0,201).
        var items = Enumerable.Range(0, 201).Select(i => ToolCallMessage($"t{i}", $"c{i}")).ToList();

        var ranges = ChatOutputHtmlModel.ComputeChunkRanges(items);

        Assert.Single(ranges);
        Assert.Equal((0, 201), ranges[0]);
    }

    [Fact]
    public void ComputeChunkRanges_ExactlyHistoryChunkSizeItems_ProducesOneChunk()
    {
        var items = Enumerable.Range(0, 200).Select(_ => TextMessage(ChatRole.User, "text")).ToList();

        var ranges = ChatOutputHtmlModel.ComputeChunkRanges(items);

        Assert.Single(ranges);
        Assert.Equal((0, 200), ranges[0]);
    }

    [Fact]
    public void ComputeChunkRanges_EmptySnapshot_ProducesNoChunks()
    {
        var ranges = ChatOutputHtmlModel.ComputeChunkRanges([]);

        Assert.Empty(ranges);
    }

    // ── ChatMessageHtmlTransformer live-transformer tests (issue #893) ─────────

    private static ChatMessageHtmlTransformer MakeLiveTransformer(
        ObservableCollection<AgentChatHistoryItem> source,
        List<RenderSlot> slots,
        RecordingSink sink,
        Dictionary<string, RenderSlot> sharedSlotByCallId,
        int preloadedCount = 0)
        => new(
            source,
            slots,
            sink,
            () => true,
            containerPath: ChatOutputHtmlRenderer.HistoryContainerId,
            elementIdForSourceIndex: ChatOutputHtmlRenderer.MessageId,
            groupIdForSourceIndex: ChatOutputHtmlRenderer.ToolGroupId,
            sharedSlotByCallId: sharedSlotByCallId,
            preloadedCount: preloadedCount);

    /// <summary>
    /// Simulates the Phase B → Phase C hand-off: builds the render plan for the current source
    /// contents, renders the single chunk blob (marking the models inserted), and returns the
    /// preloaded slots plus the shared call-id map, ready for live-transformer construction.
    /// </summary>
    private static (List<RenderSlot> Slots, Dictionary<string, RenderSlot> SharedMap) PreloadPlan(
        ObservableCollection<AgentChatHistoryItem> source,
        RecordingSink sink)
    {
        var plan = ChatOutputHtmlModel.BuildHistoryRenderPlan([.. source], sink, () => true);
        ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, plan.Slots.Length);
        return ([.. plan.Slots], plan.SlotByCallId);
    }

    [Fact]
    public void LiveTransformer_FirstItem_AppendsIntoHistoryContainer()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>();
        var sink = new RecordingSink();
        using var transformer = MakeLiveTransformer(source, [], sink, new(StringComparer.Ordinal));

        source.Add(TextMessage(ChatRole.User, "first-live"));

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputHtmlRenderer.HistoryContainerId, op.Path);
        Assert.Equal(ChatOutputUpdateLocation.Append, op.Location);
        Assert.Contains("first-live", op.Content);
    }

    [Fact]
    public void LiveTransformer_ItemAfterHistoryLoad_InsertsAfterLastTopLevelHistoryOrGroup()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "old"),
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
        };
        var sink = new RecordingSink();
        var (slots, sharedMap) = PreloadPlan(source, sink);
        using var transformer = MakeLiveTransformer(source, slots, sink, sharedMap, preloadedCount: 3);
        sink.Clear();

        source.Add(TextMessage(ChatRole.Assistant, "new-live"));

        // The last top-level element is the group wrapping items 1-2, so the live item inserts
        // after the group element, not after a nested member.
        // Two ops: the insert After, plus the #1222 header-suppression Replace on the new element
        // (same assistant role as the tool-call group predecessor).
        Assert.Equal(2, sink.ContentOperations.Count);
        var op = sink.ContentOperations[0];
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.ToolGroupId(1), op.Path);
        Assert.Contains("new-live", op.Content);
        Assert.Equal(ChatOutputUpdateLocation.Replace, sink.ContentOperations[1].Location);
        Assert.Contains("chat-header-suppressed", sink.ContentOperations[1].Content);
    }

    [Fact]
    public void LiveTransformer_ToolGroupPromotion_UsesHistoryIndex_NotLocalIndex()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 200).Select(i => TextMessage(ChatRole.User, $"text {i}")));
        var sink = new RecordingSink();
        var (slots, sharedMap) = PreloadPlan(source, sink);
        using var transformer = MakeLiveTransformer(source, slots, sink, sharedMap, preloadedCount: 200);
        sink.Clear();

        source.Add(ToolCallMessage("tool_a", "c1"));
        source.Add(ToolCallMessage("tool_b", "c2"));

        // Promotion must derive the group id from the first member's global history index (200).
        var promoteOp = sink.ContentOperations.First(op =>
            op.Location == ChatOutputUpdateLocation.Replace && op.Content.Contains("chat-tool-group"));
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(200), promoteOp.Path);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.ToolGroupId(200)}\"", promoteOp.Content);
        Assert.DoesNotContain($"id=\"{ChatOutputHtmlRenderer.ToolGroupId(0)}\"", promoteOp.Content);
    }

    [Fact]
    public void LiveTransformer_ToolGroupExtension_AppendsToGroupBody()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
        };
        var sink = new RecordingSink();
        var (slots, sharedMap) = PreloadPlan(source, sink);
        using var transformer = MakeLiveTransformer(source, slots, sink, sharedMap, preloadedCount: 2);
        sink.Clear();

        source.Add(ToolCallMessage("tool_c", "c3"));

        var appendOp = sink.ContentOperations.First(op => op.Location == ChatOutputUpdateLocation.Append);
        Assert.Equal(ChatOutputHtmlRenderer.ToolGroupBodyId(ChatOutputHtmlRenderer.ToolGroupId(0)), appendOp.Path);
        Assert.Contains("tool_c", appendOp.Content);
    }

    [Fact]
    public void LiveTransformer_ResultOnlyLiveMessage_MatchesPreloadedHistoryCall()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("my_tool", "preloaded-call"),
        };
        var sink = new RecordingSink();
        var (slots, sharedMap) = PreloadPlan(source, sink);
        using var transformer = MakeLiveTransformer(source, slots, sink, sharedMap, preloadedCount: 1);
        sink.Clear();

        source.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Tool,
            Contents = [new FunctionResultContent("preloaded-call", "\"late result\"")],
        });

        // The result was injected into the preloaded call's element rather than rendered standalone.
        Assert.False(slots[1].HasDomElement);
        Assert.Contains(sink.ContentOperations, op => op.Content.Contains("late result"));
        Assert.DoesNotContain(sink.ContentOperations, op =>
            op.Content.Contains($"id=\"{ChatOutputHtmlRenderer.MessageId(1)}\"") &&
            op.Location != ChatOutputUpdateLocation.Replace);
    }

    [Fact]
    public void LiveTransformer_OnRemove_Ungrouped_RemovesMessageElement()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "keep"),
            TextMessage(ChatRole.Assistant, "drop"),
        };
        var sink = new RecordingSink();
        var (slots, sharedMap) = PreloadPlan(source, sink);
        using var transformer = MakeLiveTransformer(source, slots, sink, sharedMap, preloadedCount: 2);
        sink.Clear();

        source.RemoveAt(1);

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal("remove", op.Kind);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(1), op.Path);
    }

    [Fact]
    public void LiveTransformer_OnRemove_Grouped_RebuildsOrRemovesAffectedGroup()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
            ToolCallMessage("tool_c", "c3"),
        };
        var sink = new RecordingSink();
        var (slots, sharedMap) = PreloadPlan(source, sink);
        using var transformer = MakeLiveTransformer(source, slots, sink, sharedMap, preloadedCount: 3);
        sink.Clear();

        // Remove the middle member: the group element is replaced with a rebuilt two-member group.
        source.RemoveAt(1);

        var rebuildOp = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.Replace, rebuildOp.Location);
        Assert.Equal(ChatOutputHtmlRenderer.ToolGroupId(0), rebuildOp.Path);
        Assert.Contains("2 calls", rebuildOp.Content);
        Assert.Contains("tool_a", rebuildOp.Content);
        Assert.Contains("tool_c", rebuildOp.Content);
        Assert.DoesNotContain("tool_b", rebuildOp.Content);
        sink.Clear();

        // Remove another member: the sole remaining member becomes a standalone element.
        source.RemoveAt(1);

        var standaloneOp = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.Replace, standaloneOp.Location);
        Assert.Equal(ChatOutputHtmlRenderer.ToolGroupId(0), standaloneOp.Path);
        Assert.DoesNotContain("chat-tool-group\"", standaloneOp.Content);
        Assert.Contains("tool_a", standaloneOp.Content);
        sink.Clear();

        // Remove the final member: the standalone element is removed by id.
        source.RemoveAt(0);

        var removeOp = Assert.Single(sink.ContentOperations);
        Assert.Equal("remove", removeOp.Kind);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), removeOp.Path);
    }

    [Fact]
    public void LiveTransformer_OnReplace_StructuralChange_RebuildsAffectedRun()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "before"),
            TextMessage(ChatRole.Assistant, "will become tool call"),
        };
        var sink = new RecordingSink();
        var (slots, sharedMap) = PreloadPlan(source, sink);
        using var transformer = MakeLiveTransformer(source, slots, sink, sharedMap, preloadedCount: 2);
        sink.Clear();

        // Replace a text message with a tool-call-only message (structural category change).
        source[1] = ToolCallMessage("replacement_tool", "c-replaced");

        var ops = sink.ContentOperations;
        // The old element is removed and the replacement is inserted after its predecessor.
        Assert.Contains(ops, op => op.Kind == "remove" && op.Path == ChatOutputHtmlRenderer.MessageId(1));
        var insertOp = ops.First(op => op.Kind == "update" && op.Content.Contains("replacement_tool"));
        Assert.Equal(ChatOutputUpdateLocation.After, insertOp.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), insertOp.Path);

        // The replacement keeps its immutable element id so per-content diffs still work.
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.MessageId(1)}\"", insertOp.Content);
    }

    [Fact]
    public void LiveTransformer_Reset_RebuildsContainerChildrenWithoutReplacingContainer()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "a"),
            TextMessage(ChatRole.Assistant, "b"),
        };
        var sink = new RecordingSink();
        var (slots, sharedMap) = PreloadPlan(source, sink);
        using var transformer = MakeLiveTransformer(source, slots, sink, sharedMap, preloadedCount: 2);
        sink.Clear();

        source.Clear();

        var ops = sink.ContentOperations;
        Assert.Equal(2, ops.Count(op => op.Kind == "remove"));
        // The persistent container itself is never removed or replaced.
        Assert.DoesNotContain(ops, op => op.Path == ChatOutputHtmlRenderer.HistoryContainerId && op.Kind == "remove");
        Assert.DoesNotContain(ops, op =>
            op.Path == ChatOutputHtmlRenderer.HistoryContainerId &&
            op.Location == ChatOutputUpdateLocation.Replace);
    }

    [Fact]
    public void LiveTransformer_PreloadedItems_AreNotReEmitted()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "preloaded-0"),
            TextMessage(ChatRole.User, "preloaded-1"),
        };
        var sink = new RecordingSink();
        var (slots, sharedMap) = PreloadPlan(source, sink);

        using var transformer = MakeLiveTransformer(source, slots, sink, sharedMap, preloadedCount: 2);

        Assert.Empty(sink.ContentOperations);

        source.Add(TextMessage(ChatRole.Assistant, "live-2"));

        var op = Assert.Single(sink.ContentOperations);
        Assert.Contains("live-2", op.Content);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.MessageId(2)}\"", op.Content);
    }

    [Fact]
    public void LiveTransformer_ItemsAddedDuringLoad_AreRenderedByConstructor()
    {
        // Items appended to the source after the snapshot (preloadedCount) but before Phase C are
        // rendered by the constructor through the normal insert path.
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "snapshot-0"),
        };
        var sink = new RecordingSink();
        var (slots, sharedMap) = PreloadPlan(source, sink);
        source.Add(TextMessage(ChatRole.Assistant, "buffered-1"));

        using var transformer = MakeLiveTransformer(source, slots, sink, sharedMap, preloadedCount: 1);

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), op.Path);
        Assert.Contains("buffered-1", op.Content);
    }

    // ── ChatOutputHtmlModel three-phase async init tests (issue #631) ──────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_500Items_DeliversSinkCallsIn3Batches()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 500).Select(_ => TextMessage(ChatRole.User, "text")));
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        Assert.Equal(3, sink.BatchCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_200Items_DeliversSinkCallsIn1Batch()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 200).Select(_ => TextMessage(ChatRole.User, "text")));
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        Assert.Equal(1, sink.BatchCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_201Items_DeliversSinkCallsIn2Batches()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 201).Select(_ => TextMessage(ChatRole.User, "text")));
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        Assert.Equal(2, sink.BatchCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_0Items_NoHistorySinkCalls()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>();
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        Assert.Equal(0, sink.BatchCount);
        Assert.Empty(sink.ContentOperations);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_OlderChunk_PrependedIntoHistoryContainer()
    {
        // 400 items → 2 chunks, newest chunk delivered first; the older chunk is then prepended
        // above it. Every chunk op targets the persistent history container with Prepend.
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 400).Select(i => TextMessage(ChatRole.User, $"item {i}")));
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var ops = sink.ContentOperations;
        Assert.Equal(2, ops.Count);
        Assert.All(ops, op =>
        {
            Assert.Equal(ChatOutputHtmlRenderer.HistoryContainerId, op.Path);
            Assert.Equal(ChatOutputUpdateLocation.Prepend, op.Location);
        });

        // Newest chunk (items 200-399) first, older chunk (items 0-199) second.
        Assert.Contains(">item 399<", ops[0].Content);
        Assert.Contains(">item 0<", ops[1].Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_ScrollToBottom_CalledExactlyOnce()
    {
        // 400 items → 2 chunks. ScrollToBottom must be called exactly once (after the newest chunk).
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 400).Select(_ => TextMessage(ChatRole.User, "text")));
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        Assert.Equal(1, sink.ScrollCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_EmptyHistory_ScrollToBottomNotCalled()
    {
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink);
        await model.HistoryLoaded;

        Assert.Equal(0, sink.ScrollCount);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_CollectionChangedWhileLoading_IsBuffered()
    {
        // Start with empty history so there are no initial chunks.
        var history = new ObservableCollection<AgentChatHistoryItem>();
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        // Add an item while historyLoading is true (before HistoryLoaded completes).
        // Since there are no initial items, Phase B has no chunks to process, but Phase C
        // still dispatches asynchronously. The Add arrives before Phase C completes.
        history.Add(TextMessage(ChatRole.User, "buffered-item"));

        // At this point the item should NOT yet be rendered (transformer not yet constructed).
        Assert.Empty(sink.ContentOperations);

        // After Phase C completes, the live transformer handles it via ApplyInitialTransform.
        await model.HistoryLoaded;

        Assert.Contains(sink.ContentOperations, op => op.Content?.Contains("buffered-item") == true);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_CollectionChangedAfterLoading_IsProcessedImmediately()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>();
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        sink.Clear();

        // Add after loading is complete; transformer should process it immediately.
        history.Add(TextMessage(ChatRole.User, "live-item"));

        Assert.Contains(sink.ContentOperations, op => op.Content?.Contains("live-item") == true);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_NoBufferedEventsAreDroppedOrReplayedTwice()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>();
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        history.Add(TextMessage(ChatRole.User, "item-0"));
        history.Add(TextMessage(ChatRole.User, "item-1"));
        history.Add(TextMessage(ChatRole.User, "item-2"));

        Assert.Empty(sink.ContentOperations);

        await model.HistoryLoaded;

        var contentOps = sink.ContentOperations;
        Assert.Equal(3, contentOps.Count(op => op.Content?.Contains("chat-message") == true));
        Assert.Equal(1, contentOps.Count(op => op.Content?.Contains("item-0") == true));
        Assert.Equal(1, contentOps.Count(op => op.Content?.Contains("item-1") == true));
        Assert.Equal(1, contentOps.Count(op => op.Content?.Contains("item-2") == true));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_ItemIdsAreAssignedInIndexOrder()
    {
        // 400 items → 2 chunks. Element ids derive from the global history index: items[0] gets
        // history-0 (older chunk), items[200] gets history-200 (newer chunk).
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 400).Select(_ => TextMessage(ChatRole.User, "text")));
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var ops = sink.ContentOperations;
        Assert.Equal(2, ops.Count);

        // The newest chunk (delivered first) starts at items[200].
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.MessageId(200)}\"", ops[0].Content);
        Assert.DoesNotContain($"id=\"{ChatOutputHtmlRenderer.MessageId(0)}\"", ops[0].Content);

        // The older chunk contains items[0].
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.MessageId(0)}\"", ops[1].Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentChatOutput_Diagnostic_DiagnosticContentRendered()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = [new TextContent("diagnostic detail")],
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            history,
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink);
        await model.HistoryLoaded;

        Assert.NotEmpty(sink.ContentOperations);
        Assert.Contains(sink.ContentOperations, op => op.Content.Contains("diagnostic"));
    }

    // ── HistoryLoad tests (issue #893) ──────────────────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryLoad_AllChunksTargetHistoryContainer_WithPrependLocation()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 500).Select(i => TextMessage(ChatRole.User, $"item {i}")));
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var ops = sink.ContentOperations;
        Assert.Equal(3, ops.Count);
        Assert.All(ops, op =>
        {
            Assert.Equal(ChatOutputHtmlRenderer.HistoryContainerId, op.Path);
            Assert.Equal(ChatOutputUpdateLocation.Prepend, op.Location);
        });
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryLoad_NewestChunkInsertedFirst()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 400).Select(i => TextMessage(ChatRole.User, $"item {i}")));
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var ops = sink.ContentOperations;
        Assert.Contains(">item 399<", ops[0].Content);
        Assert.DoesNotContain(">item 399<", ops[1].Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryLoad_ScrollCalledAfterNewestChunkOnly()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 400).Select(i => TextMessage(ChatRole.User, $"item {i}")));
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        Assert.Equal(1, sink.ScrollCount);

        // The scroll happens immediately after the first (newest) chunk, before the older chunk.
        var kinds = sink.Operations.Select(op => op.Kind).ToList();
        var firstUpdate = kinds.IndexOf("update");
        var scrollIndex = kinds.IndexOf("scroll");
        var secondUpdate = kinds.IndexOf("update", firstUpdate + 1);
        Assert.True(firstUpdate < scrollIndex);
        Assert.True(scrollIndex < secondUpdate);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryLoad_500Items_AllMessageIdsPresentInSinkOutput()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 500).Select(i => TextMessage(ChatRole.User, $"item {i}")));
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var allContent = string.Concat(sink.ContentOperations.Select(op => op.Content));
        for (var i = 0; i < 500; i++)
        {
            Assert.Contains($"id=\"{ChatOutputHtmlRenderer.MessageId(i)}\"", allContent);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryLoad_EmptyHistory_EmitsNoChunkOps()
    {
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink);
        await model.HistoryLoaded;

        Assert.DoesNotContain(sink.Operations, op => op.Path == ChatOutputHtmlRenderer.HistoryContainerId);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryLoad_CancellationMidLoad_DoesNotPublishPartialSlotsOrCallMap()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 500).Select(i => TextMessage(ChatRole.User, $"item {i}")));
        history.Add(ToolCallMessage("my_tool", "cancelled-call"));
        var sink = new RecordingSink();

        // Dispose before yielding the UI thread: no chunk or Phase C dispatcher work can have run,
        // so cancellation is observed before anything is published.
        var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        model.Dispose();
        await model.HistoryLoaded;

        Assert.Empty(model.HistorySlots);
        Assert.Empty(model.SharedSlotByCallId);
        Assert.DoesNotContain(sink.Operations, op => op.Path == ChatOutputHtmlRenderer.HistoryContainerId);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryLoad_CancellationAfterChunkGeneration_DoesNotWaitForUiDispatch()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 500).Select(i => TextMessage(ChatRole.User, $"item {i}")));
        var sink = new RecordingSink();
        using var modelAssigned = new ManualResetEventSlim();
        ChatOutputHtmlModel? model = null;

        model = new ChatOutputHtmlModel(
            history,
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink,
            beforeDispatchHistoryChunk: () =>
            {
                modelAssigned.Wait();
                model!.Dispose();
            });

        modelAssigned.Set();
        await model.HistoryLoaded;

        Assert.Empty(model.HistorySlots);
        Assert.Empty(model.SharedSlotByCallId);
        Assert.DoesNotContain(sink.Operations, op => op.Path == ChatOutputHtmlRenderer.HistoryContainerId);
    }

    // ── Running-item structure tests (issue #893) ───────────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_Container_AppendsToRunningItemsContainer()
    {
        var running = new ObservableCollection<AgentChatRunningItem>();
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;
        sink.Clear();

        running.Add(new AgentChatRunningItem());

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputHtmlRenderer.RunningContainerId, op.Path);
        Assert.Equal(ChatOutputUpdateLocation.Append, op.Location);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.RunningItemId(0)}\"", op.Content);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.RunningItemContentsId(ChatOutputHtmlRenderer.RunningItemId(0))}\"", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_InnerTransformer_FirstMessage_AppendsToRunContentsId()
    {
        var runningItem = new AgentChatRunningItem();
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;
        sink.Clear();

        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "streamed"));

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(
            ChatOutputHtmlRenderer.RunningItemContentsId(ChatOutputHtmlRenderer.RunningItemId(0)),
            op.Path);
        Assert.Equal(ChatOutputUpdateLocation.Append, op.Location);
        Assert.Contains(">streamed<", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_InnerTransformer_UsesRunningMessageIds()
    {
        var runningItem = new AgentChatRunningItem();
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;
        sink.Clear();

        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "streamed"));

        var runId = ChatOutputHtmlRenderer.RunningItemId(0);
        var op = Assert.Single(sink.ContentOperations);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.RunningMessageId(runId, 0)}\"", op.Content);
        Assert.DoesNotContain("id=\"history-", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_InnerTransformer_SecondMessage_InsertsAfterFirstRunningMessage()
    {
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "first"));
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;
        sink.Clear();

        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "second"));

        var runId = ChatOutputHtmlRenderer.RunningItemId(0);
        // Two ops: the insert After, plus the #1222 header-suppression Replace on the new element.
        Assert.Equal(2, sink.ContentOperations.Count);
        var op = sink.ContentOperations[0];
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.RunningMessageId(runId, 0), op.Path);
        Assert.Contains(">second<", op.Content);
        Assert.Equal(ChatOutputUpdateLocation.Replace, sink.ContentOperations[1].Location);
        Assert.Contains("chat-header-suppressed", sink.ContentOperations[1].Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_ReInsert_TargetsRunningItemsContainer()
    {
        var runningItem = new AgentChatRunningItem();
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;
        sink.Clear();

        model.NotifyInsertionFailed(ChatOutputHtmlRenderer.RunningItemId(0));

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputHtmlRenderer.RunningContainerId, op.Path);
        Assert.Equal(ChatOutputUpdateLocation.Append, op.Location);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.RunningItemId(0)}\"", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_Update_ReplacesContentsDiv_NotRunWrapper()
    {
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "old stream"));
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;
        sink.Clear();

        // Replace the running item with a new source (fresh Items collection).
        var replacement = new AgentChatRunningItem();
        replacement.Items.Add(TextMessage(ChatRole.Assistant, "new stream"));
        running[0] = replacement;

        var runId = ChatOutputHtmlRenderer.RunningItemId(0);
        var contentsId = ChatOutputHtmlRenderer.RunningItemContentsId(runId);
        var ops = sink.ContentOperations;

        // The contents div is replaced; the run wrapper element itself is never replaced or removed.
        Assert.Contains(ops, op => op.Path == contentsId && op.Location == ChatOutputUpdateLocation.Replace);
        Assert.DoesNotContain(ops, op => op.Path == runId);
        Assert.Contains(ops, op => op.Content.Contains(">new stream<"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task RunningItem_Removal_RemovesRunWrapperAndDisposesInnerTransformer()
    {
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "streamed"));
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;
        sink.Clear();

        running.RemoveAt(0);

        var removeOp = Assert.Single(sink.ContentOperations);
        Assert.Equal("remove", removeOp.Kind);
        Assert.Equal(ChatOutputHtmlRenderer.RunningItemId(0), removeOp.Path);
        sink.Clear();

        // The inner transformer is disposed: further additions to the removed item emit nothing.
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "after removal"));

        Assert.Empty(sink.ContentOperations);
    }

    // ── Regression tests for the redesign's bug classes (issue #893) ────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Regression_BugA_ToolGroupPromotion_UsesHistoryIndex_NotLocalIndex()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 200).Select(i => TextMessage(ChatRole.User, $"text {i}")));
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(ToolCallMessage("tool_a", "c1"));
        history.Add(ToolCallMessage("tool_b", "c2"));

        var promoteOp = sink.ContentOperations.First(op =>
            op.Location == ChatOutputUpdateLocation.Replace && op.Content.Contains("chat-tool-group"));
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(200), promoteOp.Path);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.ToolGroupId(200)}\"", promoteOp.Content);
        Assert.DoesNotContain(sink.Operations, op =>
            op.Path.Contains(ChatOutputHtmlRenderer.ToolGroupId(0)) ||
            op.Content.Contains($"id=\"{ChatOutputHtmlRenderer.ToolGroupId(0)}\""));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Regression_BugB_NoInsertAfterDivs_EmittedAnywhere()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 210).Select(i => TextMessage(ChatRole.User, $"text {i}")));
        history.Add(ToolCallMessage("tool_a", "c1"));
        history.Add(ToolCallMessage("tool_b", "c2"));
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "streaming"));
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, running, () => true, sink);
        await model.HistoryLoaded;

        history.Add(ToolCallMessage("tool_c", "c3"));
        history.Add(TextMessage(ChatRole.Assistant, "after group"));
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "more streaming"));

        Assert.DoesNotContain(sink.Operations, op =>
            op.Path.Contains("insert-after") || op.Content.Contains("insert-after"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Regression_BugD_OnInsert_UsesContainerPath_NotLoadAfterHardcode()
    {
        var runningItem = new AgentChatRunningItem();
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
        await model.HistoryLoaded;
        sink.Clear();

        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "first running message"));

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(
            ChatOutputHtmlRenderer.RunningItemContentsId(ChatOutputHtmlRenderer.RunningItemId(0)),
            op.Path);
        Assert.DoesNotContain(sink.Operations, o => o.Path == "load-after");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Regression_BugE_CrossChunkToolResult_MatchesCall()
    {
        // The call lands in the older chunk while its result lands in the newer chunk.
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 10).Select(i => TextMessage(ChatRole.User, $"text {i}")));
        history.Add(ToolCallMessage("my_tool", "cross-chunk-call"));
        foreach (var i in Enumerable.Range(11, 289))
        {
            history.Add(TextMessage(ChatRole.User, $"text {i}"));
        }

        history.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Tool,
            Contents = [new FunctionResultContent("cross-chunk-call", "\"cross-chunk result data\"")],
        });
        foreach (var i in Enumerable.Range(301, 99))
        {
            history.Add(TextMessage(ChatRole.User, $"text {i}"));
        }

        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var allContent = string.Concat(sink.ContentOperations.Select(op => op.Content));

        // The result is injected (nested under its call), never rendered standalone.
        Assert.Contains("cross-chunk result data", allContent);
        Assert.DoesNotContain($"id=\"{ChatOutputHtmlRenderer.MessageId(300)}\"", allContent);

        // The chunk containing the call renders it with the injected result.
        var chunkWithCall = sink.ContentOperations.First(op => op.Content.Contains("my_tool"));
        Assert.Contains("cross-chunk result data", chunkWithCall.Content);
        Assert.Contains("chat-tool-result", chunkWithCall.Content);
    }

    // ── Live insertion anchor regression tests (issue #900) ────────────────────

    private static AgentChatHistoryItem DiagnosticMessage(string text)
        => new() { Role = AgentChatHistoryItem.DiagnosticChatRole, Contents = [new TextContent(text)] };

    [AvaloniaFact(Timeout = 15_000)]
    public async Task UserMessage_AddedWhileRunningItemActive_InsertsIntoHistoryDom()
    {
        var history = new ObservableCollection<AgentChatHistoryItem> { TextMessage(ChatRole.Assistant, "earlier answer") };
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "streaming..."));
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, running, () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(TextMessage(ChatRole.User, "sent while agent is running"));

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), op.Path);
        Assert.Contains("sent while agent is running", op.Content);
        Assert.DoesNotContain(sink.Operations, o =>
            o.Path.Contains("insert-after") || o.Content.Contains("insert-after"));
    }

    // ── Issue #1123: grouped-member streaming must not nest tools inside tools ─

    private static AgentChatHistoryItem MultiToolCallMessage(params (string CallId, string Name)[] calls)
        => new()
        {
            Role = ChatRole.Assistant,
            Contents = calls.Select(c => (AIContent)new FunctionCallContent(c.CallId, c.Name)).ToList(),
        };

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GroupedMember_StreamsSecondToolCall_DoesNotNestToolGroupInsideToolGroup()
    {
        // Reproduces issue #1123: a message that was grouped while it had a single call later
        // streams a 2nd FunctionCallContent. The equal-category fast-path must not leave a nested
        // content-level tools(…) wrapper inside the outer message-level group body.
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("powershell", "c1"),
            MultiToolCallMessage(("c2", "report_intent")),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        // Stream: the grouped member (index 1) grows from 1 call to 2 calls.
        history[1] = MultiToolCallMessage(("c2", "report_intent"), ("c3", "powershell"));

        var contentOps = sink.ContentOperations;

        // Assert: no operation emits a content-level wrapper — the flat items are used instead.
        Assert.DoesNotContain(contentOps, op => op.Content.Contains("chat-tool-group-wrapper"));

        // The streaming delta must emit at least one flat chat-tool-group-item — the newly-added
        // 2nd call is appended as its own binding. The pre-existing 1st call binding is unchanged
        // (its key is stable), so this delta contains a single new item, not a wrapper.
        Assert.Contains(contentOps, op => op.Content.Contains("chat-tool-group-item"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GroupedMember_StreamsSecondToolCall_RefreshesOuterGroupSummaryToolNames()
    {
        // After a grouped member gains a 2nd call, the outer tools(…) summary must reflect the
        // updated distinct tool-name set and total call count.
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("powershell", "c1"),
            MultiToolCallMessage(("c2", "report_intent")),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history[1] = MultiToolCallMessage(("c2", "report_intent"), ("c3", "workspaces_entity_get"));

        var summaryOps = sink.ContentOperations
            .Where(op => op.Location == ChatOutputUpdateLocation.Replace && op.Path.Contains("summary"))
            .ToList();
        Assert.NotEmpty(summaryOps);
        var lastSummary = summaryOps.Last();

        // Total calls across the group = powershell + report_intent + workspaces_entity_get = 3.
        Assert.Contains("3 calls", lastSummary.Content);
        Assert.Contains("powershell", lastSummary.Content);
        Assert.Contains("report_intent", lastSummary.Content);
        Assert.Contains("workspaces_entity_get", lastSummary.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GroupedMember_StreamsAdditionalToolCall_FromMultiBackToSingle_StillNoNestedWrapper()
    {
        // Reverse boundary crossing: a grouped member starts with 2 calls and streams down to 1.
        // No content-level wrapper must be emitted at any point.
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("powershell", "c1"),
            MultiToolCallMessage(("c2", "tool_a"), ("c3", "tool_b")),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        // Neither the initial insertion nor any subsequent update should emit a wrapper for a
        // grouped member.
        Assert.DoesNotContain(sink.ContentOperations, op => op.Content.Contains("chat-tool-group-wrapper"));

        sink.Clear();
        history[1] = MultiToolCallMessage(("c2", "tool_a"));

        Assert.DoesNotContain(sink.ContentOperations, op => op.Content.Contains("chat-tool-group-wrapper"));

        // Summary now reflects 2 total calls (powershell + tool_a).
        var summaryOp = sink.ContentOperations.LastOrDefault(op =>
            op.Location == ChatOutputUpdateLocation.Replace && op.Path.Contains("summary"));
        Assert.NotNull(summaryOp);
        Assert.Contains("2 calls", summaryOp!.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AssistantMessageBetweenToolBatches_ProducesTwoSiblingToolGroups_NotNested()
    {
        // A visible assistant text between two tool batches yields two independent (sibling) tool
        // groups. A visible non-tool message must always close the open group.
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
            ToolCallMessage("write_file", "c2"),
            TextMessage(ChatRole.Assistant, "reply"),
            ToolCallMessage("list_files", "c3"),
            ToolCallMessage("grep", "c4"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var allContent = string.Concat(sink.ContentOperations.Select(op => op.Content));

        // Exactly two chat-tool-group top-level elements; no nesting of one inside the other.
        var groupMatches = System.Text.RegularExpressions.Regex.Matches(allContent, "class=\"chat-content chat-tool-group\"");
        Assert.Equal(2, groupMatches.Count);
        Assert.DoesNotContain("chat-tool-group-wrapper", allContent);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ConsecutiveToolCalls_NoInterleaving_CoalesceIntoSingleGroup()
    {
        // Guard against over-fixing: three single-call tool messages in a row still coalesce
        // into exactly one message-level group, no nested wrappers.
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
            ToolCallMessage("tool_c", "c3"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var allContent = string.Concat(sink.ContentOperations.Select(op => op.Content));
        var groupMatches = System.Text.RegularExpressions.Regex.Matches(allContent, "class=\"chat-content chat-tool-group\"");
        Assert.Single(groupMatches);
        Assert.DoesNotContain("chat-tool-group-wrapper", allContent);

        Assert.Contains("3 calls", allContent);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OrdinaryStreamingToolCallText_StillUsesFastPath_NoRegression()
    {
        // Ordinary equal-category streaming (a grouped single-call member gets its argument text
        // updated but the call count stays at 1) must still take the fast path: the target model
        // is updated, but no promotion, no new group, and — because the composition unchanged —
        // the outer summary is refreshed only when function-call state actually changed.
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
            ToolCallMessage("write_file", "c2"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        // Replace the second grouped member with a functionally-equivalent single-call message
        // (still tool-call-only, still one FunctionCallContent).
        history[1] = ToolCallMessage("write_file", "c2");

        // No wrapper anywhere in the fast-path ops.
        Assert.DoesNotContain(sink.ContentOperations, op => op.Content.Contains("chat-tool-group-wrapper"));
        // No new message-level group was created (no chat-tool-group-body in a Replace on msg-0).
        Assert.DoesNotContain(sink.ContentOperations, op =>
            op.Location == ChatOutputUpdateLocation.Replace &&
            op.Path == ChatOutputHtmlRenderer.MessageId(0) &&
            op.Content.Contains("chat-tool-group-body"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task StreamedToolCalls_OutOfChunkOrder_RenderFlatSiblingGroupStructure()
    {
        // Simulates the intermittent streamed-order case: the grouped member's 2nd call arrives
        // in a later chunk (rather than as part of the original message). The final DOM must
        // remain flat/sibling — no nested wrapper.
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("first_call", "c1"),
            MultiToolCallMessage(("c2", "second_call")),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        // Chunk 2: grow member[1] with a 2nd call.
        history[1] = MultiToolCallMessage(("c2", "second_call"), ("c3", "third_call"));

        // Chunk 3: grow member[1] with a 3rd call.
        history[1] = MultiToolCallMessage(("c2", "second_call"), ("c3", "third_call"), ("c4", "fourth_call"));

        // Chunk 4: replace one of them with new arguments to trigger another equal-category update.
        history[1] = MultiToolCallMessage(
            ("c2", "second_call"),
            ("c3", "third_call_renamed"),
            ("c4", "fourth_call"));

        // Assert: never a wrapper inside a body across all ops.
        Assert.DoesNotContain(sink.ContentOperations, op => op.Content.Contains("chat-tool-group-wrapper"));

        // Final summary reflects the updated tool-name set and total (4) calls.
        var summaryOp = sink.ContentOperations.Last(op =>
            op.Location == ChatOutputUpdateLocation.Replace && op.Path.Contains("summary"));
        Assert.Contains("4 calls", summaryOp.Content);
        Assert.Contains("first_call", summaryOp.Content);
        Assert.Contains("second_call", summaryOp.Content);
        Assert.Contains("third_call_renamed", summaryOp.Content);
        Assert.Contains("fourth_call", summaryOp.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GroupedMember_WithMultipleCalls_LoadedInBulk_RendersFlatItemsInsideGroupBody()
    {
        // Headless / no-visual-tree bulk-load variant: a grouped member with multiple calls
        // built via the HistoryRenderPlan path must also render flat, not nested. This exercises
        // the AppendItemStateOnly plan branch (issue #1123).
        var snapshot = new List<AgentChatHistoryItem>
        {
            ToolCallMessage("first_tool", "c1"),
            MultiToolCallMessage(("c2", "second_tool"), ("c3", "third_tool")),
            ToolCallMessage("fourth_tool", "c4"),
        };
        var sink = new RecordingSink();
        var plan = ChatOutputHtmlModel.BuildHistoryRenderPlan(snapshot, sink, () => true);
        var planHtml = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        // Exactly one message-level group; no nested content-level wrapper anywhere.
        var groupMatches = System.Text.RegularExpressions.Regex.Matches(planHtml, "class=\"chat-content chat-tool-group\"");
        Assert.Single(groupMatches);
        Assert.DoesNotContain("chat-tool-group-wrapper", planHtml);

        // Group summary sees all four calls and all distinct tool names.
        Assert.Contains("4 calls", planHtml);
        Assert.Contains("first_tool", planHtml);
        Assert.Contains("second_tool", planHtml);
        Assert.Contains("third_tool", planHtml);
        Assert.Contains("fourth_tool", planHtml);

        // The nested wrapper is absent: the second message's two calls appear as sibling
        // chat-tool-group-item elements inside its chat-message body, which is inside the group body.
        var itemMatches = System.Text.RegularExpressions.Regex.Matches(planHtml, "chat-tool-group-item");
        Assert.True(itemMatches.Count >= 4, $"Expected at least 4 flat items in bulk-loaded group; got {itemMatches.Count}.");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task UserMessage_AddedAfterRunningItemCompletes_InsertsIntoHistoryDom()
    {
        var history = new ObservableCollection<AgentChatHistoryItem> { TextMessage(ChatRole.Assistant, "earlier answer") };
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "streaming..."));
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, running, () => true, sink);
        await model.HistoryLoaded;

        running.Remove(runningItem);
        sink.Clear();

        history.Add(TextMessage(ChatRole.User, "sent after completion"));

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), op.Path);
        Assert.Contains("sent after completion", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task UserMessage_AddedToHistory_AnchorsAfterLastTopLevelHistoryElement()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "one"),
            TextMessage(ChatRole.Assistant, "two"),
            TextMessage(ChatRole.User, "three"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(TextMessage(ChatRole.User, "latest question"));

        // Two ops: the insert After, plus the #1222 header-suppression Replace on the new element
        // (same user role as the predecessor history-2).
        Assert.Equal(2, sink.ContentOperations.Count);
        var op = sink.ContentOperations[0];
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(2), op.Path);
        Assert.Contains("latest question", op.Content);
        Assert.Equal(ChatOutputUpdateLocation.Replace, sink.ContentOperations[1].Location);
        Assert.Contains("chat-header-suppressed", sink.ContentOperations[1].Content);
    }

    [Fact]
    public void LiveTransformer_LastPreloadedItemIsToolResultOnly_FirstLiveItemAnchorsToNearestDomSlot()
    {
        var source = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("my_tool", "c1"),
            ToolResultMessage("c1"),
        };
        var sink = new RecordingSink();
        var (slots, sharedMap) = PreloadPlan(source, sink);
        Assert.False(slots[1].HasDomElement);
        using var transformer = MakeLiveTransformer(source, slots, sink, sharedMap, preloadedCount: 2);
        sink.Clear();

        source.Add(TextMessage(ChatRole.User, "first live message"));

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), op.Path);
        Assert.Contains("first live message", op.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Reload_HistoryWithToolResultOnlyTail_RendersAllMessagesIncludingUserMessage()
    {
        // Simulates a WebView reload: a fresh model renders the full history, including a user
        // message that follows a tool-result-only item (which itself has no standalone element).
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
            ToolResultMessage("c1"),
            TextMessage(ChatRole.User, "user question after tool run"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputHtmlRenderer.HistoryContainerId, op.Path);
        Assert.Contains("user question after tool run", op.Content);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.MessageId(2)}\"", op.Content);

        // The result is nested under its call, never rendered as a standalone element.
        Assert.DoesNotContain($"id=\"{ChatOutputHtmlRenderer.MessageId(1)}\"", op.Content);
        Assert.Contains("chat-tool-result", op.Content);
    }

    // ── Buffered history events replayed at Phase C (issue #901, Fix A) ────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryReplaceDuringLoading_IsAppliedAfterPhaseC()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.Assistant, "original-content"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        // Replace while historyLoading is still true (Phase B/C callbacks only run once awaited).
        history[0] = TextMessage(ChatRole.Assistant, "replaced-content");

        await model.HistoryLoaded;

        Assert.Contains(sink.ContentOperations, op => op.Content.Contains("replaced-content"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryRemoveDuringLoading_IsAppliedAfterPhaseC()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "keep zero"),
            TextMessage(ChatRole.Assistant, "removed one"),
            TextMessage(ChatRole.User, "keep two"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        history.RemoveAt(1);

        await model.HistoryLoaded;

        Assert.Contains(sink.Operations, op => op.Kind == "remove" && op.Path == ChatOutputHtmlRenderer.MessageId(1));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryInsertAtMiddleDuringLoading_IsAppliedAfterPhaseC()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "first"),
            TextMessage(ChatRole.Assistant, "second"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        history.Insert(1, TextMessage(ChatRole.User, "middle-inserted"));

        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations, op => op.Content.Contains("middle-inserted"));
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), op.Path);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryTailAddDuringLoading_IsAppliedAfterPhaseC_ExactlyOnce()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "first"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        history.Add(TextMessage(ChatRole.Assistant, "tail-added"));

        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations, op => op.Content.Contains("tail-added"));
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), op.Path);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task StreamingUserMessageThenAgentReply_DuringPhaseB_BothVisibleAfterPhaseC()
    {
        // 250 items → two Phase B chunks. During loading, a user message is appended, an
        // assistant placeholder is appended, and the placeholder is replaced by streamed content.
        var history = new ObservableCollection<AgentChatHistoryItem>(
            Enumerable.Range(0, 250).Select(i => TextMessage(ChatRole.User, $"old {i}")));
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        history.Add(TextMessage(ChatRole.User, "user question mid-load"));
        history.Add(TextMessage(ChatRole.Assistant, "streaming partial"));
        history[251] = TextMessage(ChatRole.Assistant, "full streamed reply");

        await model.HistoryLoaded;

        var userOp = Assert.Single(sink.ContentOperations, op => op.Content.Contains("user question mid-load"));
        Assert.Equal(ChatOutputUpdateLocation.After, userOp.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(249), userOp.Path);
        Assert.Contains(sink.ContentOperations, op => op.Content.Contains("full streamed reply"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task UserMessage_AddedWhileRunningItemActive_DuringHistoryLoading_AppearsAfterPhaseC()
    {
        var history = new ObservableCollection<AgentChatHistoryItem> { TextMessage(ChatRole.Assistant, "earlier answer") };
        var runningItem = new AgentChatRunningItem();
        runningItem.Items.Add(TextMessage(ChatRole.Assistant, "streaming..."));
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, running, () => true, sink);

        history.Add(TextMessage(ChatRole.User, "sent while loading"));

        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations, op => op.Content.Contains("sent while loading"));
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), op.Path);
    }

    // ── History-side NotifyInsertionFailed recovery (issue #901, Fix B) ────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NotifyInsertionFailed_WithHistoryId_ReInsertsAffectedSlot()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "item zero"),
            TextMessage(ChatRole.Assistant, "item one"),
            TextMessage(ChatRole.User, "item two"),
            TextMessage(ChatRole.Assistant, "item three"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        model.NotifyInsertionFailed(ChatOutputHtmlRenderer.MessageId(2));

        // The tail from the failed slot onward is removed and re-emitted; the first re-inserted
        // slot anchors on the last still-attached element before the repair range.
        Assert.Contains(sink.Operations, op => op.Kind == "remove" && op.Path == ChatOutputHtmlRenderer.MessageId(2));
        Assert.Contains(sink.Operations, op => op.Kind == "remove" && op.Path == ChatOutputHtmlRenderer.MessageId(3));
        var slot2Op = Assert.Single(sink.ContentOperations, op => op.Content.Contains("item two"));
        Assert.Equal(ChatOutputUpdateLocation.After, slot2Op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(1), slot2Op.Path);
        var slot3Op = Assert.Single(sink.ContentOperations, op => op.Content.Contains("item three"));
        Assert.Equal(ChatOutputUpdateLocation.After, slot3Op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(2), slot3Op.Path);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NotifyInsertionFailed_WithFirstHistoryId_ReAppendsIntoHistoryContainer()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "only item"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        model.NotifyInsertionFailed(ChatOutputHtmlRenderer.MessageId(0));

        var op = Assert.Single(sink.ContentOperations, op => op.Content.Contains("only item"));
        Assert.Equal(ChatOutputUpdateLocation.Append, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.HistoryContainerId, op.Path);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NotifyInsertionFailed_WithToolGroupId_ReInsertsAffectedGroup()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "intro"),
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        model.NotifyInsertionFailed(ChatOutputHtmlRenderer.ToolGroupId(1));

        // The whole group is rebuilt: removed once, first member re-inserted standalone after the
        // intro message, then promoted back into a group when the second member is re-inserted.
        Assert.Contains(sink.Operations, op => op.Kind == "remove" && op.Path == ChatOutputHtmlRenderer.ToolGroupId(1));
        var firstMemberOp = sink.ContentOperations.First(op =>
            op.Location == ChatOutputUpdateLocation.After && op.Path == ChatOutputHtmlRenderer.MessageId(0));
        Assert.Contains("tool_a", firstMemberOp.Content);
        var promoteOp = sink.ContentOperations.First(op =>
            op.Location == ChatOutputUpdateLocation.Replace && op.Path == ChatOutputHtmlRenderer.MessageId(1));
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.ToolGroupId(1)}\"", promoteOp.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NotifyInsertionFailed_WithUnknownId_EmitsNothing()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "item zero"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        model.NotifyInsertionFailed("history-99");
        model.NotifyInsertionFailed("no-such-element");

        Assert.Empty(sink.Operations);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryAdd_WhenPreviousElementMissingFromDom_RecoversViaNotifyInsertionFailed()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "item zero"),
            TextMessage(ChatRole.Assistant, "item one"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        // The live tail Add targets history-1; the host then reports that anchor missing.
        history.Add(TextMessage(ChatRole.User, "new tail item"));
        var addOp = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(1), addOp.Path);
        sink.Clear();

        model.NotifyInsertionFailed(ChatOutputHtmlRenderer.MessageId(1));

        // Recovery re-emits both the missing anchor slot and the dropped payload.
        var anchorOp = Assert.Single(sink.ContentOperations, op => op.Content.Contains("item one"));
        Assert.Equal(ChatOutputUpdateLocation.After, anchorOp.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), anchorOp.Path);
        var payloadOp = Assert.Single(sink.ContentOperations, op => op.Content.Contains("new tail item"));
        Assert.Equal(ChatOutputUpdateLocation.After, payloadOp.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(1), payloadOp.Path);
    }

    // ── Tests for issue #957: diagnostics always visible ────────────────────────

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatMessageHtmlModel_Constructor_DoesNotAcceptIsDiagnosticsVisibleParameter()
    {
        // This test verifies that ChatMessageHtmlModel constructor signature no longer accepts
        // isDiagnosticsVisible parameter. The test will fail to compile if the parameter exists.
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = [new TextContent("diagnostic detail")],
            },
        };
        var sink = new RecordingSink();
        
        // This constructor call must not have isDiagnosticsVisible parameter
        using var model = new ChatOutputHtmlModel(
            history,
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink);
        await model.HistoryLoaded;

        Assert.NotEmpty(sink.ContentOperations);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ChatOutputHtmlModel_DiagnosticMessages_AlwaysRendered()
    {
        // Verifies that diagnostic messages are always rendered unconditionally
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = ChatRole.User,
                Contents = [new TextContent("user message")],
            },
            new()
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = [new TextContent("diagnostic detail")],
            },
            new()
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("assistant response")],
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            history,
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink);
        await model.HistoryLoaded;

        var op = Assert.Single(sink.ContentOperations);
        Assert.Contains("user message", op.Content);
        Assert.Contains("diagnostic detail", op.Content);
        Assert.Contains("assistant response", op.Content);
    }

    private sealed class CyclicPayload
    {
        // Self-reference makes JsonSerializer.SerializeToElement throw JsonException (object cycle),
        // reproducing the non-serializable tool argument that regressed history loading in #1008.
        public CyclicPayload Self => this;
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task HistoryLoad_WithToolCallContainingNonSerializableArguments_CompletesAndRendersOtherMessages()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new() { Role = ChatRole.User, Contents = [new TextContent("before-tool")] },
            new()
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent(
                        "call-1",
                        "myTool",
                        new Dictionary<string, object?> { ["x"] = new CyclicPayload() }),
                ],
            },
            new() { Role = ChatRole.User, Contents = [new TextContent("after-tool")] },
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            history,
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink);

        // Must complete (Phase C runs) rather than faulting and leaving history empty.
        await model.HistoryLoaded;

        var prependOps = sink.ContentOperations
            .Where(operation => operation.Location == ChatOutputUpdateLocation.Prepend
                             && operation.Path == ChatOutputHtmlRenderer.HistoryContainerId)
            .ToList();
        Assert.NotEmpty(prependOps);

        var allRenderedHtml = string.Concat(prependOps.Select(operation => operation.Content));
        Assert.Contains("before-tool", allRenderedHtml, StringComparison.Ordinal);
        Assert.Contains("after-tool", allRenderedHtml, StringComparison.Ordinal);
    }

    // ── Issue #1042: streaming summary-replace retains expand/collapse toggle ──

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ToolCallGroupHtmlModel_SummaryReplaceOnAppend_RetainsExpandCollapseToggle()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        // Append a second tool call to trigger the streaming summary-replace path.
        history.Add(ToolCallMessage("write_file", "c2"));

        var summaryOp = sink.ContentOperations.FirstOrDefault(op =>
            op.Location == ChatOutputUpdateLocation.Replace && op.Path.Contains("summary"));
        Assert.NotNull(summaryOp);
        Assert.Contains("data-tool-expand-toggle", summaryOp!.Content, StringComparison.Ordinal);
    }

    // ── Issue #1225: coalesce consecutive assistant tool-call messages into a single element ─

    [Fact]
    public void ConsecutiveAssistantToolCalls_CoalesceIntoSingleChatMessageElement()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
            ToolCallMessage("tool_c", "c3"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        // Exactly one <div class="chat-message"> frame for the coalesced run.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "chat-message"));
        // One <details class="chat-tool-group"> inside.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "chat-tool-group\""));
        // Three tool-group-item sub-entries.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "chat-tool-group-item").Count);
    }

    [Fact]
    public void ConsecutiveAssistantToolCalls_EmitExactlyOneAssistantHeader()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
            ToolCallMessage("tool_c", "c3"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "chat-header"));
        Assert.Contains(">assistant<", html);
    }

    [Fact]
    public void CoalescedToolCalls_ListEachInvocationWithNameArgsAndResult()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
            new() { Role = ChatRole.Tool, Contents = [new FunctionResultContent("c1", "file contents")] },
            ToolCallMessage("write_file", "c2"),
            new() { Role = ChatRole.Tool, Contents = [new FunctionResultContent("c2", "ok")] },
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        // Each tool invocation present as its own chat-tool-group-item.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "chat-tool-group-item").Count);
        Assert.Contains("read_file", html);
        Assert.Contains("write_file", html);
        // Results injected.
        Assert.Contains("file contents", html);
    }

    [Fact]
    public void ToolCallRun_InterruptedByAssistantText_StartsNewCoalescedRun()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
            TextMessage(ChatRole.Assistant, "thinking..."),
            ToolCallMessage("tool_c", "c3"),
            ToolCallMessage("tool_d", "c4"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        // Two separate coalesced groups + one standalone text message = 3 chat-message frames.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "chat-message").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "chat-tool-group\"").Count);
        Assert.Contains("thinking...", html);
    }

    [Fact]
    public void ToolCallRun_InterruptedByUserMessage_StartsNewCoalescedRun()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
            TextMessage(ChatRole.User, "stop"),
            ToolCallMessage("tool_c", "c3"),
            ToolCallMessage("tool_d", "c4"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        // Two groups + one user message = 3 chat-message frames.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "chat-message").Count);
        Assert.Contains("chat-user-message", html);
    }

    [Fact]
    public void ToolCallRun_WithInterleavedResultOnlyMessages_StaysCoalesced()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            new() { Role = ChatRole.Tool, Contents = [new FunctionResultContent("c1", "result")] },
            ToolCallMessage("tool_b", "c2"),
            new() { Role = ChatRole.Tool, Contents = [new FunctionResultContent("c2", "result")] },
            ToolCallMessage("tool_c", "c3"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        // All three tool calls in one coalesced element.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "chat-message"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "chat-tool-group\""));
        Assert.Contains("3 calls", html);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task LiveTransformer_ConsecutiveToolCalls_ArrivingIncrementally_CoalesceIntoSingleElement()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(ToolCallMessage("tool_b", "c2"));
        history.Add(ToolCallMessage("tool_c", "c3"));

        // The Replace op creates the group with a single chat-message frame.
        var replaceOp = sink.ContentOperations.First(op =>
            op.Location == ChatOutputUpdateLocation.Replace && op.Content.Contains("chat-tool-group"));
        var messageMatches = System.Text.RegularExpressions.Regex.Matches(replaceOp.Content, "chat-message");
        Assert.Single(messageMatches);
        var headerMatches = System.Text.RegularExpressions.Regex.Matches(replaceOp.Content, "chat-header");
        Assert.Single(headerMatches);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task LiveTransformer_ToolCallRun_LosingAllButOneMember_UngroupsToStandaloneMessage()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        // Remove one member, leaving a single survivor that must ungroup to standalone.
        history.RemoveAt(1);

        // The surviving member is re-emitted as a standalone chat-message with its own header.
        var replaceOp = sink.ContentOperations.First(op =>
            op.Location == ChatOutputUpdateLocation.Replace);
        Assert.Contains("chat-message", replaceOp.Content);
        Assert.Contains("chat-header", replaceOp.Content);
        Assert.Contains("tool_a", replaceOp.Content);
        // No group wrapper on the standalone message.
        Assert.DoesNotContain("chat-tool-group-body", replaceOp.Content);
    }

    [Fact]
    public void GenerateHistoryChunk_ConsecutiveToolCalls_EmitsSingleMessageFrameWithGroupBody()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "go"),
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
            ToolCallMessage("tool_c", "c3"),
            TextMessage(ChatRole.Assistant, "done"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        // 3 chat-message frames total: user, coalesced group, assistant text.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "chat-message").Count);
        // Exactly one chat-tool-group inside the coalesced frame.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "chat-tool-group\""));
        // Group has 3 calls.
        Assert.Contains("3 calls", html);
        // Group header is "assistant".
        var groupId = ChatOutputHtmlRenderer.ToolGroupId(1);
        Assert.Contains($"id=\"{groupId}\"", html);
    }

    // -----------------------------------------------------------------------------------------
    // #1222 — consecutive same-role items collapse under a single role header.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void BuildHistoryRenderPlan_WhenConsecutiveAssistantItems_OnlyFirstEmitsVisibleRoleHeader()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.Assistant, "one"),
            TextMessage(ChatRole.Assistant, "two"),
            TextMessage(ChatRole.Assistant, "three"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        // Exactly one visible role header, plus two suppressed placeholders (hidden, no <span>).
        var visibleHeaders = System.Text.RegularExpressions.Regex.Matches(html, "<div class=\"chat-header\"").Count;
        var suppressedHeaders = System.Text.RegularExpressions.Regex.Matches(html, "chat-header-suppressed").Count;
        Assert.Equal(1, visibleHeaders);
        Assert.Equal(2, suppressedHeaders);
    }

    [Fact]
    public void BuildHistoryRenderPlan_WhenRolesAlternate_EachItemEmitsItsOwnRoleHeader()
    {
        var snapshot = new List<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "u1"),
            TextMessage(ChatRole.Assistant, "a1"),
            TextMessage(ChatRole.User, "u2"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "<div class=\"chat-header\"").Count);
        Assert.DoesNotContain("chat-header-suppressed", html);
    }

    [Fact]
    public void BuildHistoryRenderPlan_WhenConsecutiveToolCallGroupsAndTextAllAssistant_OneAssistantHeaderWrapsRun()
    {
        // Screenshot scenario: user question then several tool-call-only assistant messages then
        // an assistant text summary. The run of assistant items must show one assistant header.
        var snapshot = new List<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "please investigate"),
            ToolCallMessage("read_file", "c1"),
            TextMessage(ChatRole.Assistant, "summary"),
        };
        var sink = new RecordingSink();
        var plan = BuildPlan(snapshot, sink);

        var html = ChatOutputHtmlModel.GenerateHistoryChunk(plan, 0, snapshot.Count);

        // One user header + one assistant header + one suppressed assistant header (for "summary").
        var userHeader = System.Text.RegularExpressions.Regex.Matches(html, "<span>user</span>").Count;
        var assistantHeader = System.Text.RegularExpressions.Regex.Matches(html, "<span>assistant</span>").Count;
        Assert.Equal(1, userHeader);
        Assert.Equal(1, assistantHeader);
        Assert.Contains("chat-header-suppressed", html);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task LiveInsert_WhenPredecessorSameRole_EmitsHeaderSuppressionOnNewSlot()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.Assistant, "first"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(TextMessage(ChatRole.Assistant, "second"));

        // The reconciliation pass fires a Replace op on the new element's -header placeholder.
        var suppression = sink.ContentOperations
            .SingleOrDefault(op => op.Location == ChatOutputUpdateLocation.Replace
                && op.Path == ChatOutputHtmlRenderer.HeaderId(ChatOutputHtmlRenderer.MessageId(1)));
        Assert.NotNull(suppression);
        Assert.Contains("chat-header-suppressed", suppression!.Content);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task LiveInsert_WhenPredecessorDifferentRole_DoesNotEmitHeaderSuppression()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.Assistant, "first"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        await model.HistoryLoaded;
        sink.Clear();

        history.Add(TextMessage(ChatRole.User, "second"));

        Assert.DoesNotContain(sink.ContentOperations, op =>
            op.Location == ChatOutputUpdateLocation.Replace && op.Content.Contains("chat-header-suppressed"));
    }

    [Fact]
    public void RenderHeader_WhenSuppressedTrue_EmitsHiddenPlaceholderWithStableId()
    {
        var html = ChatOutputHtmlRenderer.RenderHeader("msg-1", "assistant", timestamp: null, suppressed: true);

        Assert.Contains("id=\"msg-1-header\"", html);
        Assert.Contains("chat-header-suppressed", html);
        Assert.Contains("hidden", html);
        Assert.DoesNotContain("<span>assistant</span>", html);
    }

    [Fact]
    public void RenderHeader_WhenSuppressedFalse_EmitsFullHeaderAsBefore()
    {
        var html = ChatOutputHtmlRenderer.RenderHeader("msg-1", "assistant");

        Assert.Contains("<span>assistant</span>", html);
        Assert.DoesNotContain("chat-header-suppressed", html);
    }
}
