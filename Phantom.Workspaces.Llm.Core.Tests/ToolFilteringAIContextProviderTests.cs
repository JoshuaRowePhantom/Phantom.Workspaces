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

    [Fact]
    public async Task ToolFilteringAIContextProvider_DisabledServerNode_DoesNotInvokeInnerProvider()
    {
        var inner = new CountingContextProvider(
            AIFunctionFactory.Create(() => "search", "web_search"));
        var provider = new ToolFilteringAIContextProvider(
            inner,
            _ => true,
            isProviderEnabled: () => false);

        var tools = await GetToolsAsync(provider);

        // The server-level gate is closed: the inner provider is never invoked and no tools surface.
        Assert.Empty(tools);
        Assert.Equal(0, inner.Invocations);
    }

    [Fact]
    public async Task ToolFilteringAIContextProvider_EnabledServerNode_InvokesInnerAndFiltersByToolName()
    {
        var enabledTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web_search" };
        var inner = new CountingContextProvider(
            AIFunctionFactory.Create(() => "search", "web_search"),
            AIFunctionFactory.Create(() => "request", "web_request"));
        var provider = new ToolFilteringAIContextProvider(
            inner,
            tool => enabledTools.Contains(tool.Name),
            isProviderEnabled: () => true);

        var tools = await GetToolsAsync(provider);

        // The open server gate still invokes the inner provider and applies the per-tool name filter.
        Assert.Equal(1, inner.Invocations);
        Assert.Single(tools);
        Assert.Equal("web_search", tools[0].Name);
    }

    private sealed class CountingContextProvider : AIContextProvider
    {
        private readonly string stateKey = $"counting:{Guid.NewGuid():n}";
        private readonly AITool[] tools;

        public CountingContextProvider(params AITool[] tools)
            : base(null, null, null)
            => this.tools = tools;

        public int Invocations { get; private set; }

        public override IReadOnlyList<string> StateKeys => [this.stateKey];

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken)
        {
            this.Invocations++;
            return new ValueTask<AIContext>(new AIContext { Tools = this.tools });
        }
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
