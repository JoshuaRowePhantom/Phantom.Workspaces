using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Web.Server;
using Xunit;

namespace Phantom.Workspaces.Web.Server.Tests;

public sealed class StreamEndpointRouteBuilderExtensionsTests
{
    [Fact]
    public void MapStreamEndpoints_MapsStreamOpenRoute()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapStreamEndpoints();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/stream/open", routePatterns);
    }
}
