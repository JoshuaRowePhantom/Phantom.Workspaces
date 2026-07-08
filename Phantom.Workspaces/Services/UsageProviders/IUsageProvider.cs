using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Models;

namespace Phantom.Workspaces.Services.UsageProviders;

/// <summary>Pluggable provider for fetching usage metrics for a single external-provider account.</summary>
public interface IUsageProvider
{
    /// <summary>The provider base URL this implementation handles (e.g. "https://github.com").</summary>
    Uri ProviderUri { get; }

    /// <summary>
    /// Fetches the current usage metrics for <paramref name="account"/>.
    /// The provider is responsible for acquiring and refreshing its own credentials.
    /// Returns an empty list if the account has no subscription or data.
    /// </summary>
    Task<IReadOnlyList<UsageMetric>> GetMetricsAsync(
        UsageAccount account,
        CancellationToken cancellationToken);
}
