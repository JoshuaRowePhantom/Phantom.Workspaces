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

public sealed class GitHubCopilotUsageProviderTests
{
    private static HttpClient MakeHttpClient(
        HttpStatusCode status,
        string responseBody)
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
    public async Task GetMetricsAsync_ParsesIncludedAndAdditionalUsage()
    {
        const string json = """
            {
              "seat_breakdown": {
                "active_this_cycle": 7,
                "total": 10
              },
              "total_billed_amount": 12.50
            }
            """;

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        Assert.Equal(2, metrics.Count);

        var included = metrics[0];
        Assert.Equal("Included Usage", included.Title);
        Assert.Equal(7m, included.QuantityUsed);
        Assert.Equal(10m, included.QuantityTotal);
        Assert.Equal("AIC", included.Unit);

        var additional = metrics[1];
        Assert.Equal("Additional Usage", additional.Title);
        Assert.Equal(12.50m, additional.QuantityUsed);
    }

    [Fact]
    public async Task GetMetricsAsync_WhenNotFound_ReturnsEmpty()
    {
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.NotFound, string.Empty),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        Assert.Empty(metrics);
    }

    [Fact]
    public async Task GetMetricsAsync_WhenNotFound_LogsWarningWithStatusAndEndpoint()
    {
        var logger = new TestLogger<GitHubCopilotUsageProvider>();
        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.NotFound, string.Empty),
            () => "fake-token",
            logger);

        await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("404", entry.Message, StringComparison.Ordinal);
        Assert.Contains("copilot/billing/usage", entry.Message, StringComparison.Ordinal);
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
            () => provider.GetMetricsAsync(TestAccount, CancellationToken.None));

        var entry = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Contains("500", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMetricsAsync_OnFirstUnauthorized_RetriesWithRefreshedToken()
    {
        var callCount = 0;
        var handler = new StubHandler(
            (HttpStatusCode.Unauthorized, string.Empty),
            (HttpStatusCode.OK, """
                {
                  "seat_breakdown": { "active_this_cycle": 1, "total": 5 },
                  "total_billed_amount": 0
                }
                """));

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () =>
            {
                Interlocked.Increment(ref callCount);
                return "token";
            });

        var metrics = await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        Assert.NotEmpty(metrics);
        Assert.Equal(2, Volatile.Read(ref callCount)); // token resolver called twice
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
            () => provider.GetMetricsAsync(TestAccount, CancellationToken.None));
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
    public async Task GetMetricsAsync_FractionUsed_CorrectForIncluded()
    {
        const string json = """
            {
              "seat_breakdown": {
                "active_this_cycle": 100,
                "total": 200
              },
              "total_billed_amount": 0
            }
            """;

        var provider = new GitHubCopilotUsageProvider(
            MakeHttpClient(HttpStatusCode.OK, json),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        var included = metrics[0];
        Assert.Equal(0.5, included.FractionUsed);
    }

    [Fact]
    public async Task GetMetricsAsync_AcquiresToken_BeforeRequest()
    {
        var tokenAcquired = false;
        var requestSent = false;

        var handler = new TokenOrderTrackingHandler(() =>
        {
            tokenAcquired = true;
            Assert.False(requestSent, "Token should be acquired before sending request");
        },
        () =>
        {
            requestSent = true;
            Assert.True(tokenAcquired, "Token should be acquired before sending request");
        });

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () =>
            {
                tokenAcquired = true;
                return "token";
            });

        await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        Assert.True(tokenAcquired);
        Assert.True(requestSent);
    }

    [Fact]
    public async Task GetMetricsAsync_SetsCorrectHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new RequestCapturingHandler(req => capturedRequest = req);

        var provider = new GitHubCopilotUsageProvider(
            new HttpClient(handler),
            () => "test-token");

        await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Contains(capturedRequest.Headers.Accept, h => h.MediaType == "application/vnd.github+json");
        Assert.True(capturedRequest.Headers.TryGetValues("X-GitHub-Api-Version", out var apiVersionValues));
        Assert.Contains("2022-11-28", apiVersionValues!);
        Assert.True(capturedRequest.Headers.TryGetValues("User-Agent", out var userAgentValues));
        Assert.Contains("phantom-workspaces", userAgentValues!);
    }

    private sealed class TokenOrderTrackingHandler : HttpMessageHandler
    {
        private readonly Action onTokenAcquired;
        private readonly Action onRequestSent;

        public TokenOrderTrackingHandler(Action onTokenAcquired, Action onRequestSent)
        {
            this.onTokenAcquired = onTokenAcquired;
            this.onRequestSent = onRequestSent;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.onRequestSent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "seat_breakdown": { "active_this_cycle": 1, "total": 5 },
                      "total_billed_amount": 0
                    }
                    """),
            });
        }
    }

    private sealed class RequestCapturingHandler : HttpMessageHandler
    {
        private readonly Action<HttpRequestMessage> onRequest;

        public RequestCapturingHandler(Action<HttpRequestMessage> onRequest)
        {
            this.onRequest = onRequest;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.onRequest(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "seat_breakdown": { "active_this_cycle": 1, "total": 5 },
                      "total_billed_amount": 0
                    }
                    """),
            });
        }
    }
}
