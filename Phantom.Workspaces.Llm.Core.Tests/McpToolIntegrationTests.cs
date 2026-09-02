using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Collections.Specialized;

namespace Phantom.Workspaces.Llm.Core.Tests;

[Trait("Category", "Integration")]
public sealed class McpToolIntegrationTests
{
    [Fact]
    public async Task McpClient_WithStdioTransport_CanListAndCallPing()
    {
        var executablePath = TestMcpServerProcess.GetMcpExecutablePath();
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "test-mcp-stdio",
                Command = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath),
                Arguments =
                [
                    "--mode",
                    "stdio",
                ],
            });

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, tool => string.Equals(tool.Name, "ping", StringComparison.OrdinalIgnoreCase));

        var callResult = await client.CallToolAsync(
            "ping",
            new Dictionary<string, object?> { ["message"] = "integration" },
            cancellationToken: CancellationToken.None);
        var content = Assert.Single(callResult.Content.OfType<TextContentBlock>());
        Assert.Equal("pong:integration", content.Text);
    }

    [Fact]
    public async Task McpClient_WithHttpTransport_CanListAndCallPing()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "test-mcp-http",
                Endpoint = new Uri(server.BoundUrl, UriKind.Absolute),
            });

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, tool => string.Equals(tool.Name, "ping", StringComparison.OrdinalIgnoreCase));

        var callResult = await client.CallToolAsync(
            "ping",
            new Dictionary<string, object?> { ["message"] = "integration" },
            cancellationToken: CancellationToken.None);
        var content = Assert.Single(callResult.Content.OfType<TextContentBlock>());
        Assert.Equal("pong:integration", content.Text);
    }

    [Fact]
    public async Task InitializeMcpTools_WhileMcpServerLoads_ShowsLoadingMcpServerRunningItem()
    {
        await using var server = await TestMcpServerProcess.StartAsync();

        var seenRunningTexts = new List<string>();
        await using var chat = await CreateMcpChatAsync(
            ("test-mcp", server.BoundUrl.ToString()),
            recordRunningText: seenRunningTexts);

        Assert.Contains(seenRunningTexts, text => text == "Loading mcp server test-mcp");
    }

    [Fact]
    public async Task InitializeMcpTools_WhenMcpServerLoads_RunningItemClearsOnCompletion()
    {
        await using var server = await TestMcpServerProcess.StartAsync();

        await using var chat = await CreateMcpChatAsync(("test-mcp", server.BoundUrl.ToString()));

        Assert.Empty(chat.RunningItems);
    }

    [Fact]
    public async Task InitializeMcpTools_OnSuccess_AddsToolListingDiagnosticToHistory()
    {
        await using var server = await TestMcpServerProcess.StartAsync();

        await using var chat = await CreateMcpChatAsync(("test-mcp", server.BoundUrl.ToString()));

        Assert.Contains(
            chat.History,
            item => DiagnosticText(item).Contains("Opened MCP server 'test-mcp'. Loaded tools", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitializeMcpTools_WhenServerLoadThrows_UnpersistedHistoryContainsExceptionAndFailedStep()
    {
        // A refused endpoint makes the MCP server load throw; the per-step catch records the
        // exception and the failed step (server) name into unpersisted history, rather than a
        // generic "Agent startup failed" summary (issue #1072).
        await using var chat = await CreateMcpChatAsync(("bad-mcp", "http://127.0.0.1:1"));

        var diagnostics = chat.History.Select(DiagnosticText).ToArray();
        Assert.Contains(diagnostics, text =>
            text.Contains("Failed to open MCP server 'bad-mcp'", StringComparison.Ordinal)
            && text.Contains("Exception", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, text => text.Contains("Agent startup failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentChat_WithMcpTool_NonCopilotProvider_ExposesMcpTools()
    {
        // The DeterministicTestChatClient stands in for a non-Copilot provider: the MCP tool must
        // still reach ChatOptions.Tools, confirming the #1395 fix is provider-agnostic (the tools
        // now flow through AIContextProviders before any provider-specific forwarding).
        await using var server = await TestMcpServerProcess.StartAsync();
        var client = new DeterministicTestChatClient();
        var agentJson = $$"""
            {
              "kind": "prompt",
              "name": "mcp-non-copilot",
              "model": { "id": "test", "provider": "echo", "apiType": "Echo" },
              "tools": [
                {
                  "kind": "mcp",
                  "name": "test-mcp",
                  "serverName": "test-mcp",
                  "connection": { "kind": "Anonymous", "endpoint": "{{server.BoundUrl}}" }
                }
              ]
            }
            """;

        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(agentJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client,
            DisplayNameOverride = "test-mcp",
        });

        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        chat.EnqueueUserMessage("hello");
        await client.WaitForRequestAsync(requestTimeout.Token);

        var toolNames = client.LastRequestOptions?.Tools?
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray()
            ?? [];

        Assert.Contains(toolNames, name => string.Equals(name, "ping", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<AgentChat> CreateMcpChatAsync(
        (string ServerName, string Endpoint) server,
        List<string>? recordRunningText = null)
    {
        var agentJson = $$"""
            {
              "kind": "prompt",
              "name": "mcp-running-item-integration",
              "model": { "id": "test", "provider": "echo", "apiType": "Echo" },
              "tools": [
                {
                  "kind": "mcp",
                  "name": "{{server.ServerName}}",
                  "serverName": "{{server.ServerName}}",
                  "connection": { "kind": "Anonymous", "endpoint": "{{server.Endpoint}}" }
                }
              ]
            }
            """;

        var request = new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(agentJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = server.ServerName,
        };

        void Capture(AgentChat chat)
        {
            if (recordRunningText is null)
            {
                return;
            }

            ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += (_, e) =>
            {
                if (e.NewItems is null)
                {
                    return;
                }

                foreach (AgentChatRunningItem item in e.NewItems)
                {
                    var text = item.Items.Count > 0 ? DiagnosticText(item.Items[0]) : string.Empty;
                    lock (recordRunningText)
                    {
                        recordRunningText.Add(text);
                    }
                }
            };
        }

        return await AgentChat.CreateAsync(request, Capture);
    }

    private static string DiagnosticText(AgentChatHistoryItem item)
        => string.Concat(item.Contents.Select(static content => content switch
        {
            TextContent text => text.Text,
            ErrorContent error => error.Message,
            _ => string.Empty,
        }));

}
