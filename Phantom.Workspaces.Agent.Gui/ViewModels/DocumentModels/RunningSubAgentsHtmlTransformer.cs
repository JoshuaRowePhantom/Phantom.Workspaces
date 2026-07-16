using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

/// <summary>
/// Maintains the sub-agent panel. The panel's inner node (<c>#subagent-panel-inner</c>) is rebuilt
/// inside the persistent <c>#subagent-panel-sentinel</c> shell region whenever sub-agent state
/// changes: the old inner node is removed and, while at least one entry in the sub-agent list has
/// <see cref="AgentChatCompletionState.Running"/> state, a fresh inner node is appended. Each
/// running sub-agent is rendered as a clickable row with its display name, up to
/// <see cref="MaxActivityLines"/> recent activity lines, and any running nested sub-agents as
/// indented children. An ancestry breadcrumb above the rows allows navigation to each ancestor.
/// </summary>
internal sealed class RunningSubAgentsHtmlTransformer : IDisposable
{
    internal const int MaxActivityLines = 5;

    private readonly IChatOutputHtmlSink sink;
    private readonly IReadOnlyList<IRunningSubAgentDisplay> subAgents;
    private readonly IReadOnlyList<IRunningSubAgent> ancestors;
    private readonly Dictionary<IRunningSubAgentDisplay, EventHandler> activityHandlers = new(ReferenceEqualityComparer<IRunningSubAgentDisplay>.Instance);
    private readonly Dictionary<IRunningSubAgentDisplay, EventHandler> completionStateHandlers = new(ReferenceEqualityComparer<IRunningSubAgentDisplay>.Instance);

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

    private void OnCompletionStateChanged(object? sender, EventArgs e)
        => this.FullRender();

    private void SyncActivitySubscriptions()
    {
        var toRemove = this.activityHandlers.Keys.Where(k => !this.subAgents.Contains(k)).ToList();
        foreach (var agent in toRemove)
        {
            agent.ActivityChanged -= this.activityHandlers[agent];
            this.activityHandlers.Remove(agent);
            
            if (this.completionStateHandlers.TryGetValue(agent, out var completionHandler))
            {
                agent.CompletionStateChanged -= completionHandler;
                this.completionStateHandlers.Remove(agent);
            }
        }

        foreach (var agent in this.subAgents)
        {
            if (this.activityHandlers.ContainsKey(agent))
            {
                continue;
            }

            EventHandler activityHandler = this.OnActivityChanged;
            agent.ActivityChanged += activityHandler;
            this.activityHandlers[agent] = activityHandler;
            
            EventHandler completionHandler = this.OnCompletionStateChanged;
            agent.CompletionStateChanged += completionHandler;
            this.completionStateHandlers[agent] = completionHandler;
        }
    }

    private void FullRender()
    {
        // The inner panel node is the only removable element; the sentinel wrapper is a persistent
        // shell region that is never replaced or removed. The browser swallows a Remove of a
        // missing id, so Remove-before-Append is always safe.
        this.sink.RemoveContent(ChatOutputHtmlRenderer.SubAgentPanelInnerId);

        var hasRunning = this.subAgents.Any(a => a.CompletionState == AgentChatCompletionState.Running);
        if (!hasRunning)
        {
            return;
        }

        this.sink.UpdateContent(
            ChatOutputHtmlRenderer.SubAgentPanelSentinelId,
            ChatOutputUpdateLocation.Append,
            BuildPanelHtml(this.subAgents, this.ancestors));
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
        sb.Append("<div class=\"running-subagents-panel\" id=\"").Append(ChatOutputHtmlRenderer.SubAgentPanelInnerId).Append("\">");
        sb.Append("<h4 class=\"running-subagents-header\">[Running sub-agents]</h4>");

        // Ancestor breadcrumb is now emitted unconditionally in the history container (issue #1046);
        // omit the running-panel duplicate to avoid double breadcrumbs.

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
          .Append("\">▷ ")
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
    {
        var text = TruncateToSingleLine(line.Text);
        return line.Kind switch
        {
            SubAgentActivityKind.ToolCall => "📖 " + ChatOutputHtmlRenderer.HtmlEscape(text),
            SubAgentActivityKind.AgentText => "💬 " + ChatOutputHtmlRenderer.HtmlEscape(text),
            SubAgentActivityKind.SubAgent => "⟳ " + ChatOutputHtmlRenderer.HtmlEscape(text),
            _ => ChatOutputHtmlRenderer.HtmlEscape(text),
        };
    }

    private static string TruncateToSingleLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index >= 0 ? text[..index] : text;
    }

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
        
        foreach (var (agent, handler) in this.completionStateHandlers)
        {
            agent.CompletionStateChanged -= handler;
        }

        this.activityHandlers.Clear();
        this.completionStateHandlers.Clear();
    }
}
