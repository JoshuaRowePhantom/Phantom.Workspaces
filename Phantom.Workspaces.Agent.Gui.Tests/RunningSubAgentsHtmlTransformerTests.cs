using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        Assert.Contains(sink.Operations, op =>
            op.Kind == "update" &&
            op.Path == ChatOutputHtmlRenderer.RunningSubAgentsContainerId &&
            op.Location == ChatOutputUpdateLocation.Replace &&
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

        Assert.Contains(sink.Operations, op =>
            op.Kind == "update" &&
            op.Path == ChatOutputHtmlRenderer.RunningSubAgentsContainerId &&
            op.Location == ChatOutputUpdateLocation.Replace &&
            op.Content == string.Empty);
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

    [Fact]
    public void RunningSubAgentsPanel_AncestryChain_ShowsFromRootToCurrentAgent()
    {
        var root = new StubRunningSubAgent("root-id", "RootAgent");
        var mid = new StubRunningSubAgent("mid-id", "MidAgent");
        var current = new StubRunningSubAgent("cur-id", "CurrentAgent");
        var ancestors = new List<IRunningSubAgent> { root, mid, current };

        var agent = new StubSubAgent("a1", "Worker", AgentChatCompletionState.Running);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([agent], ancestors);

        Assert.Contains("running-subagents-breadcrumb", html, StringComparison.Ordinal);
        Assert.Contains("RootAgent", html, StringComparison.Ordinal);
        Assert.Contains("MidAgent", html, StringComparison.Ordinal);
        Assert.Contains("CurrentAgent", html, StringComparison.Ordinal);
        Assert.Contains("(root)", html, StringComparison.Ordinal);
        Assert.Contains("(current)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningSubAgentsPanel_AncestryChain_EachAncestor_IsClickable()
    {
        var root = new StubRunningSubAgent("root-id", "RootAgent");
        var current = new StubRunningSubAgent("cur-id", "CurrentAgent");
        var ancestors = new List<IRunningSubAgent> { root, current };

        var agent = new StubSubAgent("a1", "Worker", AgentChatCompletionState.Running);
        var html = RunningSubAgentsHtmlTransformer.BuildPanelHtml([agent], ancestors);

        Assert.Contains("data-navigate-agent-id=\"root-id\"", html, StringComparison.Ordinal);
        Assert.Contains("data-navigate-agent-id=\"cur-id\"", html, StringComparison.Ordinal);
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

        // Should Replace the existing panel on RunningSubAgentsContainerId, not inject After
        Assert.Contains(sink.Operations, op =>
            op.Kind == "update" &&
            op.Path == ChatOutputHtmlRenderer.RunningSubAgentsContainerId &&
            op.Location == ChatOutputUpdateLocation.Replace);
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

        public void SetCompletionState(AgentChatCompletionState state) => this.completionState = state;

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
