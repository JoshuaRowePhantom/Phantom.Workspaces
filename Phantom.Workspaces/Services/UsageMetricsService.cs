using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.Services.UsageProviders;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Discovers <c>user-account</c> entities from the DAL, populates <see cref="UsageMetrics.Accounts"/>,
/// and runs a 60-second periodic refresh loop using the configured providers.
/// All collection mutations are marshalled to the UI thread via <see cref="UsageMetrics.MutateAsync"/>.
/// </summary>
public sealed class UsageMetricsService : IAsyncDisposable
{
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly UsageMetrics usageMetrics;
    private readonly IReadOnlyList<IUsageProvider> providers;
    private readonly Func<CancellationToken, Task> waitForNextTick;
    private readonly ILogger<UsageMetricsService> logger;
    private readonly CancellationTokenSource cancellation = new();
    private Task? loopTask;

    public UsageMetricsService(
        IDataAccessLayer dataAccessLayer,
        UsageMetrics usageMetrics,
        IReadOnlyList<IUsageProvider> providers,
        TimeProvider timeProvider,
        ILogger<UsageMetricsService>? logger = null)
        : this(
            dataAccessLayer,
            usageMetrics,
            providers,
            ct => Task.Delay(TimeSpan.FromSeconds(60), timeProvider, ct),
            logger)
    {
    }

    internal UsageMetricsService(
        IDataAccessLayer dataAccessLayer,
        UsageMetrics usageMetrics,
        IReadOnlyList<IUsageProvider> providers,
        Func<CancellationToken, Task> waitForNextTick,
        ILogger<UsageMetricsService>? logger = null)
    {
        this.dataAccessLayer = dataAccessLayer;
        this.usageMetrics = usageMetrics;
        this.providers = providers;
        this.waitForNextTick = waitForNextTick;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageMetricsService>.Instance;
    }

    /// <summary>Starts the discovery-and-refresh loop. Returns immediately; the loop runs in the background.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.loopTask = Task.Run(() => this.RunLoopAsync(this.cancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var accounts = await this.DiscoverAccountsAsync(cancellationToken).ConfigureAwait(false);

        await this.RefreshAllAsync(accounts, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await this.waitForNextTick(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested) break;

            await this.RefreshAllAsync(accounts, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<(UsageAccount Account, IUsageProvider Provider)>> DiscoverAccountsAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<(UsageAccount, IUsageProvider)>();

        try
        {
            var queryResult = await this.dataAccessLayer.QueryAsync(
                new QueryRequest
                {
                    Clauses =
                    [
                        new TopLevelQueryClause
                        {
                            ClauseIdentifier = new QueryClauseIdentifier("user-accounts"),
                            Clause = new EntityTypeQueryClause
                            {
                                EntityTypeNames = new EntityTypeNameSet(["user-account"]),
                            },
                        },
                    ],
                    Timestamps = [null],
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var batch in queryResult.Batches)
            {
                foreach (var entity in batch.Entities)
                {
                    if (entity.Data is not { } data) continue;

                    if (!data.TryGetProperty("provider", out var providerElement)
                        || providerElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(providerElement.GetString()))
                        continue;

                    if (!data.TryGetProperty("user-name", out var userNameElement)
                        || userNameElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(userNameElement.GetString()))
                        continue;

                    var providerUriStr = providerElement.GetString()!;
                    if (!Uri.TryCreate(providerUriStr, UriKind.Absolute, out var providerUri))
                        continue;

                    var provider = this.FindProvider(providerUri);
                    if (provider is null) continue;

                    var account = new UsageAccount
                    {
                        Product = DeriveProductName(providerUri),
                        UserName = userNameElement.GetString()!,
                        SettingsUrl = new Uri($"{providerUriStr.TrimEnd('/')}/settings/billing/summary"),
                    };

                    result.Add((account, provider));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to discover user-account entities.");
        }

        return result;
    }

    private IUsageProvider? FindProvider(Uri providerUri)
    {
        foreach (var provider in this.providers)
        {
            if (string.Equals(provider.ProviderUri.Host, providerUri.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(provider.ProviderUri.Scheme, providerUri.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return provider;
            }
        }

        return null;
    }

    private static string DeriveProductName(Uri providerUri) =>
        providerUri.Host switch
        {
            "github.com" => "GitHub",
            _ => providerUri.Host,
        };

    private async Task RefreshAllAsync(
        List<(UsageAccount Account, IUsageProvider Provider)> accounts,
        CancellationToken cancellationToken)
    {
        foreach (var (account, provider) in accounts)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await this.RefreshAccountAsync(account, provider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshAccountAsync(
        UsageAccount account,
        IUsageProvider provider,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<UsageMetric>? metrics = null;
        try
        {
            metrics = await provider.GetMetricsAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Provider {ProviderType} failed for account {UserName}.",
                provider.GetType().Name, account.UserName);
            return;
        }

        var capturedMetrics = metrics;
        await this.usageMetrics.MutateAsync(() =>
        {
            var isVisible = this.usageMetrics.Accounts.Contains(account);

            if (capturedMetrics.Count == 0)
            {
                if (isVisible)
                {
                    this.usageMetrics.Accounts.Remove(account);
                }

                return;
            }

            account.Metrics.Clear();
            foreach (var metric in capturedMetrics)
            {
                account.Metrics.Add(metric);
            }

            if (!isVisible)
            {
                this.usageMetrics.Accounts.Add(account);
            }
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        this.cancellation.Cancel();

        if (this.loopTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        this.cancellation.Dispose();
    }
}
