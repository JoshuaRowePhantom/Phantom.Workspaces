using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Data.Web.Server;

namespace Phantom.Workspaces.Data.Web.Server.Tests;

public sealed class WebServerDataAccessLayerFactoryTests
{
    [Fact]
    public async Task CreateDefaultAsync_ComposesServerValidationPipeline()
    {
        var dataAccessLayer = await WebServerDataAccessLayerFactory.CreateDefaultAsync();

        Assert.IsType<MergeProcessingDataAccessLayer>(dataAccessLayer);
        var referentialLayer = ReadInnerDataAccessLayer(dataAccessLayer);
        Assert.IsType<ReferentialIntegrityDataAccessLayer>(referentialLayer);
        var schemaLayer = ReadInnerDataAccessLayer(referentialLayer);
        Assert.IsType<SchemaValidatingDataAccessLayer>(schemaLayer);
        var storageLayer = ReadInnerDataAccessLayer(schemaLayer);
        Assert.IsType<InMemoryDataAccessLayer>(storageLayer);
    }

    [Fact]
    public void MapWebDataAccessEndpoints_MapsAllExpectedRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IDataAccessLayer>(new InMemoryDataAccessLayer());
        var app = builder.Build();

        app.MapWebDataAccessEndpoints();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/data/update", routePatterns);
        Assert.Contains("/data/get", routePatterns);
        Assert.Contains("/data/query", routePatterns);
        Assert.Contains("/data/get-history", routePatterns);
        Assert.Contains("/data/export", routePatterns);
        Assert.Contains("/data/get-changed-entities", routePatterns);
    }

    private static IDataAccessLayer ReadInnerDataAccessLayer(IDataAccessLayer layer)
    {
        const string propertyName = "UnderlyingDataAccessLayer";
        PropertyInfo? property = null;
        for (var type = layer.GetType(); type is not null && property is null; type = type.BaseType)
        {
            property = type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
        }

        Assert.NotNull(property);
        var value = property!.GetValue(layer);
        return Assert.IsAssignableFrom<IDataAccessLayer>(value);
    }
}
