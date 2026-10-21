using System.Text.Json;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Testing;

var configurationLine = await Console.In.ReadLineAsync();
var configuration = JsonSerializer.Deserialize<WorkerConfiguration>(configurationLine
    ?? throw new InvalidOperationException("Worker configuration was not supplied."))
    ?? throw new InvalidOperationException("Worker configuration was invalid.");
using var loggerFactory = new StructuredConsoleLoggerFactory();
var identities = new TransportPeerIdentityProvider();
var providerCatalog = new FixedProviderCatalog(configuration);
const string providerEnvironmentName = "PHANTOM_COPILOT_TEST_WORKER_API_KEY";
Environment.SetEnvironmentVariable(providerEnvironmentName, configuration.ProviderApiKey);
await using var accessBroker = new StoredSecretAccessTestBroker(Environment.GetEnvironmentVariable)
{
    Requestor = new AccessRequestor
    {
        Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(
            AccessRequestorKind.Application,
            configuration.WorkerEntityId),
        Kind = AccessRequestorKind.Application,
        SafeDisplayName = "Test worker",
    },
};
var listener = new IdentityReportingListener(
    new CopilotClientTransportListener(new AgentServices
    {
        RemoteCopilotProviderCatalog = providerCatalog,
        AccessBroker = accessBroker,
        AccessRequestor = accessBroker.Requestor,
        AccessLineage = accessBroker.Lineage,
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

internal sealed class FixedProviderCatalog(WorkerConfiguration configuration)
    : IRemoteCopilotProviderCatalog
{
    public Task<RemoteCopilotProviderMetadata?> GetMetadataAsync(
        string providerReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                providerReference,
                configuration.ProviderReference,
                StringComparison.Ordinal))
        {
            return Task.FromResult<RemoteCopilotProviderMetadata?>(null);
        }

        return Task.FromResult<RemoteCopilotProviderMetadata?>(new RemoteCopilotProviderMetadata
        {
            Type = configuration.ProviderType,
            BaseUrl = configuration.ProviderBaseUrl,
            EnvironmentKeyName = "PHANTOM_COPILOT_TEST_WORKER_API_KEY",
            WireApi = configuration.WireApi,
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
