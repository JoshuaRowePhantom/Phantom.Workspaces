using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

        Assert.Contains("<details class=\"chat-tool-call\"", html, StringComparison.Ordinal);
        Assert.Contains(" open>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallPair_WithResultJson_ChatToolResultDetailsHasOpenAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", "{\"result\":\"ok\"}");

        Assert.Contains("<details class=\"chat-tool-result\"", html, StringComparison.Ordinal);
        Assert.Contains(" open>", html, StringComparison.Ordinal);
    }

    // ── Issue #1039: copy + inspect gutters on tool-call/tool-result blocks ─────

    [Fact]
    public void RenderToolCallPair_WithCallJson_ToolCallBlockHasDataCopyTargetAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", null);

        var callBlock = ExtractToolBlock(html, "chat-tool-call");
        Assert.Contains("data-copy-target", callBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallPair_WithCallJson_ToolCallBlockHasDataInspectTargetAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", null);

        var callBlock = ExtractToolBlock(html, "chat-tool-call");
        Assert.Contains("data-inspect-target", callBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallPair_WithResultJson_ToolResultBlockHasDataCopyTargetAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", "{\"result\":\"ok\"}");

        var resultBlock = ExtractToolBlock(html, "chat-tool-result");
        Assert.Contains("data-copy-target", resultBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallPair_WithResultJson_ToolResultBlockHasDataInspectTargetAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", "{\"result\":\"ok\"}");

        var resultBlock = ExtractToolBlock(html, "chat-tool-result");
        Assert.Contains("data-inspect-target", resultBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallPair_ToolBlocks_DoNotEmitStandaloneDetailsGutterOverflowMarker()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", "{}", "{\"result\":\"ok\"}");

        // The "..." overflow button is JS-injected by details-gutter (removed by #1038); the renderer
        // must never emit its marker class.
        Assert.DoesNotContain("details-gutter-btn", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallPair_WithCallDetailsJson_InspectPayloadContainsSerializedCallJson()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair(
            "c0", "my_tool", "{}", null, callDetailsJson: "{\"kind\":\"call\"}");

        Assert.Contains("data-details-target=\"{&quot;kind&quot;:&quot;call&quot;}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolGroup_SingleCallWithResult_BothToolBlocksExposeCopyAndInspectTargets()
    {
        var call = new FunctionCallContent("call-1", "my_tool", null);
        var result = new FunctionResultContent("call-1", "ok");
        var lookup = new Dictionary<string, FunctionResultContent> { ["call-1"] = result };

        var html = ChatOutputHtmlRenderer.RenderToolGroup("c0", new[] { call }, lookup);

        var callBlock = ExtractToolBlock(html, "chat-tool-call");
        var resultBlock = ExtractToolBlock(html, "chat-tool-result");
        Assert.Contains("data-copy-target", callBlock, StringComparison.Ordinal);
        Assert.Contains("data-inspect-target", callBlock, StringComparison.Ordinal);
        Assert.Contains("data-details-target=", callBlock, StringComparison.Ordinal);
        Assert.Contains("data-copy-target", resultBlock, StringComparison.Ordinal);
        Assert.Contains("data-inspect-target", resultBlock, StringComparison.Ordinal);
        Assert.Contains("data-details-target=", resultBlock, StringComparison.Ordinal);
    }

    // Extracts the opening tag (up to and including '>') of the first <details class="{cssClass}" ...> element.
    private static string ExtractToolBlock(string html, string cssClass)
    {
        var marker = $"<details class=\"{cssClass}\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected a <details class=\"{cssClass}\"> element in output.");
        var end = html.IndexOf('>', start);
        Assert.True(end >= 0);
        return html.Substring(start, end - start + 1);
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
    public void DetectStringType_JsonObject_ReturnsJson()
    {
        Assert.Equal(StringContentType.Json, ChatOutputHtmlRenderer.DetectStringType("""{"a":1}"""));
    }

    [Fact]
    public void DetectStringType_MarkdownWithHeading_ReturnsMarkdown()
    {
        Assert.Equal(StringContentType.Markdown, ChatOutputHtmlRenderer.DetectStringType("## Heading\nBody"));
    }

    [Fact]
    public void DetectStringType_MultilineIndentedCode_ReturnsCode()
    {
        Assert.Equal(StringContentType.Code, ChatOutputHtmlRenderer.DetectStringType("if (true) {\n    return value;\n}"));
    }

    [Fact]
    public void DetectStringType_SimpleSentence_ReturnsPlaintext()
    {
        Assert.Equal(StringContentType.Plaintext, ChatOutputHtmlRenderer.DetectStringType("simple sentence"));
    }

    [Fact]
    public void RenderJsonValue_Object_KeysLeftAlignedAndColonsAligned()
    {
        using var document = JsonDocument.Parse("""{"a":1,"longer":2}""");

        var html = ChatOutputHtmlRenderer.RenderJsonValue(document.RootElement, 0);

        // Keys are left-aligned (text starts immediately after the span tag) and
        // padded on the right so the colons still line up at the same column.
        Assert.Contains("<span class=\"tool-json-key\">a     </span>: 1", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tool-json-key\">longer</span>: 2", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderJsonValue_FlatObjectWithVariableLengthKeys_KeyTextLeftEdgesAlignAtSameColumn()
    {
        using var document = JsonDocument.Parse("""{"a":1,"longKey":2}""");

        var html = ChatOutputHtmlRenderer.RenderJsonValue(document.RootElement, 0);

        const string keyTagOpen = "<span class=\"tool-json-key\">";
        var lines = html.Split('\n').Where(line => line.Contains(keyTagOpen, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        // The key text begins immediately after the opening span tag on every line,
        // so all sibling keys share the same left edge (no staircase).
        var leftEdges = lines
            .Select(line => line.IndexOf(keyTagOpen, StringComparison.Ordinal) + keyTagOpen.Length)
            .Distinct()
            .ToList();
        Assert.Single(leftEdges);
    }

    [Fact]
    public void RenderJsonValue_SiblingKeysAtSameDepth_AllHaveIdenticalColonOffset()
    {
        using var document = JsonDocument.Parse(
            """{"entityId":"x","concurrencyTag":"y","names":"z"}""");

        var html = ChatOutputHtmlRenderer.RenderJsonValue(document.RootElement, 0);

        var colonOffsets = html.Split('\n')
            .Where(line => line.Contains("</span>: ", StringComparison.Ordinal))
            .Select(line => line.IndexOf("</span>: ", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        Assert.Single(colonOffsets);
    }

    [Fact]
    public void RenderJsonValue_NestedObject_InnerKeysIndentedByExactlyOneLevel()
    {
        using var document = JsonDocument.Parse("""{"outer":{"inner":1}}""");

        var html = ChatOutputHtmlRenderer.RenderJsonValue(document.RootElement, 0);

        Assert.Contains("\n  <span class=\"tool-json-key\">inner</span>: 1", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderJsonValue_ArrayItems_DashPrefixConsistentlyIndented()
    {
        using var document = JsonDocument.Parse("""["alpha","beta","gamma"]""");

        var html = ChatOutputHtmlRenderer.RenderJsonValue(document.RootElement, 1);

        var lines = html.Split('\n');
        Assert.All(lines, line => Assert.StartsWith("  - ", line, StringComparison.Ordinal));
    }

    [Fact]
    public void RenderJsonValue_NestedObjectInsideArray_KeysIndentedTwoLevelsBelowArray()
    {
        using var document = JsonDocument.Parse("""[{"key":"val"}]""");

        var html = ChatOutputHtmlRenderer.RenderJsonValue(document.RootElement, 0);

        Assert.Contains("- \n  <span class=\"tool-json-key\">key</span>: ", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderJsonValue_Object_MarkdownStringValue_RenderedAsMarkdown()
    {
        using var document = JsonDocument.Parse("""{"prompt":"## Heading\nBody"}""");

        var html = ChatOutputHtmlRenderer.RenderJsonValue(document.RootElement, 0);

        Assert.Contains("tool-json-markdown", html, StringComparison.Ordinal);
        Assert.Contains("<h2", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderJsonValue_Object_JsonStringValue_RenderedRecursively()
    {
        using var document = JsonDocument.Parse("""{"payload":"{\"child\":1}"}""");

        var html = ChatOutputHtmlRenderer.RenderJsonValue(document.RootElement, 0);

        Assert.Contains("child</span>: 1", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderJsonValue_Object_PlaintextMultiline_ContinuationLinesIndented()
    {
        using var document = JsonDocument.Parse("""{"prompt":"first\nsecond"}""");

        var html = ChatOutputHtmlRenderer.RenderJsonValue(document.RootElement, 0);

        Assert.Contains("first\n        second", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderJsonValue_Array_EachElementOnOwnLine()
    {
        using var document = JsonDocument.Parse("""["one","two"]""");

        var html = ChatOutputHtmlRenderer.RenderJsonValue(document.RootElement, 0);

        Assert.Contains("- <span class=\"tool-json-plaintext\">one</span>", html, StringComparison.Ordinal);
        Assert.Contains("\n- <span class=\"tool-json-plaintext\">two</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderJsonValue_UnicodeEscapedString_DecodedBeforeRender()
    {
        using var document = JsonDocument.Parse("""{"text":"\u0060code\u0060"}""");

        var html = ChatOutputHtmlRenderer.RenderJsonValue(document.RootElement, 0);

        Assert.Contains("`code`", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderToolCallPair_WithJsonArguments_OutputContainsToolJsonKeyClass()
    {
        var html = ChatOutputHtmlRenderer.RenderToolCallPair("c0", "my_tool", """{"arg":"val"}""", null);

        Assert.Contains("class=\"tool-json-key\"", html, StringComparison.Ordinal);
        Assert.Contains("arg</span>: <span class=\"tool-json-plaintext\">val</span>", html, StringComparison.Ordinal);
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

    private sealed class CyclicPayload
    {
        // A self-referencing property makes JsonSerializer.SerializeToElement throw JsonException
        // (object-cycle), reproducing the non-serializable tool-argument fault from #1008.
        public CyclicPayload Self => this;
    }

    [Fact]
    public void RenderContent_FunctionCallWithNonSerializableArguments_DoesNotThrowAndFallsBackToText()
    {
        var call = new FunctionCallContent(
            "call-1",
            "myTool",
            new Dictionary<string, object?> { ["x"] = new CyclicPayload() });

        var exception = Record.Exception(() =>
        {
            var html = ChatOutputHtmlRenderer.RenderContent("c0", call, includeReasoning: false, isDiagnostic: false);
            Assert.NotNull(html);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void RenderContent_FunctionResultWithNonSerializableResult_DoesNotThrow()
    {
        var result = new FunctionResultContent("call-1", new CyclicPayload());

        var exception = Record.Exception(() =>
            ChatOutputHtmlRenderer.RenderContent("c0", result, includeReasoning: false, isDiagnostic: false));

        Assert.Null(exception);
    }
}
