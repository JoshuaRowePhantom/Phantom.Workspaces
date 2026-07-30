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
/// Fetches GitHub Copilot billing usage via the enhanced billing endpoint:
/// GET https://api.github.com/users/{username}/settings/billing/usage
///
/// Parses the documented <c>usageItems[]</c> schema, filters items whose
/// <c>product</c> is <c>"copilot"</c>, and maps each Copilot SKU (e.g.
/// "Copilot Premium Request", "Copilot AI Credits") into a <see cref="UsageMetric"/>.
/// </summary>
public sealed class GitHubCopilotUsageProvider : IUsageProvider
{
    private readonly HttpClient httpClient;
    private readonly Func<string?> tokenResolver;
    private readonly ILogger<GitHubCopilotUsageProvider> logger;
    private readonly TimeProvider timeProvider;

    public Uri ProviderUri { get; } = new Uri("https://github.com/copilot");

    public GitHubCopilotUsageProvider(
        HttpClient httpClient,
        ILogger<GitHubCopilotUsageProvider>? logger = null,
        TimeProvider? timeProvider = null)
        : this(httpClient, () => GitHubAuthTokenResolver.Resolve(), logger, timeProvider)
    {
    }

    internal GitHubCopilotUsageProvider(
        HttpClient httpClient,
        Func<string?> tokenResolver,
        ILogger<GitHubCopilotUsageProvider>? logger = null,
        TimeProvider? timeProvider = null)
    {
        this.httpClient = httpClient;
        this.tokenResolver = tokenResolver;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubCopilotUsageProvider>.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<UsageMetric>> GetMetricsAsync(
        UsageAccount account,
        CancellationToken cancellationToken)
    {
        var url = BuildRequestUrl(account);
        var token = this.tokenResolver();
        var response = await this.SendRequestAsync(url, token, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            token = this.tokenResolver();
            response = await this.SendRequestAsync(url, token, cancellationToken).ConfigureAwait(false);

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
            this.logger.LogWarning(
                "GitHub Copilot usage provider returned {StatusCode} for {Endpoint}; returning empty metrics.",
                (int)response.StatusCode,
                url);
            return [];
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            this.logger.LogWarning(
                "GitHub Copilot usage provider returned {StatusCode} for {Endpoint}; the access token likely lacks the required 'Plan' user permission (read). Returning empty metrics.",
                (int)response.StatusCode,
                url);
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            this.logger.LogError(
                "GitHub Copilot usage provider returned non-success {StatusCode} for {Endpoint}.",
                (int)response.StatusCode,
                url);
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseMetrics(json, this.timeProvider.GetUtcNow().UtcDateTime);
    }

    private static string BuildRequestUrl(UsageAccount account)
    {
        var userName = account.UserName ?? string.Empty;
        return $"https://api.github.com/users/{Uri.EscapeDataString(userName)}/settings/billing/usage";
    }

    private Task<HttpResponseMessage> SendRequestAsync(string url, string? token, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        request.Headers.Add("User-Agent", "phantom-workspaces");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return this.httpClient.SendAsync(request, cancellationToken);
    }

    private static IReadOnlyList<UsageMetric> ParseMetrics(string json, DateTime now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("usageItems", out var usageItems) ||
            usageItems.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // Aggregate Copilot items by SKU. Non-Copilot products (e.g. "actions", "models")
        // are filtered out.
        var aggregates = new Dictionary<string, CopilotSkuAggregate>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var item in usageItems.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var product = item.TryGetProperty("product", out var productElement)
                && productElement.ValueKind == JsonValueKind.String
                ? productElement.GetString()
                : null;

            if (!string.Equals(product, "copilot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sku = item.TryGetProperty("sku", out var skuElement)
                && skuElement.ValueKind == JsonValueKind.String
                ? skuElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrEmpty(sku))
            {
                sku = "Copilot";
            }

            var quantity = item.TryGetProperty("quantity", out var qElement)
                && qElement.ValueKind == JsonValueKind.Number
                ? qElement.GetDecimal()
                : 0m;
            var unitType = item.TryGetProperty("unitType", out var unitElement)
                && unitElement.ValueKind == JsonValueKind.String
                ? unitElement.GetString() ?? string.Empty
                : string.Empty;
            var netAmount = item.TryGetProperty("netAmount", out var netElement)
                && netElement.ValueKind == JsonValueKind.Number
                ? netElement.GetDecimal()
                : 0m;

            if (!aggregates.TryGetValue(sku, out var aggregate))
            {
                aggregate = new CopilotSkuAggregate { Sku = sku, Unit = unitType };
                aggregates[sku] = aggregate;
                order.Add(sku);
            }
            else if (string.IsNullOrEmpty(aggregate.Unit) && !string.IsNullOrEmpty(unitType))
            {
                aggregate.Unit = unitType;
            }

            aggregate.Quantity += quantity;
            aggregate.NetAmount += netAmount;
        }

        var metrics = new List<UsageMetric>(order.Count);
        foreach (var sku in order)
        {
            var aggregate = aggregates[sku];
            metrics.Add(new UsageMetric
            {
                Title = aggregate.Sku,
                QuantityUsed = aggregate.Quantity,
                QuantityTotal = 0m,
                QuantityPresentationFormatString = "{0:N0} {2}",
                Unit = aggregate.Unit,
                LastUpdatedAt = now,
            });

            if (aggregate.NetAmount != 0m)
            {
                metrics.Add(new UsageMetric
                {
                    Title = $"{aggregate.Sku} (Cost)",
                    QuantityUsed = aggregate.NetAmount,
                    QuantityTotal = 0m,
                    QuantityPresentationFormatString = "{0:C2}",
                    Unit = string.Empty,
                    LastUpdatedAt = now,
                });
            }
        }

        return metrics;
    }

    private sealed class CopilotSkuAggregate
    {
        public string Sku { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal NetAmount { get; set; }
    }
}
