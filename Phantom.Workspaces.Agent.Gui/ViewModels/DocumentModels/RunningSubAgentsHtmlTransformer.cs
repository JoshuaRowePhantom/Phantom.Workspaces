using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

/// <summary>
/// Maintains the <c>#running-subagents</c> panel inserted after <c>#chat-history</c>. The panel is
/// present while at least one entry in <paramref name="subAgents"/> has
/// <see cref="AgentChatCompletionState.Running"/> state and is removed as soon as none remain.
/// Each running sub-agent is rendered as a clickable row with its display name, up to
/// <see cref="MaxActivityLines"/> recent activity lines, and any running nested sub-agents as
/// indented children. An ancestry breadcrumb above the rows allows navigation to each ancestor.
/// </summary>
internal sealed class RunningSubAgentsHtmlTransformer : IDisposable
{
    public const string ContainerId = "running-subagents";
    internal const int MaxActivityLines = 5;

    private readonly IChatOutputHtmlSink sink;
    private readonly IReadOnlyList<IRunningSubAgentDisplay> subAgents;
    private readonly IReadOnlyList<IRunningSubAgent> ancestors;
    private readonly Dictionary<IRunningSubAgentDisplay, EventHandler> activityHandlers = new(ReferenceEqualityComparer<IRunningSubAgentDisplay>.Instance);
    private bool isPanelPresent;

    public RunningSubAgentsHtmlTransformer(
        IReadOnlyList<IRunningSubAgentDisplay> subAgents,
        IReadOnlyList<IRunningSubAgent> ancestors,
        IChatOutputHtmlSink sink)
    {
        ArgumentNullException.ThrowIfNull(subAgents);
        ArgumentNullException.ThrowIfNull(ancestors);
        ArgumentNullException.ThrowIfNull(sink);

        this.sink = sink;
        this.subAgents = subAgents;
        this.ancestors = ancestors;

        if (subAgents is INotifyCollectionChanged notifier)
        {
            notifier.CollectionChanged += this.OnSubAgentsChanged;
        }

        this.SyncActivitySubscriptions();
        this.FullRender();
    }

    private void OnSubAgentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.SyncActivitySubscriptions();
        this.FullRender();
    }

    private void OnActivityChanged(object? sender, EventArgs e)
        => this.FullRender();

    private void SyncActivitySubscriptions()
    {
        var toRemove = this.activityHandlers.Keys.Where(k => !this.subAgents.Contains(k)).ToList();
        foreach (var agent in toRemove)
        {
            agent.ActivityChanged -= this.activityHandlers[agent];
            this.activityHandlers.Remove(agent);
        }

        foreach (var agent in this.subAgents)
        {
            if (this.activityHandlers.ContainsKey(agent))
            {
                continue;
            }

            EventHandler handler = this.OnActivityChanged;
            agent.ActivityChanged += handler;
            this.activityHandlers[agent] = handler;
        }
    }

    private void FullRender()
    {
        var hasRunning = this.subAgents.Any(a => a.CompletionState == AgentChatCompletionState.Running);

        if (!hasRunning)
        {
            if (this.isPanelPresent)
            {
                this.sink.RemoveContent(ContainerId);
                this.isPanelPresent = false;
            }

            return;
        }

        var html = BuildPanelHtml(this.subAgents, this.ancestors);

        if (this.isPanelPresent)
        {
            this.sink.UpdateContent(ContainerId, ChatOutputUpdateLocation.Replace, html);
        }
        else
        {
            this.sink.UpdateContent(ChatOutputHtmlRenderer.HistoryContainerId, ChatOutputUpdateLocation.After, html);
            this.isPanelPresent = true;
        }
    }

    /// <summary>
    /// Builds the full panel HTML. Exposed as <see langword="internal"/> for unit tests that
    /// verify rendered content without instantiating the transformer.
    /// </summary>
    internal static string BuildPanelHtml(
        IReadOnlyList<IRunningSubAgentDisplay> subAgents,
        IReadOnlyList<IRunningSubAgent> ancestors)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"running-subagents-panel\" id=\"").Append(ContainerId).Append("\">");

        AppendBreadcrumb(sb, ancestors);

        foreach (var agent in subAgents)
        {
            if (agent.CompletionState == AgentChatCompletionState.Running)
            {
                AppendSubAgentRow(sb, agent, depth: 0);
            }
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    private static void AppendBreadcrumb(StringBuilder sb, IReadOnlyList<IRunningSubAgent> ancestors)
    {
        if (ancestors.Count == 0)
        {
            return;
        }

        sb.Append("<div class=\"running-subagents-breadcrumb\">");
        for (var i = 0; i < ancestors.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" &gt; ");
            }

            var ancestor = ancestors[i];
            var label = i == 0
                ? ancestor.DisplayName + " (root)"
                : i == ancestors.Count - 1
                    ? ancestor.DisplayName + " (current)"
                    : ancestor.DisplayName;

            sb.Append("<button class=\"running-subagent-link\" data-navigate-agent-id=\"")
              .Append(ChatOutputHtmlRenderer.HtmlEscape(ancestor.AgentId))
              .Append("\">")
              .Append(ChatOutputHtmlRenderer.HtmlEscape(label))
              .Append("</button>");
        }

        sb.Append("</div>");
    }

    private static void AppendSubAgentRow(StringBuilder sb, IRunningSubAgentDisplay agent, int depth)
    {
        if (depth > 0)
        {
            sb.Append("<div class=\"running-subagent-row\" data-depth=\"").Append(depth).Append("\">");
        }
        else
        {
            sb.Append("<div class=\"running-subagent-row\">");
        }

        sb.Append("<button class=\"running-subagent-link\" data-navigate-agent-id=\"")
          .Append(ChatOutputHtmlRenderer.HtmlEscape(agent.AgentId))
          .Append("\">⟳ ")
          .Append(ChatOutputHtmlRenderer.HtmlEscape(agent.DisplayName))
          .Append("</button>");

        var activity = agent.RecentActivity;
        if (activity.Count > 0)
        {
            sb.Append("<ul class=\"running-subagent-activity\">");
            foreach (var line in activity)
            {
                sb.Append("<li>").Append(RenderActivityLine(line)).Append("</li>");
            }

            sb.Append("</ul>");
        }

        foreach (var subAgent in agent.SubAgents)
        {
            if (subAgent.CompletionState == AgentChatCompletionState.Running)
            {
                AppendSubAgentRow(sb, subAgent, depth + 1);
            }
        }

        sb.Append("</div>");
    }

    /// <summary>
    /// Renders a single activity line as HTML. Exposed as <see langword="internal"/> for unit tests.
    /// </summary>
    internal static string RenderActivityLine(SubAgentActivityLine line)
        => line.Kind switch
        {
            SubAgentActivityKind.ToolCall => "📖 " + ChatOutputHtmlRenderer.HtmlEscape(line.Text),
            SubAgentActivityKind.AgentText => "💬 " + ChatOutputHtmlRenderer.HtmlEscape(line.Text),
            SubAgentActivityKind.SubAgent => "⟳ " + ChatOutputHtmlRenderer.HtmlEscape(line.Text),
            _ => ChatOutputHtmlRenderer.HtmlEscape(line.Text),
        };

    public void Dispose()
    {
        if (this.subAgents is INotifyCollectionChanged notifier)
        {
            notifier.CollectionChanged -= this.OnSubAgentsChanged;
        }

        foreach (var (agent, handler) in this.activityHandlers)
        {
            agent.ActivityChanged -= handler;
        }

        this.activityHandlers.Clear();
    }
}
