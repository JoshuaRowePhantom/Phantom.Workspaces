using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.ReverseHttp;

var configurationLine = await Console.In.ReadLineAsync();
var configuration = JsonSerializer.Deserialize<WorkerConfiguration>(configurationLine
    ?? throw new InvalidOperationException("Worker configuration was not supplied."))
    ?? throw new InvalidOperationException("Worker configuration was invalid.");
using var loggerFactory = new StructuredConsoleLoggerFactory();
var identities = new TransportPeerIdentityProvider();
var providerResolver = new FixedProviderResolver(configuration);
var listener = new IdentityReportingListener(
    new CopilotClientTransportListener(new AgentServices
    {
        RemoteCopilotProviderResolver = providerResolver,
        LoggerFactory = loggerFactory,
    }),
    identities,
    configuration.WorkerMarker);
var registry = new TransportRegistry();
registry.Register(listener);
await using var registrationFactory = new ReverseHttpClientTransportFactory(
    configuration.HubUrl,
    configuration.WorkerEntityId);
var registration = await registrationFactory.EnsureRegisteredAsync();
await using var dispatcher = new ReverseExecutionDispatcher(
    registration,
    registry,
    identities);
StructuredConsole.Write(new
{
    type = "ready",
    worker = configuration.WorkerMarker,
    processId = Environment.ProcessId,
});
while (await Console.In.ReadLineAsync() is { } command
       && !string.Equals(command, "stop", StringComparison.Ordinal))
{
}

internal sealed record WorkerConfiguration(
    string HubUrl,
    string WorkerEntityId,
    string WorkerMarker,
    string ProviderReference,
    string ProviderType,
    string ProviderBaseUrl,
    string ProviderApiKey,
    string WireApi,
    string ModelId);

internal sealed class FixedProviderResolver(WorkerConfiguration configuration)
    : IRemoteCopilotProviderResolver
{
    public Task<ProviderConfig?> ResolveAsync(
        string providerReference,
        string modelId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                providerReference,
                configuration.ProviderReference,
                StringComparison.Ordinal)
            || !string.Equals(modelId, configuration.ModelId, StringComparison.Ordinal))
        {
            return Task.FromResult<ProviderConfig?>(null);
        }

        return Task.FromResult<ProviderConfig?>(new ProviderConfig
        {
            Type = configuration.ProviderType,
            BaseUrl = configuration.ProviderBaseUrl,
            ApiKey = configuration.ProviderApiKey,
            WireApi = configuration.WireApi,
            ModelId = modelId,
        });
    }
}

internal sealed class IdentityReportingListener(
    ITransportListener inner,
    TransportPeerIdentityProvider identities,
    string workerMarker) : ITransportListener
{
    public async Task<IAsyncDisposable?> OnChannelOpenAsync(
        JsonElement request,
        IMessageChannel channel,
        CancellationToken ct = default)
    {
        var identity = identities.GetRequiredIdentity(channel);
        StructuredConsole.Write(new
        {
            type = "request",
            worker = workerMarker,
            caller = identity.UserComputerProfileEntityId,
            requestType = request.TryGetProperty("type", out var type)
                ? type.GetString()
                : null,
        });
        return await inner.OnChannelOpenAsync(request, channel, ct).ConfigureAwait(false);
    }

    public Task<IAsyncDisposable?> OnStreamOpenAsync(
        JsonElement request,
        Stream stream,
        CancellationToken ct = default)
        => inner.OnStreamOpenAsync(request, stream, ct);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal static class StructuredConsole
{
    private static readonly object Gate = new();

    public static void Write(object value)
    {
        lock (Gate)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(value));
            Console.Out.Flush();
        }
    }
}

internal sealed class StructuredConsoleLoggerFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new StructuredConsoleLogger();

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class StructuredConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>>;
            StructuredConsole.Write(new
            {
                type = "lifecycle",
                correlationId = Value("CorrelationId"),
                role = Value("Role"),
                stage = Value("Stage"),
                lastConfirmedStage = Value("LastConfirmedStage"),
                errorCategory = Value("ErrorCategory"),
            });

            string? Value(string name) => values?
                .FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.Ordinal))
                .Value?
                .ToString();
        }
    }
}
