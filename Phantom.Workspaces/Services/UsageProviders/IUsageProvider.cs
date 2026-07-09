using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Models;

namespace Phantom.Workspaces.Services.UsageProviders;

public interface IUsageProvider
{
    /// <summary>The provider base URL this implementation handles (e.g. "https://github.com").</summary>
    Uri ProviderUri { get; }

    /// <summary>
    /// Fetches the current usage metrics for <paramref name="account"/>.
    /// The provider is responsible for acquiring and refreshing its own credentials.
    /// </summary>
    Task<IReadOnlyList<UsageMetric>> GetMetricsAsync(
        UsageAccount account,
        CancellationToken cancellationToken);
}
