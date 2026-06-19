using System;
using System.Collections.Generic;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.ScheduledTools;

/// <summary>
/// Maps a <c>tool-type</c> discriminator to its <see cref="IWorkspaceTool"/> implementation.
/// </summary>
public sealed class ScheduledToolRegistry
{
    private readonly IReadOnlyDictionary<string, IWorkspaceTool> toolsByType;

    public ScheduledToolRegistry(IEnumerable<IWorkspaceTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var map = new Dictionary<string, IWorkspaceTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.ToolType))
            {
                throw new ArgumentException("A scheduled tool must declare a non-empty tool type.", nameof(tools));
            }

            if (!map.TryAdd(tool.ToolType, tool))
            {
                throw new ArgumentException(
                    $"More than one scheduled tool is registered for tool type '{tool.ToolType}'.",
                    nameof(tools));
            }
        }

        this.toolsByType = map;
    }

    /// <summary>Returns the tool registered for the given type, if any.</summary>
    public bool TryGetTool(string toolType, out IWorkspaceTool tool)
    {
        return this.toolsByType.TryGetValue(toolType, out tool!);
    }

    /// <summary>Returns the tool registered for the given type, or throws if none is registered.</summary>
    public IWorkspaceTool GetTool(string toolType)
    {
        if (!this.TryGetTool(toolType, out var tool))
        {
            throw new InvalidOperationException($"No scheduled tool is registered for tool type '{toolType}'.");
        }

        return tool;
    }
}
