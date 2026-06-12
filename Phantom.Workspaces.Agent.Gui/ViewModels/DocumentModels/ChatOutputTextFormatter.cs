using System.Text;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

internal static class ChatOutputTextFormatter
{
    public static string BuildTranscript(
        IReadOnlyList<AgentChatHistoryItem> history,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        bool includeReasoningContent)
    {
        var builder = new StringBuilder();
        var hasAnyMessage = false;

        foreach (var item in history)
        {
            AppendMessage(builder, item, includeReasoningContent, isRunning: false);
            hasAnyMessage = true;
        }

        foreach (var runningItem in runningItems)
        {
            if (runningItem.Items.Count == 0)
            {
                continue;
            }

            foreach (var item in runningItem.Items)
            {
                if (!hasAnyMessage)
                {
                    hasAnyMessage = true;
                }

                AppendMessage(builder, item, includeReasoningContent, isRunning: true);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendMessage(
        StringBuilder builder,
        AgentChatHistoryItem item,
        bool includeReasoningContent,
        bool isRunning)
    {
        var role = item.Role.Value.ToLowerInvariant();
        if (isRunning)
        {
            role = $"{role} (running)";
        }

        builder.AppendLine(role);
        foreach (var content in item.Contents)
        {
            AppendContent(builder, content, includeReasoningContent);
        }

        builder.AppendLine();
    }

    private static void AppendContent(
        StringBuilder builder,
        AIContent content,
        bool includeReasoningContent)
    {
        switch (content)
        {
            case TextReasoningContent reasoningContent when includeReasoningContent && !string.IsNullOrWhiteSpace(reasoningContent.Text):
                builder.AppendLine(reasoningContent.Text);
                return;
            case TextReasoningContent:
                return;
            case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                builder.AppendLine(textContent.Text);
                return;
            case ErrorContent errorContent:
                builder.AppendLine(errorContent.Message);
                return;
            case FunctionCallContent functionCall:
                builder.AppendLine($"tool call: {functionCall.Name}");
                AppendMultiline(builder, DocumentBlockUtilities.PrettyJson(functionCall.Arguments));
                return;
            case FunctionResultContent functionResult:
                builder.AppendLine($"tool result: {functionResult.CallId}");
                AppendMultiline(builder, DocumentBlockUtilities.PrettyJson(functionResult.Result));
                return;
            case DataContent dataContent:
                var mediaLabel = string.IsNullOrWhiteSpace(dataContent.MediaType)
                    ? "[data]"
                    : $"[{dataContent.MediaType}]";
                builder.AppendLine(mediaLabel);
                return;
            case UriContent uriContent:
                builder.AppendLine(uriContent.Uri.ToString());
                return;
            default:
                var text = content.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.AppendLine(text);
                }

                return;
        }
    }

    private static void AppendMultiline(
        StringBuilder builder,
        string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        builder.AppendLine(text);
    }
}
