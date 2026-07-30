using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.Services.UsageProviders;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class GitHubCopilotUsageProviderTests
{
    private const string CopilotUsageItemsJson = """
        {
          "usageItems": [
            {
              "date": "2026-07-01T00:00:00Z",
              "product": "copilot",
              "sku": "Copilot AI Credits",
              "quantity": 395199.59,
              "unitType": "AICredits",
              "pricePerUnit": 0.01,
              "grossAmount": 3951.99,
              "discountAmount": 197.41,
              "netAmount": 3754.58,
              "repositoryName": ""
            },
            {
              "date": "2026-05-01T00:00:00Z",
              "product": "copilot",
              "sku": "Copilot Premium Request",
              "quantity": 1244,
              "unitType": "Requests",
              "pricePerUnit": 0.04,
              "grossAmount": 49.76,
              "discountAmount": 49.76,
              "netAmount": 0.0,
              "repositoryName": ""
            }
          ]
        }
        """;

    private static HttpClient MakeHttpClient(HttpStatusCode status, string responseBody)
    {
        return new HttpClient(new StubHandler(status, responseBody));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> responses;

        public StubHandler(params (HttpStatusCode, string)[] responses)
        {
            this.responses = new Queue<(HttpStatusCode, string)>(responses);
        }

        public StubHandler(HttpStatusCode status, string body)
            : this((status, body))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var (status, body) = this.responses.Count > 0
                ? this.responses.Dequeue()
                : (HttpStatusCode.InternalServerError, string.Empty);

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    private static readonly UsageAccount TestAccount = new()
    {
        UserName = "alice",
        Product = "GitHub",
        SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
    };

    [Fact]
    public async Task GetMetricsAsync_UsesUserScopedBillingUsageEndpoint_ForSignedInAccount()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new RequestCapturingHandler(
            req => capturedRequest = req,
            HttpStatusCode.OK,
            CopilotUsageItemsJson);

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "fake-token");

        var account = new UsageAccount
        {
            UserName = "JoshuaRowePhantom",
            Product = "GitHub",
            SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
        };

        await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.Equal(
            "https://api.github.com/users/JoshuaRowePhantom/settings/billing/usage",
            capturedRequest!.RequestUri!.ToString());
        Assert.DoesNotContain("/copilot/billing/usage", capturedRequest.RequestUri.ToString());
    }

    [Fact]
    public async Task GetMetricsAsync_UsesUserScopedBillingUsageEndpoint_EscapesUserName()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new RequestCapturingHandler(
            req => capturedRequest = req,
            HttpStatusCode.OK,
            """{ "usageItems": [] }""");

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "fake-token");

        var account = new UsageAccount
        {
            UserName = "user with space",
            Product = "GitHub",
            SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
        };

        await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.Contains("user%20with%20space", capturedRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetMetricsAsync_ParsesUsageItems_IntoCopilotMetrics()
    {
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, CopilotUsageItemsJson),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        Assert.Equal(3, metrics.Count);

        var aiCredits = metrics.Single(m => m.Title == "Copilot AI Credits");
        Assert.Equal(395199.59m, aiCredits.QuantityUsed);
        Assert.Equal("AICredits", aiCredits.Unit);

        var premium = metrics.Single(m => m.Title == "Copilot Premium Request");
        Assert.Equal(1244m, premium.QuantityUsed);
        Assert.Equal("Requests", premium.Unit);
    }

    [Fact]
    public async Task GetMetricsAsync_IgnoresNonCopilotUsageItems()
    {
        const string json = """
            {
              "usageItems": [
                { "product": "actions", "sku": "Ubuntu 2-core", "quantity": 1000, "unitType": "Minutes" },
                { "product": "models", "sku": "GPT-4o", "quantity": 500, "unitType": "Requests" },
                { "product": "copilot", "sku": "Copilot Premium Request", "quantity": 250, "unitType": "Requests" }
              ]
            }
            """;

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        var metric = Assert.Single(metrics);
        Assert.Equal("Copilot Premium Request", metric.Title);
        Assert.Equal(250m, metric.QuantityUsed);
    }

    [Fact]
    public async Task GetMetricsAsync_AggregatesRepeatedCopilotSkus()
    {
        const string json = """
            {
              "usageItems": [
                { "product": "copilot", "sku": "Copilot Premium Request", "quantity": 100, "unitType": "Requests" },
                { "product": "copilot", "sku": "Copilot Premium Request", "quantity": 44,  "unitType": "Requests" }
              ]
            }
            """;

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        var metric = Assert.Single(metrics);
        Assert.Equal("Copilot Premium Request", metric.Title);
        Assert.Equal(144m, metric.QuantityUsed);
    }

    [Fact]
    public async Task GetMetricsAsync_SendsEnhancedBillingHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new RequestCapturingHandler(
            req => capturedRequest = req,
            HttpStatusCode.OK,
            """{ "usageItems": [] }""");

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "test-token");

        await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Contains(capturedRequest.Headers.Accept, h => h.MediaType == "application/vnd.github+json");
        Assert.True(capturedRequest.Headers.TryGetValues("X-GitHub-Api-Version", out var apiVersionValues));
        Assert.Contains("2026-03-10", apiVersionValues!);
        Assert.True(capturedRequest.Headers.TryGetValues("User-Agent", out var userAgentValues));
        Assert.Contains("phantom-workspaces", userAgentValues!);
    }

    [Fact]
    public async Task GetMetricsAsync_WhenNotFound_ReturnsEmpty()
    {
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.NotFound, string.Empty),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        Assert.Empty(metrics);
    }

    [Fact]
    public async Task GetMetricsAsync_WhenNotFound_LogsWarningWithResolvedUrl()
    {
        var logger = new TestLogger<GitHubCopilotUsageProvider>();
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.NotFound, string.Empty),
            () => "fake-token",
            logger);

        var account = new UsageAccount
        {
            UserName = "octocat",
            Product = "GitHub",
            SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
        };

        await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("404", entry.Message, StringComparison.Ordinal);
        Assert.Contains(
            "https://api.github.com/users/octocat/settings/billing/usage",
            entry.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMetricsAsync_WhenForbidden_LogsPermissionWarning()
    {
        var logger = new TestLogger<GitHubCopilotUsageProvider>();
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.Forbidden, string.Empty),
            () => "fake-token",
            logger);

        var account = new UsageAccount
        {
            UserName = "octocat",
            Product = "GitHub",
            SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
        };

        var metrics = await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken);

        Assert.Empty(metrics);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("403", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Plan", entry.Message, StringComparison.Ordinal);
        Assert.Contains(
            "https://api.github.com/users/octocat/settings/billing/usage",
            entry.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMetricsAsync_WhenNonSuccess_LogsErrorWithStatus()
    {
        var logger = new TestLogger<GitHubCopilotUsageProvider>();
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.InternalServerError, string.Empty),
            () => "fake-token",
            logger);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken));

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("500", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMetricsAsync_OnFirstUnauthorized_RetriesWithRefreshedToken()
    {
        var callCount = 0;
        var handler = new StubHandler(
            (HttpStatusCode.Unauthorized, string.Empty),
            (HttpStatusCode.OK, CopilotUsageItemsJson));

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () =>
            {
                Interlocked.Increment(ref callCount);
                return "token";
            });

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        Assert.NotEmpty(metrics);
        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task GetMetricsAsync_OnDoubleUnauthorized_ThrowsHttpRequestException()
    {
        var handler = new StubHandler(
            (HttpStatusCode.Unauthorized, string.Empty),
            (HttpStatusCode.Unauthorized, string.Empty));

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "token");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ProviderUri_IsGitHub()
    {
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, "{}"),
            () => null);

        Assert.Equal("github.com", provider.ProviderUri.Host);
        Assert.Equal("https", provider.ProviderUri.Scheme);
    }

    [Fact]
    public async Task GetMetricsAsync_EmptyUsageItems_ReturnsEmpty()
    {
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, """{ "usageItems": [] }"""),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        Assert.Empty(metrics);
    }

    [Fact]
    public async Task GetMetricsAsync_UsesInjectedTimeProviderForLastUpdatedAt()
    {
        var instant = new DateTimeOffset(2024, 2, 15, 8, 30, 0, TimeSpan.Zero);
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(instant);

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, CopilotUsageItemsJson),
            () => "fake-token",
            logger: null,
            timeProvider: timeProvider);

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        Assert.NotEmpty(metrics);
        Assert.All(metrics, m => Assert.Equal(instant.UtcDateTime, m.LastUpdatedAt));
    }

    [Fact]
    public async Task GetMetricsAsync_EmitsSeparateCostMetric_ForCopilotSkuWithNetAmount()
    {
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, CopilotUsageItemsJson),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        // AI Credits: has netAmount=3754.58 → quantity + cost = 2 metrics
        // Premium Request: netAmount=0 → quantity only = 1 metric
        Assert.Equal(3, metrics.Count);

        var costMetric = Assert.Single(metrics, m => m.Title == "Copilot AI Credits (Cost)");
        Assert.Equal(3754.58m, costMetric.QuantityUsed);
        Assert.Equal(0m, costMetric.QuantityTotal);
        Assert.Equal("{0:C2}", costMetric.QuantityPresentationFormatString);
        Assert.Equal(string.Empty, costMetric.Unit);
    }

    [Fact]
    public async Task GetMetricsAsync_CostMetricPresentation_IsCurrencyFormatted()
    {
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, CopilotUsageItemsJson),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);
        var costMetric = metrics.Single(m => m.Title == "Copilot AI Credits (Cost)");

        Assert.Equal("$3,754.58", costMetric.QuantityPresentation);
    }

    [Fact]
    public async Task GetMetricsAsync_AggregatesNetAmount_AcrossRepeatedCopilotSkus()
    {
        const string json = """
            {
              "usageItems": [
                { "product": "copilot", "sku": "Copilot AI Credits", "quantity": 100, "unitType": "AICredits", "netAmount": 12.50 },
                { "product": "copilot", "sku": "Copilot AI Credits", "quantity": 200, "unitType": "AICredits", "netAmount": 7.25 }
              ]
            }
            """;

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);
        var costMetric = metrics.Single(m => m.Title == "Copilot AI Credits (Cost)");

        Assert.Equal(19.75m, costMetric.QuantityUsed);
    }

    [Fact]
    public async Task GetMetricsAsync_WhenNetAmountZeroOrAbsent_EmitsNoCostMetric()
    {
        const string json = """
            {
              "usageItems": [
                { "product": "copilot", "sku": "Copilot Premium Request", "quantity": 10, "unitType": "Requests", "netAmount": 0 },
                { "product": "copilot", "sku": "Copilot Other", "quantity": 5, "unitType": "Requests" }
              ]
            }
            """;

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        Assert.Equal(2, metrics.Count);
        Assert.DoesNotContain(metrics, m => m.Title.EndsWith("(Cost)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetMetricsAsync_CostMetricTitle_IsDistinctFromQuantitySibling()
    {
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, CopilotUsageItemsJson),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);
        var quantity = metrics.Single(m => m.Title == "Copilot AI Credits");
        var cost = metrics.Single(m => m.Title == "Copilot AI Credits (Cost)");

        Assert.NotEqual(quantity.Title, cost.Title);
    }

    [Fact]
    public async Task GetMetricsAsync_CostMetric_ImmediatelyFollowsItsQuantityMetric()
    {
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, CopilotUsageItemsJson),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        var quantityIndex = -1;
        var costIndex = -1;
        for (var i = 0; i < metrics.Count; i++)
        {
            if (metrics[i].Title == "Copilot AI Credits") quantityIndex = i;
            if (metrics[i].Title == "Copilot AI Credits (Cost)") costIndex = i;
        }

        Assert.True(quantityIndex >= 0);
        Assert.Equal(quantityIndex + 1, costIndex);
    }

    [Fact]
    public async Task GetMetricsAsync_WhenNoResetDataInSchema_LeavesAdditionalInformationNull()
    {
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, CopilotUsageItemsJson),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        Assert.All(metrics, m => Assert.Null(m.AdditionalInformation));
    }

    [Fact]
    public async Task GetMetricsAsync_PopulatesWebUrl_ForEachCopilotMetric()
    {
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, CopilotUsageItemsJson),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        Assert.NotEmpty(metrics);
        Assert.All(metrics, m => Assert.NotNull(m.WebUrl));
    }

    [Fact]
    public async Task GetMetricsAsync_MetricWebUrl_FallsBackToAccountPage_WhenNoDeepLink()
    {
        var accountUrl = new Uri("https://github.com/settings/billing/summary");
        var account = new UsageAccount
        {
            UserName = "alice",
            Product = "GitHub",
            SettingsUrl = accountUrl,
        };

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, CopilotUsageItemsJson),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken);

        Assert.All(metrics, m => Assert.Equal(accountUrl, m.WebUrl));
    }

    [Fact]
    public async Task GetMetricsAsync_MetricWebUrl_UsesOrgBillingPage_ForOrganizationAccount()
    {
        var orgBillingUrl = new Uri("https://github.com/organizations/contoso/settings/billing/summary");
        var account = new UsageAccount
        {
            UserName = "contoso",
            Product = "GitHub",
            SettingsUrl = orgBillingUrl,
        };

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, CopilotUsageItemsJson),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken);

        Assert.NotEmpty(metrics);
        Assert.All(metrics, m => Assert.Equal(orgBillingUrl, m.WebUrl));
    }

    [Fact]
    public async Task GetMetricsAsync_MetricWebUrl_FallsBackToAccountSettingsUrl_WhenUserNameMissing()
    {
        var accountUrl = new Uri("https://github.com/settings/billing/summary");
        var account = new UsageAccount
        {
            UserName = string.Empty,
            Product = "GitHub",
            SettingsUrl = accountUrl,
        };

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, CopilotUsageItemsJson),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken);

        Assert.NotEmpty(metrics);
        Assert.All(metrics, m => Assert.Equal(accountUrl, m.WebUrl));
    }

    private sealed class RequestCapturingHandler : HttpMessageHandler
    {
        private readonly Action<HttpRequestMessage> onRequest;
        private readonly HttpStatusCode status;
        private readonly string body;

        public RequestCapturingHandler(
            Action<HttpRequestMessage> onRequest,
            HttpStatusCode status = HttpStatusCode.OK,
            string body = """{ "usageItems": [] }""")
        {
            this.onRequest = onRequest;
            this.status = status;
            this.body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.onRequest(request);
            return Task.FromResult(new HttpResponseMessage(this.status)
            {
                Content = new StringContent(this.body),
            });
        }
    }

    // ---------- #1159 Fix A tests: budgets endpoint and QuantityTotal population ----------

    private const string PremiumRequestOnlyUsageJson = """
        {
          "usageItems": [
            {
              "product": "copilot",
              "sku": "Copilot Premium Request",
              "quantity": 400,
              "unitType": "Requests",
              "netAmount": 250.00
            }
          ]
        }
        """;

    private const string AiCreditsOnlyUsageJson = """
        {
          "usageItems": [
            {
              "product": "copilot",
              "sku": "Copilot AI Credits",
              "quantity": 100,
              "unitType": "AICredits",
              "netAmount": 50.00
            }
          ]
        }
        """;

    private sealed class SequencedRequestHandler : HttpMessageHandler
    {
        private readonly List<Func<HttpRequestMessage, (HttpStatusCode Status, string Body)>> responders;

        public List<HttpRequestMessage> Requests { get; } = [];

        public SequencedRequestHandler(params Func<HttpRequestMessage, (HttpStatusCode, string)>[] responders)
        {
            this.responders = new List<Func<HttpRequestMessage, (HttpStatusCode, string)>>(responders);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            var index = this.Requests.Count - 1;
            (HttpStatusCode status, string body) = index < this.responders.Count
                ? this.responders[index](request)
                : (HttpStatusCode.InternalServerError, string.Empty);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    private static UsageAccount OrgAccount(string userName = "alice", string org = "acme", decimal? monthlyBudget = null)
        => new()
        {
            UserName = userName,
            Product = "GitHub",
            SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
            Org = org,
            MonthlyBudget = monthlyBudget,
        };

    [Fact]
    public async Task GetMetricsAsync_CallsBudgetsEndpoint_WithApiVersionHeader()
    {
        var handler = new SequencedRequestHandler(
            _ => (HttpStatusCode.OK, PremiumRequestOnlyUsageJson),
            _ => (HttpStatusCode.OK, """{ "budgets": [] }"""),
            _ => (HttpStatusCode.OK, """{ "budgets": [] }"""),
            _ => (HttpStatusCode.OK, """{ "budgets": [] }"""));

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "token");

        await provider.GetMetricsAsync(OrgAccount(), TestContext.Current.CancellationToken);

        var budgetsRequest = handler.Requests.FirstOrDefault(
            r => r.RequestUri!.AbsolutePath.Contains("/settings/billing/budgets", StringComparison.Ordinal));
        Assert.NotNull(budgetsRequest);
        Assert.Contains("/organizations/acme/settings/billing/budgets", budgetsRequest!.RequestUri!.AbsoluteUri);
        Assert.True(budgetsRequest.Headers.TryGetValues("X-GitHub-Api-Version", out var apiVer));
        Assert.Contains("2026-03-10", apiVer!);
        Assert.Contains(budgetsRequest.Headers.Accept, h => h.MediaType == "application/vnd.github+json");
    }

    [Fact]
    public async Task GetMetricsAsync_BudgetsResponseWithPremiumRequestsSku_PopulatesCostQuantityTotal()
    {
        const string budgetsJson = """
            {
              "budgets": [
                {
                  "budget_type": "SkuPricing",
                  "budget_product_skus": ["premium_requests"],
                  "budget_scope": "user",
                  "budget_entity_name": "alice",
                  "budget_amount": 500
                }
              ]
            }
            """;

        var handler = new SequencedRequestHandler(
            _ => (HttpStatusCode.OK, PremiumRequestOnlyUsageJson),
            _ => (HttpStatusCode.OK, budgetsJson));

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "token");

        var metrics = await provider.GetMetricsAsync(OrgAccount(), TestContext.Current.CancellationToken);

        var costMetric = metrics.Single(m => m.Title == "Copilot Premium Request (Cost)");
        Assert.Equal(500m, costMetric.QuantityTotal);
    }

    [Fact]
    public async Task GetMetricsAsync_BudgetsResponseWithAiCreditsBundle_PopulatesCostQuantityTotal()
    {
        const string budgetsJson = """
            {
              "budgets": [
                {
                  "budget_type": "BundlePricing",
                  "budget_product_skus": ["ai_credits"],
                  "budget_scope": "user",
                  "budget_entity_name": "alice",
                  "budget_amount": 200
                }
              ]
            }
            """;

        var handler = new SequencedRequestHandler(
            _ => (HttpStatusCode.OK, AiCreditsOnlyUsageJson),
            _ => (HttpStatusCode.OK, budgetsJson));

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "token");

        var metrics = await provider.GetMetricsAsync(OrgAccount(), TestContext.Current.CancellationToken);

        var costMetric = metrics.Single(m => m.Title == "Copilot AI Credits (Cost)");
        Assert.Equal(200m, costMetric.QuantityTotal);
    }

    [Fact]
    public async Task GetMetricsAsync_BudgetsEndpointReturns403_FallsBackToConfiguredMonthlyBudget()
    {
        var handler = new SequencedRequestHandler(
            _ => (HttpStatusCode.OK, PremiumRequestOnlyUsageJson),
            _ => (HttpStatusCode.Forbidden, string.Empty),
            _ => (HttpStatusCode.Forbidden, string.Empty),
            _ => (HttpStatusCode.Forbidden, string.Empty));

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "token");

        var metrics = await provider.GetMetricsAsync(
            OrgAccount(monthlyBudget: 750m),
            TestContext.Current.CancellationToken);

        var costMetric = metrics.Single(m => m.Title == "Copilot Premium Request (Cost)");
        Assert.Equal(750m, costMetric.QuantityTotal);
    }

    [Fact]
    public async Task GetMetricsAsync_NoOrgOnAccount_DoesNotCallBudgetsEndpoint()
    {
        var handler = new SequencedRequestHandler(
            _ => (HttpStatusCode.OK, PremiumRequestOnlyUsageJson));

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "token");

        var account = new UsageAccount
        {
            UserName = "alice",
            Product = "GitHub",
            SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
            // Org intentionally null — personal account, MUST NOT hit budgets endpoint.
        };

        await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, r =>
            r.RequestUri!.AbsolutePath.Contains("/settings/billing/budgets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetMetricsAsync_ConfiguredIncludedPremiumRequests_PopulatesCountQuantityTotal()
    {
        var handler = new SequencedRequestHandler(
            _ => (HttpStatusCode.OK, PremiumRequestOnlyUsageJson));

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "token");

        var account = new UsageAccount
        {
            UserName = "alice",
            Product = "GitHub",
            SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
            IncludedPremiumRequests = 1500m,
        };

        var metrics = await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken);

        var countMetric = metrics.Single(m => m.Title == "Copilot Premium Request");
        Assert.Equal(1500m, countMetric.QuantityTotal);
    }

    [Fact]
    public async Task GetMetricsAsync_NoBudgetAndNoConfig_LeavesQuantityTotalZero()
    {
        var handler = new SequencedRequestHandler(
            _ => (HttpStatusCode.OK, PremiumRequestOnlyUsageJson));

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "token");

        var account = new UsageAccount
        {
            UserName = "alice",
            Product = "GitHub",
            SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
        };

        var metrics = await provider.GetMetricsAsync(account, TestContext.Current.CancellationToken);

        var count = metrics.Single(m => m.Title == "Copilot Premium Request");
        var cost = metrics.Single(m => m.Title == "Copilot Premium Request (Cost)");
        Assert.Equal(0m, count.QuantityTotal);
        Assert.Equal(0m, cost.QuantityTotal);
    }

    [Fact]
    public async Task GetMetricsAsync_BudgetSelection_PrefersUserScopeOverOrgScope()
    {
        const string userScopeJson = """
            {
              "budgets": [
                {
                  "budget_type": "SkuPricing",
                  "budget_product_skus": ["premium_requests"],
                  "budget_scope": "user",
                  "budget_entity_name": "alice",
                  "budget_amount": 100
                }
              ]
            }
            """;
        const string orgScopeJson = """
            {
              "budgets": [
                {
                  "budget_type": "SkuPricing",
                  "budget_product_skus": ["premium_requests"],
                  "budget_scope": "organization",
                  "budget_entity_name": "acme",
                  "budget_amount": 5000
                }
              ]
            }
            """;

        var handler = new SequencedRequestHandler(
            _ => (HttpStatusCode.OK, PremiumRequestOnlyUsageJson),
            _ => (HttpStatusCode.OK, userScopeJson),
            _ => (HttpStatusCode.OK, """{ "budgets": [] }"""),
            _ => (HttpStatusCode.OK, orgScopeJson));

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "token");

        var metrics = await provider.GetMetricsAsync(OrgAccount(), TestContext.Current.CancellationToken);

        var costMetric = metrics.Single(m => m.Title == "Copilot Premium Request (Cost)");
        Assert.Equal(100m, costMetric.QuantityTotal);
    }
}
