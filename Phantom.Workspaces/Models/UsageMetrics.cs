using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Models;

/// <summary>One measured billing dimension for a provider account.</summary>
public sealed class UsageMetric : ObservableObject
{
    private decimal quantityUsed;
    private decimal quantityTotal;
    private DateTime? lastUpdatedAt;
    private bool isSelectedAsShown;

    public string Title { get; init; } = string.Empty;

    public decimal QuantityUsed
    {
        get => this.quantityUsed;
        set
        {
            if (SetProperty(ref this.quantityUsed, value))
            {
                this.OnPropertyChanged(nameof(FractionUsed));
                this.OnPropertyChanged(nameof(QuantityPresentation));
            }
        }
    }

    public decimal QuantityTotal
    {
        get => this.quantityTotal;
        set
        {
            if (SetProperty(ref this.quantityTotal, value))
            {
                this.OnPropertyChanged(nameof(FractionUsed));
                this.OnPropertyChanged(nameof(QuantityPresentation));
            }
        }
    }

    /// <summary>
    /// Format string for <see cref="QuantityPresentation"/>.
    /// Arguments: {0} = QuantityUsed, {1} = QuantityTotal, {2} = unit label.
    /// Example: <c>"{0:N0} / {1:N0} {2}"</c> → "256 / 755 minutes"
    /// Example: <c>"{0:C2} / {1:C2}"</c> → "$356.00 / $3,000.00"
    /// </summary>
    public string QuantityPresentationFormatString { get; init; } = string.Empty;

    /// <summary>
    /// The unit label substituted as {2} in <see cref="QuantityPresentationFormatString"/>.
    /// e.g. "minutes", "AIC", or empty string for currency.
    /// </summary>
    public string Unit { get; init; } = string.Empty;

    public DateTime? LastUpdatedAt
    {
        get => this.lastUpdatedAt;
        set => SetProperty(ref this.lastUpdatedAt, value);
    }

    /// <summary>
    /// Optional short context line rendered under the metric's value (for example,
    /// "Resets in 2 days"). Null when the underlying data source provides no such note.
    /// </summary>
    public string? AdditionalInformation { get; init; }

    /// <summary>
    /// Optional web page URL that shows the source data behind this metric (billing
    /// page, per-SKU deep link, etc.). Null when no per-metric link is known; the view
    /// then falls back to the owning account's SettingsUrl.
    /// </summary>
    public Uri? WebUrl { get; init; }

    /// <summary>
    /// Optional date on which the billing period covering this metric resets. Populated
    /// from the budget's <c>current_period_end</c> (or the fallback calendar-month end)
    /// so the flyout can render "Resets on {ResetsAt:MMM d, yyyy}" text and downstream
    /// caches can detect period rollover (#1188).
    /// </summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>
    /// Start of the billing period covered by this metric's aggregate values (#1188).
    /// Set by providers that filter aggregation to the current period; the cache in
    /// <see cref="Services.UsageMetricsService"/> compares this against the previously
    /// observed period start and clears stale prior-period metrics when it changes.
    /// </summary>
    public DateTimeOffset? BillingPeriodStart { get; init; }

    /// <summary>
    /// Whether this metric is the one pinned as the top-right indicator label.
    /// Two-way bound to the row's RadioButton. The <see cref="UsageTrackerViewModel"/>
    /// enforces single-selection semantics across all rows and accounts.
    /// </summary>
    public bool IsSelectedAsShown
    {
        get => this.isSelectedAsShown;
        set => SetProperty(ref this.isSelectedAsShown, value);
    }

    /// <summary>
    /// Fraction of the quota consumed (0.0–1.0), or <see langword="null"/> when
    /// <see cref="QuantityTotal"/> is zero.
    /// </summary>
    public double? FractionUsed =>
        QuantityTotal == 0 ? null : (double)(QuantityUsed / QuantityTotal);

    /// <summary>
    /// Human-readable presentation produced by formatting
    /// <see cref="QuantityPresentationFormatString"/> with
    /// {0}=<see cref="QuantityUsed"/>, {1}=<see cref="QuantityTotal"/>, {2}=<see cref="Unit"/>.
    /// Example: "256 / 755 minutes", "$356.00 / $3,000.00"
    /// </summary>
    public string QuantityPresentation =>
        string.Format(QuantityPresentationFormatString, QuantityUsed, QuantityTotal, Unit).TrimEnd();
}

/// <summary>One external-provider account with its associated usage metrics.</summary>
public sealed class UsageAccount : ObservableObject
{
    private DateTimeOffset? resetsAt;
    private DateTimeOffset? billingPeriodStart;

    public string Product { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public Uri SettingsUrl { get; init; } = new Uri("about:blank");
    public ObservableCollection<UsageMetric> Metrics { get; } = [];

    /// <summary>
    /// Date on which the currently displayed billing period resets, if known (#1188).
    /// Populated from the provider's discovered <c>PeriodEnd</c>. The XAML flyout binds
    /// this to a "Resets on {ResetsAt:MMM d, yyyy}" line under the account header.
    /// </summary>
    public DateTimeOffset? ResetsAt
    {
        get => this.resetsAt;
        set
        {
            if (this.SetProperty(ref this.resetsAt, value))
            {
                this.OnPropertyChanged(nameof(this.ResetsAtDisplay));
            }
        }
    }

    /// <summary>
    /// Formatted "Resets on {date}" string bound by the flyout. Null when
    /// <see cref="ResetsAt"/> is null so the row is collapsed via
    /// <c>StringConverters.IsNotNullOrEmpty</c>.
    /// </summary>
    public string? ResetsAtDisplay =>
        this.resetsAt is { } r ? $"Resets on {r.UtcDateTime:MMM d, yyyy}" : null;

    /// <summary>
    /// Start of the billing period currently reflected in <see cref="Metrics"/> (#1188).
    /// The <see cref="Services.UsageMetricsService"/> uses this to detect period rollover
    /// and clear stale prior-period metrics before applying the new set.
    /// </summary>
    public DateTimeOffset? BillingPeriodStart
    {
        get => this.billingPeriodStart;
        set => this.SetProperty(ref this.billingPeriodStart, value);
    }

    /// <summary>
    /// Organisation (e.g. GitHub org) that owns / bills this account when the account is an
    /// org-managed seat. Null for personal accounts. The GitHub Copilot provider uses this to
    /// decide whether to call the org-scoped budgets endpoint (§B.1 of #1159); personal
    /// accounts must NOT call the budgets endpoint since there is no user-scoped budgets GET.
    /// </summary>
    public string? Org { get; init; }

    /// <summary>
    /// Configured "included premium requests per month" quota for this account. Used as the
    /// count-metric denominator (<see cref="UsageMetric.QuantityTotal"/>) for Premium Request
    /// rows, because the REST usage/budgets endpoints do not expose an included-quantity value.
    /// </summary>
    public decimal? IncludedPremiumRequests { get; init; }

    /// <summary>
    /// Configured "included AI credits per month" quota for this account. Used as the
    /// count-metric denominator for AI Credit rows, because the REST endpoints do not expose
    /// an included-quantity value for count metrics.
    /// </summary>
    public decimal? IncludedAiCredits { get; init; }

    /// <summary>
    /// Configured monthly dollar budget cap for the account, used as the fallback cost-metric
    /// denominator when the REST budgets endpoint returns no matching budget (or 403/404).
    /// </summary>
    public decimal? MonthlyBudget { get; init; }

    /// <summary>
    /// Returns the configured included quantity for the given SKU (Premium Request /
    /// AI Credit), or null when no configuration is available. Case-insensitive matching on
    /// the SKU label to tolerate provider variations (e.g. "Copilot Premium Request",
    /// "Copilot AI Credits").
    /// </summary>
    public decimal? GetIncludedQuantity(string sku)
    {
        if (string.IsNullOrEmpty(sku))
        {
            return null;
        }

        if (sku.Contains("Premium Request", StringComparison.OrdinalIgnoreCase))
        {
            return this.IncludedPremiumRequests;
        }

        if (sku.Contains("AI Credit", StringComparison.OrdinalIgnoreCase))
        {
            return this.IncludedAiCredits;
        }

        return null;
    }

    /// <summary>
    /// Composes a stable key for a metric identified by its owning account's product
    /// and the metric's title. Titles are not globally unique, so the account product
    /// is required to disambiguate metrics across accounts.
    /// </summary>
    public static string ComposeKey(string product, string title) => $"{product}/{title}";
}

/// <summary>
/// Top-level container. <see cref="Accounts"/> and each account's
/// <see cref="UsageAccount.Metrics"/> must only be mutated on the
/// foreground, so callers should marshal calls to the foreground.
/// </summary>
public sealed class UsageMetrics
{
    private readonly TaskScheduler foregroundScheduler;

    public UsageMetrics(TaskScheduler? foregroundScheduler = null)
    {
        this.foregroundScheduler = foregroundScheduler
            ?? (SynchronizationContext.Current is not null
                ? TaskScheduler.FromCurrentSynchronizationContext()
                : new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler);
    }

    public ObservableCollection<UsageAccount> Accounts { get; } = [];

    /// <summary>
    /// Marshals a mutation to the foreground scheduler so collections are modified on
    /// the correct thread. All mutations to <see cref="Accounts"/> and account metrics
    /// must go through this method.
    /// </summary>
    public Task MutateAsync(Func<Task> mutate)
    {
        return Task.Factory.StartNew(
            mutate,
            CancellationToken.None,
            TaskCreationOptions.None,
            this.foregroundScheduler).Unwrap();
    }
}
