using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.Web.Server.Tests;

public sealed class WorkspaceToolEndpointRouteBuilderExtensionsTests
{
    [Fact]
    public void MapWorkspaceToolEndpoints_MapsRunToolRoute()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapWorkspaceToolEndpoints();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/workspace/tools/run", routePatterns);
    }
}
