using Avalonia.Headless.XUnit;
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

using Phantom.Workspaces.Testing.Gui;

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

    [AvaloniaFact(Timeout = 15_000)]
    public async Task JumpLink_Present_WhenContentHasParentToolCallId()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = new("tool"),
                Contents =
                [
                    new FunctionResultContent("call-1", "ok")
                    {
                        AdditionalProperties = new()
                        {
                            [CopilotSdkStreamAdapter.ParentToolCallIdPropertyName] = "call-1",
                        },
                    },
                ],
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

    [AvaloniaFact(Timeout = 15_000)]
    public async Task JumpLink_Absent_WhenContentHasNoParentToolCallId()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = new("tool"),
                Contents = [new FunctionResultContent("call-1", "ok")],
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

    [AvaloniaFact(Timeout = 15_000)]
    public async Task JumpLink_Absent_WhenResolverReturnsNull()
    {
        var history = new ObservableCollection<AgentChatHistoryItem>
        {
            new()
            {
                Role = new("tool"),
                Contents =
                [
                    new FunctionResultContent("call-1", "ok")
                    {
                        AdditionalProperties = new()
                        {
                            [CopilotSdkStreamAdapter.ParentToolCallIdPropertyName] = "call-1",
                        },
                    },
                ],
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

    [AvaloniaFact(Timeout = 15_000)]
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

    [AvaloniaFact(Timeout = 15_000)]
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

    [AvaloniaFact(Timeout = 15_000)]
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

    // ── Issue #1046: ancestor breadcrumb link tests ─────────────────────────

    [Fact]
    public void RenderAncestorLinks_NestedSubAgent_RendersParentAndGrandparentLinks()
    {
        var ancestors = new List<AncestorLinkHtmlModel>
        {
            new("root-id", "RootAgent", IsRoot: true, IsCurrent: false),
            new("mid-id", "MidAgent", IsRoot: false, IsCurrent: false),
            new("cur-id", "CurrentAgent", IsRoot: false, IsCurrent: true),
        };

        var html = ChatOutputHtmlRenderer.RenderAncestorLinks(ancestors);

        Assert.Contains("data-navigate-agent-id=\"root-id\"", html, StringComparison.Ordinal);
        Assert.Contains("data-navigate-agent-id=\"mid-id\"", html, StringComparison.Ordinal);
        Assert.Contains("data-navigate-agent-id=\"cur-id\"", html, StringComparison.Ordinal);
        Assert.Contains("RootAgent", html, StringComparison.Ordinal);
        Assert.Contains("MidAgent", html, StringComparison.Ordinal);
        Assert.Contains("CurrentAgent", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderAncestorLinks_RootAgent_RendersNoAncestorLinks()
    {
        var html = ChatOutputHtmlRenderer.RenderAncestorLinks(new List<AncestorLinkHtmlModel>());

        Assert.Equal(string.Empty, html);
    }

    [Fact]
    public void RenderAncestorLinks_ReusesNavigateAgentIdMarker()
    {
        var ancestors = new List<AncestorLinkHtmlModel>
        {
            new("root-id", "Root", IsRoot: true, IsCurrent: false),
            new("cur-id", "Current", IsRoot: false, IsCurrent: true),
        };

        var html = ChatOutputHtmlRenderer.RenderAncestorLinks(ancestors);

        // Same convention as child jump links.
        Assert.Contains("data-navigate-agent-id", html, StringComparison.Ordinal);
        Assert.Contains("chat-jump-link", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderAncestorLinks_OrdersBreadcrumbRootToCurrent()
    {
        var ancestors = new List<AncestorLinkHtmlModel>
        {
            new("root-id", "RootAgent", IsRoot: true, IsCurrent: false),
            new("mid-id", "MidAgent", IsRoot: false, IsCurrent: false),
            new("cur-id", "CurrentAgent", IsRoot: false, IsCurrent: true),
        };

        var html = ChatOutputHtmlRenderer.RenderAncestorLinks(ancestors);

        Assert.Contains("(root)", html, StringComparison.Ordinal);
        Assert.Contains("(current)", html, StringComparison.Ordinal);

        // Root appears before current.
        var rootIndex = html.IndexOf("(root)", StringComparison.Ordinal);
        var currentIndex = html.IndexOf("(current)", StringComparison.Ordinal);
        Assert.True(rootIndex < currentIndex);
    }

    [Fact]
    public void RenderAncestorLinks_EscapesAgentIdAndDisplayName()
    {
        var ancestors = new List<AncestorLinkHtmlModel>
        {
            new("id<\"x\">", "Name<\"y\">", IsRoot: true, IsCurrent: true),
        };

        var html = ChatOutputHtmlRenderer.RenderAncestorLinks(ancestors);

        Assert.DoesNotContain("id<\"x\">", html, StringComparison.Ordinal);
        Assert.Contains("id&lt;&quot;x&quot;&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Name<\"y\">", html, StringComparison.Ordinal);
        Assert.Contains("Name&lt;&quot;y&quot;&gt;", html, StringComparison.Ordinal);
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
