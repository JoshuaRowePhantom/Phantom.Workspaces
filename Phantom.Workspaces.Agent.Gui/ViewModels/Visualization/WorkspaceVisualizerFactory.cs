using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

/// <summary>
/// Produces visualizations for Phantom.Workspaces embedded tools and filesystem MCP tools.
/// Recognizes well-known tool names and produces concise label + optional body summaries.
/// </summary>
public sealed class WorkspaceVisualizerFactory : IToolVisualizerFactory
{
    private static readonly HashSet<string> WorkspaceToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "workspaces_entity_get",
        "workspaces_entity_update",
        "workspaces_entity_generate_guid",
        "view",
        "Read",
        "Search",
        "make_directory",
        "remove_item",
        "move_item",
        "Edit",
        "EditApply",
        "DescribeEdit",
    };

    public object? Visualize(ToolVisualizationContext context)
    {
        return context.Content switch
        {
            FunctionCallContent call when WorkspaceToolNames.Contains(call.Name ?? string.Empty)
                => VisualizeCall(call),
            FunctionResultContent result when IsWorkspaceCallId(result.CallId)
                => VisualizeResult(result),
            _ => null,
        };
    }

    private static bool IsWorkspaceCallId(string? callId)
        => callId is not null;

    private static object? VisualizeCall(FunctionCallContent call)
    {
        var label = BuildCallLabel(call);
        return new Summary(label, null);
    }

    private static object? VisualizeResult(FunctionResultContent result)
    {
        var body = BuildResultBody(result.Result);
        return new Summary($"result: {result.CallId}", body);
    }

    private static string BuildCallLabel(FunctionCallContent call)
    {
        var args = call.Arguments;
        if (args is null || args.Count == 0)
        {
            return $"{call.Name}";
        }

        if (TryGetStringArg(args, "path", out var path)
            || TryGetStringArg(args, "file", out path))
        {
            var extras = new List<string>();
            if (TryGetArg(args, "start", out var start)) extras.Add($"start={start}");
            if (TryGetArg(args, "end", out var end)) extras.Add($"end={end}");
            if (TryGetStringArg(args, "operation", out var op)) extras.Add(op!);
            return extras.Count > 0
                ? $"{call.Name} {path} ({string.Join(", ", extras)})"
                : $"{call.Name} {path}";
        }

        return $"{call.Name}";
    }

    private static string? BuildResultBody(object? result)
    {
        if (result is null) return null;
        return result switch
        {
            string text => ChatOutputHtmlRenderer.HtmlEscape(text),
            JsonElement element => ChatOutputHtmlRenderer.HtmlEscape(element.ToString()),
            _ => ChatOutputHtmlRenderer.HtmlEscape(result.ToString() ?? string.Empty),
        };
    }

    private static bool TryGetStringArg(IDictionary<string, object?> args, string key, out string? value)
    {
        if (args.TryGetValue(key, out var raw))
        {
            value = raw switch
            {
                string s => s,
                JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
                _ => raw?.ToString(),
            };
            return value is not null;
        }

        value = null;
        return false;
    }

    private static bool TryGetArg(IDictionary<string, object?> args, string key, out object? value)
    {
        if (args.TryGetValue(key, out value))
        {
            return true;
        }

        return false;
    }
}
