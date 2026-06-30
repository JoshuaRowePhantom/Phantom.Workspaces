using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Server;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Web.Server;

var builder = WebApplication.CreateBuilder(args);
var dataAccessLayer = await WebServerDataAccessLayerFactory.CreateDefaultAsync();
builder.Services.AddSingleton<IDataAccessLayer>(dataAccessLayer);

var reverseExecutionRegistry = new ReverseExecutionRegistry();
builder.Services.AddSingleton(reverseExecutionRegistry);

builder.Services.AddSingleton<AgentChatSessionCache>();

var localTrustedExecutor = new LocalTrustedExecutor();
builder.Services.AddSingleton(localTrustedExecutor);

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/", () => $"Phantom.Workspaces.Web.Server ({typeof(WebServerMarker).Namespace})");
app.MapWebDataAccessEndpoints();
app.MapAgentEndpoints();
app.MapReverseEndpoints(reverseExecutionRegistry);
app.MapStreamEndpoints();
app.MapWorkspaceToolEndpoints();

app.Run();
