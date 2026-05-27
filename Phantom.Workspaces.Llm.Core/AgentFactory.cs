using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using OllamaSharp;
using OpenAI;
using Phantom.Workspaces.Llm.Echo;
using System.ComponentModel;
using System.ClientModel;
using System.Diagnostics;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Factory for creating Agent components from AgentSchema definitions.
/// Converts AgentSchema PromptAgent definitions to ChatClient configuration, model details, and tools.
/// Philosophy: configure all fields represented by the supported schema; if we need to
/// support more definition fields/shapes, expand schema+mapping together.
/// </summary>
public static class AgentFactory
{
    private const string GitHubModelsInferenceEndpoint = "https://models.github.ai/inference";

    /// <summary>
    /// Applies agent-definition-specific settings to chat options.
    /// </summary>
    /// <param name="agent">The agent definition to apply.</param>
    /// <param name="chatOptions">The chat options to update.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="chatOptions"/> is null.</exception>
    public static void ConfigureChatOptions(AgentDefinition agent, ChatOptions chatOptions)
    {
        ArgumentNullException.ThrowIfNull(chatOptions);
        chatOptions.AdditionalProperties ??= [];

        chatOptions.Reasoning = HasExplicitReasoningSetting(agent) ? new ReasoningOptions
        {
            Effort = ResolveReasoningEffort(agent),
        } : null;

        StoreAgentDefinition(agent, chatOptions);

        if (agent is not PromptAgent promptAgent)
        {
            return;
        }

        ConfigureChatOptions_Internal(promptAgent, chatOptions);
    }

    private static bool HasExplicitReasoningSetting(AgentDefinition agent)
    {
        if (agent is not PromptAgent promptAgent)
        {
            return false;
        }

        return promptAgent.Model?.Options?.AdditionalProperties?.ContainsKey("thinking") == true;
    }

    private static void ConfigureChatOptions_Internal(PromptAgent promptAgent, ChatOptions chatOptions)
    {
        chatOptions.Instructions = promptAgent.Instructions;

        if (!string.IsNullOrEmpty(promptAgent.AdditionalInstructions))
        {
            chatOptions.AdditionalProperties ??= [];
            chatOptions.AdditionalProperties["additionalInstructions"] = promptAgent.AdditionalInstructions;
        }
    }

    /// <summary>
    /// Stores an AgentSchema definition in ChatOptions additional properties for later reference.
    /// </summary>
    /// <param name="agent">The agent definition to store.</param>
    /// <param name="chatOptions">The ChatOptions to update with agent metadata.</param>
    public static void StoreAgentDefinition(AgentDefinition agent, ChatOptions chatOptions)
    {
        chatOptions.AdditionalProperties ??= [];
        chatOptions.AdditionalProperties["agent_definition"] = agent;
    }

    /// <summary>
    /// Extracts tool definitions from a PromptAgent for registration with ChatClient.
    /// </summary>
    /// <param name="agent">The PromptAgent to extract tools from.</param>
    /// <returns>List of tools defined in the agent, or empty when tools are not applicable.</returns>
    public static IList<Tool>? ExtractTools(AgentDefinition agent)
    {
        return (agent as PromptAgent)?.Tools ?? [];
    }

    /// <summary>
    /// Gets the system instructions from a PromptAgent.
    /// </summary>
    /// <param name="agent">The PromptAgent to extract instructions from.</param>
    /// <returns>The system instructions, or empty string if not defined/applicable.</returns>
    public static string GetSystemInstructions(AgentDefinition agent)
    {
        return (agent as PromptAgent)?.Instructions ?? string.Empty;
    }

    /// <summary>
    /// Gets the model ID from a PromptAgent.
    /// </summary>
    /// <param name="agent">The PromptAgent to extract the model from.</param>
    /// <returns>The model ID, or null if not defined/applicable.</returns>
    public static string? GetModelId(AgentDefinition agent)
    {
        return (agent as PromptAgent)?.Model?.Id;
    }

    /// <summary>
    /// Gets the model definition from a PromptAgent.
    /// </summary>
    /// <param name="agent">The PromptAgent to extract the model from.</param>
    /// <returns>The model definition containing ID, provider, API type, connection, and options, if available.</returns>
    public static Model? GetModel(AgentDefinition agent)
    {
        return (agent as PromptAgent)?.Model;
    }

    /// <summary>
    /// Creates a ChatClient from an AgentDefinition, resolving the provider and connection.
    /// </summary>
    /// <param name="agent">The agent definition to create a client from.</param>
    /// <returns>A tuple of (ChatClient, display name).</returns>
    /// <exception cref="InvalidOperationException">If the agent is invalid or provider is unsupported.</exception>
    public static (IChatClient client, string displayName) CreateChatClient(AgentDefinition agent)
        => CreateChatClient(agent, services: null);

    /// <summary>
    /// Creates a ChatClient from an AgentDefinition, resolving provider and optional service integrations.
    /// </summary>
    /// <param name="agent">The agent definition to create a client from.</param>
    /// <param name="services">Optional service integrations for runtime behavior.</param>
    /// <returns>A tuple of (ChatClient, display name).</returns>
    public static (IChatClient client, string displayName) CreateChatClient(
        AgentDefinition agent,
        AgentServices? services)
    {
        var model = GetModel(agent);
        if (model is null || string.IsNullOrEmpty(model.Id))
        {
            throw new InvalidOperationException("Agent definition does not specify a model ID.");
        }

        if (string.Equals(model.Id, "test", StringComparison.OrdinalIgnoreCase))
        {
            return (new TestProviderChatClient(), "Test Chat Client");
        }

        var provider = model.Provider?.ToLowerInvariant() ?? "unknown";

        return provider switch
        {
            "echo" => (new EchoChatClient(), "Echo Chat Client"),
            "github" => CreateGitHubModelsClient(model),
            "ollama" => CreateOllamaClient(model, services),
            "openai" => throw new NotImplementedException("OpenAI provider resolution not yet implemented."),
            "azure" => throw new NotImplementedException("Azure provider resolution not yet implemented."),
            _ => throw new InvalidOperationException(
                $"Unknown or unsupported provider: {provider}. Supported: echo, test, github, ollama, openai, azure")
        };
    }

    /// <summary>
    /// Creates a synchronously-initialized <see cref="AgentChat"/> session from an agent definition.
    /// This returns immediately without waiting for MCP tool setup. Use <see cref="CreateAgentChatAsync"/>
    /// to include MCP tool initialization.
    /// </summary>
    /// <param name="agent">Agent definition to materialize.</param>
    /// <param name="services">Optional service integrations for runtime behavior.</param>
    /// <returns>The running <see cref="AgentChat"/>.</returns>
    public static AgentChat CreateAgentChat(
        AgentDefinition agent,
        AgentServices? services = null)
    {
        ValidateServices(services);

        var configuredChatHistoryProvider = services?.ChatHistoryProvider ?? new InMemoryChatHistoryProvider();
        var chatHistoryProvider = new AgentFrameworkChatHistoryProvider(configuredChatHistoryProvider);
        var chatOptions = new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions(),
            ChatHistoryProvider = chatHistoryProvider,
            //RequirePerServiceCallChatHistoryPersistence = true,
        };
        ConfigureChatOptions(agent, chatOptions.ChatOptions);

        var clientInfo = CreateChatClient(agent, services);
        var client = clientInfo.client;
        if (services?.LogChat == true)
        {
            client = client.AsBuilder().UseLogging(services.LoggerFactory).Build();
        }

        var chatClientAgent = new ChatClientAgent(client, chatOptions);
        var session = new AgentChatSession(
            chatClientAgent,
            chatClientAgent.CreateSessionAsync(CancellationToken.None).GetAwaiter().GetResult());
        var queueManager = new AgentInputQueueManager();
        var chat = new AgentChat(
            session,
            queueManager,
            chatHistoryProvider,
            clientInfo.displayName);
        InitializeMcpToolsAsync(agent, client, chat, chatOptions, services, CancellationToken.None);

        return chat;
    }

    /// <summary>
    /// Creates a fully wired <see cref="AgentChat"/> session from an agent definition.
    /// This async overload resolves MCP tool transports and registers discovered tools.
    /// </summary>
    /// <param name="agent">Agent definition to materialize.</param>
    /// <param name="services">Optional service integrations for runtime behavior.</param>
    /// <param name="cancellationToken">Cancellation token for async MCP initialization.</param>
    /// <returns>The running <see cref="AgentChat"/>.</returns>
    public static Task<AgentChat> CreateAgentChatAsync(
        AgentDefinition agent,
        AgentServices? services = null,
        CancellationToken cancellationToken = default)
    {
        // Chat creation is synchronous; MCP tool setup continues in the background.
        var chat = CreateAgentChat(agent, services);
        return Task.FromResult(chat);
    }

    /// <summary>
    /// Initializes MCP tools asynchronously on an existing <see cref="AgentChat"/> instance.
    /// This is called after the chat has been created synchronously to avoid blocking the UI.
    /// </summary>
    private static void InitializeMcpToolsAsync(
        AgentDefinition agent,
        IChatClient client,
        AgentChat chat,
        ChatClientAgentOptions chatOptions,
        AgentServices? services = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatOptions);
        ArgumentNullException.ThrowIfNull(chatOptions.ChatOptions);
        
        var agentTools = ExtractTools(agent);
        var hasMcpTools = agentTools?.OfType<McpTool>().Any() == true;
        if (!hasMcpTools)
        {
            return;
        }

        chat.QueueManager.SetQueueHeld(chat.DefaultInputQueue, held: true);
        var startupRunningItem = chat.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents = new AIContent[] { new TextContent("Initializing tools...") },
        });
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var runtimeTools = await CreateRuntimeToolsAsync(
                        agentTools,
                        services,
                        text => chat.UpdateRunningItem(startupRunningItem, [new AgentChatHistoryItem
                        {
                            Role = AgentChatHistoryItem.DiagnosticChatRole,
                            Contents = new AIContent[] { new TextContent(text) },
                        }]),
                        resource => chat.RegisterOwnedResource(resource),
                        cancellationToken);

                    chatOptions.ChatOptions.Tools = runtimeTools;
                    var rebuiltAgent = new ChatClientAgent(client, chatOptions);
                    var session = await rebuiltAgent.CreateSessionAsync(cancellationToken);
                    chat.ResetSession(new AgentChatSession(rebuiltAgent, session), interruptCurrentResponse: false);
                    chat.UpdateRunningItem(startupRunningItem, [new AgentChatHistoryItem
                    {
                        Role = AgentChatHistoryItem.DiagnosticChatRole,
                        Contents = new AIContent[] { new TextContent(BuildStartupReadyMessage(runtimeTools)) },
                    }]);
                    chat.CompleteRunningItem(startupRunningItem, true);
                }
                catch (Exception ex)
                {
                    chat.UpdateRunningItem(startupRunningItem, [new AgentChatHistoryItem
                    {
                        Role = AgentChatHistoryItem.DiagnosticChatRole,
                        Contents = new AIContent[] { new ErrorContent($"Agent startup failed: {ex.Message}") },
                    }]);
                    chat.CompleteRunningItem(startupRunningItem, true);
                }
                finally
                {
                    chat.QueueManager.SetQueueHeld(chat.DefaultInputQueue, held: false);
                }
            },
            cancellationToken);
    }

    private static ReasoningEffort ResolveReasoningEffort(AgentDefinition agent)
    {
        if (agent is not PromptAgent promptAgent || promptAgent.Model is null)
        {
            return ReasoningEffort.High;
        }

        var model = promptAgent.Model;
        var additionalProperties = model.Options?.AdditionalProperties;
        if (additionalProperties is null)
        {
            return ReasoningEffort.High;
        }

        if (!additionalProperties.TryGetValue("thinking", out var thinkingValue) || thinkingValue is null)
        {
            return ReasoningEffort.High;
        }

        return thinkingValue switch
        {
            bool b => b ? ReasoningEffort.High : ReasoningEffort.None,
            string s => ParseReasoningEffort(s),
            System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.True => ReasoningEffort.High,
            System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.False => ReasoningEffort.None,
            System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.String => ParseReasoningEffort(jsonElement.GetString() ?? "high"),
            _ => ReasoningEffort.High,
        };
    }

    private static ReasoningEffort ParseReasoningEffort(string value) => value.ToLowerInvariant() switch
    {
        "true" or "on" or "high" => ReasoningEffort.High,
        "medium" or "med" => ReasoningEffort.Medium,
        "low" => ReasoningEffort.Low,
        "false" or "off" or "none" => ReasoningEffort.None,
        _ => ReasoningEffort.High,
    };

    private static (IChatClient client, string displayName) CreateOllamaClient(Model model, AgentServices? services)
    {
        var connection = model.Connection as AgentSchema.AnonymousConnection
            ?? throw new InvalidOperationException("Ollama model requires an AnonymousConnection.");

        var endpoint = connection.Endpoint
            ?? throw new InvalidOperationException("Ollama connection requires an endpoint URL.");

        var modelId = model.Id ?? "mistral";

        try
        {
            IChatClient client;
            if (services?.LogHttpRequests == true)
            {
                var logger = services.LoggerFactory!.CreateLogger<HttpRequestLoggingHandler>();
                var handler = new HttpRequestLoggingHandler(logger)
                {
                    InnerHandler = new HttpClientHandler(),
                };
                var httpClient = new HttpClient(handler)
                {
                    BaseAddress = new Uri(endpoint),
                };
                client = new OllamaApiClient(httpClient, modelId, jsonSerializerContext: null);
            }

            else
            {
                client = new OllamaApiClient(new Uri(endpoint), modelId);
            }

            var displayName = $"Ollama ({modelId} at {endpoint})";
            return (client, displayName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to create Ollama client for model '{modelId}' at '{endpoint}': {ex.Message}", ex);
        }
    }

    private static (IChatClient client, string displayName) CreateGitHubModelsClient(Model model)
    {
        var connection = model.Connection as ApiKeyConnection
            ?? throw new InvalidOperationException("GitHub provider requires an ApiKeyConnection.");

        var endpoint = string.IsNullOrWhiteSpace(connection.Endpoint)
            ? GitHubModelsInferenceEndpoint
            : connection.Endpoint;

        var apiKey = ResolveApiKey(connection.ApiKey, "github-models");
        var modelId = model.Id
            ?? throw new InvalidOperationException("GitHub provider requires a model id.");

        try
        {
            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(endpoint, UriKind.Absolute),
                });

            IChatClient client = openAiClient.GetChatClient(modelId).AsIChatClient();
            var displayName = $"GitHub Models ({modelId} at {endpoint})";
            return (client, displayName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to create GitHub Models client for model '{modelId}' at '{endpoint}': {ex.Message}",
                ex);
        }
    }

    private static void ValidateServices(AgentServices? services)
    {
        if ((services?.LogChat == true || services?.LogHttpRequests == true) && services.LoggerFactory is null)
        {
            throw new InvalidOperationException(
                "AgentServices.LoggerFactory is required when AgentServices.LogChat or AgentServices.LogHttpRequests is enabled.");
        }
    }

    private static async Task<List<AITool>> CreateRuntimeToolsAsync(
        IList<Tool>? agentTools,
        AgentServices? services,
        Action<string>? progressCallback,
        Action<IAsyncDisposable>? resourceCallback,
        CancellationToken cancellationToken)
    {
        var resolvedTools = new List<AITool>();
        if (agentTools is null || agentTools.Count == 0)
        {
            return resolvedTools;
        }

        foreach (var tool in agentTools)
        {
            switch (tool)
            {
                case McpTool mcpTool:
                {
                    var toolServerName = string.IsNullOrWhiteSpace(mcpTool.ServerName) ? mcpTool.Name : mcpTool.ServerName;
                    progressCallback?.Invoke($"Opening MCP server '{toolServerName}'...");

                    var transport = CreateMcpTransport(mcpTool, services);
                    var client = await McpClient.CreateAsync(
                        transport,
                        null,
                        services?.LoggerFactory,
                        cancellationToken);
                    resourceCallback?.Invoke(client);

                    var mcpTools = await client.ListToolsAsync(options: null, cancellationToken);
                    var allowed = mcpTool.AllowedTools;
                    if (allowed is { Count: > 0 })
                    {
                        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
                        mcpTools = [.. mcpTools.Where(t => allowedSet.Contains(t.Name))];
                    }

                    resolvedTools.AddRange(mcpTools);
                    progressCallback?.Invoke($"Opened MCP server '{toolServerName}' ({mcpTools.Count} tools).");
                    break;
                }
                case CustomTool { Kind: "web_search" }:
                    resolvedTools.Add(new WebSearchTool(logger: services?.LoggerFactory?.CreateLogger<WebSearchTool>()));
                    break;
                case CustomTool { Kind: "web_request" }:
                    resolvedTools.Add(new WebRequestTool(logger: services?.LoggerFactory?.CreateLogger<WebRequestTool>()));
                    break;
            }
        }

        return resolvedTools;
    }

    private static string BuildStartupReadyMessage(IReadOnlyList<AITool> runtimeTools)
    {
        if (runtimeTools.Count == 0)
        {
            return "Agent ready. Loaded tools: (none).";
        }

        var toolNames = runtimeTools
            .Select(tool => tool.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (toolNames.Length == 0)
        {
            return "Agent ready. Loaded tools: (unnamed tools).";
        }

        return $"Agent ready. Loaded tools:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", toolNames)}";
    }

    private static IClientTransport CreateMcpTransport(
        McpTool tool,
        AgentServices? services)
    {
        return tool.Connection switch
        {
            AnonymousConnection anonymous => CreateHttpTransport(
                anonymous.Endpoint,
                apiKey: null,
                tool.ServerName,
                services?.LoggerFactory),
            ApiKeyConnection apiKey => CreateHttpTransport(
                apiKey.Endpoint,
                ResolveApiKey(apiKey.ApiKey, tool.ServerName),
                tool.ServerName,
                services?.LoggerFactory),
            null => throw new InvalidOperationException($"MCP tool '{tool.Name}' must define a connection."),
            _ => throw new InvalidOperationException(
                $"MCP tool '{tool.Name}' has unsupported connection type '{tool.Connection.GetType().Name}'."),
        };
    }

    private static IClientTransport CreateHttpTransport(
        string? endpoint,
        string? apiKey,
        string? serverName,
        ILoggerFactory? loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("MCP tool endpoint is required.");
        }

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint, UriKind.Absolute),
        };

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            transportOptions.Name = serverName;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            transportOptions.AdditionalHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {apiKey}",
            };
        }

        return new HttpClientTransport(transportOptions, loggerFactory);
    }

    private static string ResolveApiKey(
        string? apiKeyValue,
        string? serverName)
    {
        if (string.IsNullOrWhiteSpace(apiKeyValue))
        {
            throw new InvalidOperationException($"MCP tool '{serverName ?? "unknown"}' API key is required.");
        }

        var trimmed = apiKeyValue.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
        {
            var envVarName = trimmed[2..^1];
            if (string.IsNullOrWhiteSpace(envVarName))
            {
                throw new InvalidOperationException("MCP API key environment variable name cannot be empty.");
            }

            var envValue = Environment.GetEnvironmentVariable(envVarName);
            if (string.IsNullOrWhiteSpace(envValue))
            {
                if (string.Equals(envVarName, "GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase))
                {
                    var githubCliToken = ResolveGithubTokenFromCli();
                    if (!string.IsNullOrWhiteSpace(githubCliToken))
                    {
                        return githubCliToken;
                    }
                }

                throw new InvalidOperationException(
                    $"Environment variable '{envVarName}' for MCP tool '{serverName ?? "unknown"}' was not found or is empty.");
            }

            return envValue;
        }

        return trimmed;
    }

    private static string? ResolveGithubTokenFromCli()
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Win32Exception)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("Timed out while resolving GITHUB_TOKEN via 'gh auth token'.");
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            return process.StandardOutput.ReadToEnd().Trim();
        }
    }
}
