using System.Collections.Generic;
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
    public void RenderHeader_EmitsDataStickyLevelOnHeader()
    {
        var html = ChatOutputHtmlRenderer.RenderHeader("msg-0", "assistant");

        Assert.Contains("data-sticky-level=\"0\"", html);
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
}
