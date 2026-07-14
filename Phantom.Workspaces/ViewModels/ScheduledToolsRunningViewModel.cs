using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ScheduledTools;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.ViewModels;

/// <summary>A summary of a single tool execution run shown in an expanded tool row.</summary>
public sealed class RunSummaryViewModel
{
    public RunSummaryViewModel(DateTimeOffset startedAt, TimeSpan? duration, string status, string? message)
    {
        this.StartedAt = startedAt;
        this.Duration = duration;
        this.Status = status;
        this.Message = message;
    }

    /// <summary>When the run started (UTC).</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>How long the run took; null if still running.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>The run status: <c>running</c>, <c>succeeded</c>, or <c>failed</c>.</summary>
    public string Status { get; }

    /// <summary>An optional error or summary message from the run's content.</summary>
    public string? Message { get; }

    /// <summary>A single-character glyph representing the run status (✓, ✗, or ⏳).</summary>
    public string StatusGlyph => this.Status switch
    {
        "succeeded" => "✓",
        "failed" => "✗",
        "running" => "⏳",
        _ => "?",
    };

    /// <summary>Sub-task results nested under this run; empty if the run has no sub-tasks.</summary>
    public IReadOnlyList<RunSummaryViewModel> SubRuns { get; init; } = [];
}

/// <summary>
/// A single tool entry in the scheduled-tools panel, representing the combined live and historical
/// state for one (host, tool-type) pair.
/// </summary>
public sealed class ToolRowViewModel : ViewModelBase
{
    private string? lastRunStatus;
    private bool isExpanded;
    private readonly Func<CancellationToken, Task<IReadOnlyList<RunSummaryViewModel>>> loadRecentRuns;

    public ToolRowViewModel(
        string toolType,
        string host,
        Func<CancellationToken, Task<IReadOnlyList<RunSummaryViewModel>>> loadRecentRuns)
    {
        this.ToolType = toolType ?? throw new ArgumentNullException(nameof(toolType));
        this.Host = host ?? throw new ArgumentNullException(nameof(host));
        this.loadRecentRuns = loadRecentRuns ?? throw new ArgumentNullException(nameof(loadRecentRuns));
        this.ExpandCommand = new RelayCommand(_ => this.OnExpandCommandExecuted());
    }

    /// <summary>The tool type discriminator.</summary>
    public string ToolType { get; }

    /// <summary>A display label for the host the tool targets.</summary>
    public string Host { get; }

    public StatusItem Status { get; } = new();

    /// <summary>Whether the tool is currently executing an in-flight run.</summary>
    public bool IsRunning
    {
        get => this.Status.RunningStatus == RunningStatus.Running;
        set
        {
            var newStatus = value ? RunningStatus.Running : RunningStatus.Idle;
            if (this.Status.RunningStatus != newStatus)
            {
                this.Status.RunningStatus = newStatus;
                this.RaisePropertyChanged();
            }
        }
    }

    /// <summary>The status of the most-recent completed run, or null if no runs exist.</summary>
    public string? LastRunStatus
    {
        get => this.lastRunStatus;
        set
        {
            if (this.SetProperty(ref this.lastRunStatus, value))
            {
                this.Status.ErrorStatus = value switch
                {
                    "succeeded" => ErrorStatus.Successful,
                    "failed" => ErrorStatus.Error,
                    _ => ErrorStatus.None,
                };
                this.RaisePropertyChanged(nameof(this.HasFailure));
            }
        }
    }

    /// <summary>True when the most-recent run failed.</summary>
    public bool HasFailure => this.Status.ErrorStatus == ErrorStatus.Error;

    /// <summary>Toggles <see cref="IsExpanded"/> and loads recent runs when expanding.</summary>
    public RelayCommand ExpandCommand { get; }

    private void OnExpandCommandExecuted()
    {
        this.IsExpanded = !this.IsExpanded;
        if (this.IsExpanded)
        {
            _ = this.LoadRecentRunsAsync(CancellationToken.None);
        }
    }

    /// <summary>Whether the run-history panel is expanded for this row.</summary>
    public bool IsExpanded
    {
        get => this.isExpanded;
        set => this.SetProperty(ref this.isExpanded, value);
    }

    /// <summary>The recent runs for this tool, populated by <see cref="LoadRecentRunsAsync"/>.</summary>
    public ObservableCollection<RunSummaryViewModel> RecentRuns { get; } = new();

    /// <summary>Loads (or reloads) the recent run history into <see cref="RecentRuns"/>.</summary>
    public async Task LoadRecentRunsAsync(CancellationToken cancellationToken = default)
    {
        var runs = await this.loadRecentRuns(cancellationToken).ConfigureAwait(false);
        this.RecentRuns.Clear();
        foreach (var run in runs)
        {
            this.RecentRuns.Add(run);
        }
    }
}

/// <summary>
/// Surfaces the scheduled tools panel, merging in-flight running state from
/// <see cref="ScheduledToolHost"/> with historical run data from <c>tool-execution-result</c>
/// entities. Refreshes live from <see cref="ScheduledToolHost.RunningExecutionsChanged"/>.
/// </summary>
public sealed class ScheduledToolsRunningViewModel : ViewModelBase, IDisposable
{
    private readonly ScheduledToolHost host;
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly Action<Action> dispatch;

    /// <param name="host">The host whose running and historical tool executions are displayed.</param>
    /// <param name="dataAccessLayer">Used to query <c>tool-execution-result</c> entities for run history.</param>
    /// <param name="dispatch">
    /// Marshals a refresh onto the UI thread. Defaults to running synchronously (used in tests); the
    /// GUI passes a dispatcher post so the observable collection is updated on the UI thread.
    /// </param>
    public ScheduledToolsRunningViewModel(
        ScheduledToolHost host,
        IDataAccessLayer dataAccessLayer,
        Action<Action>? dispatch = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
        this.dispatch = dispatch ?? (action => action());
        this.host.RunningExecutionsChanged += this.OnRunningExecutionsChanged;
        this.Refresh();
    }

    /// <summary>All known tool rows (running or with recorded history), ordered by host then tool type.</summary>
    public ObservableCollection<ToolRowViewModel> Tools { get; } = new();

    /// <summary>Whether any scheduled tool is currently running.</summary>
    public bool HasRunningTools => this.Tools.Any(t => t.IsRunning);

    /// <summary>Whether any tool's most-recent completed run has failed.</summary>
    public bool HasFailure => this.Tools.Any(t => t.HasFailure);

    private void OnRunningExecutionsChanged(object? sender, EventArgs e) => this.dispatch(this.Refresh);

    /// <summary>
    /// Synchronously refreshes the <see cref="ToolRowViewModel.IsRunning"/> flag on all existing
    /// rows from the current in-flight executions. Does not touch the DAL.
    /// </summary>
    private void Refresh()
    {
        var runningExecutions = this.host.GetRunningExecutions();
        var runningKeys = new HashSet<(string Host, string ToolType)>(
            runningExecutions.Select(e => (string.Join(" / ", e.HostNameComponents), e.ToolType)));

        foreach (var row in this.Tools)
        {
            row.IsRunning = runningKeys.Contains((row.Host, row.ToolType));
        }

        // Add rows for any newly-running tools not yet present in the collection.
        var existingKeys = new HashSet<(string Host, string ToolType)>(
            this.Tools.Select(r => (r.Host, r.ToolType)));

        foreach (var execution in runningExecutions)
        {
            var host = string.Join(" / ", execution.HostNameComponents);
            if (!existingKeys.Contains((host, execution.ToolType)))
            {
                var row = this.CreateRow(execution.ToolType, host);
                row.IsRunning = true;
                this.Tools.Add(row);
            }
        }

        this.RaisePropertyChanged(nameof(this.HasRunningTools));
        this.RaisePropertyChanged(nameof(this.HasFailure));
    }

    /// <summary>
    /// Queries all top-level <c>tool-execution-result</c> entitiesand merges them into
    /// <see cref="Tools"/>, setting <see cref="ToolRowViewModel.LastRunStatus"/> on each row.
    /// </summary>
    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
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

        // Parse all top-level run entities (SuffixComponents.Length == 2: [toolName, startTimestamp]).
        var topLevelRuns = queryResult.Batches
            .SelectMany(batch => batch.Entities)
            .Select(entity => entity.Data)
            .OfType<JsonElement>()
            .Select(ParseTopLevelRun)
            .Where(r => r is not null)
            .Select(r => r!.Value)
            .OrderByDescending(r => r.StartTime)
            .ToArray();

        // Group by (host, toolType) to find LastRunStatus.
        var latestByKey = new Dictionary<(string Host, string ToolType), string>(
            EqualityComparer<(string, string)>.Default);

        foreach (var run in topLevelRuns)
        {
            var key = (run.HostLabel, run.ToolName);
            if (!latestByKey.ContainsKey(key))
            {
                latestByKey[key] = run.Status ?? "running";
            }
        }

        // Merge into the Tools collection on the captured context (must be called without
        // ConfigureAwait(false) so the continuation stays on the UI synchronization context).
        MergeHistory(latestByKey);
    }

    private void MergeHistory(Dictionary<(string Host, string ToolType), string> latestByKey)
    {
        // Update existing rows.
        var coveredKeys = new HashSet<(string, string)>();
        foreach (var row in this.Tools)
        {
            var key = (row.Host, row.ToolType);
            if (latestByKey.TryGetValue(key, out var status))
            {
                row.LastRunStatus = status;
                coveredKeys.Add(key);
            }
        }

        // Add new rows for (host, toolType) pairs not yet in the collection.
        foreach (var ((host, toolType), status) in latestByKey)
        {
            if (!coveredKeys.Contains((host, toolType)))
            {
                var row = this.CreateRow(toolType, host);
                row.LastRunStatus = status;
                this.Tools.Add(row);
            }
        }

        this.RaisePropertyChanged(nameof(this.HasRunningTools));
        this.RaisePropertyChanged(nameof(this.HasFailure));
    }

    private ToolRowViewModel CreateRow(string toolType, string host)
    {
        var capturedToolType = toolType;
        var capturedHost = host;
        return new ToolRowViewModel(
            toolType,
            host,
            cancellationToken => this.LoadRecentRunsForToolAsync(capturedHost, capturedToolType, cancellationToken));
    }

    private async Task<IReadOnlyList<RunSummaryViewModel>> LoadRecentRunsForToolAsync(
        string hostLabel,
        string toolType,
        CancellationToken cancellationToken)
    {
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
            cancellationToken).ConfigureAwait(false);

        return queryResult.Batches
            .SelectMany(batch => batch.Entities)
            .Select(entity => entity.Data)
            .OfType<JsonElement>()
            .Select(ParseTopLevelRun)
            .Where(r => r is not null)
            .Select(r => r!.Value)
            .Where(r => string.Equals(r.HostLabel, hostLabel, StringComparison.Ordinal)
                     && string.Equals(r.ToolName, toolType, StringComparison.Ordinal))
            .OrderByDescending(r => r.StartTime)
            .Select(r => new RunSummaryViewModel(
                r.StartTime,
                r.EndTime.HasValue ? r.EndTime.Value - r.StartTime : null,
                r.Status ?? "running",
                r.Message))
            .ToArray();
    }

    private static ParsedTopLevelRun? ParseTopLevelRun(JsonElement entity)
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
                .Where(c => c.ValueKind == JsonValueKind.String)
                .Select(c => c.GetString()!)
                .ToArray();

            var executionsIndex = Array.IndexOf(components, ToolExecutionResultWriter.ToolExecutionsSegment);
            if (executionsIndex <= 0 || executionsIndex >= components.Length - 1)
            {
                continue;
            }

            var suffix = components[(executionsIndex + 1)..];
            // Only top-level runs have exactly [toolName, startTimestamp].
            if (suffix.Length != 2)
            {
                continue;
            }

            var hostLabel = string.Join(" / ", components[..executionsIndex]);
            var toolName = suffix[0];

            var status = entity.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString()
                : null;

            DateTimeOffset startTime = default;
            if (entity.TryGetProperty("start-time", out var startEl) && startEl.ValueKind == JsonValueKind.String)
            {
                DateTimeOffset.TryParse(startEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out startTime);
            }

            DateTimeOffset? endTime = null;
            if (entity.TryGetProperty("end-time", out var endEl) && endEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(endEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedEnd))
            {
                endTime = parsedEnd;
            }

            string? message = null;
            if (entity.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Object
                && contentEl.TryGetProperty("default", out var defaultEl) && defaultEl.ValueKind == JsonValueKind.Object
                && defaultEl.TryGetProperty("content", out var innerContentEl) && innerContentEl.ValueKind == JsonValueKind.Object
                && innerContentEl.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                message = textEl.GetString();
            }

            return new ParsedTopLevelRun(hostLabel, toolName, status, startTime, endTime, message);
        }

        return null;
    }

    public void Dispose()
    {
        this.host.RunningExecutionsChanged -= this.OnRunningExecutionsChanged;
    }

    private readonly record struct ParsedTopLevelRun(
        string HostLabel,
        string ToolName,
        string? Status,
        DateTimeOffset StartTime,
        DateTimeOffset? EndTime,
        string? Message);
}
