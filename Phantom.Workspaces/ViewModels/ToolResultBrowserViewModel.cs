using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.ViewModels;

/// <summary>A node in the tool-execution-result tree (a host, a tool, a run, or a sub-task).</summary>
public sealed class ToolResultNodeViewModel
{
    private readonly Dictionary<string, ToolResultNodeViewModel> childrenByLabel = new(StringComparer.Ordinal);

    public ToolResultNodeViewModel(string label)
    {
        this.Label = label;
    }

    /// <summary>The node label (a name component, or a host display label at the root).</summary>
    public string Label { get; }

    /// <summary>Child nodes, ordered as added.</summary>
    public ObservableCollection<ToolResultNodeViewModel> Children { get; } = new();

    /// <summary>The run status (running/succeeded/failed) when this node is a tool-execution-result.</summary>
    public string? Status { get; set; }

    /// <summary>The tool name when this node is a tool-execution-result.</summary>
    public string? ToolName { get; set; }

    internal ToolResultNodeViewModel GetOrAddChild(string label)
    {
        if (!this.childrenByLabel.TryGetValue(label, out var child))
        {
            child = new ToolResultNodeViewModel(label);
            this.childrenByLabel[label] = child;
            this.Children.Add(child);
        }

        return child;
    }
}

/// <summary>
/// Enumerates hosts and lets the user navigate their tool-execution-result trees (see
/// <c>docs/design/scheduled-tools.md</c>). Results are stored under a host at the name path
/// <c>[ host..., "tool-executions", tool-name, start-time, ... ]</c>; this view-model rebuilds the
/// tree from those name paths. Call <see cref="RefreshAsync"/> to pick up live progress.
/// </summary>
public sealed class ToolResultBrowserViewModel : ViewModelBase
{
    private readonly IDataAccessLayer dataAccessLayer;

    public ToolResultBrowserViewModel(IDataAccessLayer dataAccessLayer)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
    }

    /// <summary>The hosts that have recorded tool executions, each rooting a result tree.</summary>
    public ObservableCollection<ToolResultNodeViewModel> Hosts { get; } = new();

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // No ConfigureAwait(false): the continuation mutates the UI-bound Hosts collection, so it
        // must resume on the captured (UI) synchronization context.
        var queryResult = await this.dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("tool-execution-results"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet([ToolExecutionResultWriter.ToolExecutionResultEntityType]),
                        },
                    },
                ],
            },
            cancellationToken);

        var hostsByLabel = new Dictionary<string, ToolResultNodeViewModel>(StringComparer.Ordinal);
        var rootHosts = new List<ToolResultNodeViewModel>();

        var results = queryResult.Batches
            .SelectMany(batch => batch.Entities)
            .Select(entity => entity.Data)
            .OfType<JsonElement>()
            .Select(ParseResult)
            .Where(result => result is not null)
            .Select(result => result!.Value)
            .OrderBy(result => string.Join("\u0001", result.SuffixComponents), StringComparer.Ordinal)
            .ToArray();

        foreach (var result in results)
        {
            if (!hostsByLabel.TryGetValue(result.HostLabel, out var hostNode))
            {
                hostNode = new ToolResultNodeViewModel(result.HostLabel);
                hostsByLabel[result.HostLabel] = hostNode;
                rootHosts.Add(hostNode);
            }

            var node = hostNode;
            foreach (var component in result.SuffixComponents)
            {
                node = node.GetOrAddChild(component);
            }

            node.Status = result.Status;
            node.ToolName = result.ToolName;
        }

        this.Hosts.Clear();
        foreach (var host in rootHosts)
        {
            this.Hosts.Add(host);
        }
    }

    private static ParsedResult? ParseResult(JsonElement entity)
    {
        if (!entity.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var name in names.EnumerateArray())
        {
            if (name.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var components = name.EnumerateArray()
                .Where(component => component.ValueKind == JsonValueKind.String)
                .Select(component => component.GetString()!)
                .ToArray();

            var executionsIndex = Array.IndexOf(components, ToolExecutionResultWriter.ToolExecutionsSegment);
            if (executionsIndex <= 0 || executionsIndex >= components.Length - 1)
            {
                continue;
            }

            var hostLabel = string.Join(" / ", components[..executionsIndex]);
            var suffix = components[(executionsIndex + 1)..];

            return new ParsedResult(
                hostLabel,
                suffix,
                entity.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null,
                entity.TryGetProperty("tool-name", out var toolName) && toolName.ValueKind == JsonValueKind.String ? toolName.GetString() : null);
        }

        return null;
    }

    private readonly record struct ParsedResult(
        string HostLabel,
        string[] SuffixComponents,
        string? Status,
        string? ToolName);
}
