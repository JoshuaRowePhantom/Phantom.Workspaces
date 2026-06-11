using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Phantom.Workspaces.Llm;

internal static class McpClientToolListing
{
    public static async Task<AITool[]> ListToolsAsync(McpClient client, CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(options: null, cancellationToken);
        return tools.Cast<AITool>().ToArray();
    }

    public static string BuildOpenedToolsMessage(string itemKind, string displayName, IReadOnlyList<AITool> tools)
        => $"Opened {itemKind} '{displayName}'. {BuildLoadedToolsMessage(tools)}";

    public static string BuildLoadedToolsMessage(IReadOnlyList<AITool> tools)
    {
        if (tools.Count == 0)
        {
            return "Loaded tools: (none).";
        }

        var toolNames = tools
            .Select(tool => tool.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (toolNames.Length == 0)
        {
            return "Loaded tools: (unnamed tools).";
        }

        return $"Loaded tools:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", toolNames)}";
    }
}
