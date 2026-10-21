using System.Net;
using System.Net.Http;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.Services.UsageProviders;

namespace Phantom.Workspaces.Tests.Access;

public sealed class GitHubUsageProviderAccessTests
{
    [Fact]
    public void UsageProviderConstructorsRequireTheTypedBroker()
    {
        foreach (var type in new[] { typeof(GitHubCopilotUsageProvider), typeof(GitHubActionsUsageProvider) })
        {
            Assert.All(type.GetConstructors(), constructor =>
                Assert.Contains(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(IAccessBroker)));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsageProvidersUseTheLivePresentationScope(bool copilot)
    {
        await using var broker = new GitHubConsumerTestBroker();
        using var client = new HttpClient(new Transport(copilot ? """{"usageItems":[]}""" : """{"total_minutes_used":1}"""));
        var scope = AccessPresentationScope.Bootstrap;
        var provider = CreateProvider(copilot, client, broker, () => scope);

        await provider.GetMetricsAsync(Account(), TestContext.Current.CancellationToken);
        scope = AccessPresentationScope.Runtime;
        await provider.GetMetricsAsync(Account(), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { AccessPresentationScope.Bootstrap, AccessPresentationScope.Runtime },
            broker.Requests.Select(request => request.PresentationScope));
        foreach (var lease in broker.Leases)
        {
            var disposed = Assert.IsType<SensitiveValueUseFailed<int>>(await lease.UseAsync(
                (_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
            Assert.Equal(AccessErrorCode.Disposed, disposed.ErrorCode);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task UsageProvidersPreserveTypedCancellationAndShutdown(bool copilot, bool shutdown)
    {
        var transport = new Transport("{}");
        using var client = new HttpClient(transport);
        var provider = CreateProvider(copilot, client, new CancelledBroker(shutdown));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetMetricsAsync(Account(), TestContext.Current.CancellationToken));

        Assert.Equal(0, transport.SendCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsageProvidersDisposeUnclaimedLateSuccessBeforeObservingCancellation(bool copilot)
    {
        await using var broker = new GitHubConsumerTestBroker();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var transport = new Transport("{}");
        using var client = new HttpClient(transport);
        var provider = CreateProvider(copilot, client, new CancelOnSuccessBroker(broker, cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetMetricsAsync(Account(), cancellation.Token));

        Assert.Equal(0, transport.SendCount);
        var disposed = Assert.IsType<SensitiveValueUseFailed<int>>(await Assert.Single(broker.Leases).UseAsync(
            (_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
        Assert.Equal(AccessErrorCode.Disposed, disposed.ErrorCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsageProvidersWithoutBrokerFailBeforeAnyHttp(bool copilot)
    {
        var transport = new Transport("{}");
        using var client = new HttpClient(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(copilot, client, null).GetMetricsAsync(Account(), TestContext.Current.CancellationToken));

        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task GitHubCopilotUsageProvider_Refresh_UsesBrokerLease()
    {
        await using var broker = new GitHubConsumerTestBroker();
        using var client = new HttpClient(new Transport("""{"usageItems":[]}"""));
        var provider = new GitHubCopilotUsageProvider(client, broker);
        await provider.GetMetricsAsync(Account(), TestContext.Current.CancellationToken);
        Assert.Equal(GitHubCredentialPurpose.CopilotUsage, Assert.Single(broker.Requests).Purpose);
        Assert.IsType<SensitiveValueUseFailed<int>>(await broker.Leases.Single().UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHubActionsUsageProvider_Refresh_UsesBrokerLease()
    {
        await using var broker = new GitHubConsumerTestBroker();
        using var client = new HttpClient(new Transport("""{"total_minutes_used":1}"""));
        var provider = new GitHubActionsUsageProvider(client, broker);
        await provider.GetMetricsAsync(Account(), TestContext.Current.CancellationToken);
        Assert.Equal(GitHubCredentialPurpose.ActionsUsage, Assert.Single(broker.Requests).Purpose);
        Assert.IsType<SensitiveValueUseFailed<int>>(await broker.Leases.Single().UseAsync((_, _) => ValueTask.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHubUsageProviders_DifferentUses_HaveDistinctPurposeFingerprints()
    {
        await using var broker = new GitHubConsumerTestBroker();
        using var copilot = new HttpClient(new Transport("""{"usageItems":[]}"""));
        using var actions = new HttpClient(new Transport("""{"total_minutes_used":1}"""));
        await new GitHubCopilotUsageProvider(copilot, broker).GetMetricsAsync(Account(), TestContext.Current.CancellationToken);
        await new GitHubActionsUsageProvider(actions, broker).GetMetricsAsync(Account(), TestContext.Current.CancellationToken);
        Assert.Equal(2, broker.Requests.Select(request => request.Use.Fingerprint).Distinct().Count());
        Assert.Equal(2, broker.Requests.Select(request => request.Requestor.Identifier).Distinct().Count());
        Assert.Equal(broker.Requests[0].Credential.LogicalIdentifier, broker.Requests[1].Credential.LogicalIdentifier);
    }

    [Fact]
    public async Task GitHubUsageProviders_Denied_DoNotSendUnauthenticatedRequest()
    {
        await using var broker = new GitHubConsumerTestBroker { Deny = true };
        using var client = new HttpClient(new Transport("{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitHubActionsUsageProvider(client, broker).GetMetricsAsync(Account(), TestContext.Current.CancellationToken));
    }

    internal static IAccessBroker CreateBroker(Func<string?> materialProvider) => new GitHubConsumerTestBroker { MaterialProvider = materialProvider };

    private static IUsageProvider CreateProvider(
        bool copilot, HttpClient client, IAccessBroker? broker, Func<AccessPresentationScope>? scope = null) =>
        copilot
            ? new GitHubCopilotUsageProvider(client, broker, presentationScope: scope)
            : new GitHubActionsUsageProvider(client, broker, presentationScope: scope);

    private static UsageAccount Account() => new()
    {
        UserName = "private-account",
        Product = "GitHub",
        SettingsUrl = new Uri("https://github.com/settings/billing/summary"),
    };

    private sealed class Transport(string body) : HttpMessageHandler
    {
        internal int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.SendCount++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-material", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class CancelledBroker(bool shutdown) : IAccessBroker
    {
        public Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
            AccessRequest<TResult> request, CancellationToken cancellationToken = default) where TResult : IAccessResult
        {
            AccessOutcome<TResult> outcome = shutdown ? new AccessShuttingDown<TResult>() : new AccessCancelled<TResult>();
            return Task.FromResult(outcome);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancelOnSuccessBroker(
        IAccessBroker inner, CancellationTokenSource cancellation) : IAccessBroker
    {
        public async Task<AccessOutcome<TResult>> AcquireAsync<TResult>(
            AccessRequest<TResult> request, CancellationToken cancellationToken = default) where TResult : IAccessResult
        {
            var outcome = await inner.AcquireAsync(request, cancellationToken);
            await cancellation.CancelAsync();
            return outcome;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
