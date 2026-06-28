using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

/// <summary>
/// Produces visualizations for tools emitted through the Copilot SDK mapping, including GitHub
/// Copilot built-in tools and the <c>report_intent</c> built-in tool.
/// </summary>
public sealed class CopilotToolVisualizerFactory : IToolVisualizerFactory
{
    public object? Visualize(ToolVisualizationContext context)
    {
        return context.Content switch
        {
            FunctionCallContent call => VisualizeCall(call),
            FunctionResultContent => null,
            _ => null,
        };
    }

    private static object? VisualizeCall(FunctionCallContent call)
    {
        if (string.Equals(call.Name, "report_intent", StringComparison.OrdinalIgnoreCase))
        {
            return VisualizeReportIntentCall(call);
        }

        return null;
    }

    private static object? VisualizeReportIntentCall(FunctionCallContent call)
    {
        var intentText = ExtractIntentText(call.Arguments);
        if (intentText is null)
        {
            return null;
        }

        return new StatusUpdate(AgentStatusField.Intent, intentText, ChatSummary: null);
    }

    private static string? ExtractIntentText(IDictionary<string, object?>? args)
    {
        if (args is null)
        {
            return null;
        }

        if (args.TryGetValue("intent", out var raw))
        {
            return raw switch
            {
                string s => s,
                JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
                _ => raw?.ToString(),
            };
        }

        return null;
    }
}
