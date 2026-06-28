using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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

}
