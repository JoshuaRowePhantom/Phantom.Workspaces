using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public static class AgentChatSummaryExtractor
{
    private const int MaxChars = 100;

    /// <summary>
    /// Extracts a short live summary from the current agent running state.
    /// Scans running items newest-to-oldest for streaming text, then falls back to history.
    /// Also extracts the most recent tool call name.
    /// </summary>
    public static (string? TextSummary, string? ToolSummary) ExtractRunning(
        IReadOnlyList<AgentChatHistoryItem> history,
        IReadOnlyList<AgentChatRunningItem> runningItems)
    {
        string? textSummary = null;
        string? toolSummary = null;

        // Step 1: scan running items newest→oldest
        for (var ri = runningItems.Count - 1; ri >= 0; ri--)
        {
            var runningItem = runningItems[ri];
            for (var ii = runningItem.Items.Count - 1; ii >= 0; ii--)
            {
                var item = runningItem.Items[ii];

                if (textSummary is null)
                {
                    var text = string.Concat(item.Contents.OfType<TextContent>().Select(c => c.Text ?? string.Empty)).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        textSummary = TruncateAtWordBoundary(text, MaxChars);
                    }
                }

                if (toolSummary is null)
                {
                    var toolCall = item.Contents.OfType<FunctionCallContent>().FirstOrDefault();
                    if (toolCall is not null)
                    {
                        toolSummary = toolCall.Name;
                    }
                }

                if (textSummary is not null && toolSummary is not null)
                {
                    return (textSummary, toolSummary);
                }
            }
        }

        // Step 2: if no running text, scan history newest→oldest
        if (textSummary is null)
        {
            string? userFallback = null;
            for (var i = history.Count - 1; i >= 0; i--)
            {
                var item = history[i];

                if (item.Role == ChatRole.Assistant)
                {
                    var text = string.Concat(item.Contents.OfType<TextContent>().Select(c => c.Text ?? string.Empty)).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        textSummary = TruncateAtWordBoundary(text, MaxChars);
                        break;
                    }
                }

                if (item.Role == ChatRole.User && userFallback is null)
                {
                    var text = string.Concat(item.Contents.OfType<TextContent>().Select(c => c.Text ?? string.Empty)).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        userFallback = TruncateAtWordBoundary(text, MaxChars);
                    }
                }
            }

            textSummary ??= userFallback;
        }

        // Step 3: scan history for most recent tool call after last user message
        if (toolSummary is null)
        {
            var lastUserIndex = -1;
            for (var i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Role == ChatRole.User)
                {
                    lastUserIndex = i;
                    break;
                }
            }

            if (lastUserIndex >= 0)
            {
                for (var i = history.Count - 1; i > lastUserIndex; i--)
                {
                    var toolCall = history[i].Contents.OfType<FunctionCallContent>().FirstOrDefault();
                    if (toolCall is not null)
                    {
                        toolSummary = toolCall.Name;
                        break;
                    }
                }
            }
        }

        return (textSummary, toolSummary);
    }

    internal static string TruncateAtWordBoundary(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var cutIndex = text.LastIndexOf(' ', maxChars - 1);
        return cutIndex > 0
            ? text[..cutIndex] + "\u2026"
            : text[..maxChars] + "\u2026";
    }
}
