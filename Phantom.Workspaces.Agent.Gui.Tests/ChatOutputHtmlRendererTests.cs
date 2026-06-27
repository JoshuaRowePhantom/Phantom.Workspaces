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
}
