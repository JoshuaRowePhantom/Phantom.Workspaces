using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Echo;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class ToolFilteringAIContextProviderTests
{
    [Fact]
    public async Task FiltersToolsUsingEnabledPredicate()
    {
        var enabledTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "web_search",
        };
        var provider = new ToolFilteringAIContextProvider(
            ToolsetFactory.CreateFixedToolset(new WebSearchTool(), new WebRequestTool()),
            tool => enabledTools.Contains(tool.Name));

        var tools = await GetToolsAsync(provider);

        Assert.Single(tools);
        Assert.Equal("web_search", tools[0].Name);
    }

    [Fact]
    public async Task EvaluatesPredicateForEachInvocation()
    {
        var enabledTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "web_search",
        };
        var provider = new ToolFilteringAIContextProvider(
            ToolsetFactory.CreateFixedToolset(new WebSearchTool(), new WebRequestTool()),
            tool => enabledTools.Contains(tool.Name));

        var firstTools = await GetToolsAsync(provider);
        enabledTools.Add("web_request");
        var secondTools = await GetToolsAsync(provider);

        Assert.Single(firstTools);
        Assert.Equal(2, secondTools.Length);
    }

    private static async Task<AITool[]> GetToolsAsync(AIContextProvider provider)
    {
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        return await AIContextProviderToolReader.GetToolsAsync(provider, agent, session, CancellationToken.None);
    }
}
