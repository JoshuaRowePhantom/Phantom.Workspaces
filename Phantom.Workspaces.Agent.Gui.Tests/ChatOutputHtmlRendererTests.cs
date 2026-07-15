using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatOutputHtmlRendererTests
{
    [Fact]
    public void RenderCollapsible_EmitsDataStickyLevelOnSummary()
    {
        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new FunctionCallContent("call-1", "read_file", null),
            includeReasoning: true,
            isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-sticky-level=\"0\"", html);
    }

    [Fact]
    public void RenderCollapsible_EmitsDataStickyBaseLevelOnDetails()
    {
        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new FunctionCallContent("call-1", "read_file", null),
            includeReasoning: true,
            isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-sticky-base-level=\"1\"", html);
    }

    [Fact]
    public void RenderHeader_EmitsDataStickyLevel1OnChatHeader()
    {
        var html = ChatOutputHtmlRenderer.RenderHeader("msg-0", "assistant");

        Assert.Contains("data-sticky-level=\"1\"", html);
    }

    [Fact]
    public void RenderMessage_EmitsDataStickyBaseLevelOnMessageDiv()
    {
        var html = ChatOutputHtmlRenderer.RenderMessage(
            "msg-0",
            "assistant",
            [("msg-0-c0", "<div>hi</div>")]);

        Assert.Contains("data-sticky-base-level=\"0\"", html);
    }

    [Fact]
    public void TextBlock_ViaRenderContent_HasDataCopyTargetAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextContent("hello"), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-copy-target", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownBlock_ViaRenderContent_HasDataCopyTargetAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextContent("**bold**"), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-copy-target", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ReasoningBlock_ViaRenderContent_HasDataCopyTargetAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextReasoningContent("thinking..."), includeReasoning: true, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-copy-target", html, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionCallBlock_ViaRenderContent_HasDataCopyTargetAttribute()
    {
        var call = new FunctionCallContent("call-1", "my_tool", new Dictionary<string, object?> { ["arg"] = "val" });
        var html = ChatOutputHtmlRenderer.RenderContent("c0", call, includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-copy-target", html, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionResultBlock_ViaRenderContent_HasDataCopyTargetAttribute()
    {
        var result = new FunctionResultContent("call-1", "result value");
        var html = ChatOutputHtmlRenderer.RenderContent("c0", result, includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-copy-target", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorBlock_ViaRenderContent_HasDataCopyTargetAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new ErrorContent("oops"), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-copy-target", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextWithHttpsUrl_RendersAsAnchorElement()
    {
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextContent("Visit https://example.com for details"), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("<a href", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextWithHttpsUrl_AnchorHasCorrectHref()
    {
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextContent("Visit https://example.com for details"), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("href=\"https://example.com\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextWithHttpsUrl_AnchorHasTargetBlank()
    {
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextContent("https://example.com"), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("target=\"_blank\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextWithHttpsUrl_AnchorHasRelNoopenerNoreferrer()
    {
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextContent("https://example.com"), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextWithoutUrl_DoesNotRenderAnchorElement()
    {
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextContent("Hello, no links here."), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.DoesNotContain("<a href", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLinkSyntax_RendersAsAnchorWithTargetAndRel()
    {
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextContent("[click here](https://example.com)"), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("href=\"https://example.com\"", html, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
        Assert.Contains("click here", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHeader_WithTimestamp_EmitsChatTimestampSpan()
    {
        var timestamp = new DateTimeOffset(2026, 6, 27, 15, 13, 0, TimeSpan.Zero);

        var html = ChatOutputHtmlRenderer.RenderHeader("msg-0", "assistant", timestamp);

        Assert.Contains("chat-timestamp", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHeader_WithTimestamp_EmitsDataUtcAttribute()
    {
        var timestamp = new DateTimeOffset(2026, 6, 27, 15, 13, 0, TimeSpan.Zero);

        var html = ChatOutputHtmlRenderer.RenderHeader("msg-0", "assistant", timestamp);

        Assert.Contains("data-utc=\"", html, StringComparison.Ordinal);
        Assert.Contains("2026-06-27T15:13:00", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHeader_WithNullTimestamp_OmitsChatTimestampSpan()
    {
        var html = ChatOutputHtmlRenderer.RenderHeader("msg-0", "assistant", timestamp: null);

        Assert.DoesNotContain("chat-timestamp", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderHeader_WithNoTimestampArgument_OmitsChatTimestampSpan()
    {
        var html = ChatOutputHtmlRenderer.RenderHeader("msg-0", "assistant");

        Assert.DoesNotContain("chat-timestamp", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMessage_WithTimestamp_EmitsDataUtcInHeader()
    {
        var timestamp = new DateTimeOffset(2026, 6, 27, 15, 13, 0, TimeSpan.Zero);

        var html = ChatOutputHtmlRenderer.RenderMessage(
            "msg-0",
            "assistant",
            [("msg-0-c0", "<div>hi</div>")],
            timestamp);

        Assert.Contains("data-utc=\"", html, StringComparison.Ordinal);
        Assert.Contains("2026-06-27T15:13:00", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMessage_WithNullTimestamp_OmitsDataUtcInHeader()
    {
        var html = ChatOutputHtmlRenderer.RenderMessage(
            "msg-0",
            "assistant",
            [("msg-0-c0", "<div>hi</div>")],
            timestamp: null);

        Assert.DoesNotContain("data-utc=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticBlock_WhenIsDiagnosticTrue_RendersCollapsibleElement()
    {
        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new TextContent("diag info"),
            includeReasoning: false,
            isDiagnostic: true);

        Assert.NotNull(html);
        Assert.Contains("chat-diagnostic", html, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticBlock_DefaultIncludeDiagnostics_RendersCollapsibleElement()
    {
        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new TextContent("diag info"),
            includeReasoning: false,
            isDiagnostic: true);

        Assert.NotNull(html);
        Assert.Contains("chat-diagnostic", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderContent_TextContent_DataDetailsTargetIsJsonNotRawText()
    {
        const string text = "hello world";
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextContent(text), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.DoesNotContain($"data-details-target=\"{text}\"", html, StringComparison.Ordinal);
        Assert.Contains("data-details-target=\"{", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderContent_TextReasoningContent_DataDetailsTargetIsJsonNotRawText()
    {
        const string text = "thinking about this";
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new TextReasoningContent(text), includeReasoning: true, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.DoesNotContain($"data-details-target=\"{text}\"", html, StringComparison.Ordinal);
        Assert.Contains("data-details-target=\"{", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderContent_FunctionCallContent_DataDetailsTargetIsJsonNotRawText()
    {
        var call = new FunctionCallContent("call-1", "my_tool", new Dictionary<string, object?> { ["arg"] = "val" });
        var html = ChatOutputHtmlRenderer.RenderContent("c0", call, includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("data-details-target=\"{", html, StringComparison.Ordinal);
        // The attribute should be the full content JSON, not just the arguments body
        Assert.DoesNotContain("data-details-target=\"{\n  &quot;arg&quot;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderContent_ErrorContent_DataDetailsTargetIsJsonNotRawText()
    {
        const string message = "something went wrong";
        var html = ChatOutputHtmlRenderer.RenderContent("c0", new ErrorContent(message), includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.DoesNotContain($"data-details-target=\"{message}\"", html, StringComparison.Ordinal);
        Assert.Contains("data-details-target=\"{", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallGroupSummary_SummaryHasDataStickyLevel2()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallGroupSummary("grp-0", "my_tool", 3);

        Assert.Contains("data-sticky-level=\"2\"", html);
    }

    [Fact]
    public void RenderToolCallGroup_SummaryHasDataStickyLevel2()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallGroup("grp-0", "my_tool", 1, "<div>body</div>");

        Assert.Contains("data-sticky-level=\"2\"", html);
    }

    [Fact]
    public void RenderToolGroupWrapper_SummaryHasDataStickyLevel2()
    {
        var html = ChatOutputHtmlRenderer.RenderToolGroupWrapper("c0", 2, "last_tool(…)", "<div>inner</div>");

        Assert.Contains("data-sticky-level=\"2\"", html);
    }

    [Fact]
    public void RenderToolCallPair_GroupItemSummaryHasDataStickyLevel3()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", null);

        Assert.Contains("data-sticky-level=\"3\"", html);
    }

    [Fact]
    public void RenderToolCallPair_WithCallJson_ChatToolCallDetailsHasOpenAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", null);

        Assert.Contains("<details class=\"chat-tool-call\" open>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallPair_WithResultJson_ChatToolResultDetailsHasOpenAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", "{\"result\":\"ok\"}");

        Assert.Contains("<details class=\"chat-tool-result\" open>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallPair_ChatToolGroupItem_DoesNotHaveOpenAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", null);

        Assert.DoesNotContain("<details class=\"chat-content chat-tool-group-item\" id=\"c0\" open>", html, StringComparison.Ordinal);
        Assert.Contains("<details class=\"chat-content chat-tool-group-item\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderContent_FunctionCallWithDescription_LabelIncludesDescription()
    {
        var call = new FunctionCallContent("call-1", "powershell", new Dictionary<string, object?> { ["command"] = "ls", ["description"] = "Read issue #70" });
        var html = ChatOutputHtmlRenderer.RenderContent("c0", call, includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("tool call: powershell: Read issue #70", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderContent_FunctionCallWithoutDescription_LabelIsToolCallName()
    {
        var call = new FunctionCallContent("call-1", "powershell", new Dictionary<string, object?> { ["command"] = "ls" });
        var html = ChatOutputHtmlRenderer.RenderContent("c0", call, includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("tool call: powershell", html, StringComparison.Ordinal);
        Assert.DoesNotContain("tool call: powershell:", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderContent_FunctionCallWithEmptyDescription_LabelIsToolCallName()
    {
        var call = new FunctionCallContent("call-1", "powershell", new Dictionary<string, object?> { ["command"] = "ls", ["description"] = "" });
        var html = ChatOutputHtmlRenderer.RenderContent("c0", call, includeReasoning: false, isDiagnostic: false);

        Assert.NotNull(html);
        Assert.Contains("tool call: powershell", html, StringComparison.Ordinal);
        Assert.DoesNotContain("tool call: powershell:", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallPair_CallSummary_HasStickyLevel4()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", null);

        Assert.Contains("<summary class=\"chat-collapsible-summary\" data-sticky-level=\"4\">call", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallPair_ResultSummary_HasStickyLevel4()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", "{\"result\":\"ok\"}");

        Assert.Contains("<summary class=\"chat-collapsible-summary\" data-sticky-level=\"4\">result", html, StringComparison.Ordinal);
    }

    // ── Issue #893: id surface and shell tests ─────────────────────────────────

    [Fact]
    public void MessageId_HistoryIndex_ReturnsHistoryDashIndex()
    {
        Assert.Equal("history-7", ChatOutputHtmlRenderer.MessageId(7));
    }

    [Fact]
    public void RunningMessageId_RunIdAndLocalIndex_ReturnsRunMsgId()
    {
        Assert.Equal("run-3-msg-0", ChatOutputHtmlRenderer.RunningMessageId("run-3", 0));
    }

    [Fact]
    public void ToolGroupId_FirstHistoryIndex_ReturnsSingleIndexId()
    {
        Assert.Equal("tool-group-42", ChatOutputHtmlRenderer.ToolGroupId(42));
    }

    [Fact]
    public void Renderer_RemovedAnchorMembers_DoNotExist()
    {
        var memberNames = typeof(ChatOutputHtmlRenderer)
            .GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
            .Select(member => member.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("LoadAfterAnchorId", memberNames);
        Assert.DoesNotContain("HistoryBeforeAnchorId", memberNames);
        Assert.DoesNotContain("RunningSubAgentsContainerId", memberNames);
        Assert.DoesNotContain("InsertAfterItemId", memberNames);
        Assert.DoesNotContain("InsertAfterContentId", memberNames);
        Assert.DoesNotContain("ToolGroupInsertAfterId", memberNames);
        Assert.DoesNotContain("ToolCallGroupId", memberNames);
    }

    [Fact]
    public void RenderMessage_Output_ContainsNoInsertAfterDiv()
    {
        var html = ChatOutputHtmlRenderer.RenderMessage(
            "history-0",
            "assistant",
            [("history-0-0", "<div>content</div>")],
            null);

        Assert.DoesNotContain("insert-after", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallGroup_Output_ContainsNoInsertAfterDiv()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallGroup("tool-group-0", "my_tool", 2, "<div>body</div>");

        Assert.DoesNotContain("insert-after", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_StaticMarkup_ContainsPersistentContainers()
    {
        var assembly = typeof(ChatOutputHtmlRenderer).Assembly;
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith("chat-output-shell.html", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded chat-output-shell.html resource was not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Could not open the chat-output-shell.html resource stream.");
        using var reader = new System.IO.StreamReader(stream);
        var shell = reader.ReadToEnd();

        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.HistoryContainerId}\"", shell);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.RunningContainerId}\"", shell);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.SubAgentPanelSentinelId}\"", shell);

        Assert.DoesNotContain("id=\"load-after\"", shell);
        Assert.DoesNotContain("id=\"history-before\"", shell);
        Assert.DoesNotContain("id=\"running-items-inside\"", shell);
        Assert.DoesNotContain("id=\"subagent-items-inside\"", shell);
    }

    // ── Issue #332: Help role rendering tests ──────────────────────────────────

    [Fact]
    public void RenderContent_HelpRole_RendersNonCollapsibleBlock()
    {
        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new TextContent("Help message text"),
            includeReasoning: false,
            isDiagnostic: false,
            isHelp: true);

        Assert.NotNull(html);
        Assert.Contains("chat-help", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<details", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderContent_DiagnosticRole_RendersCollapsible()
    {
        var html = ChatOutputHtmlRenderer.RenderContent(
            "c0",
            new TextContent("Diagnostic message"),
            includeReasoning: false,
            isDiagnostic: true,
            isHelp: false);

        Assert.NotNull(html);
        Assert.Contains("chat-diagnostic", html, StringComparison.Ordinal);
        Assert.Contains("<details", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RoleClass_HelpRole_ReturnsChatHelpMessage()
    {
        var roleClass = ChatOutputHtmlRenderer.RoleClass("help");

        Assert.Equal("chat-help-message", roleClass);
    }

    [Fact]
    public void RenderMessage_HelpRole_ShowsHelpLabel()
    {
        var html = ChatOutputHtmlRenderer.RenderMessage(
            "msg-0",
            "help",
            [("msg-0-c0", "<div>help content</div>")]);

        Assert.Contains("[help]", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[diagnostic]", html, StringComparison.Ordinal);
    }
}
