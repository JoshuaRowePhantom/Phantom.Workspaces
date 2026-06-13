using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Web.Server;

public static class WebDataAccessEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapWebDataAccessEndpoints(this IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("/data/update", async (
            UpdateRequest request,
            IDataAccessLayer dataAccessLayer,
            CancellationToken cancellationToken) =>
        {
            var result = await dataAccessLayer.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });

        endpointRouteBuilder.MapPost("/data/get", async (
            GetRequest request,
            IDataAccessLayer dataAccessLayer,
            CancellationToken cancellationToken) =>
        {
            var result = await dataAccessLayer.GetAsync(request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });

        endpointRouteBuilder.MapPost("/data/query", async (
            QueryRequest request,
            IDataAccessLayer dataAccessLayer,
            CancellationToken cancellationToken) =>
        {
            var result = await dataAccessLayer.QueryAsync(request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });

        endpointRouteBuilder.MapPost("/data/get-history", async (
            GetHistoryRequest request,
            IDataAccessLayer dataAccessLayer,
            CancellationToken cancellationToken) =>
        {
            var result = await dataAccessLayer.GetHistoryAsync(request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });

#pragma warning disable CS0618
        endpointRouteBuilder.MapPost("/data/export", async (
            ExportRequest request,
            IDataAccessLayer dataAccessLayer,
            CancellationToken cancellationToken) =>
        {
            var result = await dataAccessLayer.ExportAsync(request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });
#pragma warning restore CS0618

        endpointRouteBuilder.MapPost("/data/get-changed-entities", async (
            GetChangedEntitiesRequest request,
            IDataAccessLayer dataAccessLayer,
            CancellationToken cancellationToken) =>
        {
            var result = await dataAccessLayer.GetChangedEntitiesAsync(request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });

        return endpointRouteBuilder;
    }
}
