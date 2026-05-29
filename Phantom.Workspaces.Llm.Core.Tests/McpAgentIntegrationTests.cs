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

    private static PromptAgent CreateMcpAgentDefinition(string endpoint)
    {
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
                {
                  "kind": "mcp",
                  "name": "test-mcp",
                  "serverName": "test-mcp",
                  "connection": {
                    "kind": "Anonymous",
                    "endpoint": "{{endpoint}}"
                  }
                }
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

            await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Timed out waiting for ping MCP tool. Recent history: {GetRecentHistory(chat)}",
                ex);
        }
        finally
        {
            chat.ToolsChanged -= OnToolsChanged;
        }
    }

    private static bool HasPingTool(AgentChat chat)
        => FlattenTools(chat.Tools).Any(tool => string.Equals(tool.Name, "ping", StringComparison.OrdinalIgnoreCase));

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
                    snapshot.TakeLast(5).Select(item => $"{item.Role}:{item.Text}"));
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(20);
            }
        }

        return "(history unavailable)";
    }

}
