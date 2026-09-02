using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using System.Reflection;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Exercises the fix for issue #1395: MCP server tools must be registered as first-class runtime
/// context providers so they land in <c>chatOptions.AIContextProviders</c> and reach the model,
/// exactly like <c>CustomTool</c> toolsets. These tests drive a real (loopback) test MCP server, so
/// they are tagged Integration and excluded from the default fast suite.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentChatMcpToolExposureTests
{
    [Fact]
    public async Task AgentChat_WithMcpTool_RegistersMcpProviderInAIContextProviders()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var (chat, _) = await CreateChatAsync(BuildMcpAgentJson(("test-mcp", server.BoundUrl.ToString())));
        await using var _chat = chat;

        var providers = GetAIContextProviders(chat);
        Assert.NotEmpty(providers);

        var exposed = await EnumerateProvidersAsync(providers);
        Assert.Contains(exposed, name => string.Equals(name, "ping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AgentChat_WithMcpTool_ExposesMcpToolsToChatOptions()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var (chat, client) = await CreateChatAsync(BuildMcpAgentJson(("test-mcp", server.BoundUrl.ToString())));
        await using var _chat = chat;

        var toolNames = await ExposedToolNamesAsync(chat, client);

        Assert.Contains(toolNames, name => string.Equals(name, "ping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AgentChat_WithMcpTool_LoadedToolsDiagnosticMatchesExposedTools()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var (chat, client) = await CreateChatAsync(BuildMcpAgentJson(("test-mcp", server.BoundUrl.ToString())));
        await using var _chat = chat;

        var diagnosticTools = ParseLoadedToolsDiagnostic(chat);
        Assert.NotEmpty(diagnosticTools);

        // With no custom tools configured, every exposed tool is an MCP tool.
        var exposedTools = await ExposedToolNamesAsync(chat, client);

        Assert.Equal(
            new SortedSet<string>(diagnosticTools, StringComparer.OrdinalIgnoreCase),
            new SortedSet<string>(exposedTools, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AgentChat_WithMcpTool_ReusesSingleProviderInstanceForTreeAndExposure()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var (chat, _) = await CreateChatAsync(BuildMcpAgentJson(("test-mcp", server.BoundUrl.ToString())));
        await using var _chat = chat;

        var registrationProviders = GetRegistrationProviders(chat)
            .OfType<McpToolContextProvider>()
            .ToArray();
        var registrationProvider = Assert.Single(registrationProviders);

        var wrappedProviders = GetAIContextProviders(chat)
            .Select(GetWrappedProvider)
            .OfType<McpToolContextProvider>()
            .ToArray();
        var wrappedProvider = Assert.Single(wrappedProviders);

        // The instance that fed the UI tree/diagnostic (the registration provider) must be the very
        // same instance wired into AIContextProviders — no duplicate MCP connection.
        Assert.Same(registrationProvider, wrappedProvider);
    }

    [Fact]
    public async Task AgentChat_WithDisabledMcpTool_ExcludesItFromChatOptions()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var (chat, client) = await CreateChatAsync(BuildMcpAgentJson(("test-mcp", server.BoundUrl.ToString())));
        await using var _chat = chat;

        var pingNode = chat.Tools
            .SelectMany(root => root.Children)
            .Single(child => string.Equals(child.Name, "ping", StringComparison.OrdinalIgnoreCase));
        await chat.SetToolEnabledAsync(pingNode.Id, enabled: false);

        var toolNames = await ExposedToolNamesAsync(chat, client);

        Assert.DoesNotContain(toolNames, name => string.Equals(name, "ping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AgentChat_WithMcpAndCustomTools_ExposesBoth()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var agentJson = $$"""
            {
              "kind": "prompt",
              "name": "mcp-and-custom",
              "model": { "id": "test", "provider": "echo", "apiType": "Echo" },
              "tools": [
                { "kind": "web_search", "description": "Search docs" },
                {
                  "kind": "mcp",
                  "name": "test-mcp",
                  "serverName": "test-mcp",
                  "connection": { "kind": "Anonymous", "endpoint": "{{server.BoundUrl}}" }
                }
              ]
            }
            """;
        var (chat, client) = await CreateChatAsync(agentJson);
        await using var _chat = chat;

        var toolNames = await ExposedToolNamesAsync(chat, client);

        Assert.Contains(toolNames, name => string.Equals(name, "web_search", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(toolNames, name => string.Equals(name, "ping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AgentChat_WithFailedMcpServer_DoesNotRegisterProviderButKeepsDiagnostic()
    {
        // A refused endpoint makes the MCP server fail to open at enumeration time.
        var (chat, client) = await CreateChatAsync(BuildMcpAgentJson(("bad-mcp", "http://127.0.0.1:1")));
        await using var _chat = chat;

        var diagnostics = chat.History.Select(DiagnosticText).ToArray();
        Assert.Contains(diagnostics, text => text.Contains("Failed to open MCP server 'bad-mcp'", StringComparison.Ordinal));

        // The failed server must expose no tools to the model, and the turn must still complete
        // (the resilient ToolFilteringAIContextProvider degrades gracefully rather than throwing).
        var toolNames = await ExposedToolNamesAsync(chat, client);
        Assert.DoesNotContain(toolNames, name => string.Equals(name, "ping", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildMcpAgentJson((string ServerName, string Endpoint) server)
        => $$"""
            {
              "kind": "prompt",
              "name": "mcp-exposure",
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

    private static async Task<(AgentChat Chat, DeterministicTestChatClient Client)> CreateChatAsync(string agentJson)
    {
        var client = new DeterministicTestChatClient();
        var request = new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(agentJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client,
            DisplayNameOverride = "mcp-exposure",
        };
        var chat = await AgentChat.CreateAsync(request);
        return (chat, client);
    }

    private static async Task<string[]> ExposedToolNamesAsync(AgentChat chat, DeterministicTestChatClient client)
    {
        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        chat.EnqueueUserMessage("hello");
        await client.WaitForRequestAsync(requestTimeout.Token);

        return client.LastRequestOptions?.Tools?
            .Select(static tool => tool.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray()
            ?? [];
    }

    private static IReadOnlyList<AIContextProvider> GetAIContextProviders(AgentChat chat)
    {
        var chatOptions = typeof(AgentChat)
            .GetField("chatOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(chat);
        Assert.NotNull(chatOptions);
        var providers = (IEnumerable<AIContextProvider>?)chatOptions!.GetType()
            .GetProperty("AIContextProviders")!
            .GetValue(chatOptions);
        return providers?.ToArray() ?? [];
    }

    private static IReadOnlyList<AIContextProvider?> GetRegistrationProviders(AgentChat chat)
    {
        var registrations = (System.Collections.IEnumerable)typeof(AgentChat)
            .GetField("runtimeContextProviderRegistrations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(chat)!;

        var providers = new List<AIContextProvider?>();
        foreach (var registration in registrations)
        {
            var provider = registration.GetType()
                .GetProperty("Provider")!
                .GetValue(registration);
            providers.Add(provider as AIContextProvider);
        }

        return providers;
    }

    private static AIContextProvider? GetWrappedProvider(AIContextProvider provider)
    {
        if (provider is not ToolFilteringAIContextProvider)
        {
            return provider;
        }

        var inner = typeof(ToolFilteringAIContextProvider)
            .GetField("provider", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(provider);
        return inner as AIContextProvider;
    }

    private static async Task<string[]> EnumerateProvidersAsync(IReadOnlyList<AIContextProvider> providers)
    {
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);

        var names = new List<string>();
        foreach (var provider in providers)
        {
            var tools = await AIContextProviderToolReader.GetToolsAsync(provider, agent, session, CancellationToken.None);
            names.AddRange(tools.Select(tool => tool.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
        }

        return names.ToArray();
    }

    private static string[] ParseLoadedToolsDiagnostic(AgentChat chat)
    {
        var diagnostic = chat.History
            .Select(DiagnosticText)
            .FirstOrDefault(text => text.Contains("Loaded tools:", StringComparison.Ordinal)
                && text.Contains("Opened MCP server", StringComparison.Ordinal))
            ?? string.Empty;

        return diagnostic
            .Split('\n', '\r')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..].Trim())
            .Where(name => name.Length > 0)
            .ToArray();
    }

    private static string DiagnosticText(AgentChatHistoryItem item)
        => string.Concat(item.Contents.Select(static content => content switch
        {
            TextContent text => text.Text,
            ErrorContent error => error.Message,
            _ => string.Empty,
        }));
}
