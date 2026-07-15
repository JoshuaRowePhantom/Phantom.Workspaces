using System.Collections.Concurrent;
using System.Text.Json;

namespace Phantom.Workspaces.Transport.ReverseHttp;

public sealed class ReverseHttpServerTransportFactory : ITransportListener
{
    private readonly ConcurrentDictionary<string, IMessageChannel> registrations = new(StringComparer.Ordinal);

    public int RegistrationCount => this.registrations.Count;

    public bool IsRegistered(string entityId) => this.registrations.ContainsKey(entityId);

    public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
    {
        if (!request.TryGetProperty("type", out var typeProperty)
            || typeProperty.GetString() is not { } type)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        if (string.Equals(type, "reverse-register", StringComparison.OrdinalIgnoreCase))
        {
            var entityId = ReadEntityId(request);
            this.registrations[entityId] = channel;
            return Task.FromResult<IAsyncDisposable?>(new RegistrationLease(this.registrations, entityId, channel));
        }

        if (string.Equals(type, "reverse-http", StringComparison.OrdinalIgnoreCase))
        {
            var entityId = ReadEntityId(request);
            if (this.registrations.ContainsKey(entityId))
            {
                return Task.FromResult<IAsyncDisposable?>(NoopLease.Instance);
            }

            return Task.FromResult<IAsyncDisposable?>(new ErrorLease(channel, "not-registered", $"No reverse HTTP registration exists for '{entityId}'."));
        }

        return Task.FromResult<IAsyncDisposable?>(null);
    }

    public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(null);

    public ValueTask DisposeAsync()
    {
        this.registrations.Clear();
        return ValueTask.CompletedTask;
    }

    private static string ReadEntityId(JsonElement request)
    {
        if (!request.TryGetProperty("entity-id", out var entityIdProperty)
            || entityIdProperty.GetString() is not { Length: > 0 } entityId)
        {
            throw new TransportException("Reverse HTTP descriptors must include entity-id.");
        }

        return entityId;
    }

    private sealed class RegistrationLease(
        ConcurrentDictionary<string, IMessageChannel> registrations,
        string entityId,
        IMessageChannel channel) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            registrations.TryRemove(new KeyValuePair<string, IMessageChannel>(entityId, channel));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ErrorLease(IMessageChannel channel, string code, string message) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using var document = JsonDocument.Parse($$"""{"type":"channel-open-error","error-code":"{{code}}","message":"{{message}}"}""");
            await channel.Writer.WriteAsync(document.RootElement.Clone()).ConfigureAwait(false);
            channel.Writer.TryComplete();
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static readonly NoopLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
