using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.Services.UsageProviders;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Discovers user-account entities from the DAL, creates UsageAccount instances,
/// and runs a 60-second periodic refresh loop using Task.Delay + TimeProvider pattern.
/// Providers are fully responsible for credential acquisition; the service only calls
/// them with an account and receives metrics.
/// </summary>
public sealed class UsageMetricsService : IAsyncDisposable
{
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly UsageMetrics usageMetrics;
    private readonly IReadOnlyDictionary<Uri, IUsageProvider> providersByUri;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<UsageMetricsService> logger;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly Dictionary<string, UsageAccount> accountsByKey = new();
    private Task? loopTask;

    public UsageMetricsService(
        IDataAccessLayer dataAccessLayer,
        UsageMetrics usageMetrics,
        IReadOnlyList<IUsageProvider> providers,
        TimeProvider timeProvider,
        ILogger<UsageMetricsService> logger)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
        this.usageMetrics = usageMetrics ?? throw new ArgumentNullException(nameof(usageMetrics));
        ArgumentNullException.ThrowIfNull(providers);
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        this.providersByUri = providers.ToDictionary(p => p.ProviderUri, p => p);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (this.loopTask != null)
        {
            return Task.CompletedTask;
        }

        this.loopTask = Task.Run(async () => await this.RunLoopAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken externalCancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            this.cancellationTokenSource.Token,
            externalCancellationToken);

        var cancellationToken = linkedCts.Token;

        // Discover accounts once at startup
        var discoveredAccounts = await this.DiscoverAccountsAsync(cancellationToken).ConfigureAwait(false);

        // Immediate run on startup
        await this.RefreshAllAccountsAsync(discoveredAccounts, cancellationToken).ConfigureAwait(false);

        // Periodic refresh every 60 seconds
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), this.timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await this.RefreshAllAccountsAsync(discoveredAccounts, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<DiscoveredAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        var queryResult = await this.dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses = new[]
                {
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("user-accounts"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet(new[] { "user-account" })
                        }
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);

        var discoveredAccounts = new List<DiscoveredAccount>();

        foreach (var batch in queryResult.Batches)
        {
            foreach (var entity in batch.Entities)
            {
                if (entity.Data is not { } data)
                {
                    continue;
                }

                if (!data.TryGetProperty("provider", out var providerElement)
                    || providerElement.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(providerElement.GetString(), UriKind.Absolute, out var providerUri))
                {
                    continue;
                }

                if (!data.TryGetProperty("user-name", out var userNameElement)
                    || userNameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var userName = userNameElement.GetString() ?? string.Empty;

                // Only include accounts with a registered provider
                if (!this.providersByUri.TryGetValue(providerUri, out var provider))
                {
                    continue;
                }

                discoveredAccounts.Add(new DiscoveredAccount
                {
                    ProviderUri = providerUri,
                    UserName = userName,
                    Provider = provider
                });
            }
        }

        return discoveredAccounts;
    }

    private async Task RefreshAllAccountsAsync(
        List<DiscoveredAccount> discoveredAccounts,
        CancellationToken cancellationToken)
    {
        foreach (var discovered in discoveredAccounts)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await this.RefreshAccountAsync(discovered, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshAccountAsync(
        DiscoveredAccount discovered,
        CancellationToken cancellationToken)
    {
        var accountKey = $"{discovered.ProviderUri}|{discovered.UserName}";

        try
        {
            // Create or get the UsageAccount for this provider/user
            if (!this.accountsByKey.TryGetValue(accountKey, out var account))
            {
                account = new UsageAccount
                {
                    Product = discovered.ProviderUri.Host,
                    UserName = discovered.UserName,
                    SettingsUrl = discovered.ProviderUri
                };
                this.accountsByKey[accountKey] = account;
            }

            // Call the provider
            var metrics = await discovered.Provider.GetMetricsAsync(account, cancellationToken).ConfigureAwait(false);

            // Apply account visibility rule
            var hasMetrics = metrics.Count > 0;
            var isCurrentlyVisible = this.usageMetrics.Accounts.Contains(account);

            if (hasMetrics)
            {
                // Update metrics
                account.Metrics.Clear();
                foreach (var metric in metrics)
                {
                    account.Metrics.Add(metric);
                }

                // Add account if not already visible
                if (!isCurrentlyVisible)
                {
                    this.usageMetrics.Accounts.Add(account);
                }
            }
            else
            {
                // Remove account if it was visible
                if (isCurrentlyVisible)
                {
                    this.usageMetrics.Accounts.Remove(account);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown
            throw;
        }
        catch (Exception exception)
        {
            this.logger.LogWarning(
                exception,
                "Failed to refresh usage metrics for {Provider}/{UserName}",
                discovered.ProviderUri,
                discovered.UserName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.cancellationTokenSource.Cancel();

        if (this.loopTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
        }

        this.cancellationTokenSource.Dispose();
    }

    private sealed class DiscoveredAccount
    {
        public required Uri ProviderUri { get; init; }
        public required string UserName { get; init; }
        public required IUsageProvider Provider { get; init; }
    }
}
