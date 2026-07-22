using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using MongoDB.Bson;
using OllamaSharp;
using OpenAI;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections;
using System.Collections.Generic;
using System.ClientModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Factory for creating Agent components from AgentSchema definitions.
/// Converts AgentSchema PromptAgent definitions to ChatClient configuration, model details, and tools.
/// Philosophy: configure all fields represented by the supported schema; if we need to
/// support more definition fields/shapes, expand schema+mapping together.
/// </summary>
/// <remarks>
/// When adding or changing a provider, model options, or connection kinds, update the workspace
/// documentation entities: <c>["documentation", "agent-options", "providers"]</c> and
/// <c>["documentation", "agent-options", "model-options"]</c>.
/// </remarks>
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
        ApplyModelOptions(promptAgent, chatOptions);

        if (!string.IsNullOrEmpty(promptAgent.AdditionalInstructions))
        {
            chatOptions.AdditionalProperties ??= [];
            chatOptions.AdditionalProperties["additionalInstructions"] = promptAgent.AdditionalInstructions;
        }
    }

    private static void ApplyModelOptions(PromptAgent promptAgent, ChatOptions chatOptions)
    {
        var modelOptions = promptAgent.Model?.Options;
        if (modelOptions is null)
        {
            return;
        }

        chatOptions.Temperature = modelOptions.Temperature;
        chatOptions.TopP = modelOptions.TopP;
        chatOptions.FrequencyPenalty = modelOptions.FrequencyPenalty;
        chatOptions.PresencePenalty = modelOptions.PresencePenalty;
        chatOptions.MaxOutputTokens = modelOptions.MaxOutputTokens;
        chatOptions.AdditionalProperties ??= [];

        if (modelOptions.TopK is not null)
        {
            chatOptions.AdditionalProperties["topK"] = modelOptions.TopK.Value;
        }

        if (modelOptions.AdditionalProperties is null)
        {
            return;
        }

        foreach (var (key, value) in modelOptions.AdditionalProperties)
        {
            chatOptions.AdditionalProperties[key] = value;
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
    /// Extracts the agent's tools and tags each one with the <see cref="Core.Transport.ExecutorTarget"/>
    /// execution class it must run in, based on the tool's <see cref="Tool.Kind"/>. Tagging happens at
    /// construction time (from the static tool kind), not at call time: <c>mcp</c>/<c>function</c> tools
    /// are tagged <see cref="Core.Transport.ExecutorTarget.AgentExecutor"/>, <c>workspace-gui</c>/
    /// <c>workspace-entity</c> tools <see cref="Core.Transport.ExecutorTarget.GuiLocal"/>, and
    /// <c>agent-session</c>/<c>workspace-agent-session</c> tools
    /// <see cref="Core.Transport.ExecutorTarget.HostingInstance"/>.
    /// </summary>
    /// <param name="agent">The agent definition to extract and tag tools from.</param>
    /// <returns>The agent's tools paired with their resolved execution class.</returns>
    public static IReadOnlyList<(Tool Tool, Core.Transport.ExecutorTarget Target)> ExtractToolExecutorTargets(
        AgentDefinition agent)
    {
        var tools = ExtractTools(agent) ?? [];
        return [.. tools.Select(tool => (tool, Core.Transport.ExecutorTargetResolver.ForTool(tool)))];
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
    public static ChatClientResult CreateChatClient(AgentDefinition agent)
        => CreateChatClient(agent, services: null, queueManager: null);

    /// <summary>
    /// Creates a ChatClient from an AgentDefinition, resolving provider and optional service integrations.
    /// </summary>
    /// <param name="agent">The agent definition to create a client from.</param>
    /// <param name="services">Optional service integrations for runtime behavior.</param>
    /// <param name="queueManager">
    /// Optional input-queue manager enabling tool-result steering. When supplied, non-self-invoking
    /// providers are wrapped with <see cref="ToolResultSteeringMiddleware"/> and the GitHub Copilot
    /// client receives it directly for its <c>QueueStateChanged</c> steering path.
    /// </param>
    /// <returns>The resolved chat client and its display name.</returns>
    public static ChatClientResult CreateChatClient(
        AgentDefinition agent,
        AgentServices? services,
        AgentInputQueueManager? queueManager = null,
        IApiKeyResolver? apiKeyResolver = null,
        ISubAgentChatRegistry? subAgentChatRegistry = null,
        SubAgentDispatcherDependencies? dispatcherDependencies = null)
    {
        var resolver = apiKeyResolver ?? EnvironmentApiKeyResolver.Instance;

        var model = (agent as PromptAgent)?.Model;
        if (model is null)
        {
            throw new InvalidOperationException("Agent definition does not specify a model.");
        }

        var provider = model.Provider?.ToLowerInvariant() ?? "unknown";

        // The sub-agent receiver provider must resolve before the model-ID validation: it mirrors
        // a CLI-hosted sub-agent whose model is chosen by the CLI, so its definition legitimately
        // carries no model ID (see CopilotSubAgentRouter.SubAgentDefinition). Requiring an
        // ID here made every real sub-agent creation throw, killing the session event dispatch
        // loop and silently dropping all further live output (issue #912).
        if (provider == "github-copilot-subagent")
        {
            return new ChatClientResult(new CopilotSubAgentChatClient(), "GitHub Copilot Sub-Agent");
        }

        if (string.IsNullOrEmpty(model.Id))
        {
            throw new InvalidOperationException("Agent definition does not specify a model ID.");
        }

        if (string.Equals(model.Id, "test", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatClientResult(new TestProviderChatClient(), "Test Chat Client");
        }

        return provider switch
        {
            "echo" => new ChatClientResult(new EchoChatClient(), "Echo Chat Client"),
            "github-models" => WrapWithMiddleware(CreateGitHubModelsClient(model, resolver), queueManager),
            "github-copilot" => CreateGitHubCopilotResult(model, services, queueManager, resolver, subAgentChatRegistry),
            "openai" or "azure-openai" => CreateGitHubCopilotByokResult(provider, model, services, queueManager, resolver, subAgentChatRegistry),
            "ollama" => WrapWithMiddleware(CreateOllamaClient(model, services), queueManager),
            "sub-agent-dispatcher" => CreateSubAgentDispatcherResult(services, dispatcherDependencies),
            _ => throw new InvalidOperationException(
                $"Unknown or unsupported provider: {provider}. Supported: echo, test, github-models, github-copilot, github-copilot-subagent, sub-agent-dispatcher, ollama, openai, azure-openai"),
        };
    }

    /// <summary>
    /// Asynchronously creates a ChatClient from an AgentDefinition, resolving provider and optional service integrations.
    /// </summary>
    /// <param name="agent">The agent definition to create a client from.</param>
    /// <param name="services">Optional service integrations for runtime behavior.</param>
    /// <param name="queueManager">
    /// Optional input-queue manager enabling tool-result steering. When supplied, non-self-invoking
    /// providers are wrapped with <see cref="ToolResultSteeringMiddleware"/> and the GitHub Copilot
    /// client receives it directly for its <c>QueueStateChanged</c> steering path.
    /// </param>
    /// <param name="apiKeyResolver">Optional API key resolver for test injection.</param>
    /// <param name="subAgentChatRegistry">Optional sub-agent chat registry.</param>
    /// <param name="cancellationToken">A token to cancel the client creation operation.</param>
    /// <returns>The resolved chat client and its display name.</returns>
    public static async Task<ChatClientResult> CreateChatClientAsync(
        AgentDefinition agent,
        AgentServices? services,
        AgentInputQueueManager? queueManager = null,
        IApiKeyResolver? apiKeyResolver = null,
        ISubAgentChatRegistry? subAgentChatRegistry = null,
        SubAgentDispatcherDependencies? dispatcherDependencies = null,
        CancellationToken cancellationToken = default)
    {
        var resolver = apiKeyResolver ?? EnvironmentApiKeyResolver.Instance;

        var model = (agent as PromptAgent)?.Model;
        if (model is null)
        {
            throw new InvalidOperationException("Agent definition does not specify a model.");
        }

        var provider = model.Provider?.ToLowerInvariant() ?? "unknown";

        if (provider == "github-copilot-subagent")
        {
            return new ChatClientResult(new CopilotSubAgentChatClient(), "GitHub Copilot Sub-Agent");
        }

        if (string.IsNullOrEmpty(model.Id))
        {
            throw new InvalidOperationException("Agent definition does not specify a model ID.");
        }

        if (string.Equals(model.Id, "test", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatClientResult(new TestProviderChatClient(), "Test Chat Client");
        }

        return provider switch
        {
            "echo" => new ChatClientResult(new EchoChatClient(), "Echo Chat Client"),
            "github-models" => WrapWithMiddleware(await CreateGitHubModelsClientAsync(model, resolver, cancellationToken).ConfigureAwait(false), queueManager),
            "github-copilot" => await CreateGitHubCopilotResultAsync(model, services, queueManager, resolver, subAgentChatRegistry, cancellationToken).ConfigureAwait(false),
            "openai" or "azure-openai" => await CreateGitHubCopilotByokResultAsync(provider, model, services, queueManager, resolver, subAgentChatRegistry, cancellationToken).ConfigureAwait(false),
            "ollama" => WrapWithMiddleware(CreateOllamaClient(model, services), queueManager),
            "sub-agent-dispatcher" => CreateSubAgentDispatcherResult(services, dispatcherDependencies),
            _ => throw new InvalidOperationException(
                $"Unknown or unsupported provider: {provider}. Supported: echo, test, github-models, github-copilot, github-copilot-subagent, sub-agent-dispatcher, ollama, openai, azure-openai"),
        };
    }

    // Constructs the SubAgentDispatcherChatClient for the "sub-agent-dispatcher" provider from the
    // supplied dependencies. The dispatcher entity name, resolved AgentDefinitionTool list and
    // embeddings/data-access services are threaded in via SubAgentDispatcherDependencies because
    // AgentServices (in Llm.Interfaces) cannot reference the Data.Core types these require. The
    // running-agent-chat factory falls back to AgentServices.RunningAgentChatFactory when it is not
    // supplied explicitly.
    private static ChatClientResult CreateSubAgentDispatcherResult(
        AgentServices? services,
        SubAgentDispatcherDependencies? dependencies)
    {
        if (dependencies is null)
        {
            throw new InvalidOperationException(
                "The 'sub-agent-dispatcher' provider requires SubAgentDispatcherDependencies to construct "
                + "its SubAgentDispatcherChatClient. Supply them via the dispatcherDependencies parameter.");
        }

        var factory = dependencies.RunningAgentChatFactory
            ?? services?.RunningAgentChatFactory as IRunningAgentChatFactory
            ?? throw new InvalidOperationException(
                "The 'sub-agent-dispatcher' provider requires an IRunningAgentChatFactory, supplied either in "
                + "SubAgentDispatcherDependencies or via AgentServices.RunningAgentChatFactory.");

        var dataAccessLayer = dependencies.DataAccessLayer
            ?? throw new InvalidOperationException(
                "The 'sub-agent-dispatcher' provider requires an IDataAccessLayer in SubAgentDispatcherDependencies.");

        var options = new SubAgentDispatcherOptions
        {
            AgentDefinitionTools = dependencies.AgentDefinitionTools,
        };

        var client = new SubAgentDispatcherChatClient(
            factory,
            dependencies.EmbeddingsProvider,
            dataAccessLayer,
            dependencies.DispatcherEntityName,
            options,
            subAgentServices: services,
            slashCommandRegistry: services?.SlashCommandRegistry as SlashCommands.ISlashCommandRegistry);

        return new ChatClientResult(client, "Sub-agent dispatcher");
    }

    // Wraps the inner client with ToolResultSteeringMiddleware when a queue manager is provided.
    // Never wraps self-invoking clients — they drive their own tool loop and GetStreamingResponseAsync
    // is never re-called with FunctionResultContent, so the middleware would never inject anything;
    // worse, delegating GetService would make the framework suppress its FunctionInvocationMiddleware.
    private static ChatClientResult WrapWithMiddleware(
        (IChatClient client, string displayName) inner,
        AgentInputQueueManager? queueManager)
    {
        if (queueManager is null
            || inner.client is ISelfInvokingToolChatClient
            || inner.client.GetService(typeof(ISelfInvokingToolChatClient)) is not null)
        {
            return new ChatClientResult(inner.client, inner.displayName);
        }

        return new ChatClientResult(
            new ToolResultSteeringMiddleware(inner.client, queueManager),
            inner.displayName);
    }

    private static ChatClientResult CreateGitHubCopilotResult(
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager,
        IApiKeyResolver resolver,
        ISubAgentChatRegistry? subAgentChatRegistry = null)
    {
        var (client, displayName) = CreateGitHubCopilotClient(model, services, queueManager, resolver, subAgentChatRegistry);
        return new ChatClientResult(client, displayName);
    }

    private static async Task<ChatClientResult> CreateGitHubCopilotResultAsync(
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager,
        IApiKeyResolver resolver,
        ISubAgentChatRegistry? subAgentChatRegistry,
        CancellationToken cancellationToken)
    {
        var (client, displayName) = await CreateGitHubCopilotClientAsync(model, services, queueManager, resolver, subAgentChatRegistry, cancellationToken).ConfigureAwait(false);
        return new ChatClientResult(client, displayName);
    }

    private static ChatClientResult CreateGitHubCopilotByokResult(
        string provider,
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager,
        IApiKeyResolver resolver,
        ISubAgentChatRegistry? subAgentChatRegistry = null)
    {
        var (client, displayName) = CreateGitHubCopilotByokClient(provider, model, services, queueManager, resolver, subAgentChatRegistry);
        return new ChatClientResult(client, displayName);
    }

    private static async Task<ChatClientResult> CreateGitHubCopilotByokResultAsync(
        string provider,
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager,
        IApiKeyResolver resolver,
        ISubAgentChatRegistry? subAgentChatRegistry,
        CancellationToken cancellationToken)
    {
        var (client, displayName) = await CreateGitHubCopilotByokClientAsync(provider, model, services, queueManager, resolver, subAgentChatRegistry, cancellationToken).ConfigureAwait(false);
        return new ChatClientResult(client, displayName);
    }

    /// <summary>
    /// Projects an <see cref="AgentManifest"/> into a concrete <see cref="AgentDefinition"/> by
    /// cloning the manifest's template and resolving each tool resource into a concrete tool.
    /// </summary>
    /// <param name="request">The manifest and the tool resource factory used to resolve resources.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The projected agent definition with resolved tools appended.</returns>
    /// <exception cref="InvalidOperationException">
    /// If the manifest has no template, the template cannot be cloned, the tool resource factory is
    /// missing, or a tool resource cannot be resolved.
    /// </exception>
    public static async Task<AgentDefinition> CreateAgentDefinitionAsync(
        CreateAgentDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        var manifest = request.AgentManifest
            ?? throw new InvalidOperationException("Create agent definition request does not specify a manifest.");

        // Apply parameter substitution when Parameters are provided; otherwise clone the template.
        AgentDefinition definition;
        if (request.Parameters is { } parameterValues)
        {
            definition = AgentDefinitionParameterSubstitutor.Substitute(manifest, parameterValues);
        }
        else
        {
            var template = manifest.Template
                ?? throw new InvalidOperationException("Agent manifest does not specify a template agent definition.");

            definition = AgentDefinition.FromJson(template.ToJson())
                ?? throw new InvalidOperationException("Failed to clone the agent manifest template.");
        }

        var toolResources = manifest.Resources?.OfType<ToolResource>().ToArray() ?? [];
        if (toolResources.Length == 0)
        {
            return definition;
        }

        var toolResourceFactory = request.ToolResourceFactory
            ?? throw new InvalidOperationException(
                "Create agent definition request does not specify a tool resource factory but the manifest references tool resources.");

        if (definition is not PromptAgent promptAgent)
        {
            throw new InvalidOperationException(
                "Agent manifest template must be a prompt agent to resolve tool resources.");
        }

        promptAgent.Tools ??= [];
        foreach (var toolResource in toolResources)
        {
            var tool = await toolResourceFactory
                .ResolveToolResourceAsync(toolResource, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Tool resource '{toolResource.Id}:{toolResource.Name}' could not be resolved.");

            promptAgent.Tools.Add(tool);
        }

        return definition;
    }

    /// <summary>
    /// Creates an initialized <see cref="AgentChat"/> session from an agent definition,
    /// including configured tool initialization before returning.
    /// </summary>
    /// <param name="createAgentChatRequest">Request for creating or restoring a chat.</param>
    /// <returns>The running <see cref="AgentChat"/>.</returns>
    public static async Task<AgentChat> CreateAgentChatAsync(
        CreateAgentChatRequest createAgentChatRequest)
    {
        var services = createAgentChatRequest.AgentServices;
        ValidateServices(services);

        var requestedAgentDefinition = createAgentChatRequest.AgentManifest is { } agentManifest
            ? await CreateAgentDefinitionAsync(new CreateAgentDefinitionRequest
                {
                    AgentManifest = agentManifest,
                    Parameters = createAgentChatRequest.Parameters,
                    ToolResourceFactory = createAgentChatRequest.ToolResourceFactory ?? services?.ToolResourceFactory,
                })
            : createAgentChatRequest.AgentDefinition;

        await EnforceTrustProfileAsync(
            requestedAgentDefinition,
            createAgentChatRequest.TrustProfileProvider);

        IAgentPersistenceStore configuredStore = services?.AgentPersistenceStoreOverride
            ?? new InMemoryAgentPersistenceStore();

        var ct = createAgentChatRequest.CancellationToken;

        // Try to extract chat-history tool from agent definition (skipped if override is provided)
        if (services?.AgentPersistenceStoreOverride is null
            && requestedAgentDefinition is PromptAgent promptAgent
            && promptAgent.Tools != null)
        {
            var chatHistoryTool = promptAgent.Tools.OfType<CustomTool>()
                .FirstOrDefault(t => t.Kind == "chat-history" || t.Name == "chat-history");
            
            if (chatHistoryTool?.Options != null && 
                chatHistoryTool.Options.TryGetValue("connection", out var connectionObj) && 
                connectionObj is IDictionary<string, object> connectionDict)
            {
                try
                {
                    // Convert the connection options to JSON then deserialize as ChatHistoryProviderDefinition
                    var connectionJson = System.Text.Json.JsonSerializer.Serialize(connectionDict);
                    var definition = ChatHistoryProviderDefinition.FromJson(connectionJson);
                    var storeFactory = createAgentChatRequest.PersistenceStoreFactory
                        ?? AgentPersistenceStoreFactory.CreateAsync;
                    configuredStore = await storeFactory(definition, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Log warning but don't fail - fall back to in-memory
                    System.Diagnostics.Debug.WriteLine($"Failed to create chat history provider from agent definition: {ex.Message}");
                }
            }
        }

        // Wire AgentSessionToolsetFactory when a running-agent-chat factory is available.
        // The factory needs the parent AgentChat, which is set on agentChatRef after CreateAsync returns.
        AgentChatRef? agentChatRef = null;
        AgentServices? effectiveServices = services;
        if (services?.RunningAgentChatFactory is IRunningAgentChatFactory runningFactory)
        {
            var sessionId = createAgentChatRequest.AgentSessionId ?? Guid.NewGuid().ToString("n");
            var sessionContext = new CurrentSessionContext { AgentSessionId = sessionId };
            agentChatRef = new AgentChatRef();
            var sessionToolsetFactory = ToolsetFactory.CreateAgentSessionToolsetFactory(
                agentChatRef,
                sessionContext,
                runningFactory,
                services.ToolsetFactory ?? ToolsetFactory.CreateDefaultToolsetFactory());
            effectiveServices = services with { ToolsetFactory = sessionToolsetFactory };
        }

        var chat = await AgentChat.CreateAsync(
            new InternalCreateAgentChatRequest
            {
                AgentDefinition = requestedAgentDefinition,
                AgentSessionId = createAgentChatRequest.AgentSessionId,
                AgentServices = effectiveServices,
                ConfiguredStore = configuredStore,
                ClientOverride = services?.ChatClientOverride,
                CancellationToken = CancellationToken.None,
                ForegroundScheduler = createAgentChatRequest.ForegroundScheduler,
            });

        // Complete the late-bound reference so AgentSessionToolset tools can access the parent chat.
        if (agentChatRef is not null)
            agentChatRef.Chat = chat;

        return chat;
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

    private static (IChatClient client, string displayName) CreateGitHubModelsClient(Model model, IApiKeyResolver resolver)
    {
        var connection = model.Connection as ApiKeyConnection
            ?? throw new InvalidOperationException("GitHub provider requires an ApiKeyConnection.");

        var endpoint = string.IsNullOrWhiteSpace(connection.Endpoint)
            ? GitHubModelsInferenceEndpoint
            : connection.Endpoint;

        var apiKey = resolver.ResolveApiKey(connection.ApiKey, "github-models");
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

    private static async Task<(IChatClient client, string displayName)> CreateGitHubModelsClientAsync(
        Model model, IApiKeyResolver resolver, CancellationToken cancellationToken)
    {
        var connection = model.Connection as ApiKeyConnection
            ?? throw new InvalidOperationException("GitHub provider requires an ApiKeyConnection.");

        var endpoint = string.IsNullOrWhiteSpace(connection.Endpoint)
            ? GitHubModelsInferenceEndpoint
            : connection.Endpoint;

        var apiKey = await resolver.ResolveApiKeyAsync(connection.ApiKey, "github-models", cancellationToken).ConfigureAwait(false);
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

    // Creates the built-in GitHub Copilot client. The factory resolves only model.Connection here
    // (an optional GitHub token); model.Options is forwarded verbatim to the client, which
    // interprets provider-specific keys itself (issue #896). model.Id is a value forwarded to the
    // chat client; it is never inspected to route provider selection.
    private static (IChatClient client, string displayName) CreateGitHubCopilotClient(
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager = null,
        IApiKeyResolver? resolver = null,
        ISubAgentChatRegistry? subAgentChatRegistry = null)
    {
        resolver ??= EnvironmentApiKeyResolver.Instance;

        var modelId = model.Id
            ?? throw new InvalidOperationException("GitHub Copilot provider requires a model id.");

        if (model.Connection is ApiKeyConnection connWithEndpoint && !string.IsNullOrWhiteSpace(connWithEndpoint.Endpoint))
        {
            // github-copilot + explicit endpoint == Copilot SDK BYOK against an OpenAI-compatible
            // endpoint (e.g. local Ollama). The wire provider defaults to "openai" (matching the
            // schema's providerType default) so there is no ambiguous endpoint-presence heuristic
            // (cf. issue #896). Delegating here keeps the schema and runtime in agreement (#1106).
            return CreateGitHubCopilotByokClient("openai", model, services, queueManager, resolver, subAgentChatRegistry);
        }

        // Authenticate as a Copilot user, optionally with an explicit GitHub token. When no token
        // is provided the SDK falls back to the logged-in Copilot user.
        var gitHubToken = model.Connection switch
        {
            ApiKeyConnection apiKeyConn when !string.IsNullOrWhiteSpace(apiKeyConn.ApiKey)
                => resolver.ResolveApiKey(apiKeyConn.ApiKey, "github-copilot"),
            _ => null,
        };
        var displayName = $"GitHub Copilot ({modelId})";

        var client = new CopilotSdkChatClient(
            modelId,
            displayName,
            gitHubToken,
            services?.LoggerFactory,
            queueManager: queueManager,
            modelOptions: model.Options,
            subAgentChatRegistry: subAgentChatRegistry,
            accountUpsertService: services?.AccountUpsertService,
            slashCommandRegistry: services?.SlashCommandRegistry as SlashCommands.ISlashCommandRegistry);

        return (client, displayName);
    }

    // Creates a BYOK Copilot client for the openai / azure-openai providers. The factory resolves
    // model.Connection into the endpoint + credential pair; the wire knobs (wireApi, wireModel,
    // headers) stay in model.Options, which is forwarded verbatim and interpreted by the Copilot
    // SDK via CopilotSdkChatClient.CreateProviderConfig (issue #896).
    private static (IChatClient client, string displayName) CreateGitHubCopilotByokClient(
        string provider,
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager = null,
        IApiKeyResolver? resolver = null,
        ISubAgentChatRegistry? subAgentChatRegistry = null)
    {
        resolver ??= EnvironmentApiKeyResolver.Instance;

        var modelId = model.Id
            ?? throw new InvalidOperationException($"The {provider} provider requires a model id.");

        var conn = model.Connection as ApiKeyConnection
            ?? throw new InvalidOperationException($"The {provider} provider requires an ApiKeyConnection.");

        var endpoint = conn.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException($"The {provider} provider requires a connection endpoint.");
        }

        var resolvedApiKey = string.IsNullOrWhiteSpace(conn.ApiKey)
            ? null
            : resolver.ResolveApiKey(conn.ApiKey, provider);

        var byokOptions = new CopilotByokOptions
        {
            Provider = provider,
            BaseUrl = endpoint,
            ApiKey = resolvedApiKey,
        };

        // gitHubToken stays null — no GitHub auth is required in BYOK mode.
        var displayName = $"GitHub Copilot BYOK ({modelId} @ {endpoint})";

        var client = new CopilotSdkChatClient(
            modelId,
            displayName,
            gitHubToken: null,
            services?.LoggerFactory,
            byokOptions: byokOptions,
            queueManager: queueManager,
            modelOptions: model.Options,
            subAgentChatRegistry: subAgentChatRegistry,
            slashCommandRegistry: services?.SlashCommandRegistry as SlashCommands.ISlashCommandRegistry);

        return (client, displayName);
    }

    private static async Task<(IChatClient client, string displayName)> CreateGitHubCopilotClientAsync(
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager,
        IApiKeyResolver resolver,
        ISubAgentChatRegistry? subAgentChatRegistry,
        CancellationToken cancellationToken)
    {
        var modelId = model.Id
            ?? throw new InvalidOperationException("GitHub Copilot provider requires a model id.");

        if (model.Connection is ApiKeyConnection connWithEndpoint && !string.IsNullOrWhiteSpace(connWithEndpoint.Endpoint))
        {
            // github-copilot + explicit endpoint == Copilot SDK BYOK against an OpenAI-compatible
            // endpoint (e.g. local Ollama). Route through the BYOK client with the "openai" wire
            // provider (schema default); see #1106.
            return await CreateGitHubCopilotByokClientAsync(
                "openai", model, services, queueManager, resolver, subAgentChatRegistry, cancellationToken).ConfigureAwait(false);
        }

        var gitHubToken = model.Connection switch
        {
            ApiKeyConnection apiKeyConn when !string.IsNullOrWhiteSpace(apiKeyConn.ApiKey)
                => await resolver.ResolveApiKeyAsync(apiKeyConn.ApiKey, "github-copilot", cancellationToken).ConfigureAwait(false),
            _ => null,
        };
        var displayName = $"GitHub Copilot ({modelId})";

        var client = new CopilotSdkChatClient(
            modelId,
            displayName,
            gitHubToken,
            services?.LoggerFactory,
            queueManager: queueManager,
            modelOptions: model.Options,
            subAgentChatRegistry: subAgentChatRegistry,
            accountUpsertService: services?.AccountUpsertService,
            slashCommandRegistry: services?.SlashCommandRegistry as SlashCommands.ISlashCommandRegistry);

        return (client, displayName);
    }

    private static async Task<(IChatClient client, string displayName)> CreateGitHubCopilotByokClientAsync(
        string provider,
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager,
        IApiKeyResolver resolver,
        ISubAgentChatRegistry? subAgentChatRegistry,
        CancellationToken cancellationToken)
    {
        var modelId = model.Id
            ?? throw new InvalidOperationException($"The {provider} provider requires a model id.");

        var conn = model.Connection as ApiKeyConnection
            ?? throw new InvalidOperationException($"The {provider} provider requires an ApiKeyConnection.");

        var endpoint = conn.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException($"The {provider} provider requires a connection endpoint.");
        }

        var resolvedApiKey = string.IsNullOrWhiteSpace(conn.ApiKey)
            ? null
            : await resolver.ResolveApiKeyAsync(conn.ApiKey, provider, cancellationToken).ConfigureAwait(false);

        var byokOptions = new CopilotByokOptions
        {
            Provider = provider,
            BaseUrl = endpoint,
            ApiKey = resolvedApiKey,
        };

        var displayName = $"GitHub Copilot BYOK ({modelId} @ {endpoint})";

        var client = new CopilotSdkChatClient(
            modelId,
            displayName,
            gitHubToken: null,
            services?.LoggerFactory,
            byokOptions: byokOptions,
            queueManager: queueManager,
            modelOptions: model.Options,
            subAgentChatRegistry: subAgentChatRegistry,
            slashCommandRegistry: services?.SlashCommandRegistry as SlashCommands.ISlashCommandRegistry);

        return (client, displayName);
    }

    private static void ValidateServices(AgentServices? services)
    {
        if ((services?.LogChat == true || services?.LogHttpRequests == true) && services.LoggerFactory is null)
        {
            throw new InvalidOperationException(
                "AgentServices.LoggerFactory is required when AgentServices.LogChat or AgentServices.LogHttpRequests is enabled.");
        }
    }

    private static async Task EnforceTrustProfileAsync(
        AgentDefinition? agentDefinition,
        Phantom.Workspaces.Llm.Trust.ITrustProfileProvider? trustProfileProvider)
    {
        if (agentDefinition is null || trustProfileProvider is null)
        {
            return;
        }

        var trustProfile = await Phantom.Workspaces.Llm.Trust.AgentTrustProfileResolver
            .ResolveAsync(agentDefinition, trustProfileProvider);

        if (trustProfile is not null && !trustProfile.AllowsLocalExecution())
        {
            throw new InvalidOperationException(
                "The agent's trust profile does not permit local execution on this client instance.");
        }
    }

    internal static async Task<string> ResolveApiKeyAsync(
        string? apiKeyValue,
        string? serverName,
        CancellationToken cancellationToken = default)
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
                    var githubCliToken = await GitHubAuthTokenResolver.ResolveFromCliAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
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

    internal static string ResolveApiKey(
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
                    var githubCliToken = GitHubAuthTokenResolver.ResolveFromCli();
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
}
