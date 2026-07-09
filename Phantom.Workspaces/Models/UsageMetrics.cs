using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Models;

/// <summary>One measured billing dimension for a provider account.</summary>
public sealed class UsageMetric : INotifyPropertyChanged
{
    private decimal quantityUsed;
    private decimal quantityTotal;
    private DateTime? lastUpdatedAt;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>e.g. "Included Usage", "Additional Usage"</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Format string for <see cref="QuantityPresentation"/>.
    /// Arguments: {0} = QuantityUsed, {1} = QuantityTotal, {2} = unit label.
    /// Example: <c>"{0:N0} / {1:N0} {2}"</c> → "256 / 755 minutes"
    /// Example: <c>"{0:C2} / {1:C2}"</c> → "$356.00 / $3,000.00"
    /// </summary>
    public string QuantityPresentationFormatString { get; init; } = "{0} / {1} {2}";

    /// <summary>Unit label substituted as {2} in the format string. e.g. "minutes", "AIC", or empty for currency.</summary>
    public string Unit { get; init; } = string.Empty;

    public decimal QuantityUsed
    {
        get => this.quantityUsed;
        set
        {
            if (this.quantityUsed == value) return;
            this.quantityUsed = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(this.FractionUsed));
            this.RaisePropertyChanged(nameof(this.QuantityPresentation));
        }
    }

    public decimal QuantityTotal
    {
        get => this.quantityTotal;
        set
        {
            if (this.quantityTotal == value) return;
            this.quantityTotal = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(this.FractionUsed));
            this.RaisePropertyChanged(nameof(this.QuantityPresentation));
        }
    }

    public DateTime? LastUpdatedAt
    {
        get => this.lastUpdatedAt;
        set
        {
            if (this.lastUpdatedAt == value) return;
            this.lastUpdatedAt = value;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>
    /// Fraction of the quota consumed (0.0–1.0), or <see langword="null"/> when
    /// <see cref="QuantityTotal"/> is zero.
    /// </summary>
    public double? FractionUsed =>
        QuantityTotal == 0 ? null : (double)(QuantityUsed / QuantityTotal);

    /// <summary>
    /// Human-readable presentation produced by formatting <see cref="QuantityPresentationFormatString"/>
    /// with {0}=<see cref="QuantityUsed"/>, {1}=<see cref="QuantityTotal"/>, {2}=<see cref="Unit"/>.
    /// </summary>
    public string QuantityPresentation =>
        string.Format(QuantityPresentationFormatString, QuantityUsed, QuantityTotal, Unit).TrimEnd();

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>One external-provider account with its associated usage metrics.</summary>
public sealed class UsageAccount
{
    /// <summary>e.g. "GitHub Copilot", "GitHub Actions"</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>e.g. "jrowe"</summary>
    public string UserName { get; init; } = string.Empty;

    public Uri SettingsUrl { get; init; } = new Uri("https://github.com");

    public ObservableCollection<UsageMetric> Metrics { get; } = [];
}

/// <summary>
/// Top-level container. <see cref="Accounts"/> and each account's
/// <see cref="UsageAccount.Metrics"/> must only be mutated on the foreground.
/// Use <see cref="MutateAsync"/> to marshal mutations to the configured scheduler.
/// </summary>
public sealed class UsageMetrics
{
    private readonly TaskScheduler scheduler;

    public UsageMetrics(TaskScheduler? scheduler = null)
    {
        this.scheduler = scheduler ?? TaskScheduler.Default;
    }

    public ObservableCollection<UsageAccount> Accounts { get; } = [];

    /// <summary>Runs <paramref name="mutation"/> on the foreground scheduler.</summary>
    public Task MutateAsync(Action mutation) =>
        Task.Factory.StartNew(mutation, CancellationToken.None, TaskCreationOptions.None, this.scheduler);
}
