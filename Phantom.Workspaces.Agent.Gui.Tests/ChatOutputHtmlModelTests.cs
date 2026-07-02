using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatOutputHtmlModelTests
{
    private sealed record Operation(string Kind, string Path, ChatOutputUpdateLocation Location, string Content);

    private sealed class RecordingSink : IChatOutputHtmlSink
    {
        public List<Operation> Operations { get; } = [];

        public List<Operation> ContentOperations
            => this.Operations.Where(operation => operation.Kind is "update" or "remove").ToList();

        public int ScrollCount => this.Operations.Count(operation => operation.Kind == "scroll");

        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
            => this.Operations.Add(new Operation("update", path, location, content));

        public void RemoveContent(string path)
            => this.Operations.Add(new Operation("remove", path, ChatOutputUpdateLocation.Replace, string.Empty));

        public void ScrollToBottom()
            => this.Operations.Add(new Operation("scroll", string.Empty, ChatOutputUpdateLocation.Replace, string.Empty));

        public void Clear() => this.Operations.Clear();
    }

    private static AgentChatHistoryItem TextMessage(ChatRole role, string text)
        => new() { Role = role, Contents = [new TextContent(text)] };

    [Fact]
    public void InitialHistory_EmitsOneAppendPerMessage_IntoHistoryContainer()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "hello"),
            TextMessage(ChatRole.Assistant, "hi there"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        var appends = sink.ContentOperations;
        Assert.Equal(2, appends.Count);
        Assert.Equal(ChatOutputUpdateLocation.Append, appends[0].Location);
        Assert.Equal(ChatOutputHtmlRenderer.HistoryContainerId, appends[0].Path);
        Assert.Equal(ChatOutputUpdateLocation.After, appends[1].Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), appends[1].Path);
        Assert.Contains("chat-message", appends[0].Content);
        Assert.Contains(">hello<", appends[0].Content);
        Assert.Contains("chat-user-message", appends[0].Content);
        Assert.Contains("chat-assistant-message", appends[1].Content);
        Assert.True(sink.ScrollCount >= 1);
    }

    [Fact]
    public void AddingMessage_AppendsAfterPreviousMessage()
    {
        var history = new ObservableCollection<AgentChatHistoryItem> { TextMessage(ChatRole.User, "first") };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        sink.Clear();

        history.Add(TextMessage(ChatRole.Assistant, "second"));

        var operation = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.After, operation.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), operation.Path);
        Assert.Contains(">second<", operation.Content);
    }

    [Fact]
    public void StreamingUpdate_WhenLeadingContentUnchanged_OnlyEmitsForChangedContent()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("stable")] },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
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

    [Fact]
    public void StreamingUpdate_WhenLastContentChanges_ReplacesThatContentById()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("partial")] },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
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

    [Fact]
    public void RemovingMessage_EmitsRemoveByElementId()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "keep"),
            TextMessage(ChatRole.Assistant, "drop"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        sink.Clear();

        history.RemoveAt(1);

        var operation = Assert.Single(sink.ContentOperations);
        Assert.Equal("remove", operation.Kind);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(1), operation.Path);
    }

    [Fact]
    public void ReasoningHidden_DoesNotRenderReasoningContent_UntilToggledOn()
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

        var initial = Assert.Single(sink.ContentOperations);
        Assert.DoesNotContain("thinking", initial.Content);
        Assert.Contains("answer", initial.Content);

        sink.Clear();
        reasoningVisible = true;
        model.Refresh();

        Assert.Contains(sink.ContentOperations, operation => operation.Content.Contains("thinking"));
    }

    [Fact]
    public void DiagnosticsHidden_DoesNotRenderDiagnosticContent_UntilToggledOn()
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
        var diagnosticsVisible = false;
        using var model = new ChatOutputHtmlModel(
            history,
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink,
            isDiagnosticsVisible: () => diagnosticsVisible);

        var initial = Assert.Single(sink.ContentOperations);
        Assert.DoesNotContain("diagnostic detail", initial.Content);

        sink.Clear();
        diagnosticsVisible = true;
        model.Refresh();

        Assert.Contains(sink.ContentOperations, operation => operation.Content.Contains("diagnostic detail"));
    }

    [Fact]
    public void RunningItem_RendersContainerThenAppendsMessagesIntoIt()
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

        var operations = sink.ContentOperations;
        Assert.Equal(2, operations.Count);

        // First the empty running container appended into the running region.
        Assert.Equal(ChatOutputHtmlRenderer.RunningContainerId, operations[0].Path);
        Assert.Contains(ChatOutputHtmlRenderer.RunningItemId(0), operations[0].Content);

        // Then the message appended into that container.
        Assert.Equal(ChatOutputHtmlRenderer.RunningItemContentsId(ChatOutputHtmlRenderer.RunningItemId(0)), operations[1].Path);
        Assert.Contains(">working<", operations[1].Content);
    }

    [Fact]
    public void HtmlEscape_EscapesMarkupInMessageText()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.User, "<script>alert('x')</script>"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

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

    [Fact]
    public void Update_EmitsNoOperations_WhenSourceIsReferenceEqual()
    {
        var item = TextMessage(ChatRole.Assistant, "hello");
        var history = new ObservableCollection<AgentChatHistoryItem> { item };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        sink.Clear();

        // Trigger a Replace event with the same reference — ChatMessageHtmlModel.Update must short-circuit.
        history[0] = item;

        Assert.Empty(sink.ContentOperations);
    }

    [Fact]
    public void Update_EmitsOperations_WhenSourceDiffers()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.Assistant, "original"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
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

    [Fact]
    public void RunningItem_StreamingUpdate_EmitsNoHtmlOps_WhenItemsAreReferenceEqual()
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
        sink.Clear();

        // Replace the item in the running item's inner collection with the same reference.
        // ChatMessageHtmlModel.Update must short-circuit on ReferenceEquals.
        runningItem.Items[0] = item;

        Assert.Empty(sink.ContentOperations);
    }

    [Fact]
    public void RunningItem_StreamingUpdate_EmitsHtmlOps_WhenItemContentChanges()
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

    [Fact]
    public void SingleToolCallMessage_IsInsertedStandalone_WithoutGroupWrapper()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal("update", op.Kind);
        // Single standalone tool-call message: content-level item present, no message-level group body.
        Assert.Contains("chat-tool-group-item", op.Content);
        Assert.DoesNotContain("chat-tool-group-body", op.Content);
        Assert.Contains("chat-message", op.Content);
    }

    [Fact]
    public void TwoConsecutiveToolCalls_AreGroupedIntoSingleDetailsElement()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        sink.Clear();

        history.Add(ToolCallMessage("write_file", "c2"));

        var contentOps = sink.ContentOperations;
        Assert.True(contentOps.Count >= 2, "Expected Replace + Append + summary update");

        // First op: replace msg-0 with group wrapping it
        var replaceOp = contentOps.First(op => op.Location == ChatOutputUpdateLocation.Replace && op.Content.Contains("chat-tool-group"));
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), replaceOp.Path);
        Assert.Contains("grp-", replaceOp.Content);
        Assert.Contains("read_file", replaceOp.Content);
        Assert.Contains("chat-tool-group-body", replaceOp.Content);
        Assert.Contains(ChatOutputHtmlRenderer.MessageId(0), replaceOp.Content);

        // Second message appended into the group body
        var appendOp = contentOps.First(op => op.Location == ChatOutputUpdateLocation.Append && op.Path.Contains("body"));
        Assert.Contains("write_file", appendOp.Content);

        // Summary updated to count 2
        var summaryOp = contentOps.First(op => op.Location == ChatOutputUpdateLocation.Replace && op.Path.Contains("summary"));
        Assert.Contains("2 calls", summaryOp.Content);
        Assert.Contains("write_file", summaryOp.Content);
    }

    [Fact]
    public void ThreeConsecutiveToolCalls_GroupIsExtendedInPlace()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
            ToolCallMessage("write_file", "c2"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
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

    [Fact]
    public void TextThenToolCall_ToolCallIsStandalone_NoGroupWrapper()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            TextMessage(ChatRole.Assistant, "thinking"),
            ToolCallMessage("read_file"),
        };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);

        var contentOps = sink.ContentOperations;
        Assert.Equal(2, contentOps.Count);
        // Neither message should be inside a message-level group body.
        Assert.DoesNotContain(contentOps, op => op.Content.Contains("chat-tool-group-body"));
    }

    [Fact]
    public void ToolCallThenText_TextInsertsAfterToolCallMessage()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        sink.Clear();

        history.Add(TextMessage(ChatRole.Assistant, "done"));

        var op = Assert.Single(sink.ContentOperations);
        Assert.Equal(ChatOutputUpdateLocation.After, op.Location);
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), op.Path);
        Assert.Contains(">done<", op.Content);
    }

    [Fact]
    public void ConsecutiveToolCallsThenText_TextAnchorsAfterGroupElement()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
            ToolCallMessage("write_file", "c2"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        sink.Clear();

        history.Add(TextMessage(ChatRole.Assistant, "finished"));

        var contentOps = sink.ContentOperations;
        var insertOp = contentOps.First(op => op.Content.Contains("finished"));
        Assert.Equal(ChatOutputUpdateLocation.After, insertOp.Location);
        Assert.StartsWith("grp-", insertOp.Path);
    }

    // ── Content-level tool-group tests (issue #154) ────────────────────────────

    [Fact]
    public void MessageWithSingleFunctionCall_RendersToolGroupItem_NoOuterWrapper()
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

        var op = Assert.Single(sink.ContentOperations);
        Assert.Contains("chat-tool-group-item", op.Content);
        Assert.DoesNotContain("chat-tool-group-wrapper", op.Content);
    }

    [Fact]
    public void MessageWithMultipleFunctionCalls_RendersOuterToolGroupWrapper()
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

        var op = Assert.Single(sink.ContentOperations);
        Assert.Contains("chat-tool-group-wrapper", op.Content);
        Assert.Contains("chat-tool-group-item", op.Content);
        Assert.Contains("2 calls", op.Content);
    }

    [Fact]
    public void MessageWithFunctionCallAndMatchingResult_ResultNestedInsideCallItem()
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

        var op = Assert.Single(sink.ContentOperations);
        // Both call and result sections present in a single element.
        Assert.Contains("chat-tool-call", op.Content);
        Assert.Contains("chat-tool-result", op.Content);
    }

    [Fact]
    public void MessageWithFunctionResultOnly_NoMatchingCall_RenderedStandalone()
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
        var html = ChatOutputHtmlRenderer.RenderToolGroupWrapper("g0", 3, "last_tool(…)", inner);

        Assert.Contains("chat-tool-group-wrapper", html);
        Assert.Contains("3 calls", html);
        Assert.Contains(inner, html);
        Assert.DoesNotContain("<details open", html, StringComparison.OrdinalIgnoreCase);
    }

    // ── Cross-message tool-result injection tests (issue #154 bug fix) ────────

    [Fact]
    public void ToolResultMessage_CrossMessage_MatchedByCallId_InjectedIntoCallItem()
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

    [Fact]
    public void ToolResultMessage_CrossMessage_Unmatched_RenderedStandalone()
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

        var op = Assert.Single(sink.ContentOperations);
        Assert.DoesNotContain("chat-tool-group-item", op.Content);
        Assert.DoesNotContain("chat-tool-group-wrapper", op.Content);
    }

    [Fact]
    public void ToolResultMessage_CrossMessage_DoesNotTriggerMessageLevelGroup()
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

    [Fact]
    public void TwoToolCallBatches_SeparatedByResults_AreGroupedTogether()
    {
        // Arrange: batch 1
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            ToolCallMessage("read_file", "c1"),
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(history, new ObservableCollection<AgentChatRunningItem>(), () => true, sink);
        sink.Clear();

        // Tool result for batch 1 (injected — no DOM element)
        history.Add(ToolResultMessage("c1"));

        // batch 2 — should be grouped with batch 1 even though a result message sits between them
        history.Add(ToolCallMessage("write_file", "c2"));

        var contentOps = sink.ContentOperations;

        // A Replace on msg-0 must have created the message-level group wrapper (contains group body).
        var groupCreateOp = contentOps.FirstOrDefault(op =>
            op.Location == ChatOutputUpdateLocation.Replace &&
            op.Path == ChatOutputHtmlRenderer.MessageId(0) &&
            op.Content.Contains("chat-tool-group-body"));
        Assert.NotNull(groupCreateOp);
        Assert.Contains("read_file", groupCreateOp!.Content);
        Assert.Contains("grp-", groupCreateOp.Content);

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

    [Fact]
    public void ThreeToolCallBatches_SeparatedByResults_AllInSameGroup()
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

    [Fact]
    public void ToolCallBatch_AfterNonToolMessage_IsStandalone_EvenWithResults()
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

    [Fact]
    public void RunningItem_Activate_EmitsContainerInsertBeforeMessageAppend()
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

        var ops = sink.ContentOperations;
        Assert.Equal(2, ops.Count);

        // First op must be the outer running-item container appended into the running region.
        Assert.Equal(ChatOutputHtmlRenderer.RunningContainerId, ops[0].Path);
        Assert.Contains(ChatOutputHtmlRenderer.RunningItemId(0), ops[0].Content);

        // Second op must be the message appended into that container's contents div — which only
        // exists because the first op already created it.
        var contentsId = ChatOutputHtmlRenderer.RunningItemContentsId(ChatOutputHtmlRenderer.RunningItemId(0));
        Assert.Equal(contentsId, ops[1].Path);
        Assert.Contains(">hello<", ops[1].Content);
    }

    [Fact]
    public void RunningItem_StreamingChunksContinueAfterToolCallResultInsertion()
    {
        var runningItem = new AgentChatRunningItem();
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);

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

    [Fact]
    public void RunningItem_WhenInsertionFailed_NotifyInsertionFailed_ReInsertsAndRestoresStreaming()
    {
        var runningItem = new AgentChatRunningItem();
        var running = new ObservableCollection<AgentChatRunningItem> { runningItem };
        var sink = new RecordingSink();

        using var model = new ChatOutputHtmlModel(
            new ObservableCollection<AgentChatHistoryItem>(),
            running,
            () => true,
            sink);
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

    [Fact]
    public void ChatOutputHtmlModel_InspectMessage_HandledByBridge()
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

    [Fact]
    public void GenerateHistoryChunk_EmptyChunk_ReturnsNoCommandsAndNullFirstElementId()
    {
        var idBox = new int[1];
        var (commands, firstElementId) = ChatOutputHtmlModel.GenerateHistoryChunk(
            [], idBox, () => true, null, null, null);

        Assert.Empty(commands);
        Assert.Null(firstElementId);
    }

    [Fact]
    public void GenerateHistoryChunk_SingleTextItem_ReturnsAppendCommandAndFirstElementId()
    {
        var chunk = new List<AgentChatHistoryItem> { TextMessage(ChatRole.User, "hello world") };
        var idBox = new int[1];

        var (commands, firstElementId) = ChatOutputHtmlModel.GenerateHistoryChunk(
            chunk, idBox, () => true, null, null, null);

        Assert.NotEmpty(commands);
        Assert.NotNull(firstElementId);

        // The first element id for a standalone text message is msg-0.
        Assert.Equal(ChatOutputHtmlRenderer.MessageId(0), firstElementId);

        // There must be an Append into HistoryContainerId for the message element.
        Assert.Contains(commands, cmd =>
            cmd.Location == ChatOutputUpdateLocation.Append &&
            cmd.Path == ChatOutputHtmlRenderer.HistoryContainerId);
    }

    [Fact]
    public void GenerateHistoryChunk_TwoConsecutiveToolCallItems_FirstElementIdIsGroupId()
    {
        var chunk = new List<AgentChatHistoryItem>
        {
            ToolCallMessage("tool_a", "c1"),
            ToolCallMessage("tool_b", "c2"),
        };
        var idBox = new int[1];

        var (commands, firstElementId) = ChatOutputHtmlModel.GenerateHistoryChunk(
            chunk, idBox, () => true, null, null, null);

        Assert.NotNull(firstElementId);
        // Two consecutive tool-call items are promoted into a group; firstElementId is the group id.
        Assert.StartsWith("grp-", firstElementId);
    }

    [Fact]
    public void GenerateHistoryChunk_200Items_IdBoxAdvancedByExpectedAmount()
    {
        var chunk = Enumerable
            .Range(0, 200)
            .Select(i => TextMessage(ChatRole.User, $"message {i}"))
            .ToList();
        var idBox = new int[1];

        var (commands, _) = ChatOutputHtmlModel.GenerateHistoryChunk(
            chunk, idBox, () => true, null, null, null);

        // 200 text items each consume exactly one id (msg-N); no grouping.
        Assert.Equal(200, idBox[0]);
        Assert.Equal(200, commands.Count);

        // All element ids referenced in Append commands must be unique.
        var appendPaths = commands
            .Where(c => c.Location == ChatOutputUpdateLocation.Append || c.Location == ChatOutputUpdateLocation.After)
            .Select(c => c.Content)
            .ToList();
        Assert.Equal(200, appendPaths.Distinct().Count());
    }

    [Fact]
    public void GenerateHistoryChunk_ContentMatchesRenderer_ForSimpleTextItem()
    {
        const string messageText = "simple text";
        var item = TextMessage(ChatRole.Assistant, messageText);
        var chunk = new List<AgentChatHistoryItem> { item };
        var idBox = new int[1];

        var (commands, firstElementId) = ChatOutputHtmlModel.GenerateHistoryChunk(
            chunk, idBox, () => true, null, null, null);

        var appendCmds = commands
            .Where(c => c.Location == ChatOutputUpdateLocation.Append && c.Path == ChatOutputHtmlRenderer.HistoryContainerId)
            .ToList();

        var appendCmd = Assert.Single(appendCmds);
        Assert.Contains("chat-message", appendCmd.Content);
        Assert.Contains("chat-assistant-message", appendCmd.Content);
        Assert.Contains(messageText, appendCmd.Content);
        Assert.Contains($"id=\"{firstElementId}\"", appendCmd.Content);
    }

    [Fact]
    public async Task GenerateHistoryChunk_CanBeCalledOffUIThread()
    {
        var chunk = new List<AgentChatHistoryItem> { TextMessage(ChatRole.Assistant, "off-thread") };
        var idBox = new int[1];

        var (commands, firstElementId) = await Task.Run(() =>
            ChatOutputHtmlModel.GenerateHistoryChunk(chunk, idBox, () => true, null, null, null));

        Assert.NotEmpty(commands);
        Assert.NotNull(firstElementId);
    }

    // ── ComputeChunkRanges / SnapCutPoint tests ───────────────────────────────

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
}
