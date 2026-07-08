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
    public async Task GetMetricsAsync_WhenNotFound_ReturnsEmpty()
    {
        var provider = new GitHubActionsUsageProvider(
            MakeHttpClient(HttpStatusCode.NotFound, string.Empty),
            () => "fake-token");

        var metrics = await provider.GetMetricsAsync(TestAccount, CancellationToken.None);

        Assert.Empty(metrics);
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
}
