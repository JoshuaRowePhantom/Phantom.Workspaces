using System.Collections.Concurrent;
using System.Text.Json;

namespace Phantom.Workspaces.Transport.Tests.Infrastructure;

/// <summary>
/// An in-process <see cref="ITransportFactory"/> that bridges the <c>{"type":"http","url":...}</c>
/// descriptors emitted by <see cref="ReverseHttp.ReverseHttpForwardingTransportFactory"/> to an
/// <see cref="InProcessReverseHubFixture"/>. Each configured hub URL maps to a behaviour, allowing
/// hermetic simulation of healthy hubs, hard failures and stale/hung hubs for hub-URL
/// racing/fallback tests.
/// </summary>
internal sealed class InProcessHubHttpTransportFactory : ITransportFactory
{
    public enum HubBehavior
    {
        Healthy,
        Fail,
        Hang,
    }

    private readonly InProcessReverseHubFixture fixture;
    private readonly ConcurrentDictionary<string, HubBehavior> behaviors = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> connectedUrls = new();

    public InProcessHubHttpTransportFactory(InProcessReverseHubFixture fixture, params string[] healthyUrls)
    {
        this.fixture = fixture;
        foreach (var url in healthyUrls)
        {
            this.behaviors[url] = HubBehavior.Healthy;
        }
    }

    public IReadOnlyCollection<string> ConnectedUrls => this.connectedUrls.ToArray();

    public void SetBehavior(string url, HubBehavior behavior) => this.behaviors[url] = behavior;

    public async Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
    {
        if (!connectionDescriptor.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), "http", StringComparison.OrdinalIgnoreCase)
            || !connectionDescriptor.TryGetProperty("url", out var urlProperty)
            || urlProperty.GetString() is not { } url)
        {
            return null;
        }

        var behavior = this.behaviors.GetValueOrDefault(url, HubBehavior.Fail);
        switch (behavior)
        {
            case HubBehavior.Fail:
                throw new TransportException($"Simulated hub connection failure for '{url}'.");

            case HubBehavior.Hang:
                // Never completes until the caller's race/timeout cancels this attempt.
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return null;

            default:
                this.connectedUrls.Enqueue(url);
                return await this.fixture.CreateForwardingClientAsync(ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
