using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Transport.Logging;

namespace Phantom.Workspaces.Agent.Gui;

public sealed class ObservableLoggerFactory : ILoggerFactory
{
    private readonly object lockObj = new();
    private readonly List<string> entries = [];
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
                return this.entries.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new ObservableLogger(this, categoryName);

    internal bool VerboseMetadataEnabled => this.verboseMetadataEnabled;

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    internal void AddEntry(string entry)
    {
        lock (this.lockObj)
        {
            this.entries.Add(entry);
        }

        this.EntryAdded?.Invoke(this, entry);
    }
}

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
