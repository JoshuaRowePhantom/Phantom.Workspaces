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

    [Fact]
    public void BuildSessionConfig_ForwardsFunctionToolsInstructionsAndModel()
    {
        var tool = AIFunctionFactory.Create(
            (string id) => id,
            "lookup_issue",
            "Fetch issue details from our tracker");
        var options = new ChatOptions
        {
            Instructions = "system prompt",
            Tools = [tool],
        };

        var config = CopilotSdkChatClient.BuildSessionConfig("gpt-test", byokOptions: null, options);

        Assert.Equal("gpt-test", config.Model);
        Assert.Equal("system prompt", config.SystemMessage!.Content);
        Assert.NotNull(config.Tools);
        Assert.Contains(config.Tools!, candidate => candidate.Name == "lookup_issue");
    }

    [Fact]
    public void BuildSessionConfig_IgnoresNonFunctionToolsAndMissingOptions()
    {
        var config = CopilotSdkChatClient.BuildSessionConfig("gpt-test", byokOptions: null, options: null);

        Assert.Equal("gpt-test", config.Model);
        Assert.True(config.Tools is null || config.Tools.Count == 0);
    }
}
