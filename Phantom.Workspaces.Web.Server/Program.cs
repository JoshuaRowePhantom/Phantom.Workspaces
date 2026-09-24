using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Server;
using Phantom.Workspaces.Containers;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services.Logging;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Transport.Logging;
using Phantom.Workspaces.Web.Server;

var builder = WebApplication.CreateBuilder(args);

// #1095: this standalone host never loads a WorkspacesConfiguration, so it resolves its own log
// directory (content root / PHANTOM_WORKSPACES_LOG_DIRECTORY override) and registers the shared
// #1086 rolling file provider against it — independent of the main .exe's single-resolution path.
var logDirectory = HostLogDirectoryResolver.Resolve(builder.Environment.ContentRootPath);
builder.Logging.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(
    _ => new RollingFileLoggerProvider(logDirectory, HostFileLoggerFactory.DefaultRetention));
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
if (TransportMetadataLoggingOptions.FromEnvironment().Enabled)
{
    builder.Logging.AddFilter("Phantom.Workspaces.Transport.Logging", LogLevel.Debug);
    builder.Logging.AddFilter("Phantom.Workspaces.Transport.ReverseHttp", LogLevel.Debug);
    builder.Logging.AddFilter("Phantom.Workspaces.Llm.HttpRequestLoggingHandler", LogLevel.Debug);
}

var dataAccessLayer = await WebServerDataAccessLayerFactory.CreateDefaultAsync();
builder.Services.AddSingleton<IDataAccessLayer>(dataAccessLayer);

var transportRegistry = new TransportRegistry();
var reverseConnectionStatusRegistry = new ReverseConnectionStatusRegistry();
builder.Services.AddSingleton(reverseConnectionStatusRegistry);
builder.Services.AddSingleton(sp => new ReverseHttpServerTransportFactory(
    reverseConnectionStatusRegistry,
    loggerFactory: sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton(transportRegistry);
var transportFactoryRegistry = new TransportFactoryRegistry();
transportFactoryRegistry.Register(new LocalTransportFactory(transportRegistry));
transportFactoryRegistry.Register(new HttpClientTransportFactory());
transportFactoryRegistry.Register(new ReverseHttpForwardingTransportFactory());
builder.Services.AddSingleton<ITransportFactoryRegistry>(transportFactoryRegistry);
builder.Services.AddSingleton<HttpServerTransportFactory>();

builder.Services.AddSingleton(sp => new AgentChatSessionCache(new AgentServices
{
    LoggerFactory = sp.GetRequiredService<ILoggerFactory>(),
    AgentPersistenceStoreOverride = sp.GetRequiredService<IAgentPersistenceStore>(),
}));

var localTrustedExecutor = new LocalTrustedExecutor();
builder.Services.AddSingleton(localTrustedExecutor);

builder.Services.AddSingleton<IAgentPersistenceStore>(AgentPersistenceStoreFactory.CreateInMemory());

var app = builder.Build();
var processLoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var reverseTransportServerFactory = app.Services.GetRequiredService<ReverseHttpServerTransportFactory>();
transportRegistry.Register(TransportMetadataLoggingOptions.FromEnvironment().Enabled
    ? reverseTransportServerFactory.WithLogging(processLoggerFactory)
    : reverseTransportServerFactory);

// #1093: log global uncaught/unobserved exceptions for this standalone host through the file
// provider registered above.
GlobalExceptionLogging.Register(processLoggerFactory);

// #1373: install the process-wide ambient docker logger factory so the production
// MongoDbConnectionBroker default path logs docker stdout/stderr through the real host logger.
DockerCommandRunnerLogging.LoggerFactory = processLoggerFactory;

app.UseWebSockets();

app.MapGet("/", () => $"Phantom.Workspaces.Web.Server ({typeof(WebServerMarker).Namespace})");
app.MapWebDataAccessEndpoints();
app.MapAgentEndpoints();
app.MapTransportReverseEndpoints(reverseTransportServerFactory, reverseConnectionStatusRegistry);
app.Services.GetRequiredService<HttpServerTransportFactory>().Map(app);
app.MapWorkspaceToolEndpoints();
app.MapAgentPersistenceEndpoints();

app.Run();
