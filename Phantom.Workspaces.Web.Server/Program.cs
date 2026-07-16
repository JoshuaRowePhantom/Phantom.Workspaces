using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Server;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.Local;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Web.Server;

var builder = WebApplication.CreateBuilder(args);
var dataAccessLayer = await WebServerDataAccessLayerFactory.CreateDefaultAsync();
builder.Services.AddSingleton<IDataAccessLayer>(dataAccessLayer);

var transportRegistry = new TransportRegistry();
transportRegistry.Register(new ReverseHttpServerTransportFactory());
builder.Services.AddSingleton(transportRegistry);
var reverseConnectionStatusRegistry = new ReverseConnectionStatusRegistry();
builder.Services.AddSingleton(reverseConnectionStatusRegistry);
var reverseTransportServerFactory = new ReverseHttpServerTransportFactory(reverseConnectionStatusRegistry);
builder.Services.AddSingleton(reverseTransportServerFactory);
var transportFactoryRegistry = new TransportFactoryRegistry();
transportFactoryRegistry.Register(new LocalTransportFactory(transportRegistry));
transportFactoryRegistry.Register(new HttpClientTransportFactory());
transportFactoryRegistry.Register(new ReverseHttpForwardingTransportFactory());
builder.Services.AddSingleton<ITransportFactoryRegistry>(transportFactoryRegistry);
builder.Services.AddSingleton<HttpServerTransportFactory>();

builder.Services.AddSingleton<AgentChatSessionCache>();

var localTrustedExecutor = new LocalTrustedExecutor();
builder.Services.AddSingleton(localTrustedExecutor);

builder.Services.AddSingleton<IAgentPersistenceStore>(AgentPersistenceStoreFactory.CreateInMemory());

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/", () => $"Phantom.Workspaces.Web.Server ({typeof(WebServerMarker).Namespace})");
app.MapWebDataAccessEndpoints();
app.MapAgentEndpoints();
app.MapTransportReverseEndpoints(reverseTransportServerFactory, reverseConnectionStatusRegistry);
app.Services.GetRequiredService<HttpServerTransportFactory>().Map(app);
app.MapWorkspaceToolEndpoints();
app.MapAgentPersistenceEndpoints();

app.Run();
