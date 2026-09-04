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
    private bool isEndOfHistory;
    private readonly Func<CancellationToken, Task<IReadOnlyList<RunSummaryViewModel>>> loadRecentRuns;
    private readonly Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<RunSummaryViewModel>>>? loadWindow;
    private readonly Func<DateTimeOffset, CancellationToken, Task<bool>>? hasOlderRuns;
    private readonly TimeProvider timeProvider;
    private readonly TaskScheduler? foregroundScheduler;
    private DateTimeOffset currentWindowStart;
    private bool isLoadingWindow;

    public ToolRowViewModel(
        string toolType,
        string host,
        Func<CancellationToken, Task<IReadOnlyList<RunSummaryViewModel>>> loadRecentRuns,
        TaskScheduler? foregroundScheduler = null,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<RunSummaryViewModel>>>? loadWindow = null,
        Func<DateTimeOffset, CancellationToken, Task<bool>>? hasOlderRuns = null,
        TimeProvider? timeProvider = null)
    {
        this.ToolType = toolType ?? throw new ArgumentNullException(nameof(toolType));
        this.Host = host ?? throw new ArgumentNullException(nameof(host));
        this.loadRecentRuns = loadRecentRuns ?? throw new ArgumentNullException(nameof(loadRecentRuns));
        this.loadWindow = loadWindow;
        this.hasOlderRuns = hasOlderRuns;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.foregroundScheduler = foregroundScheduler;
        this.currentWindowStart = this.timeProvider.GetUtcNow();
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
        // #1203: Run the loader off the UI thread and marshal the RecentRuns mutation back
        // via the injected foreground TaskScheduler (or a scheduler captured from the
        // current SynchronizationContext when none was injected). This mirrors the
        // AgentViewModel pattern established for #1122 and stops the UI freezing while
        // the query runs and rows are enumerated.
        var scheduler = ScheduledToolsRunningViewModel.ResolveForegroundScheduler(this.foregroundScheduler);

        var loadTask = Task.Run(
            async () => await this.loadRecentRuns(cancellationToken).ConfigureAwait(false),
            cancellationToken);

        await loadTask.ContinueWith(
            t =>
            {
                this.RecentRuns.Clear();
                foreach (var run in t.Result)
                {
                    this.RecentRuns.Add(run);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            scheduler).ConfigureAwait(false);
    }

    /// <summary>
    /// #1357: True once paging has reached the oldest run (no older runs remain); further
    /// <see cref="LoadNextWindowAsync"/> calls become no-ops.
    /// </summary>
    public bool IsEndOfHistory
    {
        get => this.isEndOfHistory;
        private set => this.SetProperty(ref this.isEndOfHistory, value);
    }

    /// <summary>#1357: The (exclusive) upper boundary of the next-older history window to page in.</summary>
    internal DateTimeOffset CurrentWindowStart => this.currentWindowStart;

    /// <summary>
    /// #1357: Loads the most-recent ~1-hour window of run history (bounded by time and count),
    /// replacing <see cref="RecentRuns"/>, and records whether older runs remain.
    /// </summary>
    public async Task LoadInitialWindowAsync(CancellationToken cancellationToken = default)
    {
        if (this.loadWindow is null)
        {
            return;
        }

        var upper = this.timeProvider.GetUtcNow();
        var lower = upper - ScheduledToolsRunningViewModel.HistoryPageWindow;
        var scheduler = ScheduledToolsRunningViewModel.ResolveForegroundScheduler(this.foregroundScheduler);

        var loadTask = Task.Run(
            async () => await this.loadWindow(lower, upper, cancellationToken).ConfigureAwait(false),
            cancellationToken);

        await loadTask.ContinueWith(
            t =>
            {
                this.RecentRuns.Clear();
                foreach (var run in t.Result)
                {
                    this.RecentRuns.Add(run);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            scheduler).ConfigureAwait(false);

        this.currentWindowStart = lower;
        this.IsEndOfHistory = false;
        await this.UpdateEndOfHistoryAsync(lower, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// #1357: Loads the next-older ~1-hour window and APPENDS it to <see cref="RecentRuns"/> (does
    /// not clear existing items). Re-entrant calls (from overlapping scroll events) and calls past
    /// the end of history are no-ops.
    /// </summary>
    public async Task LoadNextWindowAsync(CancellationToken cancellationToken = default)
    {
        if (this.loadWindow is null || this.isEndOfHistory || this.isLoadingWindow)
        {
            return;
        }

        // Set the re-entrancy guard synchronously, before the first await, so overlapping scroll
        // events observe it and return without starting a second load of the same window.
        this.isLoadingWindow = true;
        try
        {
            var upper = this.currentWindowStart;
            var lower = upper - ScheduledToolsRunningViewModel.HistoryPageWindow;
            var scheduler = ScheduledToolsRunningViewModel.ResolveForegroundScheduler(this.foregroundScheduler);

            var loadTask = Task.Run(
                async () => await this.loadWindow(lower, upper, cancellationToken).ConfigureAwait(false),
                cancellationToken);

            await loadTask.ContinueWith(
                t =>
                {
                    foreach (var run in t.Result)
                    {
                        this.RecentRuns.Add(run);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                scheduler).ConfigureAwait(false);

            this.currentWindowStart = lower;
            await this.UpdateEndOfHistoryAsync(lower, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.isLoadingWindow = false;
        }
    }

    private async Task UpdateEndOfHistoryAsync(DateTimeOffset upperExclusive, CancellationToken cancellationToken)
    {
        if (this.hasOlderRuns is null)
        {
            return;
        }

        var hasOlder = await Task.Run(
            async () => await this.hasOlderRuns(upperExclusive, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        this.IsEndOfHistory = !hasOlder;
    }
}

/// <summary>
/// Surfaces the scheduled tools panel, merging in-flight running state from
/// <see cref="ScheduledToolHost"/> with historical run data from <c>tool-execution-result</c>
/// entities. Refreshes live from <see cref="ScheduledToolHost.RunningExecutionsChanged"/>.
/// </summary>
public sealed class ScheduledToolsRunningViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// #1358: upper bound on the number of <c>tool-execution-result</c> entities a single history
    /// query materialises, so refresh work is O(recent window) rather than O(entire history).
    /// </summary>
    internal const int RecentHistoryResultLimit = 500;

    /// <summary>
    /// #1357: the duration of a single history paging window. Each scroll-driven page loads
    /// approximately this much older history and appends it.
    /// </summary>
    internal static readonly TimeSpan HistoryPageWindow = TimeSpan.FromHours(1);

    private readonly ScheduledToolHost host;
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly Action<Action> dispatch;
    private readonly TaskScheduler? foregroundScheduler;
    private readonly TimeProvider timeProvider;

    /// <param name="host">The host whose running and historical tool executions are displayed.</param>
    /// <param name="dataAccessLayer">Used to query <c>tool-execution-result</c> entities for run history.</param>
    /// <param name="dispatch">
    /// Marshals a refresh of live in-flight executions onto the UI thread. Defaults to running
    /// synchronously (used in tests); the GUI passes a dispatcher post so the observable
    /// collection is updated on the UI thread.
    /// </param>
    /// <param name="foregroundScheduler">
    /// #1203: Foreground <see cref="TaskScheduler"/> used by <see cref="RefreshHistoryAsync"/> and
    /// <see cref="ToolRowViewModel.LoadRecentRunsAsync"/> to marshal the final collection mutations
    /// back onto the UI thread while the query and JSON parsing run off-thread. When null, the
    /// caller's <see cref="SynchronizationContext"/> is captured at each call site to preserve
    /// legacy behaviour; production wires the Avalonia dispatcher scheduler here.
    /// </param>
    public ScheduledToolsRunningViewModel(
        ScheduledToolHost host,
        IDataAccessLayer dataAccessLayer,
        Action<Action>? dispatch = null,
        TaskScheduler? foregroundScheduler = null,
        TimeProvider? timeProvider = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
        this.dispatch = dispatch ?? (action => action());
        this.foregroundScheduler = foregroundScheduler;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.host.RunningExecutionsChanged += this.OnRunningExecutionsChanged;
        this.Refresh();
    }

    /// <summary>
    /// Resolves the foreground scheduler used for marshaling UI mutations: uses the injected
    /// scheduler when provided, otherwise falls back to the current
    /// <see cref="SynchronizationContext"/>, and finally to <see cref="TaskScheduler.Default"/>.
    /// </summary>
    internal static TaskScheduler ResolveForegroundScheduler(TaskScheduler? injected)
    {
        if (injected is not null)
        {
            return injected;
        }

        return SynchronizationContext.Current is not null
            ? TaskScheduler.FromCurrentSynchronizationContext()
            : TaskScheduler.Default;
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
    /// Queries all top-level <c>tool-execution-result</c> entities off the UI thread and merges
    /// them into <see cref="Tools"/> on the injected foreground scheduler, setting
    /// <see cref="ToolRowViewModel.LastRunStatus"/> on each row. #1203: the query and JSON parsing
    /// run on a thread-pool thread so the UI does not freeze while history is populated; only the
    /// final <see cref="MergeHistory"/> mutation is marshaled back onto the foreground scheduler.
    /// </summary>
    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        var scheduler = ResolveForegroundScheduler(this.foregroundScheduler);

        var loadTask = Task.Run(
            async () =>
            {
                var queryResult = await this.dataAccessLayer.QueryAsync(
                    new QueryRequest
                    {
                        Clauses =
                        [
                            new TopLevelQueryClause
                            {
                                ClauseIdentifier = new QueryClauseIdentifier("tool-execution-results"),
                                // #1358: bound the recent-history window so refresh work is
                                // O(window), not O(entire history).
                                Clause = new TopQueryClause
                                {
                                    ResultLimit = new QueryResultLimit(RecentHistoryResultLimit),
                                    // #1360: order by start-time descending server-side so the bounded
                                    // window always contains the most-recent runs, not an arbitrary page.
                                    SortSpecifications = StartTimeDescendingSort,
                                    Clause = new EntityTypeQueryClause
                                    {
                                        EntityTypeNames = new EntityTypeNameSet([ToolExecutionResultWriter.ToolExecutionResultEntityType]),
                                    },
                                },
                            },
                        ],
                    },
                    cancellationToken).ConfigureAwait(false);

                // Parse all top-level run entities off the UI thread.
                var topLevelRuns = queryResult.Batches
                    .SelectMany(batch => batch.Entities)
                    .Select(entity => entity.Data)
                    .OfType<JsonElement>()
                    .Select(ParseTopLevelRun)
                    .Where(r => r is not null)
                    .Select(r => r!.Value)
                    .OrderByDescending(r => r.StartTime)
                    .ToArray();

                // Group by (host, toolType) to find LastRunStatus, still off the UI thread.
                var latestByKey = new Dictionary<(string Host, string ToolType), string>();
                foreach (var run in topLevelRuns)
                {
                    var key = (run.HostLabel, run.ToolName);
                    if (!latestByKey.ContainsKey(key))
                    {
                        latestByKey[key] = run.Status ?? "running";
                    }
                }

                return latestByKey;
            },
            cancellationToken);

        await loadTask.ContinueWith(
            t => this.MergeHistory(t.Result),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            scheduler).ConfigureAwait(false);
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
            cancellationToken => this.LoadRecentRunsForToolAsync(capturedHost, capturedToolType, cancellationToken),
            this.foregroundScheduler,
            // #1357: time-windowed paging loaders.
            (lowerInclusive, upperExclusive, cancellationToken) => this.LoadWindowForToolAsync(
                capturedHost, capturedToolType, lowerInclusive, upperExclusive, cancellationToken),
            (upperExclusive, cancellationToken) => this.HasOlderRunsForToolAsync(
                capturedToolType, upperExclusive, cancellationToken),
            this.timeProvider);
    }

    private async Task<IReadOnlyList<RunSummaryViewModel>> LoadRecentRunsForToolAsync(
        string hostLabel,
        string toolType,
        CancellationToken cancellationToken)
    {
        // #1358/#1360: filter by tool AND host at the query (so other tools'/hosts' runs are never
        // materialised and cannot starve the window) and return the most-recent runs via a
        // server-side start-time-descending sort bounded to a recent window.
        var queryResult = await this.dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("tool-execution-results"),
                        Clause = new TopQueryClause
                        {
                            ResultLimit = new QueryResultLimit(RecentHistoryResultLimit),
                            SortSpecifications = StartTimeDescendingSort,
                            Clause = BuildToolHistoryClause(toolType, BuildHostLabelClause(hostLabel)),
                        },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        return ParseRuns(queryResult)
            .Where(r => string.Equals(r.HostLabel, hostLabel, StringComparison.Ordinal))
            .OrderByDescending(r => r.StartTime)
            .Select(ToRunSummary)
            .ToArray();
    }

    private static IEnumerable<ParsedTopLevelRun> ParseRuns(QueryResult queryResult)
    {
        return queryResult.Batches
            .SelectMany(batch => batch.Entities)
            .Select(entity => entity.Data)
            .OfType<JsonElement>()
            .Select(ParseTopLevelRun)
            .Where(r => r is not null)
            .Select(r => r!.Value);
    }

    private static RunSummaryViewModel ToRunSummary(ParsedTopLevelRun r) => new(
        r.StartTime,
        r.EndTime.HasValue ? r.EndTime.Value - r.StartTime : null,
        r.Status ?? "running",
        r.Message);

    /// <summary>
    /// #1357: Loads the runs for a tool whose <c>start-time</c> falls in the half-open window
    /// <c>[lowerInclusive, upperExclusive)</c>, bounded both by time (server-side clauses) and by
    /// count (<see cref="RecentHistoryResultLimit"/>). #1360: host is filtered and results are ordered
    /// (start-time descending) server-side so a busy hour's window is not starved by other hosts.
    /// </summary>
    private async Task<IReadOnlyList<RunSummaryViewModel>> LoadWindowForToolAsync(
        string hostLabel,
        string toolType,
        DateTimeOffset lowerInclusive,
        DateTimeOffset upperExclusive,
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
                        Clause = new TopQueryClause
                        {
                            ResultLimit = new QueryResultLimit(RecentHistoryResultLimit),
                            SortSpecifications = StartTimeDescendingSort,
                            Clause = BuildToolHistoryClause(
                                toolType,
                                BuildHostLabelClause(hostLabel),
                                BuildStartTimeClause(lowerInclusive, FieldComparisonOperator.GreaterThanOrEqualTo),
                                BuildStartTimeClause(upperExclusive, FieldComparisonOperator.LessThan)),
                        },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        return ParseRuns(queryResult)
            .Where(r => string.Equals(r.HostLabel, hostLabel, StringComparison.Ordinal))
            .OrderByDescending(r => r.StartTime)
            .Select(ToRunSummary)
            .ToArray();
    }

    /// <summary>
    /// #1357: Returns whether any run for the tool started strictly before <paramref name="upperExclusive"/>,
    /// used to detect the end of history so paging can stop. Bounded to a single entity (<c>Top(1)</c>).
    /// Filters by tool only (not host) so a host whose in-memory refinement is empty does not stop paging
    /// prematurely while older runs still exist.
    /// </summary>
    private async Task<bool> HasOlderRunsForToolAsync(
        string toolType,
        DateTimeOffset upperExclusive,
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
                        Clause = new TopQueryClause
                        {
                            ResultLimit = new QueryResultLimit(1),
                            Clause = BuildToolHistoryClause(
                                toolType,
                                BuildStartTimeClause(upperExclusive, FieldComparisonOperator.LessThan)),
                        },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        return queryResult.Batches.SelectMany(batch => batch.Entities).Any();
    }

    /// <summary>
    /// Builds a clause matching top-level <c>tool-execution-result</c> entities for a specific tool
    /// (filtered by the <c>tool-name</c> field), optionally AND-ed with additional clauses (e.g. a
    /// <c>start-time</c> window; see <see cref="BuildStartTimeClause"/>).
    /// </summary>
    private static QueryClause BuildToolHistoryClause(string toolType, params QueryClause[] additionalClauses)
    {
        var clauses = new List<QueryClause>
        {
            new EntityTypeQueryClause
            {
                EntityTypeNames = new EntityTypeNameSet([ToolExecutionResultWriter.ToolExecutionResultEntityType]),
            },
            new EntityFieldQueryClause
            {
                FieldPath = new FieldPath("tool-name"),
                ComparisonOperator = FieldComparisonOperator.Equals,
                Value = JsonSerializer.SerializeToElement(toolType),
            },
        };
        clauses.AddRange(additionalClauses);
        return new AndQueryClause { Clauses = clauses };
    }

    /// <summary>
    /// #1357: Builds a <c>start-time</c> field clause. The value is formatted identically to how
    /// <see cref="ToolExecutionResultWriter"/> stores it (round-trip "O", UTC), so ordinal string
    /// comparison in the query layer is monotonic in time.
    /// </summary>
    private static EntityFieldQueryClause BuildStartTimeClause(DateTimeOffset value, FieldComparisonOperator comparisonOperator)
    {
        return new EntityFieldQueryClause
        {
            FieldPath = new FieldPath("start-time"),
            ComparisonOperator = comparisonOperator,
            Value = JsonSerializer.SerializeToElement(
                value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>
    /// #1360: A server-side sort by <c>start-time</c> descending, applied before the top-N limit so a
    /// bounded/windowed query returns the most-recent runs rather than an arbitrary page.
    /// </summary>
    private static readonly IReadOnlyList<SortSpecification> StartTimeDescendingSort =
    [
        new SortSpecification
        {
            FieldPath = new FieldPath("start-time"),
            Direction = SortDirection.Descending,
        },
    ];

    /// <summary>
    /// #1360: Builds a <c>host-label</c> field clause so the run-history query is filtered to a single
    /// host server-side, preventing a per-tool/per-host window from being starved by other hosts' runs.
    /// The value matches how <see cref="ToolExecutionResultWriter"/> stores <c>host-label</c>.
    /// </summary>
    private static EntityFieldQueryClause BuildHostLabelClause(string hostLabel)
    {
        return new EntityFieldQueryClause
        {
            FieldPath = new FieldPath("host-label"),
            ComparisonOperator = FieldComparisonOperator.Equals,
            Value = JsonSerializer.SerializeToElement(hostLabel),
        };
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
