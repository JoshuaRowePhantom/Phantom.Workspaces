using System.Text;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

/// <summary>
/// Translates an <see cref="IToolVisualizerFactory.Visualize"/> return value into an HTML string
/// fragment (or <see langword="null"/> to fall back to the generic collapsible renderer).
/// <see cref="StatusUpdate"/> results are also routed to an <see cref="IAgentStatusSink"/>.
/// </summary>
internal static class ToolVisualizationInterpreter
{
    /// <summary>
    /// Interprets <paramref name="factoryResult"/> and returns the HTML to emit for the content
    /// block, or <see langword="null"/> when the caller should use the generic collapsible fallback.
    /// </summary>
    /// <param name="factoryResult">The value returned by <see cref="IToolVisualizerFactory.Visualize"/>.</param>
    /// <param name="contentId">Element id for the rendered block.</param>
    /// <param name="statusSink">Receives <see cref="StatusUpdate"/> field updates (may be null to suppress).</param>
    public static string? Interpret(
        object? factoryResult,
        string contentId,
        IAgentStatusSink? statusSink)
    {
        switch (factoryResult)
        {
            case null:
                return null;

            case Summary summary:
                return RenderExpandedDetails(contentId, summary);

            case StatusUpdate update:
                statusSink?.UpdateStatus(update.Field, update.Value);
                return update.ChatSummary is { } chatSummary
                    ? RenderCollapsedDetails(contentId, chatSummary)
                    : string.Empty;

            default:
                return null;
        }
    }

    private static string RenderExpandedDetails(string contentId, Summary summary)
    {
        var builder = new StringBuilder();
        builder.Append("<details class=\"chat-content chat-tool\" data-copy-target open id=\"")
            .Append(contentId).Append("\">");
        builder.Append("<summary class=\"chat-collapsible-summary\">")
            .Append(ChatOutputHtmlRenderer.HtmlEscape(summary.Label))
            .Append("</summary>");
        if (!string.IsNullOrEmpty(summary.HtmlBody))
        {
            builder.Append("<div class=\"chat-collapsible-body\">").Append(summary.HtmlBody).Append("</div>");
        }

        builder.Append("</details>");
        return builder.ToString();
    }

    private static string RenderCollapsedDetails(string contentId, string chatSummary)
    {
        var builder = new StringBuilder();
        builder.Append("<details class=\"chat-content chat-tool\" data-copy-target id=\"")
            .Append(contentId).Append("\">");
        builder.Append("<summary class=\"chat-collapsible-summary\">")
            .Append(ChatOutputHtmlRenderer.HtmlEscape(chatSummary))
            .Append("</summary>");
        builder.Append("</details>");
        return builder.ToString();
    }
}
