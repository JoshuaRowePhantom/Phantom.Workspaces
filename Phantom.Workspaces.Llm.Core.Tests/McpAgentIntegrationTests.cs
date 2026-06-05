using AgentSchema;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class McpAgentIntegrationTests
{
    [Fact]
    public async Task CreateAgentChat_WithStdioMcpServer_CanInvokePingTool()
    {
        var endpoint = BuildStdioEndpoint();
        var agent = CreateMcpAgentDefinition(endpoint);
        Assert.IsType<McpTool>(Assert.Single(agent.Tools ?? []));

        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = agent,
            });

        await WaitForPingToolAsync(chat);
        Assert.True(HasPingTool(chat));
    }

    [Fact]
    public async Task CreateAgentChat_WithTwoHttpMcpServers_InitializesInParallel()
    {
        var barrier = new AsyncBarrier(2);
        await using var firstServer = await InProcessMcpServer.StartAsync(barrier);
        await using var secondServer = await InProcessMcpServer.StartAsync(barrier);
        var agent = CreateMcpAgentDefinition(
            ("slow-one", firstServer.BoundUrl),
            ("slow-two", secondServer.BoundUrl));

        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = agent,
            });

        await WaitForPingToolAsync(chat);
        Assert.True(HasPingTool(chat));
        var rootTools = chat.Tools.Where(tool => tool.Kind == "mcp").ToArray();
        Assert.Contains(rootTools, tool => string.Equals(tool.Name, "slow-one", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rootTools, tool => string.Equals(tool.Name, "slow-two", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateAgentChat_WithHttpMcpServer_CanInvokePingTool()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var agent = CreateMcpAgentDefinition(server.BoundUrl);
        Assert.IsType<McpTool>(Assert.Single(agent.Tools ?? []));

        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = agent,
            });

        await WaitForPingToolAsync(chat);
        Assert.True(HasPingTool(chat));
    }

    [Fact]
    public async Task CreateAgentChat_WithOneFailedMcpServer_StillLoadsOtherTools()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var agent = CreateMcpAgentDefinition(
            ("good-server", server.BoundUrl),
            ("bad-server", "http://127.0.0.1:1"));

        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = agent,
            });

        await WaitForPingToolAsync(chat);
        Assert.True(HasPingTool(chat));

        var failedServer = FindToolByName(chat.Tools, "bad-server");
        Assert.NotNull(failedServer);
        Assert.False(failedServer!.IsEnabled);
        Assert.Contains("Failed", failedServer.Status ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static PromptAgent CreateMcpAgentDefinition(string endpoint)
        => CreateMcpAgentDefinition(("test-mcp", endpoint));

    private static PromptAgent CreateMcpAgentDefinition(params (string ServerName, string Endpoint)[] servers)
    {
        var tools = string.Join(
            ",\n                ",
            servers.Select(server => $$"""
                 {
                   "kind": "mcp",
                   "name": "{{server.ServerName}}",
                   "serverName": "{{server.ServerName}}",
                   "connection": {
                     "kind": "Anonymous",
                     "endpoint": "{{server.Endpoint}}"
                   }
                 }
                """));

        var agentJson = $$"""
            {
              "kind": "prompt",
              "name": "mcp-agent-integration",
              "model": {
                "id": "test",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": [
                {{tools}}
              ]
            }
            """;

        return Assert.IsType<PromptAgent>(AgentDefinitionLoader.LoadAgentFromJson(agentJson));
    }

    private static string BuildStdioEndpoint()
    {
        var executablePath = TestMcpServerProcess.GetMcpExecutablePath();
        var executableDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("MCP executable directory could not be determined.");
        var args = new[]
        {
            "--mode",
            "stdio",
        };

        var queryParts = new List<string>
        {
            $"command={Uri.EscapeDataString(executablePath)}",
            $"cwd={Uri.EscapeDataString(executableDirectory)}",
        };
        queryParts.AddRange(args.Select(arg => $"arg={Uri.EscapeDataString(arg)}"));
        return $"stdio://local?{string.Join("&", queryParts)}";
    }

    private static async Task WaitForPingToolAsync(AgentChat chat)
    {
        if (HasPingTool(chat))
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnToolsChanged(object? _, EventArgs __)
        {
            if (HasPingTool(chat))
            {
                signal.TrySetResult();
            }
        }

        chat.ToolsChanged += OnToolsChanged;
        try
        {
            if (HasPingTool(chat))
            {
                return;
            }

            await signal.Task;
        }
        finally
        {
            chat.ToolsChanged -= OnToolsChanged;
        }
    }

    private static bool HasPingTool(AgentChat chat)
        => FlattenTools(chat.Tools).Any(tool => string.Equals(tool.Name, "ping", StringComparison.OrdinalIgnoreCase));

    private static AgentChatToolItem? FindToolByName(IEnumerable<AgentChatToolItem> roots, string name)
        => FlattenTools(roots).FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<AgentChatToolItem> FlattenTools(IEnumerable<AgentChatToolItem> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in FlattenTools(root.Children))
            {
                yield return child;
            }
        }
    }

    private static string GetRecentHistory(AgentChat chat)
    {
        for (var retry = 0; retry < 5; retry++)
        {
            try
            {
                var snapshot = chat.History.ToArray();
                if (snapshot.Length == 0)
                {
                    return "(history empty)";
                }

                return string.Join(
                    " || ",
                    snapshot.TakeLast(5).Select(item => $"{item.Role}:{string.Concat(item.Contents.Where(static content => content is Microsoft.Extensions.AI.TextContent).Select(static content => ((Microsoft.Extensions.AI.TextContent)content).Text))}"));
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(20);
            }
        }

        return "(history unavailable)";
    }

}
