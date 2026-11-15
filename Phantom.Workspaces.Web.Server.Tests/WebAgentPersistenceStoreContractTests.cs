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

public class WebAgentPersistenceStoreContractTests : AgentPersistenceStoreContractTests, IAsyncLifetime
{
    private readonly InMemoryAgentPersistenceStore backingStore = new();
    private WebApplication? app;
    private readonly List<HttpClient> clients = [];

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
        var client = app!.GetTestServer().CreateClient();
        this.clients.Add(client);
        return ValueTask.FromResult(this.CreateHttpStore(client));
    }

    protected virtual IAgentPersistenceStore CreateHttpStore(HttpClient client) => new WebAgentPersistenceStore(client);

    protected override ValueTask ResetStoreAsync()
    {
        backingStore.Reset();
        return ValueTask.CompletedTask;
    }

    public sealed class WebClientAgentPersistenceStoreContractTests : WebAgentPersistenceStoreContractTests
    {
        protected override IAgentPersistenceStore CreateHttpStore(HttpClient client) =>
            new WebClientAgentPersistenceStore("http://localhost", client, ownsHttpClient: true);
    }

    public async Task DisposeAsync()
    {
        foreach (var client in this.clients) client.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }
}
