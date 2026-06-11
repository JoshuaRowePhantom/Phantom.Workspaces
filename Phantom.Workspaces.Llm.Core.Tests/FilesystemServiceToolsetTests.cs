using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Echo;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class FilesystemServiceToolsetTests
{
    [Fact]
    public async Task ListToolsAsync_ReturnsFilesystemToolSet()
    {
        await using var toolset = new FilesystemServiceContextProvider();

        var tools = await GetToolsAsync(toolset);
        var toolNames = tools
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["describe_edit", "edit", "edit_apply", "make_directory", "move_item", "read", "remove_item", "search"],
            toolNames);
    }

    [Fact]
    public async Task ListToolsAsync_CanBeCalledMultipleTimes()
    {
        await using var toolset = new FilesystemServiceContextProvider();

        var firstTools = await GetToolsAsync(toolset);
        var secondTools = await GetToolsAsync(toolset);

        Assert.Equal(firstTools.Length, secondTools.Length);
        Assert.NotEmpty(firstTools);
    }

    [Fact]
    public void BuildOpenedToolsMessage_ListsToolNames()
    {
        var message = McpClientToolListing.BuildOpenedToolsMessage(
            "toolset",
            "filesystem",
            [new WebSearchTool(), new WebRequestTool()]);

        Assert.Contains("Opened toolset 'filesystem'.", message);
        Assert.Contains("- web_search", message);
        Assert.Contains("- web_request", message);
    }

    private static async Task<AITool[]> GetToolsAsync(FilesystemServiceContextProvider provider)
    {
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        return await AIContextProviderToolReader.GetToolsAsync(provider, agent, session, CancellationToken.None);
    }
}
