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

    [Fact]
    public void BuildResumeSessionConfig_ForwardsFunctionToolsInstructionsAndModel()
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

        var config = CopilotSdkChatClient.BuildResumeSessionConfig("gpt-test", byokOptions: null, options);

        Assert.Equal("gpt-test", config.Model);
        Assert.Equal("system prompt", config.SystemMessage!.Content);
        Assert.NotNull(config.Tools);
        Assert.Contains(config.Tools!, candidate => candidate.Name == "lookup_issue");
    }

    [Fact]
    public void BuildResumeSessionConfig_IgnoresNonFunctionToolsAndMissingOptions()
    {
        var config = CopilotSdkChatClient.BuildResumeSessionConfig("gpt-test", byokOptions: null, options: null);

        Assert.Equal("gpt-test", config.Model);
        Assert.True(config.Tools is null || config.Tools.Count == 0);
    }

    [Fact]
    public void BuildResumeSessionConfig_MapsReasoningEffort()
    {
        var config = CopilotSdkChatClient.BuildResumeSessionConfig(
            "gpt-test",
            byokOptions: null,
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } });

        Assert.Equal("high", config.ReasoningEffort);
    }

    [Fact]
    public void ComputeSessionSignature_IsStableForEquivalentOptions_IgnoringToolOrder()
    {
        var toolA = AIFunctionFactory.Create((string id) => id, "alpha", "a");
        var toolB = AIFunctionFactory.Create((string id) => id, "beta", "b");

        var first = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions
        {
            Instructions = "system",
            Tools = [toolA, toolB],
        });
        var second = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions
        {
            Instructions = "system",
            Tools = [toolB, toolA],
        });

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeSessionSignature_ChangesWhenToolSetChanges()
    {
        var toolA = AIFunctionFactory.Create((string id) => id, "alpha", "a");
        var toolB = AIFunctionFactory.Create((string id) => id, "beta", "b");

        var withOne = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions { Tools = [toolA] });
        var withTwo = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions { Tools = [toolA, toolB] });

        Assert.NotEqual(withOne, withTwo);
    }

    [Fact]
    public void ComputeSessionSignature_ChangesWhenInstructionsChange()
    {
        var first = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions { Instructions = "one" });
        var second = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions { Instructions = "two" });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeSessionSignature_ChangesWhenReasoningEffortChanges()
    {
        var low = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
        });
        var high = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
        });

        Assert.NotEqual(low, high);
    }

    [Fact]
    public void ComputeSessionSignature_TreatsNullAndEmptyOptionsAsEquivalent()
    {
        var fromNull = CopilotSdkChatClient.ComputeSessionSignature(null);
        var fromEmpty = CopilotSdkChatClient.ComputeSessionSignature(new ChatOptions());

        Assert.Equal(fromNull, fromEmpty);
    }
}
