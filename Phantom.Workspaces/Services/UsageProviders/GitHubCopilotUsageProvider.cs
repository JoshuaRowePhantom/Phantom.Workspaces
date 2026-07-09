using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Models;

namespace Phantom.Workspaces.Services.UsageProviders;

/// <summary>
/// Fetches GitHub Copilot billing usage via:
/// GET https://api.github.com/copilot/billing/usage
///
/// Maps response to two metrics:
/// - Included Usage: seat_breakdown.active_this_cycle / seat_breakdown.total (unit: "AIC")
/// - Additional Usage: billed overage dollars (format: "{0:C2} / {1:C2}")
/// </summary>
public sealed class GitHubCopilotUsageProvider : IUsageProvider
{
    private readonly HttpClient httpClient;
    private readonly Func<string?> tokenResolver;
    private readonly ILogger<GitHubCopilotUsageProvider> logger;

    public Uri ProviderUri { get; } = new Uri("https://github.com");

    public GitHubCopilotUsageProvider(
        HttpClient httpClient,
        ILogger<GitHubCopilotUsageProvider>? logger = null)
        : this(httpClient, () => GitHubAuthTokenResolver.Resolve(), logger)
    {
    }

    internal GitHubCopilotUsageProvider(
        HttpClient httpClient,
        Func<string?> tokenResolver,
        ILogger<GitHubCopilotUsageProvider>? logger = null)
    {
        this.httpClient = httpClient;
        this.tokenResolver = tokenResolver;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubCopilotUsageProvider>.Instance;
    }

    public async Task<IReadOnlyList<UsageMetric>> GetMetricsAsync(
        UsageAccount account,
        CancellationToken cancellationToken)
    {
        var token = this.tokenResolver();
        var response = await this.SendRequestAsync(token, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            token = this.tokenResolver();
            response = await this.SendRequestAsync(token, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new HttpRequestException(
                    "GitHub Copilot API returned 401 after token refresh.",
                    null,
                    HttpStatusCode.Unauthorized);
            }
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseMetrics(json);
    }

    private Task<HttpResponseMessage> SendRequestAsync(string? token, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/copilot/billing/usage");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Add("User-Agent", "phantom-workspaces");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return this.httpClient.SendAsync(request, cancellationToken);
    }

    private static IReadOnlyList<UsageMetric> ParseMetrics(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var now = DateTime.UtcNow;
        var metrics = new List<UsageMetric>();

        // Included Usage: seat_breakdown.active_this_cycle / seat_breakdown.total
        if (root.TryGetProperty("seat_breakdown", out var seatBreakdown))
        {
            var activeThisCycle = seatBreakdown.TryGetProperty("active_this_cycle", out var atc)
                && atc.ValueKind == JsonValueKind.Number
                ? atc.GetDecimal()
                : 0m;
            var total = seatBreakdown.TryGetProperty("total", out var tot)
                && tot.ValueKind == JsonValueKind.Number
                ? tot.GetDecimal()
                : 0m;

            metrics.Add(new UsageMetric
            {
                Title = "Included Usage",
                QuantityUsed = activeThisCycle,
                QuantityTotal = total,
                QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
                Unit = "AIC",
                LastUpdatedAt = now,
            });
        }

        // Additional Usage: billed overage dollars
        var billedAmount = root.TryGetProperty("total_billed_amount", out var billed)
            && billed.ValueKind == JsonValueKind.Number
            ? billed.GetDecimal()
            : 0m;
        var cap = root.TryGetProperty("plan_type", out _) ? 0m : 0m; // cap not always available

        metrics.Add(new UsageMetric
        {
            Title = "Additional Usage",
            QuantityUsed = billedAmount,
            QuantityTotal = cap,
            QuantityPresentationFormatString = "{0:C2} / {1:C2}",
            Unit = string.Empty,
            LastUpdatedAt = now,
        });

        return metrics;
    }
}
