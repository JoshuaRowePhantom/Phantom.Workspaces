using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Backs the usage-tracker toolbar button and popup. Observes <see cref="UsageMetrics"/> and exposes:
/// <list type="bullet">
///   <item><see cref="TopRightLabel"/> — the <see cref="UsageMetric.QuantityPresentation"/> of the most
///     recently updated metric (null when no accounts).</item>
///   <item><see cref="IsOpen"/> — whether the flyout is open.</item>
///   <item><see cref="Accounts"/> — all accounts for flyout rendering.</item>
/// </list>
/// </summary>
internal sealed class UsageTrackerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly UsageMetrics usageMetrics;
    private readonly ILogger<UsageTrackerViewModel> logger;
    private readonly Func<string?, Task>? persistSelectionAsync;
    private readonly Func<IUrlOpener?>? urlOpenerProvider;
    private string? topRightLabel;
    private string? selectedUsageMetricKey;
    private bool suppressSelectionSync;
    private bool isOpen;
    private IReadOnlyList<UsageAccount> accounts;
    private bool disposed;

    private readonly List<(
        UsageAccount Account,
        NotifyCollectionChangedEventHandler MetricsHandler,
        List<(UsageMetric Metric, PropertyChangedEventHandler Handler)> MetricSubscriptions)> accountSubscriptions = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public UsageTrackerViewModel(
        UsageMetrics usageMetrics,
        ILogger<UsageTrackerViewModel>? logger = null,
        string? initialSelectedUsageMetricKey = null,
        Func<string?, Task>? persistSelectionAsync = null,
        Func<IUrlOpener?>? urlOpenerProvider = null)
    {
        this.usageMetrics = usageMetrics;
        this.logger = logger ?? NullLogger<UsageTrackerViewModel>.Instance;
        this.selectedUsageMetricKey = initialSelectedUsageMetricKey;
        this.persistSelectionAsync = persistSelectionAsync;
        this.urlOpenerProvider = urlOpenerProvider;
        this.accounts = [.. usageMetrics.Accounts];
        this.ToggleOpenCommand = new RelayCommand(_ => this.ToggleOpen());
        this.OpenUrlCommand = new RelayCommand(parameter => _ = this.OpenUrlAsync(parameter as string));

        usageMetrics.Accounts.CollectionChanged += this.OnAccountsChanged;

        foreach (var account in usageMetrics.Accounts)
        {
            this.SubscribeAccount(account);
        }

        this.RecomputeTopRightLabel();
    }

    /// <summary>
    /// The underlying <see cref="UsageMetrics"/> model. Exposed for tests that need to
    /// mutate accounts on the same instance that drives this view model.
    /// </summary>
    internal UsageMetrics Metrics => this.usageMetrics;

    /// <summary>
    /// The QuantityPresentation of the most recently updated metric from the most recently charged account.
    /// Falls back to the first account's first metric if no charge has been detected yet.
    /// Returns null when Accounts is empty.
    /// </summary>
    public string? TopRightLabel
    {
        get => this.topRightLabel;
        private set
        {
            if (this.topRightLabel == value) return;
            this.topRightLabel = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.TopRightLabel)));
        }
    }

    /// <summary>Whether the flyout popup is open.</summary>
    public bool IsOpen
    {
        get => this.isOpen;
        set
        {
            if (this.isOpen == value) return;
            this.isOpen = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsOpen)));
        }
    }

    /// <summary>All accounts, for flyout rendering.</summary>
    public IReadOnlyList<UsageAccount> Accounts
    {
        get => this.accounts;
        private set
        {
            this.accounts = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.Accounts)));
        }
    }

    public ICommand ToggleOpenCommand { get; }

    /// <summary>
    /// #1172: XAML-bound command wired to every <c>HyperlinkButton</c> in
    /// <see cref="Phantom.Workspaces.Controls.UsageTrackerControl"/>. The <c>CommandParameter</c>
    /// carries the URL to open. Routes through <see cref="IUrlOpener"/> (Auto preference →
    /// http(s) opens embedded with same-URL dedup; other schemes go external). The view model
    /// deliberately never touches <c>Launcher</c> / <c>Process.Start</c> directly.
    /// </summary>
    public ICommand OpenUrlCommand { get; }

    private async Task OpenUrlAsync(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        var opener = this.urlOpenerProvider?.Invoke();
        if (opener is null)
        {
            return;
        }

        await opener.OpenAsync(new OpenUrlRequest(url));
    }

    /// <summary>
    /// The stable key (see <see cref="UsageAccount.ComposeKey"/>) of the metric the
    /// user has pinned as the top-right indicator. Setting this raises PropertyChanged,
    /// re-runs <see cref="RecomputeTopRightLabel"/>, and (when provided) persists the
    /// value via the configured save callback. Null means auto (most-recently-updated).
    /// </summary>
    public string? SelectedUsageMetricKey
    {
        get => this.selectedUsageMetricKey;
        set
        {
            if (string.Equals(this.selectedUsageMetricKey, value, StringComparison.Ordinal))
            {
                return;
            }

            this.selectedUsageMetricKey = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.SelectedUsageMetricKey)));
            this.SyncSelectionFlagsFromKey();
            this.RecomputeTopRightLabel();

            if (this.persistSelectionAsync is { } persist)
            {
                _ = persist(value);
            }
        }
    }

    private void SyncSelectionFlagsFromKey()
    {
        if (this.suppressSelectionSync) return;

        // #1160: When there is no user pin, respect provider-supplied IsSelectedAsShown
        // defaults (e.g. the net-dollar cost metric is marked as the default budget
        // surface). Clobbering all flags to false here would erase those defaults and
        // let a saturated credit-quantity metric win the toolbar slot.
        if (string.IsNullOrEmpty(this.selectedUsageMetricKey)) return;

        this.suppressSelectionSync = true;
        try
        {
            foreach (var account in this.usageMetrics.Accounts)
            {
                foreach (var metric in account.Metrics)
                {
                    var isMatch = string.Equals(
                        UsageAccount.ComposeKey(account.Product, metric.Title),
                        this.selectedUsageMetricKey,
                        StringComparison.Ordinal);
                    if (metric.IsSelectedAsShown != isMatch)
                    {
                        metric.IsSelectedAsShown = isMatch;
                    }
                }
            }
        }
        finally
        {
            this.suppressSelectionSync = false;
        }
    }

    private void ToggleOpen() => this.IsOpen = !this.IsOpen;

    private void OnAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (this.disposed) return;

        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is UsageAccount account)
                    this.UnsubscribeAccount(account);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is UsageAccount account)
                    this.SubscribeAccount(account);
            }
        }

        this.Accounts = [.. this.usageMetrics.Accounts];
        this.RecomputeTopRightLabel();
    }

    private void SubscribeAccount(UsageAccount account)
    {
        var metricSubscriptions = new List<(UsageMetric, PropertyChangedEventHandler)>();

        foreach (var metric in account.Metrics)
        {
            this.SubscribeMetric(account, metric, metricSubscriptions);
        }

        NotifyCollectionChangedEventHandler metricsHandler = (_, me) =>
        {
            if (this.disposed) return;

            if (me.OldItems is not null)
            {
                foreach (var item in me.OldItems)
                {
                    if (item is UsageMetric m)
                        UnsubscribeMetric(m, metricSubscriptions);
                }
            }

            if (me.NewItems is not null)
            {
                foreach (var item in me.NewItems)
                {
                    if (item is UsageMetric m)
                        this.SubscribeMetric(account, m, metricSubscriptions);
                }
            }

            this.SyncSelectionFlagsFromKey();
            this.RecomputeTopRightLabel();
        };

        account.Metrics.CollectionChanged += metricsHandler;
        this.accountSubscriptions.Add((account, metricsHandler, metricSubscriptions));

        // Apply any pinned selection to the freshly-subscribed metrics.
        this.SyncSelectionFlagsFromKey();
    }

    private void SubscribeMetric(
        UsageAccount account,
        UsageMetric metric,
        List<(UsageMetric, PropertyChangedEventHandler)> subscriptions)
    {
        PropertyChangedEventHandler handler = (_, pe) =>
        {
            if (this.disposed) return;
            if (string.Equals(pe.PropertyName, nameof(UsageMetric.QuantityPresentation), StringComparison.Ordinal)
                || string.Equals(pe.PropertyName, nameof(UsageMetric.LastUpdatedAt), StringComparison.Ordinal))
            {
                this.RecomputeTopRightLabel();
            }
            else if (string.Equals(pe.PropertyName, nameof(UsageMetric.IsSelectedAsShown), StringComparison.Ordinal))
            {
                if (this.suppressSelectionSync) return;
                if (metric.IsSelectedAsShown)
                {
                    this.SelectedUsageMetricKey = UsageAccount.ComposeKey(account.Product, metric.Title);
                }
            }
        };

        metric.PropertyChanged += handler;
        subscriptions.Add((metric, handler));
    }

    private static void UnsubscribeMetric(
        UsageMetric metric,
        List<(UsageMetric Metric, PropertyChangedEventHandler Handler)> subscriptions)
    {
        for (var i = subscriptions.Count - 1; i >= 0; i--)
        {
            var (m, h) = subscriptions[i];
            if (ReferenceEquals(m, metric))
            {
                metric.PropertyChanged -= h;
                subscriptions.RemoveAt(i);
                break;
            }
        }
    }

    private void UnsubscribeAccount(UsageAccount account)
    {
        for (var i = this.accountSubscriptions.Count - 1; i >= 0; i--)
        {
            var (a, metricsHandler, metricSubscriptions) = this.accountSubscriptions[i];
            if (!ReferenceEquals(a, account)) continue;

            account.Metrics.CollectionChanged -= metricsHandler;
            foreach (var (metric, handler) in metricSubscriptions)
            {
                metric.PropertyChanged -= handler;
            }

            this.accountSubscriptions.RemoveAt(i);
            break;
        }
    }

    private void RecomputeTopRightLabel()
    {
        var allAccounts = this.usageMetrics.Accounts;

        if (allAccounts.Count == 0)
        {
            this.logger.LogDebug("Usage panel hidden: no accounts.");
            this.TopRightLabel = null;
            return;
        }

        this.logger.LogInformation(
            "Usage panel shown for {AccountCount} account(s).",
            allAccounts.Count);

        // Explicit user pin: if a saved key matches an existing account+metric, use it.
        if (!string.IsNullOrEmpty(this.selectedUsageMetricKey))
        {
            foreach (var account in allAccounts)
            {
                foreach (var metric in account.Metrics)
                {
                    if (string.Equals(
                        UsageAccount.ComposeKey(account.Product, metric.Title),
                        this.selectedUsageMetricKey,
                        StringComparison.Ordinal))
                    {
                        this.TopRightLabel = metric.QuantityPresentation;
                        return;
                    }
                }
            }
            // Fall through: saved key does not match any current metric. Leave the
            // stored key intact so the pin re-applies if the metric reappears.
        }

        // #1160: When there is no user pin, prefer any metric the provider has marked as
        // the default budget surface (IsSelectedAsShown = true). This makes the net-dollar
        // cost metric win over the credit-quantity metric so the toolbar shows real
        // budget consumption rather than a saturated included-allotment counter.
        foreach (var account in allAccounts)
        {
            foreach (var metric in account.Metrics)
            {
                if (metric.IsSelectedAsShown)
                {
                    this.TopRightLabel = metric.QuantityPresentation;
                    return;
                }
            }
        }

        // Find the metric with the most recent non-null LastUpdatedAt across all accounts
        UsageMetric? mostRecentMetric = null;
        DateTime mostRecentTime = DateTime.MinValue;

        foreach (var account in allAccounts)
        {
            foreach (var metric in account.Metrics)
            {
                if (metric.LastUpdatedAt is { } t && t > mostRecentTime)
                {
                    mostRecentTime = t;
                    mostRecentMetric = metric;
                }
            }
        }

        if (mostRecentMetric is not null)
        {
            this.TopRightLabel = mostRecentMetric.QuantityPresentation;
            return;
        }

        // Fallback: first account's first metric
        var firstMetric = allAccounts[0].Metrics.FirstOrDefault();
        this.TopRightLabel = firstMetric?.QuantityPresentation;
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;

        this.usageMetrics.Accounts.CollectionChanged -= this.OnAccountsChanged;

        foreach (var (account, metricsHandler, metricSubscriptions) in this.accountSubscriptions)
        {
            account.Metrics.CollectionChanged -= metricsHandler;
            foreach (var (metric, handler) in metricSubscriptions)
            {
                metric.PropertyChanged -= handler;
            }
        }

        this.accountSubscriptions.Clear();
    }
}
