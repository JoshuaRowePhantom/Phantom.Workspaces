using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.Web.Server.Tests;

public sealed class ReverseEndpointRouteBuilderExtensionsTests
{
    [Fact]
    public void MapReverseEndpoints_MapsReverseConnectRoute()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapReverseEndpoints(new ReverseExecutionRegistry());

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/reverse/connect", routePatterns);
    }
}
