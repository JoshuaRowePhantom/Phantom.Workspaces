using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Transport.Logging;

namespace Phantom.Workspaces.Agent.Gui;

public sealed class ObservableLoggerFactory : ILoggerFactory
{
    public const int RecentEntryLimit = 2000;

    private readonly object lockObj = new();
    private readonly Queue<ObservableLogEntry> entries = new();
    private readonly List<Action<ObservableLogEntry>> subscribers = [];
    private long nextSequence;
    private readonly bool verboseMetadataEnabled;

    public ObservableLoggerFactory(bool? verboseMetadataEnabled = null)
    {
        this.verboseMetadataEnabled = verboseMetadataEnabled
            ?? TransportMetadataLoggingOptions.FromEnvironment().Enabled;
    }

    public event EventHandler<string>? EntryAdded;

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (this.lockObj)
            {
                return this.entries.Select(static entry => entry.Text).ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new ObservableLogger(this, categoryName);

    internal bool VerboseMetadataEnabled => this.verboseMetadataEnabled;

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    public IDisposable Subscribe(
        Action<ObservableLogEntry> onEntry, out IReadOnlyList<ObservableLogEntry> snapshot)
    {
        lock (this.lockObj)
        {
            snapshot = this.entries.ToArray();
            this.subscribers.Add(onEntry);
        }
        return new Subscription(this, onEntry);
    }

    internal void AddEntry(string entry)
    {
        lock (this.lockObj)
        {
            var item = new ObservableLogEntry(++this.nextSequence, entry);
            this.entries.Enqueue(item);
            if (this.entries.Count > RecentEntryLimit)
                this.entries.Dequeue();
            foreach (var subscriber in this.subscribers.ToArray())
                subscriber(item);
        }

        this.EntryAdded?.Invoke(this, entry);
    }

    private sealed class Subscription(
        ObservableLoggerFactory factory, Action<ObservableLogEntry> handler) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
                return;
            lock (factory.lockObj)
                factory.subscribers.Remove(handler);
        }
    }
}

public readonly record struct ObservableLogEntry(long Sequence, string Text);

internal sealed class ObservableLogger(ObservableLoggerFactory factory, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel switch
    {
        >= LogLevel.Information and < LogLevel.None => true,
        LogLevel.Debug => factory.VerboseMetadataEnabled && (
            category.StartsWith("Phantom.Workspaces.Transport.Logging.", StringComparison.Ordinal)
            || category.StartsWith("Phantom.Workspaces.Transport.ReverseHttp.", StringComparison.Ordinal)
            || category == "Phantom.Workspaces.Llm.HttpRequestLoggingHandler"
            || category == "Phantom.Workspaces.Llm.Mcp.ProcessExecutorBackedClientTransport"),
        _ => false,
    };

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!this.IsEnabled(logLevel))
            return;
        var message = formatter(state, exception);
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var levelStr = logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };

        var entry = $"[{timestamp}] [{levelStr}] {category}: {message}";
        if (exception != null)
        {
            entry += $"\n  Exception: {exception}";
        }

        factory.AddEntry(entry);
    }
}
