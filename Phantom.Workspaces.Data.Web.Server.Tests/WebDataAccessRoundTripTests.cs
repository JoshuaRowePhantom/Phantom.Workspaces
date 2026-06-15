using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Data.Web.Server;

namespace Phantom.Workspaces.Data.Web.Server.Tests;

public sealed class WebDataAccessRoundTripTests
{
    [Fact]
    public async Task GetAsync_RoundTripsThroughInProcessWebServer()
    {
        var dataAccessLayer = await WebServerDataAccessLayerFactory.CreateDefaultAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDataAccessLayer>(dataAccessLayer);
        var app = builder.Build();
        app.MapWebDataAccessEndpoints();

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();

            using var client = new WebClientDataAccessLayer(address);

            // A request for a random (non-existent) entity exercises the full
            // client -> HTTP -> server -> validated DAL -> response path.
            var result = await client.GetAsync(new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = new EntityId(System.Guid.NewGuid()) }],
            });

            Assert.NotNull(result);
            Assert.NotNull(result.Batches);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
