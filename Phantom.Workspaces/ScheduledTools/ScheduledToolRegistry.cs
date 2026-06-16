using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ScheduledTools;

/// <summary>
/// A tool that can be run by the scheduled-tool host. Implementations are keyed by the
/// <c>tool</c> entity's <see cref="ToolType"/> discriminator (see
/// <c>docs/design/scheduled-tools.md</c>).
/// </summary>
public interface IScheduledTool
{
    /// <summary>The <c>tool.type</c> discriminator this tool handles (for example, <c>vector-indexer</c>).</summary>
    string ToolType { get; }

    /// <summary>Runs the tool against its targets for a single scheduled execution.</summary>
    Task RunAsync(ScheduledToolContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The context passed to a scheduled tool for a single run: the tool entity, the entities it runs
/// against, and the data access layer it operates through.
/// </summary>
public sealed record ScheduledToolContext
{
    /// <summary>The <c>tool</c> entity's data (its <c>type</c> discriminator and parameters).</summary>
    public required JsonElement ToolEntity { get; init; }

    /// <summary>The target entities the tool should run against.</summary>
    public required IReadOnlyList<EntityId> TargetEntityIds { get; init; }

    /// <summary>The data access layer the tool operates through.</summary>
    public required IDataAccessLayer DataAccessLayer { get; init; }
}

/// <summary>
/// Maps a <c>tool.type</c> discriminator to its <see cref="IScheduledTool"/> implementation.
/// </summary>
public sealed class ScheduledToolRegistry
{
    private readonly IReadOnlyDictionary<string, IScheduledTool> toolsByType;

    public ScheduledToolRegistry(IEnumerable<IScheduledTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var map = new Dictionary<string, IScheduledTool>(StringComparer.Ordinal);
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
    public bool TryGetTool(string toolType, out IScheduledTool tool)
    {
        return this.toolsByType.TryGetValue(toolType, out tool!);
    }

    /// <summary>Returns the tool registered for the given type, or throws if none is registered.</summary>
    public IScheduledTool GetTool(string toolType)
    {
        if (!this.TryGetTool(toolType, out var tool))
        {
            throw new InvalidOperationException($"No scheduled tool is registered for tool type '{toolType}'.");
        }

        return tool;
    }
}
