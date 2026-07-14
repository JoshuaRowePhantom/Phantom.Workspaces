using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests;

public sealed class GitHubDevTunnelAuthTokenProviderTests
{
    [Fact]
    public async Task GetAccessTokenAsync_WithResolvedToken_CallsUpsertService()
    {
        var upsertService = new RecordingUpsertService();
        // Use an environment variable stub by wrapping the provider test with a known GITHUB_TOKEN.
        // Since GitHubAuthTokenResolver reads GITHUB_TOKEN, we use a subclass approach instead.
        // We test via a subclass of GitHubDevTunnelAuthTokenProvider that returns a fixed token.
        var provider = new FakeGitHubDevTunnelAuthTokenProvider("ghs_testtoken", upsertService);

        var token = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("ghs_testtoken", token);
        Assert.Single(upsertService.UpsertedTokens);
        Assert.Equal("ghs_testtoken", upsertService.UpsertedTokens[0]);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithNoUpsertService_ReturnsToken()
    {
        var provider = new FakeGitHubDevTunnelAuthTokenProvider("ghs_testtoken", accountUpsertService: null);

        var token = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("ghs_testtoken", token);
    }

    // A testable subclass that overrides token resolution to avoid process invocations.
    private sealed class FakeGitHubDevTunnelAuthTokenProvider(
        string resolvedToken,
        IGitHubAccountUpsertService? accountUpsertService) : IDevTunnelAuthTokenProvider
    {
        private readonly GitHubDevTunnelAuthTokenProvider inner = new(accountUpsertService);

        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            // The real provider reads GITHUB_TOKEN env var. Set it temporarily for this test.
            var previous = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            try
            {
                Environment.SetEnvironmentVariable("GITHUB_TOKEN", resolvedToken);
                return await this.inner.GetAccessTokenAsync(cancellationToken);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GITHUB_TOKEN", previous);
            }
        }
    }

    private sealed class RecordingUpsertService : IGitHubAccountUpsertService
    {
        public List<string> UpsertedTokens { get; } = [];

        public Task UpsertForTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            this.UpsertedTokens.Add(token);
            return Task.CompletedTask;
        }
    }
}
