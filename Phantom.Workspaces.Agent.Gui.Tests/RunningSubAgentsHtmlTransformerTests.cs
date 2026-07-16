using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class RunningSubAgentsHtmlTransformerTests
{
    // ── Panel insertion ───────────────────────────────────────────────────────

    [Fact]
    public void RunningSubAgentsHtmlTransformer_Panel_ContentUpdated_WhenFirstRunningSubAgentAppears()
    {
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay>();
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);

        sink.Clear();

        var agent = new StubSubAgent("a1", "Code Reviewer", AgentChatCompletionState.Running);
        subAgents.Add(agent);

        // The old inner node is removed, then a fresh inner node is appended into the sentinel.
        Assert.Contains(sink.Operations, op =>
            op.Kind == "remove" &&
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelInnerId);
        Assert.Contains(sink.Operations, op =>
            op.Kind == "update" &&
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelSentinelId &&
            op.Location == ChatOutputUpdateLocation.Append &&
            op.Content.Contains("running-subagents"));
    }

    [Fact]
    public void RunningSubAgentsHtmlTransformer_Panel_NoDynamicContainerInjection()
    {
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay>();
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);

        var agent = new StubSubAgent("a1", "Code Reviewer", AgentChatCompletionState.Running);
        subAgents.Add(agent);

        Assert.DoesNotContain(sink.Operations, op =>
            op.Location == ChatOutputUpdateLocation.After);
    }

    [Fact]
    public void RunningSubAgentsHtmlTransformer_Panel_ClearedWhenNoRunningSubAgents()
    {
        var agent = new StubSubAgent("a1", "Code Reviewer", AgentChatCompletionState.Running);
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay> { agent };
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);

        sink.Clear();

        agent.SetCompletionState(AgentChatCompletionState.Succeeded);
        agent.RaiseActivityChanged();

        // The empty state removes the inner node and appends nothing back.
        Assert.Contains(sink.Operations, op =>
            op.Kind == "remove" &&
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelInnerId);
        Assert.DoesNotContain(sink.Operations, op =>
            op.Kind == "update" &&
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelSentinelId);
    }

    // ── Content rendering ─────────────────────────────────────────────────────

    [Fact]
    public void RunningSubAgentsHtmlTransformer_ActivityCap_AtFiveLines()
    {
        var activity = new List<SubAgentActivityLine>
        {
            new(SubAgentActivityKind.AgentText, "line1"),
            new(SubAgentActivityKind.AgentText, "line2"),
            new(SubAgentActivityKind.AgentText, "line3"),
            new(SubAgentActivityKind.AgentText, "line4"),
            new(SubAgentActivityKind.AgentText, "line5"),
        };
        var agent = new StubSubAgent("a1", "Writer", AgentChatCompletionState.Running, activity: activity);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([agent], []);

        Assert.Contains("line1", html, StringComparison.Ordinal);
        Assert.Contains("line5", html, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(html, "<li>"));
    }

    [Fact]
    public void RunningSubAgentsHtmlTransformer_ToolUses_RenderedAsChildren()
    {
        var activity = new List<SubAgentActivityLine>
        {
            new(SubAgentActivityKind.ToolCall, "powershell"),
            new(SubAgentActivityKind.ToolCall, "edit"),
        };
        var agent = new StubSubAgent("a1", "Task agent", AgentChatCompletionState.Running, activity: activity);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([agent], []);

        Assert.Contains("<ul class=\"running-subagent-activity\">", html, StringComparison.Ordinal);
        Assert.Contains("powershell", html, StringComparison.Ordinal);
        Assert.Contains("edit", html, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(html, "<li>"));
    }

    [Fact]
    public void RunningSubAgentsPanel_NestedSubAgent_RenderedAsIndentedChild()
    {
        var nested = new StubSubAgent("a2", "Nested", AgentChatCompletionState.Running);
        var parent = new StubSubAgent("a1", "Parent", AgentChatCompletionState.Running, subAgents: [nested]);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([parent], []);

        Assert.Contains("data-navigate-agent-id=\"a1\"", html, StringComparison.Ordinal);
        Assert.Contains("data-navigate-agent-id=\"a2\"", html, StringComparison.Ordinal);
        Assert.Contains("data-depth=\"1\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningSubAgentsPanel_ToolCallLine_ShowsToolNameAndArgSummary()
    {
        var line = new SubAgentActivityLine(SubAgentActivityKind.ToolCall, "read_file(src/auth.ts)");
        var rendered = RunningSubAgentsHtmlTransformer.RenderActivityLine(line);

        Assert.StartsWith("📖 ", rendered, StringComparison.Ordinal);
        Assert.Contains("read_file", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningSubAgentsPanel_TextLine_ShowsTruncatedFirstLine()
    {
        var line = new SubAgentActivityLine(SubAgentActivityKind.AgentText, "Checking for SQL injection");
        var rendered = RunningSubAgentsHtmlTransformer.RenderActivityLine(line);

        Assert.StartsWith("💬 ", rendered, StringComparison.Ordinal);
        Assert.Contains("Checking for SQL injection", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityLine_MultiLineContent_TruncatedToFirstLine()
    {
        var line = new SubAgentActivityLine(SubAgentActivityKind.AgentText, "first line\r\nsecond line");
        var rendered = RunningSubAgentsHtmlTransformer.RenderActivityLine(line);

        Assert.Contains("first line", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("second line", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningSubAgentsHtmlTransformer_MultipleAgents_EachRenderedAsSiblingRow()
    {
        var a1 = new StubSubAgent("a1", "Code Reviewer", AgentChatCompletionState.Running);
        var a2 = new StubSubAgent("a2", "Doc Writer", AgentChatCompletionState.Running);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([a1, a2], []);

        Assert.Contains("data-navigate-agent-id=\"a1\"", html, StringComparison.Ordinal);
        Assert.Contains("data-navigate-agent-id=\"a2\"", html, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(html, "running-subagent-row"));
    }

    [Fact]
    public void RunningSubAgentsPanel_CompletedSubAgent_NotShownInPanel()
    {
        var running = new StubSubAgent("a1", "Running", AgentChatCompletionState.Running);
        var completed = new StubSubAgent("a2", "Done", AgentChatCompletionState.Succeeded);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([running, completed], []);

        Assert.Contains("data-navigate-agent-id=\"a1\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-navigate-agent-id=\"a2\"", html, StringComparison.Ordinal);
    }

    // ── Clickability ──────────────────────────────────────────────────────────

    [Fact]
    public void RunningSubAgentsPanel_SubAgentRow_IsClickable_NavigatesToSubAgentView()
    {
        var agent = new StubSubAgent("agent-xyz", "My Agent", AgentChatCompletionState.Running);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([agent], []);

        Assert.Contains("data-navigate-agent-id=\"agent-xyz\"", html, StringComparison.Ordinal);
        Assert.Contains("<button", html, StringComparison.Ordinal);
    }

    // ── Ancestry breadcrumb ───────────────────────────────────────────────────
    // Issue #1046: the ancestor breadcrumb was lifted out of the running panel into an
    // always-present region in ChatOutputHtmlModel. These tests now verify the panel
    // no longer renders the breadcrumb (the always-present breadcrumb is tested in
    // AgentChatOutputJumpLinkTests).

    [Fact]
    public void RunningSubAgentsPanel_AncestryChain_NoBreadcrumbInRunningPanel()
    {
        var root = new StubRunningSubAgent("root-id", "RootAgent");
        var mid = new StubRunningSubAgent("mid-id", "MidAgent");
        var current = new StubRunningSubAgent("cur-id", "CurrentAgent");
        var ancestors = new List<IRunningSubAgent> { root, mid, current };

        var agent = new StubSubAgent("a1", "Worker", AgentChatCompletionState.Running);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([agent], ancestors);

        // Breadcrumb has been lifted to the always-present region; panel should not duplicate it.
        Assert.DoesNotContain("running-subagents-breadcrumb", html, StringComparison.Ordinal);
        // The panel should still render the running agent row.
        Assert.Contains("data-navigate-agent-id=\"a1\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningSubAgentsPanel_AncestryChain_ClickableAgentsAreChildren_NotAncestors()
    {
        var root = new StubRunningSubAgent("root-id", "RootAgent");
        var current = new StubRunningSubAgent("cur-id", "CurrentAgent");
        var ancestors = new List<IRunningSubAgent> { root, current };

        var agent = new StubSubAgent("a1", "Worker", AgentChatCompletionState.Running);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([agent], ancestors);

        // Only the running child agent should have a navigate button, not the ancestors.
        Assert.Contains("data-navigate-agent-id=\"a1\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-navigate-agent-id=\"root-id\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-navigate-agent-id=\"cur-id\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningSubAgentsPanel_UpdatedInPlace_WhenPanelAlreadyPresent()
    {
        var agent = new StubSubAgent("a1", "Code Reviewer", AgentChatCompletionState.Running);
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay> { agent };
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);

        sink.Clear();

        // Trigger a re-render while still running (ActivityChanged)
        agent.RaiseActivityChanged();

        // Should remove the old inner node and append a fresh one into the sentinel, never inject After.
        Assert.Contains(sink.Operations, op =>
            op.Kind == "remove" &&
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelInnerId);
        Assert.Contains(sink.Operations, op =>
            op.Kind == "update" &&
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelSentinelId &&
            op.Location == ChatOutputUpdateLocation.Append);
        Assert.DoesNotContain(sink.Operations, op =>
            op.Location == ChatOutputUpdateLocation.After);
    }

    [Fact]
    public void RunningSubAgentsPanel_SubAgentActivityLine_ShowsSubAgentName()
    {
        var line = new SubAgentActivityLine(SubAgentActivityKind.SubAgent, "nested-agent");
        var rendered = RunningSubAgentsHtmlTransformer.RenderActivityLine(line);

        Assert.StartsWith("⟳ ", rendered, StringComparison.Ordinal);
        Assert.Contains("nested-agent", rendered, StringComparison.Ordinal);
    }

    // ── Issue #798: Header, borders, hierarchy, completion state ─────────────

    [Fact]
    public void RunningSubAgentsPanel_Header_ShowsRunningSubAgentsLabel()
    {
        var agent = new StubSubAgent("a1", "Test Agent", AgentChatCompletionState.Running);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([agent], []);

        Assert.Contains("<h4 class=\"running-subagents-header\">[Running sub-agents]</h4>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningSubAgentsPanel_Row_HasNoBoxBorder()
    {
        var agent = new StubSubAgent("a1", "Test Agent", AgentChatCompletionState.Running);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([agent], []);

        // Verify no Border wrapper elements are present in the HTML
        Assert.DoesNotContain("Border", html, StringComparison.Ordinal);
        Assert.DoesNotContain("interactive-row", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningSubAgentsPanel_NestedSubAgent_IndentedByDepth()
    {
        var childAgent = new StubSubAgent("a2", "Child Agent", AgentChatCompletionState.Running);
        var parentAgent = new StubSubAgent("a1", "Parent Agent", AgentChatCompletionState.Running, subAgents: [childAgent]);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([parentAgent], []);

        Assert.Contains("data-depth=\"1\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningSubAgentsPanel_StoppedSubAgent_RemovedImmediately_OnCompletionStateChange()
    {
        var agent = new StubSubAgent("a1", "Test Agent", AgentChatCompletionState.Running);
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay> { agent };
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);

        sink.Clear();

        // Change completion state - this should trigger immediate re-render without needing ActivityChanged
        agent.SetCompletionState(AgentChatCompletionState.Succeeded);

        // Verify panel inner node was removed immediately on CompletionStateChanged, with nothing re-appended.
        Assert.Contains(sink.Operations, op =>
            op.Kind == "remove" &&
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelInnerId);
        Assert.DoesNotContain(sink.Operations, op =>
            op.Kind == "update" &&
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelSentinelId);
    }

    [Fact]
    public void RunningSubAgentsPanel_AllSubAgentsStopped_ContainerHidden_NotLeftBlank()
    {
        var agent = new StubSubAgent("a1", "Test Agent", AgentChatCompletionState.Running);
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay> { agent };
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);

        sink.Clear();

        agent.SetCompletionState(AgentChatCompletionState.Succeeded);

        // Verify that when all agents stop, the inner panel node is removed (hiding the panel)
        // while the persistent sentinel remains untouched.
        Assert.Contains(sink.Operations, op =>
            op.Kind == "remove" &&
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelInnerId);
        Assert.DoesNotContain(sink.Operations, op =>
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelSentinelId);
    }

    [Fact]
    public void RunningSubAgentsPanel_DepthTwo_NestedSubAgent_MoreIndentedThanDepthOne()
    {
        var grandchildAgent = new StubSubAgent("a3", "Grandchild Agent", AgentChatCompletionState.Running);
        var childAgent = new StubSubAgent("a2", "Child Agent", AgentChatCompletionState.Running, subAgents: [grandchildAgent]);
        var parentAgent = new StubSubAgent("a1", "Parent Agent", AgentChatCompletionState.Running, subAgents: [childAgent]);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([parentAgent], []);

        // Verify depth-1 and depth-2 attributes are present
        Assert.Contains("data-depth=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("data-depth=\"2\"", html, StringComparison.Ordinal);

        // Verify the grandchild (depth 2) appears after the child (depth 1)
        var depth1Index = html.IndexOf("data-depth=\"1\"", StringComparison.Ordinal);
        var depth2Index = html.IndexOf("data-depth=\"2\"", StringComparison.Ordinal);
        Assert.True(depth2Index > depth1Index, "Depth-2 agent should appear after depth-1 agent in HTML");
    }


    // ── Issue #893: sentinel/inner protocol tests ─────────────────────────────

    [Fact]
    public void SubAgentPanel_Update_RemovesOldInnerAndAppendsNewInner()
    {
        var agent = new StubSubAgent("a1", "Code Reviewer", AgentChatCompletionState.Running);
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay> { agent };
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);
        sink.Clear();

        agent.RaiseActivityChanged();

        Assert.Equal(2, sink.Operations.Count);
        Assert.Equal("remove", sink.Operations[0].Kind);
        Assert.Equal(ChatOutputHtmlRenderer.SubAgentPanelInnerId, sink.Operations[0].Path);
        Assert.Equal("update", sink.Operations[1].Kind);
        Assert.Equal(ChatOutputHtmlRenderer.SubAgentPanelSentinelId, sink.Operations[1].Path);
        Assert.Equal(ChatOutputUpdateLocation.Append, sink.Operations[1].Location);
        Assert.Contains($"id=\"{ChatOutputHtmlRenderer.SubAgentPanelInnerId}\"", sink.Operations[1].Content);
    }

    [Fact]
    public void SubAgentPanel_Update_NeverReplacesSentinel()
    {
        var agent = new StubSubAgent("a1", "Code Reviewer", AgentChatCompletionState.Running);
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay> { agent };
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);

        agent.RaiseActivityChanged();
        agent.SetCompletionState(AgentChatCompletionState.Succeeded);
        subAgents.Add(new StubSubAgent("a2", "Another", AgentChatCompletionState.Running));

        Assert.DoesNotContain(sink.Operations, op =>
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelSentinelId &&
            op.Location == ChatOutputUpdateLocation.Replace);
    }

    [Fact]
    public void SubAgentPanel_Update_NeverRemovesSentinel()
    {
        var agent = new StubSubAgent("a1", "Code Reviewer", AgentChatCompletionState.Running);
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay> { agent };
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);

        agent.RaiseActivityChanged();
        agent.SetCompletionState(AgentChatCompletionState.Succeeded);
        subAgents.Clear();

        Assert.DoesNotContain(sink.Operations, op =>
            op.Kind == "remove" &&
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelSentinelId);
    }

    [Fact]
    public void SubAgentPanel_EmptyState_OnlyRemovesInner()
    {
        var agent = new StubSubAgent("a1", "Code Reviewer", AgentChatCompletionState.Running);
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay> { agent };
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);
        sink.Clear();

        agent.SetCompletionState(AgentChatCompletionState.Succeeded);

        var op = Assert.Single(sink.Operations);
        Assert.Equal("remove", op.Kind);
        Assert.Equal(ChatOutputHtmlRenderer.SubAgentPanelInnerId, op.Path);
    }

    [Fact]
    public void SubAgentPanel_RemoveMissingInner_IsAllowedByBrowserContract()
    {
        // The very first render removes an inner node that does not exist yet; the shell's
        // applyCommand treats a remove of a missing id as a no-op, so this is always safe.
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay>();
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);

        var op = Assert.Single(sink.Operations);
        Assert.Equal("remove", op.Kind);
        Assert.Equal(ChatOutputHtmlRenderer.SubAgentPanelInnerId, op.Path);
    }

    // ── Issue #1046: ancestor breadcrumb decoupled from hasRunning ────────────

    [Fact]
    public void AncestorBreadcrumb_RenderedEvenWhenNoRunningChildren()
    {
        // The running-panel breadcrumb was previously gated behind hasRunning. Now the
        // always-present breadcrumb is emitted in the ChatOutputHtmlModel constructor,
        // so the running panel no longer includes it. Verify the panel renders without
        // a breadcrumb (no duplication) even when ancestors are provided.
        var root = new StubRunningSubAgent("root-id", "RootAgent");
        var current = new StubRunningSubAgent("cur-id", "CurrentAgent");
        var ancestors = new List<IRunningSubAgent> { root, current };

        // All sub-agents completed — no running children.
        var completed = new StubSubAgent("a1", "Done", AgentChatCompletionState.Succeeded);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([completed], ancestors);

        // The panel should NOT include the running-subagents-breadcrumb (it's been lifted out).
        Assert.DoesNotContain("running-subagents-breadcrumb", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Regression_BugC_SubAgentPanelSentinel_NeverReplacedOrRemoved()
    {
        // Full lifecycle: appear, activity, nested agents, completion, reappear, clear. At no point
        // may the persistent sentinel be replaced or removed — it is the permanent re-append anchor.
        var subAgents = new ObservableCollection<IRunningSubAgentDisplay>();
        var sink = new RecordingSink();
        using var transformer = new RunningSubAgentsHtmlTransformer(subAgents, [], sink);

        var nested = new StubSubAgent("a2", "Nested", AgentChatCompletionState.Running);
        var agent = new StubSubAgent("a1", "Parent", AgentChatCompletionState.Running, subAgents: [nested]);
        subAgents.Add(agent);
        agent.RaiseActivityChanged();
        agent.SetCompletionState(AgentChatCompletionState.Succeeded);
        var second = new StubSubAgent("a3", "Second wave", AgentChatCompletionState.Running);
        subAgents.Add(second);
        second.SetCompletionState(AgentChatCompletionState.Failed);
        subAgents.Clear();

        Assert.DoesNotContain(sink.Operations, op =>
            op.Path == ChatOutputHtmlRenderer.SubAgentPanelSentinelId &&
            (op.Kind == "remove" || op.Location == ChatOutputUpdateLocation.Replace));

        // Every append of a fresh panel targets the sentinel, and every removal targets the inner node.
        Assert.All(
            sink.Operations.Where(op => op.Kind == "update"),
            op =>
            {
                Assert.Equal(ChatOutputHtmlRenderer.SubAgentPanelSentinelId, op.Path);
                Assert.Equal(ChatOutputUpdateLocation.Append, op.Location);
            });
        Assert.All(
            sink.Operations.Where(op => op.Kind == "remove"),
            op => Assert.Equal(ChatOutputHtmlRenderer.SubAgentPanelInnerId, op.Path));
    }

    private static int CountOccurrences(string text, string substring)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(substring, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += substring.Length;
        }

        return count;
    }

    private sealed record Operation(string Kind, string Path, ChatOutputUpdateLocation Location, string Content);

    private sealed class RecordingSink : IChatOutputHtmlSink
    {
        public List<Operation> Operations { get; } = [];

        public void Clear() => this.Operations.Clear();

        public void UpdateContent(string path, ChatOutputUpdateLocation location, string content)
            => this.Operations.Add(new Operation("update", path, location, content));

        public void RemoveContent(string path)
            => this.Operations.Add(new Operation("remove", path, ChatOutputUpdateLocation.Replace, string.Empty));

        public void ScrollToBottom()
            => this.Operations.Add(new Operation("scroll", string.Empty, ChatOutputUpdateLocation.Replace, string.Empty));
    }

    private sealed class StubSubAgent : IRunningSubAgentDisplay
    {
        private AgentChatCompletionState completionState;
        private readonly IReadOnlyList<IRunningSubAgentDisplay> subAgents;

        public StubSubAgent(
            string agentId,
            string displayName,
            AgentChatCompletionState completionState,
            IReadOnlyList<SubAgentActivityLine>? activity = null,
            IReadOnlyList<IRunningSubAgentDisplay>? subAgents = null)
        {
            this.AgentId = agentId;
            this.DisplayName = displayName;
            this.completionState = completionState;
            this.RecentActivity = activity ?? [];
            this.subAgents = subAgents ?? [];
        }

        public string AgentId { get; }
        public string DisplayName { get; }
        public string Description => string.Empty;
        public AgentChatCompletionState CompletionState => this.completionState;
        public IReadOnlyList<SubAgentActivityLine> RecentActivity { get; }
        public IReadOnlyList<IRunningSubAgentDisplay> SubAgents => this.subAgents;

        public event EventHandler? ActivityChanged;
        public event EventHandler? CompletionStateChanged;

        public void SetCompletionState(AgentChatCompletionState state)
        {
            this.completionState = state;
            this.CompletionStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseActivityChanged() => this.ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class StubRunningSubAgent : IRunningSubAgent
    {
        public StubRunningSubAgent(string agentId, string displayName)
        {
            this.AgentId = agentId;
            this.DisplayName = displayName;
        }

        public string AgentId { get; }
        public string DisplayName { get; }
        public string Description => string.Empty;
        public AgentChatCompletionState CompletionState => AgentChatCompletionState.Running;
        public DateTime LastUpdatedAt => DateTime.UtcNow;
        public IReadOnlyList<IRunningSubAgent> SubAgents => [];
    }
}
