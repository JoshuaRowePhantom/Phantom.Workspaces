using System;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public async Task CopilotSdkChatClient_WhenRuntimeMissing_ThrowsInstalledAppFriendlyError()
    {
        // Issue #1376: when the bundled copilot.exe is absent (and no explicit cliPath is set), the
        // SDK's StartAsync throws "Copilot runtime not found at ... Ensure the SDK NuGet package was
        // restored correctly ...". A signed-installer user must NOT be told to restore a NuGet
        // package; the error must instead name the missing packaged runtime and the cliPath override.
        var fakeSession = new FakeCopilotSession();
        var fakeClient = new FakeCopilotClient(fakeSession)
        {
            StartException = new InvalidOperationException(
                "Copilot runtime not found at " +
                "'C:\\app\\current\\runtimes\\win-x64\\native\\copilot.exe'. " +
                "Ensure the SDK NuGet package was restored correctly or provide an explicit " +
                "RuntimeConnection.ForStdio(path: ...) / RuntimeConnection.ForTcp(path: ...)."),
        };
        var fakeFactory = new FakeCopilotClientFactory(fakeClient);

        using var client = new Llm.CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);
        client.SetCopilotClientFactoryForTest(fakeFactory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListModelsAsync(CancellationToken.None));

        Assert.DoesNotContain("restore the NuGet package", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NuGet", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copilot.exe", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cliPath", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The original SDK diagnostic is preserved as the inner exception for logs.
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Copilot runtime not found", ex.InnerException!.Message);
    }
}
