using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotSdkChatClientTests
{
    [Fact]
    public void Construction_DoesNotStartProcess_AndExposesDisplayName()
    {
        // Constructing the adapter must be lazy: no Copilot CLI process is started until a
        // request is made, so this is safe without authentication.
        using var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

        Assert.Equal("GitHub Copilot (gpt-5)", client.DisplayName);
    }

    [Fact]
    public void GetService_ReturnsSelf_ForChatClientType()
    {
        using var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Same(client, client.GetService(typeof(CopilotSdkChatClient)));
        Assert.Null(client.GetService(typeof(string)));
        Assert.Null(client.GetService(typeof(IChatClient), serviceKey: "key"));
    }

    [Fact]
    public void Dispose_IsSafe_WhenNeverStarted()
    {
        var client = new CopilotSdkChatClient("gpt-5", "GitHub Copilot (gpt-5)", gitHubToken: null, loggerFactory: null);

        client.Dispose();
        client.Dispose();
    }
}
