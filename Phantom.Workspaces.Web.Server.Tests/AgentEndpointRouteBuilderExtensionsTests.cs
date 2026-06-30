using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.Web.Server.Tests;

public sealed class AgentEndpointRouteBuilderExtensionsTests
{
    [Fact]
    public void MapAgentEndpoints_MapsAgentRespondRoute()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapAgentEndpoints();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/agent/respond", routePatterns);
    }

    [Fact]
    public void MapAgentEndpoints_MapsAgentChatTurnRoute()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapAgentEndpoints();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/agent/chat/{sessionId}/turn", routePatterns);
    }
}