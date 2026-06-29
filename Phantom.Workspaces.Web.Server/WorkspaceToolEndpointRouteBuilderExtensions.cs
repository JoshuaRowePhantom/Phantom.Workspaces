using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Web.Server;

/// <summary>
/// Maps the workspace tool execution endpoint. A remote caller POST-s here to run a scheduled
/// workspace tool locally on this host. Execution is delegated to the registered
/// <see cref="LocalTrustedExecutor"/> with the target client instance normalised to the local
/// instance (<c>"."</c>), mirroring the pattern used by <c>POST /agent/respond</c>.
/// </summary>
public static class WorkspaceToolEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Maps <c>POST /workspace/tools/run</c> onto the supplied route builder.</summary>
    public static IEndpointRouteBuilder MapWorkspaceToolEndpoints(this IEndpointRouteBuilder endpointRouteBuilder)
    {
        ArgumentNullException.ThrowIfNull(endpointRouteBuilder);

        endpointRouteBuilder.MapPost("/workspace/tools/run", async (HttpContext httpContext) =>
        {
            var cancellationToken = httpContext.RequestAborted;
            var request = await JsonSerializer
                .DeserializeAsync<TrustedToolRequest>(httpContext.Request.Body, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Results.BadRequest("Empty tool request.");
            }

            var localExecutor = httpContext.RequestServices.GetService(typeof(LocalTrustedExecutor))
                as LocalTrustedExecutor;

            if (localExecutor is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            // Always run on this host — normalise the target to local so LocalTrustedExecutor accepts it.
            var localRequest = request with { TargetClientInstance = TrustProfile.LocalClientInstance };

            try
            {
                await localExecutor.RunToolAsync(localRequest, cancellationToken).ConfigureAwait(false);
                return Results.Ok();
            }
            catch (NotSupportedException)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }
        });

        return endpointRouteBuilder;
    }
}
