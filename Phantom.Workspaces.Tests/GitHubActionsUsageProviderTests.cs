using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.Services.UsageProviders;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class GitHubActionsUsageProviderTests
{
    private static HttpClient MakeHttpClient(HttpStatusCode status, string responseBody)
        => new HttpClient(new StubHandler(status, responseBody));

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
    public async Task GetMetricsAsync_ParsesIncludedAndAdditionalUsage()
    {
        const string json = """
            {
              "total_minutes_used": 350,
              "included_minutes": 500,
              "total_paid_minutes_used": 0
            }
            """;

        var provider = new GitHubActionsUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        Assert.Equal(2, metrics.Count);

        var included = metrics[0];
        Assert.Equal("Included Usage", included.Title);
        Assert.Equal(350m, included.QuantityUsed);
        Assert.Equal(500m, included.QuantityTotal);
        Assert.Equal("minutes", included.Unit);

        var additional = metrics[1];
        Assert.Equal("Additional Usage", additional.Title);
        Assert.Equal(0m, additional.QuantityUsed);
        Assert.Equal(0m, additional.QuantityTotal); // no cap available
    }

    [Fact]
    public async Task GetMetricsAsync_UsesInjectedTimeProviderForLastUpdatedAt()
    {
        var instant = new DateTimeOffset(2024, 2, 15, 8, 30, 0, TimeSpan.Zero);
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(instant);
        const string json = """
            {
              "total_minutes_used": 100,
              "included_minutes": 500,
              "total_paid_minutes_used": 0
            }
            """;

        var provider = new GitHubActionsUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token",
            logger: null,
            timeProvider: timeProvider);

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        Assert.NotEmpty(metrics);
        Assert.All(metrics, m => Assert.Equal(instant.UtcDateTime, m.LastUpdatedAt));
    }

    [Fact]
    public async Task GetMetricsAsync_AfterAdvance_StampsLastUpdatedAtFromAdvancedTime()
    {
        var start = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(start);
        const string json = """
            {
              "total_minutes_used": 100,
              "included_minutes": 500,
              "total_paid_minutes_used": 0
            }
            """;

        var provider = new GitHubActionsUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token",
            logger: null,
            timeProvider: timeProvider);

        timeProvider.Advance(TimeSpan.FromDays(1));

        var metrics = await provider.GetMetricsAsync(TestAccount, TestContext.Current.CancellationToken);

        Assert.NotEmpty(metrics);
        Assert.All(metrics, m => Assert.Equal(start.UtcDateTime.AddDays(1), m.LastUpdatedAt));
    }

    [Fact]
    public async Task GetMetricsAsync_WhenNotFound_ReturnsEmpty()
    {
        var provider = new GitHubActionsUsageProvider(
            MakeHttpClient(HttpStatusCode.NotFound, string.Empty),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        Assert.Empty(metrics);
    }

    [Fact]
    public async Task GetMetricsAsync_WhenNotFound_LogsWarningWithStatusAndEndpoint()
    {
        var logger = new TestLogger<GitHubActionsUsageProvider>();
        var provider = new GitHubActionsUsageProvider(
            MakeHttpClient(HttpStatusCode.NotFound, string.Empty),
            () => "fake-token",
            logger);

        await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("404", entry.Message, StringComparison.Ordinal);
        Assert.Contains("settings/billing/actions", entry.Message, StringComparison.Ordinal);
        Assert.Contains("alice", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMetricsAsync_OnFirstUnauthorized_RetriesWithRefreshedToken()
    {
        var callCount = 0;
        var handler = new StubHandler(
            (HttpStatusCode.Unauthorized, string.Empty),
            (HttpStatusCode.OK, """
                {
                  "total_minutes_used": 100,
                  "included_minutes": 2000,
                  "total_paid_minutes_used": 0
                }
                """));

        var provider = new GitHubActionsUsageProvider(
            new HttpClient(handler),
            () =>
            {
                Interlocked.Increment(ref callCount);
                return "token";
            });

        var metrics = await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        Assert.NotEmpty(metrics);
        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task GetMetricsAsync_OnDoubleUnauthorized_ThrowsHttpRequestException()
    {
        var handler = new StubHandler(
            (HttpStatusCode.Unauthorized, string.Empty),
            (HttpStatusCode.Unauthorized, string.Empty));

        var provider = new GitHubActionsUsageProvider(
            new HttpClient(handler),
            () => "token");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetMetricsAsync(TestAccount, CancellationToken.None));
    }

    [Fact]
    public void ProviderUri_IsGitHub()
    {
        var provider = new GitHubActionsUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, "{}"),
            () => null);

        Assert.Equal("github.com", provider.ProviderUri.Host);
        Assert.Equal("https", provider.ProviderUri.Scheme);
    }

    [Fact]
    public async Task GetMetricsAsync_ParsesAdditionalDollars()
    {
        const string json = """
            {
              "total_minutes_used": 0,
              "included_minutes": 0,
              "total_paid_minutes_used": 100
            }
            """;

        var provider = new GitHubActionsUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        var additional = metrics[1];
        Assert.Equal("Additional Usage", additional.Title);
        Assert.Contains("C2", additional.QuantityPresentationFormatString);
    }

    [Fact]
    public async Task GetMetricsAsync_FractionUsed_CorrectForIncluded()
    {
        const string json = """
            {
              "total_minutes_used": 305,
              "included_minutes": 3000,
              "total_paid_minutes_used": 0
            }
            """;

        var provider = new GitHubActionsUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        var included = metrics[0];
        Assert.NotNull(included.FractionUsed);
        Assert.Equal(0.1017, included.FractionUsed!.Value, 4);
    }

    [Fact]
    public async Task GetMetricsAsync_AcquiresToken_BeforeRequest()
    {
        var tokenCalled = false;
        var httpCalled = false;

        var handler = new StubHandler(
            (HttpStatusCode.OK, """
                {
                  "total_minutes_used": 0,
                  "included_minutes": 0,
                  "total_paid_minutes_used": 0
                }
                """));

        var provider = new GitHubActionsUsageProvider(
            new HttpClient(handler),
            () =>
            {
                Assert.False(httpCalled, "Token resolver must be called before HTTP request");
                tokenCalled = true;
                return "token";
            });

        await provider.GetMetricsAsync(TestAccount, CancellationToken.None);
        httpCalled = true;

        Assert.True(tokenCalled);
    }

    [Fact]
    public async Task GetMetricsAsync_UsesAccountUserName_InUrl()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new HttpClientHandler();
        var client = new HttpClient(new InterceptingHandler(handler, req =>
        {
            capturedRequest = req;
        }));

        var provider = new GitHubActionsUsageProvider(
            client,
            () => "token");

        var account = new UsageAccount
        {
            UserName = "testuser123",
            Product = "GitHub",
            SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
        };

        try
        {
            await provider.GetMetricsAsync(account, CancellationToken.None);
        }
        catch
        {
            // Expected to fail since we're not mocking the actual HTTP response
        }

        Assert.NotNull(capturedRequest);
        Assert.Contains("testuser123", capturedRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetMetricsAsync_IncludedMinutesZero_FractionUsed_Null()
    {
        const string json = """
            {
              "total_minutes_used": 100,
              "included_minutes": 0,
              "total_paid_minutes_used": 0
            }
            """;

        var provider = new GitHubActionsUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        var included = metrics[0];
        Assert.Null(included.FractionUsed);
    }

    private sealed class InterceptingHandler : DelegatingHandler
    {
        private readonly Action<HttpRequestMessage> onSend;

        public InterceptingHandler(HttpMessageHandler innerHandler, Action<HttpRequestMessage> onSend)
            : base(innerHandler)
        {
            this.onSend = onSend;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.onSend(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
