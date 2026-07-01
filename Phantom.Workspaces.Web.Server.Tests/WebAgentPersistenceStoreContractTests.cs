using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Tests;

namespace Phantom.Workspaces.Web.Server.Tests;

public sealed class WebAgentPersistenceStoreContractTests : AgentPersistenceStoreContractTests, IAsyncLifetime
{
    private readonly InMemoryAgentPersistenceStore backingStore = new();
    private WebApplication? app;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IAgentPersistenceStore>(backingStore);
        app = builder.Build();
        app.MapAgentPersistenceEndpoints();
        await app.StartAsync();
    }

    protected override ValueTask<IAgentPersistenceStore> CreateStoreAsync()
    {
        return ValueTask.FromResult<IAgentPersistenceStore>(
            new WebAgentPersistenceStore(app!.GetTestServer().CreateClient()));
    }

    protected override ValueTask ResetStoreAsync()
    {
        backingStore.Reset();
        return ValueTask.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }
}
