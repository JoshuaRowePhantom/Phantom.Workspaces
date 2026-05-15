using Phantom.Workspaces.Dal.Web.Server;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => $"Phantom.Workspaces.Web.Server ({typeof(WebServerMarker).Namespace})");

app.Run();
