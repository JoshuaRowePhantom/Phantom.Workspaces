using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Web.Server;

public static class WebDataAccessEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = WebDataAccessJsonSerialization.Options;

    public static IEndpointRouteBuilder MapWebDataAccessEndpoints(this IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("/data/update", (HttpContext httpContext, IDataAccessLayer dataAccessLayer) =>
            HandleAsync<UpdateRequest, UpdateResult>(httpContext, dataAccessLayer, static (dal, request, token) => dal.UpdateAsync(request, token)));

        endpointRouteBuilder.MapPost("/data/get", (HttpContext httpContext, IDataAccessLayer dataAccessLayer) =>
            HandleAsync<GetRequest, GetResult>(httpContext, dataAccessLayer, static (dal, request, token) => dal.GetAsync(request, token)));

        endpointRouteBuilder.MapPost("/data/query", (HttpContext httpContext, IDataAccessLayer dataAccessLayer) =>
            HandleAsync<QueryRequest, QueryResult>(httpContext, dataAccessLayer, static (dal, request, token) => dal.QueryAsync(request, token)));

        endpointRouteBuilder.MapPost("/data/get-history", (HttpContext httpContext, IDataAccessLayer dataAccessLayer) =>
            HandleAsync<GetHistoryRequest, GetHistoryResult>(httpContext, dataAccessLayer, static (dal, request, token) => dal.GetHistoryAsync(request, token)));

#pragma warning disable CS0618
        endpointRouteBuilder.MapPost("/data/export", (HttpContext httpContext, IDataAccessLayer dataAccessLayer) =>
            HandleAsync<ExportRequest, ExportResult>(httpContext, dataAccessLayer, static (dal, request, token) => dal.ExportAsync(request, token)));
#pragma warning restore CS0618

        endpointRouteBuilder.MapPost("/data/get-changed-entities", (HttpContext httpContext, IDataAccessLayer dataAccessLayer) =>
            HandleAsync<GetChangedEntitiesRequest, GetChangedEntitiesResult>(httpContext, dataAccessLayer, static (dal, request, token) => dal.GetChangedEntitiesAsync(request, token)));

        return endpointRouteBuilder;
    }

    private static async Task<IResult> HandleAsync<TRequest, TResult>(
        HttpContext httpContext,
        IDataAccessLayer dataAccessLayer,
        Func<IDataAccessLayer, TRequest, CancellationToken, Task<TResult>> operation)
    {
        var cancellationToken = httpContext.RequestAborted;
        var request = await JsonSerializer
            .DeserializeAsync<TRequest>(httpContext.Request.Body, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return Results.BadRequest("Empty request body.");
        }

        var result = await operation(dataAccessLayer, request, cancellationToken).ConfigureAwait(false);
        return Results.Json(result, SerializerOptions);
    }
}
