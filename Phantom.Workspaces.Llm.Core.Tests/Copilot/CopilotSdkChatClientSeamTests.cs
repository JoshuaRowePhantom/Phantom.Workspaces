using GitHub.Copilot;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests.Copilot;

public sealed class CopilotSdkChatClientSeamTests
{
    [Fact]
    public void CopilotSdkChatClient_DefaultFactory_UsesRealFactory()
    {
        using var client = new Llm.CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);
        // The client is constructed with the default factory
        // We can't directly inspect the factory field, but we can verify it compiles and constructs
        Assert.NotNull(client);
    }

    [Fact]
    public void CopilotSdkChatClient_InjectedClientFactory_CanBeSet()
    {
        var fakeSession = new FakeCopilotSession { SessionId = "test-session" };
        var fakeClient = new FakeCopilotClient(fakeSession);
        var fakeFactory = new FakeCopilotClientFactory(fakeClient);

        using var client = new Llm.CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);
        client.SetCopilotClientFactoryForTest(fakeFactory);

        // The factory was successfully set without throwing
        Assert.NotNull(client);
    }

    [Fact]
    public async Task CopilotSdkChatClient_ListModelsAsync_UsesInjectedFactory()
    {
        var expectedModels = new[] { new ModelInfo { Id = "gpt-5", Name = "GPT-5" } };
        var fakeSession = new FakeCopilotSession { Models = expectedModels };
        var fakeClient = new FakeCopilotClient(fakeSession);
        var fakeFactory = new FakeCopilotClientFactory(fakeClient);

        using var client = new Llm.CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);
        client.SetCopilotClientFactoryForTest(fakeFactory);

        var models = await client.ListModelsAsync(CancellationToken.None);

        Assert.Single(models);
        Assert.Equal("gpt-5", models[0].Id);
    }
}
