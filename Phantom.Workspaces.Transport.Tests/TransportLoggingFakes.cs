using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Transport.Tests;

internal sealed record CapturedLogEntry(LogLevel Level, Exception? Exception, string Message);

internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this.Entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Enqueue(new CapturedLogEntry(logLevel, exception, formatter(state, exception)));
    }
}

internal sealed class FakeMessageChannel : IMessageChannel
{
    private readonly Channel<JsonElement> channel = Channel.CreateUnbounded<JsonElement>();

    public ChannelWriter<JsonElement> Writer => this.channel.Writer;

    public ChannelReader<JsonElement> Reader => this.channel.Reader;

    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        this.Disposed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<JsonElement> ReadWrittenAsync() => this.channel.Reader.ReadAsync();
}

internal sealed class FakeListener : ITransportListener
{
    public Exception? ThrowOnChannelOpen { get; set; }

    public bool ReadIncoming { get; set; }

    public bool ReturnNull { get; set; }

    public JsonElement? EchoResponse { get; set; }

    public bool Disposed { get; private set; }

    public async Task<IAsyncDisposable?> OnChannelOpenAsync(
        JsonElement request,
        IMessageChannel channel,
        CancellationToken ct = default)
    {
        if (this.ThrowOnChannelOpen is { } ex)
        {
            throw ex;
        }

        if (this.ReadIncoming)
        {
            _ = await channel.Reader.ReadAsync(ct).ConfigureAwait(false);
        }

        var response = this.EchoResponse ?? JsonDocument.Parse("{}").RootElement;
        await channel.Writer.WriteAsync(response, ct).ConfigureAwait(false);

        return this.ReturnNull ? null : new FakeDisposable();
    }

    public Task<IAsyncDisposable?> OnStreamOpenAsync(
        JsonElement request,
        Stream stream,
        CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(this.ReturnNull ? null : new FakeDisposable());

    public ValueTask DisposeAsync()
    {
        this.Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeListenerFactory : ITransportListenerFactory
{
    public List<FakeListener> Produced { get; } = new();

    public Func<FakeListener>? Configure { get; set; }

    public ITransportListener CreateListener()
    {
        var listener = new FakeListener { ReadIncoming = true };
        this.Configure?.Invoke();
        this.Produced.Add(listener);
        return listener;
    }
}

internal sealed class FakeDisposable : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
