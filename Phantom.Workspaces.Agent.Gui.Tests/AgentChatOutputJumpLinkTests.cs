using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatOutputJumpLinkTests
{
    // ── ChatOutputHtmlRenderer ──────────────────────────────────────────────

    [Fact]
    public void RenderMessage_WithJumpLinkHtml_IncludesJumpLinkInOutput()
    {
        var html = ChatOutputHtmlRenderer.RenderMessage(
            "msg-0",
            "tool",
            [],
            jumpLinkHtml: "<div class=\"chat-subagent-jump\">JUMP</div>");

        Assert.Contains("chat-subagent-jump", html, StringComparison.Ordinal);
        Assert.Contains("JUMP", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMessage_WithoutJumpLinkHtml_DoesNotIncludeJumpLinkClass()
    {
        var html = ChatOutputHtmlRenderer.RenderMessage(
            "msg-0",
            "tool",
            []);

        Assert.DoesNotContain("chat-subagent-jump", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderSubAgentJumpLink_ContainsAgentIdAndNavigateAttribute()
    {
        var html = ChatOutputHtmlRenderer.RenderSubAgentJumpLink("agent-abc");

        Assert.Contains("data-navigate-agent-id=\"agent-abc\"", html, StringComparison.Ordinal);
        Assert.Contains("→ Open sub-agent", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderSubAgentJumpLink_EscapesAgentIdForHtml()
    {
        var html = ChatOutputHtmlRenderer.RenderSubAgentJumpLink("agent<\"x\">");

        Assert.DoesNotContain("agent<\"x\">", html, StringComparison.Ordinal);
        Assert.Contains("agent&lt;&quot;x&quot;&gt;", html, StringComparison.Ordinal);
    }

    // ── ChatOutputHtmlModel (jump-link via resolveSubAgentId) ───────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task JumpLink_Present_WhenHistoryItemHasParentToolCallId()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = new("tool"),
                Contents = [new FunctionResultContent("call-1", "ok")],
                ParentToolCallId = "call-1",
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            history,
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink,
            resolveSubAgentId: _ => "sub-agent-42");
        await model.HistoryLoaded;

        var content = Assert.Single(sink.ContentOperations);
        Assert.Contains("data-navigate-agent-id=\"sub-agent-42\"", content.Content, StringComparison.Ordinal);
        Assert.Contains("→ Open sub-agent", content.Content, StringComparison.Ordinal);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task JumpLink_Absent_WhenParentToolCallIdIsNull()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = new("tool"),
                Contents = [new FunctionResultContent("call-1", "ok")],
                ParentToolCallId = null,
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            history,
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink,
            resolveSubAgentId: _ => "sub-agent-42");
        await model.HistoryLoaded;

        var content = Assert.Single(sink.ContentOperations);
        Assert.DoesNotContain("data-navigate-agent-id", content.Content, StringComparison.Ordinal);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task JumpLink_Absent_WhenResolverReturnsNull()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = new("tool"),
                Contents = [new FunctionResultContent("call-1", "ok")],
                ParentToolCallId = "call-1",
            },
        };
        var sink = new RecordingSink();
        using var model = new ChatOutputHtmlModel(
            history,
            new ObservableCollection<AgentChatRunningItem>(),
            () => true,
            sink,
            resolveSubAgentId: _ => null);
        await model.HistoryLoaded;

        var content = Assert.Single(sink.ContentOperations);
        Assert.DoesNotContain("data-navigate-agent-id", content.Content, StringComparison.Ordinal);
    }

    // ── AgentChatOutputControl (navigateToAgent bridge) ─────────────────────

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_NavigateToAgentMessage_RaisesNavigateToAgentRequested()
    {
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        string? receivedAgentId = null;
        control.NavigateToAgentRequested += (_, id) => receivedAgentId = id;

        browser.FireMessage("""{"type":"navigateToAgent","agentId":"agent-xyz"}""");

        Assert.Equal("agent-xyz", receivedAgentId);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void AgentChatOutputControl_NavigateToAgentMessage_WithEmptyAgentId_DoesNotRaiseEvent()
    {
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var raised = false;
        control.NavigateToAgentRequested += (_, _) => raised = true;

        browser.FireMessage("""{"type":"navigateToAgent","agentId":""}""");

        Assert.False(raised);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void JumpLink_NavigatesToCorrectSubAgent_OnClick()
    {
        var control = new AgentChatOutputControl();
        var browserField = typeof(AgentChatOutputControl)
            .GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(browserField);
        var browser = Assert.IsType<HeadlessControllableBrowser>(browserField!.GetValue(control));

        var received = new List<string>();
        control.NavigateToAgentRequested += (_, id) => received.Add(id);

        browser.FireMessage("""{"type":"navigateToAgent","agentId":"correct-agent-id"}""");

        Assert.Single(received);
        Assert.Equal("correct-agent-id", received[0]);
    }

    // ── HTML shell contains navigate-to-agent listener ──────────────────────

    [Fact]
    public void ChatOutputShellHtml_ContainsNavigateAgentClickHandler()
    {
        var html = ReadShellHtml();

        Assert.Contains("data-navigate-agent-id", html, StringComparison.Ordinal);
        Assert.Contains("navigateToAgent", html, StringComparison.Ordinal);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed record Operation(string Kind, string Path, ChatOutputUpdateLocation Location, string Content);

    private sealed class RecordingSink : IChatOutputHtmlSink
    {
        public List<Operation> Operations { get; } = [];

        public List<Operation> ContentOperations
            => this.Operations.Where(operation => operation.Kind is "update" or "remove").ToList();

        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
            => this.Operations.Add(new Operation("update", path, location, content));

        public void RemoveContent(string path)
            => this.Operations.Add(new Operation("remove", path, ChatOutputUpdateLocation.Replace, string.Empty));

        public void ScrollToBottom()
            => this.Operations.Add(new Operation("scroll", string.Empty, ChatOutputUpdateLocation.Replace, string.Empty));
    }

    private static string ReadShellHtml()
    {
        var assembly = typeof(AgentChatOutputControl).Assembly;
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith("chat-output-shell.html", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded chat-output-shell.html not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
