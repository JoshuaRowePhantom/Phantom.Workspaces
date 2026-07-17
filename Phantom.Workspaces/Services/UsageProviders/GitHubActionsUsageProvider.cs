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
/// Fetches GitHub Actions billing usage via:
/// GET https://api.github.com/users/{username}/settings/billing/actions
///
/// Maps response to two metrics:
/// - Included Usage: total_minutes_used / included_minutes (unit: "minutes")
/// - Additional Usage: total_paid_minutes_used * per-minute rate (format: "{0:C2} / {1:C2}")
///
/// Note: GitHub Actions billing API does not expose a billing cap, so QuantityTotal for Additional
/// Usage is set to 0 (FractionUsed will be null).
/// </summary>
public sealed class GitHubActionsUsageProvider : IUsageProvider
{
    private readonly HttpClient httpClient;
    private readonly Func<string?> tokenResolver;
    private readonly ILogger<GitHubActionsUsageProvider> logger;

    public Uri ProviderUri { get; } = new Uri("https://github.com");

    public GitHubActionsUsageProvider(
        HttpClient httpClient,
        ILogger<GitHubActionsUsageProvider>? logger = null)
        : this(httpClient, () => GitHubAuthTokenResolver.Resolve(), logger)
    {
    }

    internal GitHubActionsUsageProvider(
        HttpClient httpClient,
        Func<string?> tokenResolver,
        ILogger<GitHubActionsUsageProvider>? logger = null)
    {
        this.httpClient = httpClient;
        this.tokenResolver = tokenResolver;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubActionsUsageProvider>.Instance;
    }

    public async Task<IReadOnlyList<UsageMetric>> GetMetricsAsync(
        UsageAccount account,
        CancellationToken cancellationToken)
    {
        var token = this.tokenResolver();
        var response = await this.SendRequestAsync(account.UserName, token, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            token = this.tokenResolver();
            response = await this.SendRequestAsync(account.UserName, token, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new HttpRequestException(
                    "GitHub Actions billing API returned 401 after token refresh.",
                    null,
                    HttpStatusCode.Unauthorized);
            }
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            this.logger.LogWarning(
                "GitHub Actions usage provider returned {StatusCode} for {Endpoint}; returning empty metrics.",
                (int)response.StatusCode,
                $"https://api.github.com/users/{account.UserName}/settings/billing/actions");
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            this.logger.LogError(
                "GitHub Actions usage provider returned non-success {StatusCode} for {Endpoint}.",
                (int)response.StatusCode,
                $"https://api.github.com/users/{account.UserName}/settings/billing/actions");
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseMetrics(json);
    }

    private Task<HttpResponseMessage> SendRequestAsync(
        string userName,
        string? token,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/users/{Uri.EscapeDataString(userName)}/settings/billing/actions");
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

        var totalMinutesUsed = root.TryGetProperty("total_minutes_used", out var tmu)
            && tmu.ValueKind == JsonValueKind.Number
            ? tmu.GetDecimal()
            : 0m;

        var includedMinutes = root.TryGetProperty("included_minutes", out var im)
            && im.ValueKind == JsonValueKind.Number
            ? im.GetDecimal()
            : 0m;

        var totalPaidMinutesUsed = root.TryGetProperty("total_paid_minutes_used", out var tpmu)
            && tpmu.ValueKind == JsonValueKind.Number
            ? tpmu.GetDecimal()
            : 0m;

        return
        [
            new UsageMetric
            {
                Title = "Included Usage",
                QuantityUsed = totalMinutesUsed,
                QuantityTotal = includedMinutes,
                QuantityPresentationFormatString = "{0:N0} / {1:N0} {2}",
                Unit = "minutes",
                LastUpdatedAt = now,
            },
            new UsageMetric
            {
                Title = "Additional Usage",
                QuantityUsed = totalPaidMinutesUsed,
                QuantityTotal = 0m,
                QuantityPresentationFormatString = "{0:C2} / {1:C2}",
                Unit = string.Empty,
                LastUpdatedAt = now,
            },
        ];
    }
}
