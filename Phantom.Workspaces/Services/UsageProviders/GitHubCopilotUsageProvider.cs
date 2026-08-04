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
        var token = this.tokenResolver();
        var now = this.timeProvider.GetUtcNow();

        // Fetch org budgets first (#1188): they carry the current billing period, which
        // we then pass to /settings/billing/usage as year/month/day so aggregation is
        // bounded to the current period. Personal accounts (account.Org == null) skip
        // this call entirely.
        var budgets = account.Org is { Length: > 0 } org
            ? await this.FetchOrgBudgetsAsync(org, account.UserName, token, cancellationToken).ConfigureAwait(false)
            : Array.Empty<ScopedBudget>();

        // Discover the current period start / end. Prefer any budget that supplies
        // current_period_start (selected via the same scope priority as budget amount);
        // else fall back to the calendar-month start as a documented approximation.
        var (periodStart, periodEnd) = SelectPeriod(budgets, account.UserName, now);

        var url = BuildRequestUrl(account, periodStart);

        // #1211: Log the fetch URL + period so the request the server received is
        // reconstructable from the log stream alone (previously any usage
        // discrepancy could only be root-caused with a debugger attached).
        this.logger.LogInformation(
            "GitHubCopilotUsageProvider fetching usage from {Endpoint} for period {PeriodStart:o}..{PeriodEnd:o}.",
            url,
            periodStart,
            periodEnd);

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

        // #1211: Success-path fetch observability. Information-level entries capture
        // URL + status + body-size so a routine log grep confirms the fetch happened;
        // the raw JSON body is only emitted at Debug to avoid log bloat. The
        // Authorization header is set in SendRequestAsync and is deliberately never
        // passed to the logger — the body itself does not contain credentials.
        this.logger.LogInformation(
            "GitHubCopilotUsageProvider received {StatusCode} for {Endpoint} ({ByteCount} bytes).",
            (int)response.StatusCode,
            url,
            json.Length);

        if (this.logger.IsEnabled(LogLevel.Debug))
        {
            this.logger.LogDebug(
                "GitHubCopilotUsageProvider response body for {Endpoint}: {Body}",
                url,
                json);
        }

        return ParseMetrics(json, account, budgets, now.UtcDateTime, periodStart, periodEnd);
    }

    private static string BuildRequestUrl(UsageAccount account, DateTimeOffset periodStart)
    {
        var userName = Uri.EscapeDataString(account.UserName ?? string.Empty);
        var utc = periodStart.UtcDateTime;
        // #1211: Do NOT include `day=`. GitHub Enhanced Billing treats `day=` as a
        // single-calendar-day filter, which would exclude any paid overage /
        // "additional usage" accrued after the period-start day. Fetch the whole
        // month at server side and let ParseMetrics trim to [periodStart, periodEnd)
        // client-side via the #1188 filter.
        return $"https://api.github.com/users/{userName}/settings/billing/usage?year={utc.Year}&month={utc.Month}";
    }

    /// <summary>
    /// Picks the current billing period, preferring a period surfaced by the org budgets
    /// (user scope first, then multi-user, then organization) and falling back to the
    /// first day of the current calendar month with no upper bound (#1188). PeriodEnd is
    /// only known when a budget explicitly provides <c>current_period_end</c>.
    /// </summary>
    private static (DateTimeOffset PeriodStart, DateTimeOffset? PeriodEnd) SelectPeriod(
        IReadOnlyList<ScopedBudget> budgets,
        string userName,
        DateTimeOffset now)
    {
        foreach (var scope in new[] { "user", "multi_user_customer", "organization" })
        {
            foreach (var b in budgets)
            {
                if (!string.Equals(b.Scope, scope, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (scope == "user"
                    && !string.IsNullOrEmpty(userName)
                    && !string.IsNullOrEmpty(b.EntityName)
                    && !string.Equals(b.EntityName, userName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (b.PeriodStart is { } start)
                {
                    return (start, b.PeriodEnd);
                }
            }
        }

        // Personal-account / no-period-metadata fallback: the current calendar month.
        var monthStart = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (monthStart, null);
    }

    private Task<HttpResponseMessage> SendRequestAsync(string url, string? token, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        request.Headers.Add("User-Agent", "phantom-workspaces");
        if (!string.IsNullOrWhiteSpace(token))
        {
            // #1211: Never pass `token` or `request.Headers.Authorization` to the logger.
            // The URL, status, and body are sufficient for triage; the bearer token has
            // no operational benefit in a log file and would leak into any attached
            // bug-report snippet.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return this.httpClient.SendAsync(request, cancellationToken);
    }

    private static IReadOnlyList<UsageMetric> ParseMetrics(
        string json,
        UsageAccount account,
        IReadOnlyList<ScopedBudget> budgets,
        DateTime now,
        DateTimeOffset periodStart,
        DateTimeOffset? periodEnd)
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

            // #1188: When the item carries a date, exclude items outside the current
            // billing period. Items without a date field are still included (the API
            // does not always emit one, and older responses used by tests do not).
            if (item.TryGetProperty("date", out var dateElement)
                && dateElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(dateElement.GetString(), out var itemDate))
            {
                if (itemDate < periodStart)
                {
                    continue;
                }

                if (periodEnd is { } end && itemDate >= end)
                {
                    continue;
                }
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
            var webUrl = BuildMetricWebUrl(account, aggregate.Sku);

            // Count-metric denominator: configured included quantity for the SKU (Premium
            // Request). AI Credits deliberately gets no denominator here — its included
            // allotment is exactly the budget signal that saturates at 100% (see #1160),
            // and the net-dollar cost metric is now the budget-relevant surface for AI
            // credits.
            var isAiCredits = aggregate.Sku.Contains("AI Credit", StringComparison.OrdinalIgnoreCase);
            var countTotal = isAiCredits
                ? 0m
                : account.GetIncludedQuantity(aggregate.Sku) ?? 0m;

            metrics.Add(new UsageMetric
            {
                Title = aggregate.Sku,
                QuantityUsed = aggregate.Quantity,
                QuantityTotal = countTotal,
                QuantityPresentationFormatString = "{0:N0} {2}",
                Unit = aggregate.Unit,
                LastUpdatedAt = now,
                WebUrl = webUrl,
                ResetsAt = periodEnd,
                BillingPeriodStart = periodStart,
            });

            if (aggregate.NetAmount != 0m)
            {
                // Cost-metric denominator: prefer the SKU-specific budget from the budgets
                // endpoint; fall back to the account's configured MonthlyBudget; else 0m
                // (documented "unknown" state — the view renders it as an unknown-limit bar).
                var costBudget = SelectCostBudget(budgets, aggregate.Sku, account.UserName);
                var costTotal = costBudget ?? account.MonthlyBudget ?? 0m;

                // #1160: When a budget denominator is known, present the cost metric as
                // "$spent / $budget" so the progress bar shows real budget consumption.
                // When unknown, fall back to bare currency to avoid a misleading "$X / $0".
                var costFormat = costTotal > 0m
                    ? "{0:C2} / {1:C2}"
                    : "{0:C2}";

                metrics.Add(new UsageMetric
                {
                    // #1211: Renamed from "(Cost)" to "(Additional Usage)" so the label
                    // matches GitHub's own vocabulary and GitHubActionsUsageProvider's
                    // convention. This is the paid-overage / metered-billing surface.
                    Title = $"{aggregate.Sku} (Additional Usage)",
                    QuantityUsed = aggregate.NetAmount,
                    QuantityTotal = costTotal,
                    QuantityPresentationFormatString = costFormat,
                    Unit = string.Empty,
                    LastUpdatedAt = now,
                    WebUrl = webUrl,
                    ResetsAt = periodEnd,
                    BillingPeriodStart = periodStart,
                    // #1160: Mark the net-dollar cost metric as the default budget-relevant
                    // surface. When no user pin exists, the ViewModel prefers this over the
                    // credit-quantity metric so the toolbar shows billable dollars rather
                    // than credits that saturate at the included allotment.
                    IsSelectedAsShown = true,
                });
            }
            else
            {
                // #1211 (Tertiary): Emit the additional-usage row unconditionally so users
                // can distinguish "within included allotment ($0.00)" from "no data fetched
                // at all" (previously indistinguishable — both surfaces were absent). When
                // NetAmount is 0, we do NOT set IsSelectedAsShown so the toolbar continues
                // to prefer the credit-quantity metric until real spend accrues.
                var costBudget = SelectCostBudget(budgets, aggregate.Sku, account.UserName);
                var costTotal = costBudget ?? account.MonthlyBudget ?? 0m;
                var costFormat = costTotal > 0m
                    ? "{0:C2} / {1:C2}"
                    : "{0:C2}";

                metrics.Add(new UsageMetric
                {
                    Title = $"{aggregate.Sku} (Additional Usage)",
                    QuantityUsed = 0m,
                    QuantityTotal = costTotal,
                    QuantityPresentationFormatString = costFormat,
                    Unit = string.Empty,
                    LastUpdatedAt = now,
                    WebUrl = webUrl,
                    ResetsAt = periodEnd,
                    BillingPeriodStart = periodStart,
                });
            }
        }

        return metrics;
    }

    /// <summary>
    /// Selects the cost denominator (<c>budget_amount</c>) for a given SKU from a merged list
    /// of budgets tagged by scope. Selection rules (per §B.1 of #1159):
    /// <list type="bullet">
    ///   <item>Premium Requests ($): any budget whose <c>budget_product_skus</c> contains
    ///     <c>"premium_requests"</c>.</item>
    ///   <item>AI Credits ($): <c>BundlePricing</c> budget whose <c>budget_product_skus</c>
    ///     contains <c>"ai_credits"</c>.</item>
    ///   <item>Scope priority: <c>user</c> matching <paramref name="userName"/> →
    ///     <c>multi_user_customer</c> → <c>organization</c>.</item>
    /// </list>
    /// </summary>
    private static decimal? SelectCostBudget(
        IReadOnlyList<ScopedBudget> budgets,
        string sku,
        string userName)
    {
        if (budgets.Count == 0)
        {
            return null;
        }

        bool isPremium = sku.Contains("Premium Request", StringComparison.OrdinalIgnoreCase);
        bool isAiCredits = sku.Contains("AI Credit", StringComparison.OrdinalIgnoreCase);
        if (!isPremium && !isAiCredits)
        {
            return null;
        }

        bool Matches(ScopedBudget b)
        {
            if (isPremium)
            {
                return b.ProductSkus.Contains("premium_requests", StringComparer.OrdinalIgnoreCase);
            }

            // AI Credits — restrict to BundlePricing per spec.
            return string.Equals(b.BudgetType, "BundlePricing", StringComparison.OrdinalIgnoreCase)
                && b.ProductSkus.Contains("ai_credits", StringComparer.OrdinalIgnoreCase);
        }

        foreach (var scope in new[] { "user", "multi_user_customer", "organization" })
        {
            foreach (var b in budgets)
            {
                if (!string.Equals(b.Scope, scope, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (scope == "user"
                    && !string.IsNullOrEmpty(userName)
                    && !string.IsNullOrEmpty(b.EntityName)
                    && !string.Equals(b.EntityName, userName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Matches(b))
                {
                    return b.Amount;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Fetches org-scoped budgets, trying <c>user</c> → <c>multi_user_customer</c> →
    /// <c>organization</c>. Swallows 403/404 (graceful fallback per §D of #1159) so callers
    /// can rely on account-level configuration when the token lacks
    /// <c>Administration:read</c> or the org has no matching budgets.
    /// </summary>
    private async Task<IReadOnlyList<ScopedBudget>> FetchOrgBudgetsAsync(
        string org,
        string userName,
        string? token,
        CancellationToken cancellationToken)
    {
        var results = new List<ScopedBudget>();
        var scopes = new[] { "user", "multi_user_customer", "organization" };

        foreach (var scope in scopes)
        {
            var url = $"https://api.github.com/organizations/{Uri.EscapeDataString(org)}/settings/billing/budgets?scope={scope}";
            if (scope == "user" && !string.IsNullOrEmpty(userName))
            {
                url += $"&user={Uri.EscapeDataString(userName)}";
            }

            HttpResponseMessage response;
            try
            {
                response = await this.SendRequestAsync(url, token, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                this.logger.LogWarning(
                    ex,
                    "GitHub Copilot budgets request failed for scope {Scope}; falling back.",
                    scope);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden
                || response.StatusCode == HttpStatusCode.NotFound)
            {
                this.logger.LogWarning(
                    "GitHub Copilot budgets endpoint returned {StatusCode} for scope {Scope}; falling back.",
                    (int)response.StatusCode,
                    scope);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                this.logger.LogWarning(
                    "GitHub Copilot budgets endpoint returned non-success {StatusCode} for scope {Scope}; falling back.",
                    (int)response.StatusCode,
                    scope);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = ParseBudgets(body, scope);
            results.AddRange(parsed);
        }

        return results;
    }

    private static IReadOnlyList<ScopedBudget> ParseBudgets(string json, string scope)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ScopedBudget>();
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Array.Empty<ScopedBudget>();
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("budgets", out var budgets)
                || budgets.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ScopedBudget>();
            }

            var list = new List<ScopedBudget>();
            foreach (var element in budgets.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var budgetType = element.TryGetProperty("budget_type", out var bt) && bt.ValueKind == JsonValueKind.String
                    ? bt.GetString() ?? string.Empty
                    : string.Empty;
                var budgetScope = element.TryGetProperty("budget_scope", out var bs) && bs.ValueKind == JsonValueKind.String
                    ? bs.GetString() ?? scope
                    : scope;
                var entityName = element.TryGetProperty("budget_entity_name", out var be) && be.ValueKind == JsonValueKind.String
                    ? be.GetString() ?? string.Empty
                    : string.Empty;
                var amount = element.TryGetProperty("budget_amount", out var ba) && ba.ValueKind == JsonValueKind.Number
                    ? ba.GetDecimal()
                    : 0m;

                DateTimeOffset? periodStart = null;
                DateTimeOffset? periodEnd = null;
                if (element.TryGetProperty("current_period_start", out var cps)
                    && cps.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(cps.GetString(), out var parsedStart))
                {
                    periodStart = parsedStart;
                }
                if (element.TryGetProperty("current_period_end", out var cpe)
                    && cpe.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(cpe.GetString(), out var parsedEnd))
                {
                    periodEnd = parsedEnd;
                }
                else if (periodStart is { } ps)
                {
                    // Fallback per #1188: when only a period start is present, treat the
                    // period as one month wide.
                    periodEnd = ps.AddMonths(1);
                }

                var skus = new List<string>();
                if (element.TryGetProperty("budget_product_skus", out var bpsku)
                    && bpsku.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in bpsku.EnumerateArray())
                    {
                        if (s.ValueKind == JsonValueKind.String)
                        {
                            skus.Add(s.GetString() ?? string.Empty);
                        }
                    }
                }

                list.Add(new ScopedBudget(scope, budgetType, budgetScope, entityName, skus, amount, periodStart, periodEnd));
            }

            return list;
        }
    }

    /// <summary>
    /// Builds the best web URL for a Copilot SKU on the given account. Falls back to the
    /// account's <see cref="UsageAccount.SettingsUrl"/> (which is already host+account aware,
    /// so it points at either the user's or the organization's billing/usage page).
    /// </summary>
    private static Uri? BuildMetricWebUrl(UsageAccount account, string sku)
    {
        // No per-SKU deep link exists today; always fall back to the account's
        // pre-resolved billing/usage page. Callers ensure SettingsUrl is populated.
        return account.SettingsUrl;
    }

    private sealed class CopilotSkuAggregate
    {
        public string Sku { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal NetAmount { get; set; }
    }

    /// <summary>
    /// A single budget entry from the org-scoped budgets endpoint, tagged with the request
    /// scope that produced it. <see cref="RequestedScope"/> is what we asked for (used to
    /// enforce scope priority in selection); <see cref="BudgetScope"/> is the value the API
    /// returned in <c>budget_scope</c> (kept for logging/diagnostics).
    /// </summary>
    private sealed record ScopedBudget(
        string RequestedScope,
        string BudgetType,
        string BudgetScope,
        string EntityName,
        IReadOnlyList<string> ProductSkus,
        decimal Amount,
        DateTimeOffset? PeriodStart = null,
        DateTimeOffset? PeriodEnd = null)
    {
        public string Scope => this.RequestedScope;
    }
}
