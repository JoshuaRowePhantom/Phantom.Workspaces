using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Server;

var builder = WebApplication.CreateBuilder(args);
var dataAccessLayer = await WebServerDataAccessLayerFactory.CreateDefaultAsync();
builder.Services.AddSingleton<IDataAccessLayer>(dataAccessLayer);
var app = builder.Build();

app.MapGet("/", () => $"Phantom.Workspaces.Web.Server ({typeof(WebServerMarker).Namespace})");
app.MapWebDataAccessEndpoints();

app.Run();
