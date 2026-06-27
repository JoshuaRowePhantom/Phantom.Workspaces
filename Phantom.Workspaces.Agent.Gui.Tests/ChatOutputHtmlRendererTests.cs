using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class ChatOutputHtmlRendererTests
{
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
