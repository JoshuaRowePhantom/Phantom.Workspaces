using System.Collections.Generic;
using Azure.Core;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Mcp;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers <see cref="EntraPinnedTokenProvider"/> (#1420): in-memory reuse of a still-valid token,
/// single-flight coalescing of concurrent first-time acquisitions, and cancellation propagation
/// without leaving a partial cached token.
/// </summary>
public sealed class EntraPinnedTokenProviderTests
{
    private static readonly string[] Scopes = ["api://example/.default"];

    [Fact]
    public async Task EntraPinnedTokenProvider_SecondCallWithinLifetime_DoesNotReauthenticate()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var credential = new CountingCredential(
            () => new AccessToken("token-1", clock.GetUtcNow().AddHours(1)));
        var provider = new EntraPinnedTokenProvider(credential, Scopes, clock);

        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("token-1", first);
        Assert.Equal("token-1", second);
        Assert.Equal(1, credential.CallCount);
    }

    [Fact]
    public async Task EntraPinnedTokenProvider_TokenExpired_ReacquiresToken()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var credential = new CountingCredential(
            () => new AccessToken($"token-{clock.GetUtcNow():HH-mm-ss}", clock.GetUtcNow().AddMinutes(10)));
        var provider = new EntraPinnedTokenProvider(credential, Scopes, clock);

        var first = await provider.GetAccessTokenAsync(CancellationToken.None);
        // Advance past the expiry (10m) minus the 5m safety buffer, so the cached token is stale.
        clock.Advance(TimeSpan.FromMinutes(6));
        var second = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.Equal(2, credential.CallCount);
    }

    [Fact]
    public async Task EntraPinnedTokenProvider_ConcurrentAcquisitions_TriggerSingleInteractiveFlow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var gate = new TaskCompletionSource();
        var credential = new GatedCredential(
            gate.Task,
            () => new AccessToken("token-shared", clock.GetUtcNow().AddHours(1)));
        var provider = new EntraPinnedTokenProvider(credential, Scopes, clock);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => provider.GetAccessTokenAsync(CancellationToken.None).AsTask())
            .ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, token => Assert.Equal("token-shared", token));
        Assert.Equal(1, credential.CallCount);
    }

    [Fact]
    public async Task EntraPinnedTokenProvider_Cancellation_PropagatesOperationCanceled()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var credential = new ScriptedCredential(
            () => throw new OperationCanceledException(),
            () => new AccessToken("token-after", clock.GetUtcNow().AddHours(1)));
        var provider = new EntraPinnedTokenProvider(credential, Scopes, clock);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.GetAccessTokenAsync(CancellationToken.None).AsTask());

        // No partial token was cached: the next call must acquire afresh.
        var token = await provider.GetAccessTokenAsync(CancellationToken.None);
        Assert.Equal("token-after", token);
        Assert.Equal(2, credential.CallCount);
    }

    private sealed class CountingCredential : TokenCredential
    {
        private readonly Func<AccessToken> factory;
        private int callCount;

        public CountingCredential(Func<AccessToken> factory) => this.factory = factory;

        public int CallCount => Volatile.Read(ref this.callCount);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.callCount);
            return this.factory();
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.callCount);
            return new ValueTask<AccessToken>(this.factory());
        }
    }

    private sealed class GatedCredential : TokenCredential
    {
        private readonly Task gate;
        private readonly Func<AccessToken> factory;
        private int callCount;

        public GatedCredential(Task gate, Func<AccessToken> factory)
        {
            this.gate = gate;
            this.factory = factory;
        }

        public int CallCount => Volatile.Read(ref this.callCount);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => this.GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.callCount);
            await this.gate.ConfigureAwait(false);
            return this.factory();
        }
    }

    private sealed class ScriptedCredential : TokenCredential
    {
        private readonly Queue<Func<AccessToken>> steps;
        private int callCount;

        public ScriptedCredential(params Func<AccessToken>[] steps) => this.steps = new Queue<Func<AccessToken>>(steps);

        public int CallCount => this.callCount;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            this.callCount++;
            return this.steps.Dequeue()();
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            this.callCount++;
            return new ValueTask<AccessToken>(this.steps.Dequeue()());
        }
    }
}
