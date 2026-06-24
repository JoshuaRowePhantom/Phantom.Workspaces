using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>A scheduled tool-relationship shown in the scheduled tasks view.</summary>
public sealed class ScheduledTaskItemViewModel
{
    public ScheduledTaskItemViewModel(
        string toolDisplayName,
        string scheduleDisplayName,
        string targetDisplayName,
        string? note)
    {
        this.ToolDisplayName = toolDisplayName;
        this.ScheduleDisplayName = scheduleDisplayName;
        this.TargetDisplayName = targetDisplayName;
        this.Note = note;
    }

    public string ToolDisplayName { get; }

    public string ScheduleDisplayName { get; }

    public string TargetDisplayName { get; }

    public string? Note { get; }

    public bool HasNote => !string.IsNullOrWhiteSpace(this.Note);
}

/// <summary>
/// View model for the scheduled tasks view. Lists the scheduled <c>tool-relationship</c> entities
/// (tool + schedule + target, with their ids resolved to display names) and hosts the tool-execution
/// results tree so currently running and recently completed tool runs can be inspected.
/// </summary>
public sealed class ScheduledTasksViewModel : ViewModelBase
{
    private const string ToolRelationshipEntityType = "tool-relationship";

    private readonly EntityBroker entityBroker;
    private readonly EntityReferenceSearch entityReferenceSearch;
    private bool isLoading;

    public ScheduledTasksViewModel(
        EntityBroker entityBroker)
    {
        this.entityBroker = entityBroker ?? throw new ArgumentNullException(nameof(entityBroker));
        this.entityReferenceSearch = new EntityReferenceSearch(entityBroker);
        this.ToolResults = new ToolResultBrowserViewModel(entityBroker.EntityRepository.DataAccessLayer);
    }

    /// <summary>The scheduled tool-relationships.</summary>
    public ObservableCollection<ScheduledTaskItemViewModel> ScheduledTasks { get; } = new();

    /// <summary>The currently running and recently completed tool executions.</summary>
    public ToolResultBrowserViewModel ToolResults { get; }

    public bool IsLoading
    {
        get => this.isLoading;
        private set => this.SetProperty(ref this.isLoading, value);
    }

    public bool HasScheduledTasks => this.ScheduledTasks.Count > 0;

    public async Task RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        this.IsLoading = true;
        try
        {
            var items = await this.LoadScheduledTasksAsync(cancellationToken).ConfigureAwait(true);
            this.ScheduledTasks.Clear();
            foreach (var item in items)
            {
                this.ScheduledTasks.Add(item);
            }

            this.RaisePropertyChanged(nameof(this.HasScheduledTasks));
            await this.ToolResults.RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            this.IsLoading = false;
        }
    }

    private async Task<IReadOnlyList<ScheduledTaskItemViewModel>> LoadScheduledTasksAsync(
        CancellationToken cancellationToken)
    {
        var queryResult = await this.entityBroker.EntityRepository.DataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier { Value = "scheduled-tasks" },
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet { Values = [ToolRelationshipEntityType] },
                        },
                    },
                ],
                Timestamps = [null],
            },
            cancellationToken).ConfigureAwait(true);

        var items = new List<ScheduledTaskItemViewModel>();
        foreach (var snapshot in queryResult.Batches.SelectMany(batch => batch.Entities))
        {
            if (snapshot.Data is not JsonElement data
                || !data.TryGetProperty("participants", out var participants)
                || participants.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var toolName = await this.ResolveNameAsync(ReadReference(participants, "tool")).ConfigureAwait(true);
            var scheduleName = await this.ResolveNameAsync(ReadFirstReference(participants, "schedule")).ConfigureAwait(true);
            var targetName = await this.ResolveNameAsync(ReadFirstReference(participants, "target")).ConfigureAwait(true);
            var note = data.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String
                ? noteElement.GetString()
                : null;

            items.Add(new ScheduledTaskItemViewModel(toolName, scheduleName, targetName, note));
        }

        return items
            .OrderBy(item => item.ToolDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<string> ResolveNameAsync(
        string? entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return "(none)";
        }

        var candidate = await this.entityReferenceSearch.ResolveAsync(entityId).ConfigureAwait(true);
        return candidate?.DisplayName ?? entityId;
    }

    private static string? ReadReference(
        JsonElement participants,
        string propertyName)
        => participants.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? ReadFirstReference(
        JsonElement participants,
        string propertyName)
    {
        if (!participants.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    return item.GetString();
                }
            }
        }

        return null;
    }
}
