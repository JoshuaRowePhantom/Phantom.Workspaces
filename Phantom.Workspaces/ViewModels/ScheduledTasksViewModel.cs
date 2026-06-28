using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ScheduledTools;

namespace Phantom.Workspaces.ViewModels;

/// <summary>A scheduled tool-relationship shown in the scheduled tasks view.</summary>
public sealed class ScheduledTaskItemViewModel : ViewModelBase
{
    private bool isRunning;
    private bool hasFailure;
    private bool lastRunSucceeded;

    public ScheduledTaskItemViewModel(
        string toolType,
        string toolDisplayName,
        string scheduleDisplayName,
        string targetDisplayName,
        string? note)
    {
        this.ToolType = toolType;
        this.ToolDisplayName = toolDisplayName;
        this.ScheduleDisplayName = scheduleDisplayName;
        this.TargetDisplayName = targetDisplayName;
        this.Note = note;
    }

    /// <summary>The raw tool-type discriminator; matches <see cref="ToolRowViewModel.ToolType"/>.</summary>
    public string ToolType { get; }

    public string ToolDisplayName { get; }

    public string ScheduleDisplayName { get; }

    public string TargetDisplayName { get; }

    public string? Note { get; }

    public bool HasNote => !string.IsNullOrWhiteSpace(this.Note);

    /// <summary>Whether the tool is currently executing an in-flight run.</summary>
    public bool IsRunning
    {
        get => this.isRunning;
        set => this.SetProperty(ref this.isRunning, value);
    }

    /// <summary>True when the most-recent completed run failed.</summary>
    public bool HasFailure
    {
        get => this.hasFailure;
        set => this.SetProperty(ref this.hasFailure, value);
    }

    /// <summary>True when the most-recent completed run succeeded.</summary>
    public bool LastRunSucceeded
    {
        get => this.lastRunSucceeded;
        set => this.SetProperty(ref this.lastRunSucceeded, value);
    }
}

/// <summary>
/// View model for the scheduled tasks view. Lists the scheduled <c>tool-relationship</c> entities
/// (tool + schedule + target, with their ids resolved to display names) and hosts the tool-execution
/// results tree so currently running and recently completed tool runs can be inspected.
/// </summary>
public sealed class ScheduledTasksViewModel : ViewModelBase, IDisposable
{
    private const string ToolRelationshipEntityType = "tool-relationship";

    private readonly EntityBroker entityBroker;
    private readonly EntityReferenceSearch entityReferenceSearch;
    private readonly ScheduledToolPauseStateService? pauseStateService;
    private readonly EntityId hostEntityId;
    private readonly Action<Action> dispatch;
    private bool isLoading;
    private bool isToggleInProgress;
    private ScheduledTaskItemViewModel? selectedTask;

    public ScheduledTasksViewModel(
        EntityBroker entityBroker,
        ScheduledToolPauseStateService? pauseStateService = null,
        EntityId hostEntityId = default,
        ScheduledToolHost? scheduledToolHost = null,
        Action<Action>? dispatch = null)
    {
        this.entityBroker = entityBroker ?? throw new ArgumentNullException(nameof(entityBroker));
        this.entityReferenceSearch = new EntityReferenceSearch(entityBroker);
        this.ScheduledToolsRunning = scheduledToolHost is not null
            ? new ScheduledToolsRunningViewModel(scheduledToolHost, entityBroker.EntityRepository.DataAccessLayer, dispatch)
            : null;
        this.pauseStateService = pauseStateService;
        this.hostEntityId = hostEntityId;
        this.dispatch = dispatch ?? (action => action());
        this.TogglePauseCommand = new RelayCommand(
            _ => _ = this.TogglePauseAsync(),
            _ => this.CanTogglePause);

        if (this.pauseStateService is not null)
        {
            this.pauseStateService.PauseStateChanged += this.OnPauseStateChanged;
        }

        if (this.ScheduledToolsRunning is not null)
        {
            this.ScheduledToolsRunning.PropertyChanged += this.OnRunningToolsPropertyChanged;
        }
    }

    /// <summary>The scheduled tool-relationships.</summary>
    public ObservableCollection<ScheduledTaskItemViewModel> ScheduledTasks { get; } = new();

    /// <summary>The running and historical tool executions; null when no tool host is available.</summary>
    public ScheduledToolsRunningViewModel? ScheduledToolsRunning { get; }

    /// <summary>The currently selected task in the top pane; drives <see cref="SelectedToolRow"/>.</summary>
    public ScheduledTaskItemViewModel? SelectedTask
    {
        get => this.selectedTask;
        set
        {
            if (this.SetProperty(ref this.selectedTask, value))
            {
                this.RaisePropertyChanged(nameof(this.SelectedToolRow));
                if (this.SelectedToolRow is { } row)
                {
                    _ = row.LoadRecentRunsAsync();
                }
            }
        }
    }

    /// <summary>
    /// The <see cref="ToolRowViewModel"/> in <see cref="ScheduledToolsRunning"/> whose
    /// <see cref="ToolRowViewModel.ToolType"/> matches <see cref="SelectedTask"/>; null when no task
    /// is selected or no runs have been recorded for that tool type.
    /// </summary>
    public ToolRowViewModel? SelectedToolRow =>
        this.selectedTask is null || this.ScheduledToolsRunning is null
            ? null
            : this.ScheduledToolsRunning.Tools.FirstOrDefault(r =>
                string.Equals(r.ToolType, this.selectedTask.ToolType, StringComparison.Ordinal));

    /// <summary>Whether the host-wide pause control should be shown at all.</summary>
    public bool HasPauseControl => this.pauseStateService is not null;

    /// <summary>Whether the host-wide "Stop all / Pause" toggle is available.</summary>
    public bool CanTogglePause => this.pauseStateService is not null && !this.isToggleInProgress;

    /// <summary>Whether scheduled tools are currently paused on the host.</summary>
    public bool IsPaused => this.pauseStateService?.IsPaused ?? false;

    /// <summary>The label for the host-wide pause/resume button.</summary>
    public string PauseButtonText => this.IsPaused ? "Resume scheduled tools" : "Stop all / Pause";

    /// <summary>Toggles the persisted host-wide pause state (the "Stop all / Pause" action).</summary>
    public RelayCommand TogglePauseCommand { get; }

    /// <summary>Toggles the persisted host-wide pause state.</summary>
    public async Task TogglePauseAsync(CancellationToken cancellationToken = default)
    {
        if (this.pauseStateService is not { } service)
        {
            return;
        }

        this.isToggleInProgress = true;
        this.TogglePauseCommand.RaiseCanExecuteChanged();
        this.RaisePropertyChanged(nameof(this.CanTogglePause));
        try
        {
            await service.SetPausedAsync(this.hostEntityId, !this.IsPaused, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            this.isToggleInProgress = false;
            this.TogglePauseCommand.RaiseCanExecuteChanged();
            this.RaisePropertyChanged(nameof(this.CanTogglePause));
        }
    }

    private void OnPauseStateChanged(object? sender, EventArgs e) => this.dispatch(() =>
    {
        this.RaisePropertyChanged(nameof(this.IsPaused));
        this.RaisePropertyChanged(nameof(this.PauseButtonText));
    });

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
            if (this.ScheduledToolsRunning is not null)
            {
                await this.ScheduledToolsRunning.RefreshHistoryAsync(cancellationToken).ConfigureAwait(true);
                this.SyncStatusIndicators();
                this.RaisePropertyChanged(nameof(this.SelectedToolRow));
            }
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

            var toolEntityId = ReadReference(participants, "tool");
            var toolType = await this.ResolveToolTypeAsync(toolEntityId, cancellationToken).ConfigureAwait(true);
            var toolName = await this.ResolveNameAsync(toolEntityId).ConfigureAwait(true);
            var scheduleName = await this.ResolveNameAsync(ReadFirstReference(participants, "schedule")).ConfigureAwait(true);
            var targetName = await this.ResolveNameAsync(ReadFirstReference(participants, "target")).ConfigureAwait(true);
            var note = data.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String
                ? noteElement.GetString()
                : null;

            items.Add(new ScheduledTaskItemViewModel(toolType, toolName, scheduleName, targetName, note));
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

    private async Task<string> ResolveToolTypeAsync(
        string? entityId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return string.Empty;
        }

        EntityId id;
        try
        {
            id = new EntityId(entityId);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }

        var getResult = await this.entityBroker.EntityRepository.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = id }],
                Timestamps = [null],
            },
            cancellationToken).ConfigureAwait(true);

        var snapshot = getResult.Batches
            .SelectMany(batch => batch.Entities)
            .FirstOrDefault(s => s.EntityId == id);

        if (snapshot?.Data is not JsonElement data)
        {
            return string.Empty;
        }

        return data.TryGetProperty("tool-type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString() ?? string.Empty
            : string.Empty;
    }

    private void SyncStatusIndicators()
    {
        if (this.ScheduledToolsRunning is null)
        {
            return;
        }

        foreach (var task in this.ScheduledTasks)
        {
            var row = this.ScheduledToolsRunning.Tools.FirstOrDefault(r =>
                string.Equals(r.ToolType, task.ToolType, StringComparison.Ordinal));
            task.IsRunning = row?.IsRunning ?? false;
            task.HasFailure = row?.HasFailure ?? false;
            task.LastRunSucceeded = row is not null && row.LastRunStatus == "succeeded";
        }
    }

    private void OnRunningToolsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScheduledToolsRunningViewModel.HasRunningTools)
                           or nameof(ScheduledToolsRunningViewModel.HasFailure))
        {
            this.SyncStatusIndicators();
        }
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

    public void Dispose()
    {
        this.ScheduledToolsRunning?.Dispose();
        if (this.ScheduledToolsRunning is not null)
        {
            this.ScheduledToolsRunning.PropertyChanged -= this.OnRunningToolsPropertyChanged;
        }

        if (this.pauseStateService is not null)
        {
            this.pauseStateService.PauseStateChanged -= this.OnPauseStateChanged;
        }
    }
}
