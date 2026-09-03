using AgentSchema;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using MongoDB.Bson;
using OllamaSharp;
using OpenAI;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Secrets;
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
    /// Resolves the <see cref="CurrentSessionContext"/> used to wire the agent-session toolset. Prefers a
    /// host-supplied context (<see cref="AgentServices.CurrentSessionContext"/>), merging in the effective
    /// <paramref name="sessionId"/>, so the Copilot / running-agent path serves <c>get_current_session</c>
    /// with populated user / computer / profile members (issue #1236). Falls back to a session-id-only
    /// context when the host supplied none, preserving legacy behaviour.
    /// </summary>
    internal static CurrentSessionContext ResolveSessionContext(AgentServices? services, string sessionId)
    {
        if (services?.CurrentSessionContext is CurrentSessionContext supplied)
        {
            return supplied with { AgentSessionId = sessionId };
        }

        return new CurrentSessionContext { AgentSessionId = sessionId };
    }

    /// <summary>
    /// Extracts tool definitions from a PromptAgent for registration with ChatClient.
    /// </summary>
    /// <param name="agent">The PromptAgent to extract tools from.</param>
    /// <returns>List of tools defined in the agent, or empty when tools are not applicable.</returns>
    public static IList<Tool>? ExtractTools(AgentDefinition agent)
    {
        return (agent as PromptAgent)?.Tools?
            .Where(static tool => !IsGitHubCliBuiltinToolsTool(tool))
            .ToArray() ?? [];
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
        // Synchronous entry point retained only for callers (tests, and the previously-synchronous
        // remote respond handler) that need a blocking client construction. It delegates to the
        // resolver-first async factory so that model-connection API keys (including
        // ${SECRET:...} placeholders) go through the same secret-aware path as MCP/OAuth. Blocking
        // is safe here: CreateChatClientAsync and all of its internal awaits use
        // ConfigureAwait(false), so no SynchronizationContext is captured and no deadlock can occur.
        => CreateChatClientAsync(
                agent,
                services,
                queueManager,
                apiKeyResolver,
                subAgentChatRegistry,
                dispatcherDependencies)
            .GetAwaiter()
            .GetResult();

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

        // #1186: See CreateChatClient for rationale — restored hosted sub-agent
        // stubs with empty AgentDefinitions must reach the sub-agent fast-path
        // rather than throwing the null-model guard on startup.
        if (agent is null || (agent as PromptAgent)?.Model is null)
        {
            return new ChatClientResult(new CopilotSubAgentChatClient(), "GitHub Copilot Sub-Agent");
        }

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

        model = await CreateClientModelWithResolvedSecretPlaceholdersAsync(
            agent,
            model,
            services,
            cancellationToken).ConfigureAwait(false);

        return provider switch
        {
            "echo" => new ChatClientResult(new EchoChatClient(), "Echo Chat Client"),
            "github-models" => WrapWithMiddleware(await CreateGitHubModelsClientAsync(model, services, resolver, cancellationToken).ConfigureAwait(false), queueManager),
            "github-copilot" => await CreateGitHubCopilotResultAsync(agent, model, services, queueManager, resolver, subAgentChatRegistry, cancellationToken).ConfigureAwait(false),
            "openai" or "azure-openai" => await CreateGitHubCopilotByokResultAsync(agent, provider, model, services, queueManager, resolver, subAgentChatRegistry, cancellationToken).ConfigureAwait(false),
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

    private static async Task<ChatClientResult> CreateGitHubCopilotResultAsync(
        AgentDefinition agent,
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager,
        IApiKeyResolver resolver,
        ISubAgentChatRegistry? subAgentChatRegistry,
        CancellationToken cancellationToken)
    {
        var builtinToolPolicy = ExtractBuiltinToolPolicy(agent, services);
        var (client, displayName) = await CreateGitHubCopilotClientAsync(model, services, queueManager, resolver, subAgentChatRegistry, builtinToolPolicy, cancellationToken).ConfigureAwait(false);
        return new ChatClientResult(client, displayName);
    }

    private static async Task<ChatClientResult> CreateGitHubCopilotByokResultAsync(
        AgentDefinition agent,
        string provider,
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager,
        IApiKeyResolver resolver,
        ISubAgentChatRegistry? subAgentChatRegistry,
        CancellationToken cancellationToken)
    {
        var builtinToolPolicy = ExtractBuiltinToolPolicy(agent, services);
        var (client, displayName) = await CreateGitHubCopilotByokClientAsync(provider, model, services, queueManager, resolver, subAgentChatRegistry, builtinToolPolicy, cancellationToken).ConfigureAwait(false);
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

        StampOriginManifestLineage(definition, manifest);

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
    /// Stamps the manifest's stable identity and content hash into
    /// <see cref="AgentDefinition.Metadata"/> so a later manifest-less session launch (reopen, or a
    /// sibling session derived from the same manifest) recomputes the same manifest-scope consent
    /// hashes. Mirrors the <c>Metadata["trust-profile"]</c> pattern; no secrets are touched here.
    /// </summary>
    private static void StampOriginManifestLineage(AgentDefinition definition, AgentManifest manifest)
    {
        var manifestId = AgentManifestSecretUseMemoryFactory.ReadStableManifestIdentity(manifest);
        var contentHash = AgentManifestSecretUseMemoryFactory.ComputeManifestContentHash(manifest);

        definition.Metadata ??= new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(manifestId))
        {
            definition.Metadata[AgentManifestSecretUseMemoryFactory.OriginManifestIdMetadataKey] = manifestId;
        }

        definition.Metadata[AgentManifestSecretUseMemoryFactory.OriginManifestContentHashMetadataKey] = contentHash;
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

        var ct = createAgentChatRequest.CancellationToken;
        var agentManifest = createAgentChatRequest.AgentManifest;
        var requestedAgentDefinition = agentManifest is not null
            ? await CreateAgentDefinitionAsync(new CreateAgentDefinitionRequest
                {
                    AgentManifest = agentManifest,
                    Parameters = createAgentChatRequest.Parameters,
                    ToolResourceFactory = createAgentChatRequest.ToolResourceFactory ?? services?.ToolResourceFactory,
                }, ct).ConfigureAwait(false)
            : createAgentChatRequest.AgentDefinition;

        if (requestedAgentDefinition is not null
            && services?.SecretProvider is ISecretProvider secretProvider)
        {
            var materialized = await new AgentDefinitionSecretMaterializer()
                .MaterializeAsync(
                    requestedAgentDefinition,
                    secretProvider,
                    ct,
                    agentManifest,
                    createAgentChatRequest.AgentSessionId)
                .ConfigureAwait(false);
            requestedAgentDefinition = materialized.Definition;
            services = services with { SecretPlaceholderResolver = materialized.Resolver };
        }

        await EnforceTrustProfileAsync(
            requestedAgentDefinition,
            createAgentChatRequest.TrustProfileProvider);

        ChatHistoryProviderDefinition? definition = null;
        if (requestedAgentDefinition is PromptAgent promptAgent
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
                    definition = ChatHistoryProviderDefinition.FromJson(connectionJson);
                }
                catch (Exception ex)
                {
                    // Log warning but don't fail - fall through to null -> in-memory via the default delegate
                    System.Diagnostics.Debug.WriteLine($"Failed to parse chat-history connection options: {ex.Message}");
                    definition = null;
                }
            }
        }

        IAgentPersistenceStore configuredStore;
        if (services?.AgentPersistenceStoreOverride is { } explicitOverride)
        {
            // Higher-precedence seam: the explicit store override short-circuits the factory
            // entirely (kept for backwards compatibility with existing test/DI wiring).
            configuredStore = explicitOverride;
        }
        else
        {
            var storeFactory = createAgentChatRequest.PersistenceStoreFactory
                ?? DefaultPersistenceStoreFactory;
            try
            {
                configuredStore = await storeFactory(definition, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Any failure during store creation (including cancellation) is swallowed to keep
                // the session alive; fall back to an in-memory store (#698).
                System.Diagnostics.Debug.WriteLine($"Failed to create persistence store: {ex.Message}");
                configuredStore = new InMemoryAgentPersistenceStore();
            }
        }

        // Wire AgentSessionToolsetFactory when a running-agent-chat factory is available.
        // The factory needs the parent AgentChat, which is set on agentChatRef after CreateAsync returns.
        AgentChatRef? agentChatRef = null;
        AgentServices? effectiveServices = services;
        if (services?.RunningAgentChatFactory is IRunningAgentChatFactory runningFactory)
        {
            var sessionId = createAgentChatRequest.AgentSessionId ?? Guid.NewGuid().ToString("n");
            var sessionContext = ResolveSessionContext(services, sessionId);
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
                TimeProvider = createAgentChatRequest.TimeProvider ?? TimeProvider.System,
            });

        // Complete the late-bound reference so AgentSessionToolset tools can access the parent chat.
        if (agentChatRef is not null)
            agentChatRef.Chat = chat;

        return chat;
    }

    // Default persistence-store factory used when CreateAgentChatRequest.PersistenceStoreFactory is
    // not supplied. A null definition (agent has no chat-history tool) maps to an in-memory store;
    // a non-null definition is delegated to AgentPersistenceStoreFactory.CreateAsync, whose null
    // invariant (ArgumentNullException) is intentionally preserved for direct callers.
    private static ValueTask<IAgentPersistenceStore> DefaultPersistenceStoreFactory(
        ChatHistoryProviderDefinition? definition,
        CancellationToken cancellationToken)
        => definition is null
            ? new ValueTask<IAgentPersistenceStore>(AgentPersistenceStoreFactory.CreateInMemory())
            : AgentPersistenceStoreFactory.CreateAsync(definition, cancellationToken);

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

    private static async Task<(IChatClient client, string displayName)> CreateGitHubModelsClientAsync(
        Model model, AgentServices? services, IApiKeyResolver resolver, CancellationToken cancellationToken)
    {
        var connection = model.Connection as ApiKeyConnection
            ?? throw new InvalidOperationException("GitHub provider requires an ApiKeyConnection.");

        var endpoint = string.IsNullOrWhiteSpace(connection.Endpoint)
            ? GitHubModelsInferenceEndpoint
            : connection.Endpoint;

        var modelId = model.Id
            ?? throw new InvalidOperationException("GitHub provider requires a model id.");

        return await WithRequiredApiKeyForSdkAsync(
            services,
            resolver,
            connection.ApiKey,
            "github-models",
            cancellationToken,
            apiKey =>
            {
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
            }).ConfigureAwait(false);
    }

    private static async Task<(IChatClient client, string displayName)> CreateGitHubCopilotClientAsync(
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager,
        IApiKeyResolver resolver,
        ISubAgentChatRegistry? subAgentChatRegistry,
        CopilotBuiltinToolPolicy? builtinToolPolicy,
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
                "openai", model, services, queueManager, resolver, subAgentChatRegistry, builtinToolPolicy, cancellationToken).ConfigureAwait(false);
        }

        if (model.Connection is ApiKeyConnection apiKeyConn && !string.IsNullOrWhiteSpace(apiKeyConn.ApiKey))
        {
            return await WithOptionalApiKeyForSdkAsync(
                services,
                resolver,
                apiKeyConn.ApiKey,
                "github-copilot",
                cancellationToken,
                CreateClient).ConfigureAwait(false);
        }

        return CreateClient(null);

        (IChatClient client, string displayName) CreateClient(string? gitHubToken)
        {
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
                slashCommandRegistry: services?.SlashCommandRegistry as SlashCommands.ISlashCommandRegistry,
                builtinToolPolicy: builtinToolPolicy);

            if (services?.CopilotClientFactory is ICopilotClientFactory factory)
            {
                client.SetCopilotClientFactoryForTest(factory);
            }

            return (client, displayName);
        }
    }

    private static async Task<(IChatClient client, string displayName)> CreateGitHubCopilotByokClientAsync(
        string provider,
        Model model,
        AgentServices? services,
        AgentInputQueueManager? queueManager,
        IApiKeyResolver resolver,
        ISubAgentChatRegistry? subAgentChatRegistry,
        CopilotBuiltinToolPolicy? builtinToolPolicy,
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

        return string.IsNullOrWhiteSpace(conn.ApiKey)
            ? CreateClient(null)
            : await WithOptionalApiKeyForSdkAsync(
                services,
                resolver,
                conn.ApiKey,
                provider,
                cancellationToken,
                CreateClient).ConfigureAwait(false);

        (IChatClient client, string displayName) CreateClient(string? resolvedApiKey)
        {
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
                slashCommandRegistry: services?.SlashCommandRegistry as SlashCommands.ISlashCommandRegistry,
                builtinToolPolicy: builtinToolPolicy);

            if (services?.CopilotClientFactory is ICopilotClientFactory factory)
            {
                client.SetCopilotClientFactoryForTest(factory);
            }

            return (client, displayName);
        }
    }

    private static bool IsGitHubCliBuiltinToolsTool(Tool tool)
        => tool is GitHubCliBuiltinToolsTool
            || (tool is CustomTool customTool
                && string.Equals(customTool.Kind, GitHubCliBuiltinToolsTool.KindName, StringComparison.Ordinal));

    private static CopilotBuiltinToolPolicy? ExtractBuiltinToolPolicy(
        AgentDefinition agent,
        AgentServices? services)
    {
        if (agent is not PromptAgent promptAgent || promptAgent.Tools is null)
        {
            return null;
        }

        var matchingTools = promptAgent.Tools
            .Where(IsGitHubCliBuiltinToolsTool)
            .ToArray();
        if (matchingTools.Length == 0)
        {
            return null;
        }

        if (matchingTools.Length > 1)
        {
            throw new InvalidOperationException(
                "Agent definition may contain only one github-cli-builtin-tools entry.");
        }

        var logger = services?.LoggerFactory?.CreateLogger("github-cli-builtin-tools");
        var tool = ToBuiltinToolsTool(matchingTools[0]);
        ValidateClientModeInterlock(tool);

        var available = ResolveBuiltinToolSet(tool.AvailableTools, "available-tools", logger);
        var excluded = ResolveBuiltinToolSet(tool.ExcludedTools, "excluded-tools", logger);

        IReadOnlyList<string>? availableList = null;
        if (available is ResolvedToolSet.Concrete concreteAvailable)
        {
            if (concreteAvailable.Tools.Count == 1
                && concreteAvailable.Tools[0] == "*"
                && tool.ClientMode != CopilotClientMode.Empty)
            {
                availableList = null;
            }
            else
            {
                availableList = ToSdkAvailableList(concreteAvailable.Tools);
            }
        }

        var excludedList = excluded is ResolvedToolSet.Concrete concreteExcluded
            ? ToSdkBuiltinList(concreteExcluded.Tools)
            : null;

        return new CopilotBuiltinToolPolicy(availableList, excludedList, tool.ClientMode);
    }

    private static GitHubCliBuiltinToolsTool ToBuiltinToolsTool(Tool tool)
    {
        if (tool is GitHubCliBuiltinToolsTool typedTool)
        {
            return typedTool;
        }

        var customTool = (CustomTool)tool;
        var options = customTool.Options;
        return new GitHubCliBuiltinToolsTool
        {
            Kind = GitHubCliBuiltinToolsTool.KindName,
            Name = customTool.Name,
            Description = customTool.Description,
            Bindings = customTool.Bindings,
            Connection = customTool.Connection,
            Options = customTool.Options,
            AvailableTools = options is not null && options.TryGetValue("available-tools", out var available)
                ? ReadBuiltinToolSet(available, "available-tools")
                : null,
            ExcludedTools = options is not null && options.TryGetValue("excluded-tools", out var excluded)
                ? ReadBuiltinToolSet(excluded, "excluded-tools")
                : null,
            ClientMode = options is not null && options.TryGetValue("client-mode", out var mode)
                ? ReadClientMode(mode)
                : CopilotClientMode.CopilotCli,
        };
    }

    private static void ValidateClientModeInterlock(GitHubCliBuiltinToolsTool tool)
    {
        if (tool.ClientMode != CopilotClientMode.Empty)
        {
            return;
        }

        var available = ResolveBuiltinToolSet(tool.AvailableTools, "available-tools", log: null);
        if (available is not ResolvedToolSet.Concrete concrete || concrete.Tools.Count == 0)
        {
            throw new InvalidOperationException(
                "'client-mode: empty' requires 'available-tools' to be present and non-empty; Empty mode exposes no tools by default so every session must opt in.");
        }
    }

    private abstract record ResolvedToolSet
    {
        public sealed record Absent : ResolvedToolSet;
        public sealed record Concrete(IReadOnlyList<string> Tools) : ResolvedToolSet;
    }

    private static ResolvedToolSet ResolveBuiltinToolSet(
        BuiltinToolSet? selector,
        string slotName,
        ILogger? log)
    {
        if (selector is null)
        {
            return new ResolvedToolSet.Absent();
        }

        if (selector.Tools is { } tools && selector.Isolated)
        {
            log?.LogWarning(
                "github-cli-builtin-tools {Slot}: both 'tools' and 'isolated' set; applying precedence tools > isolated.",
                slotName);
        }

        if (selector.Tools is { } concreteTools)
        {
            return new ResolvedToolSet.Concrete(concreteTools);
        }

        if (selector.Isolated)
        {
            return new ResolvedToolSet.Concrete(BuiltInTools.Isolated.ToArray());
        }

        throw new InvalidOperationException(
            $"github-cli-builtin-tools {slotName} selector must set either 'tools' or 'isolated'.");
    }

    private static IReadOnlyList<string> ToSdkBuiltinList(IReadOnlyList<string> names)
    {
        var set = new ToolSet();
        foreach (var name in names)
        {
            if (name.Contains(':', StringComparison.Ordinal))
            {
                set.Add(name);
            }
            else
            {
                set.AddBuiltIn(name);
            }
        }

        return set.ToArray();
    }

    private static IReadOnlyList<string> ToSdkAvailableList(IReadOnlyList<string> names)
    {
        var anySourceQualified = names.Any(static name => name.Contains(':', StringComparison.Ordinal));
        var set = new ToolSet();
        foreach (var name in names)
        {
            if (name.Contains(':', StringComparison.Ordinal))
            {
                set.Add(name);
            }
            else
            {
                set.AddBuiltIn(name);
            }
        }

        if (!anySourceQualified)
        {
            set.AddCustom("*");
            set.AddMcp("*");
        }

        return set.ToArray();
    }

    private static BuiltinToolSet ReadBuiltinToolSet(object value, string slotName)
    {
        if (value is not IDictionary<string, object> dictionary)
        {
            throw new InvalidOperationException($"github-cli-builtin-tools {slotName} must be a selector object.");
        }

        IReadOnlyList<string>? tools = null;
        if (dictionary.TryGetValue("tools", out var toolsValue))
        {
            if (toolsValue is not IEnumerable enumerable || toolsValue is string)
            {
                throw new InvalidOperationException($"github-cli-builtin-tools {slotName}.tools must be an array of strings.");
            }

            var names = new List<string>();
            var index = 0;
            foreach (var item in enumerable)
            {
                if (item is not string name)
                {
                    throw new InvalidOperationException(
                        $"github-cli-builtin-tools {slotName}.tools[{index}] must be a string.");
                }

                names.Add(name);
                index++;
            }

            tools = names;
        }

        var isolated = false;
        if (dictionary.TryGetValue("isolated", out var isolatedValue))
        {
            isolated = isolatedValue is bool b
                ? b
                : throw new InvalidOperationException($"github-cli-builtin-tools {slotName}.isolated must be a boolean.");
        }

        return new BuiltinToolSet(tools, isolated);
    }

    private static CopilotClientMode ReadClientMode(object value)
        => value is string mode
            ? mode.ToLowerInvariant() switch
            {
                "empty" => CopilotClientMode.Empty,
                "copilot-cli" => CopilotClientMode.CopilotCli,
                _ => throw new InvalidOperationException(
                    $"'client-mode' must be one of \"empty\" or \"copilot-cli\"; got {mode}."),
            }
            : throw new InvalidOperationException("'client-mode' must be one of \"empty\" or \"copilot-cli\".");

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

    private static Task<T> WithRequiredApiKeyForSdkAsync<T>(
        AgentServices? services,
        IApiKeyResolver resolver,
        string? apiKeyValue,
        string? serverName,
        CancellationToken cancellationToken,
        Func<string, T> body)
        => WithOptionalApiKeyForSdkAsync(
            services,
            resolver,
            apiKeyValue,
            serverName,
            cancellationToken,
            apiKey => body(apiKey ?? throw new InvalidOperationException($"API key for '{serverName ?? "unknown"}' is required.")));

    private static async Task<T> WithOptionalApiKeyForSdkAsync<T>(
        AgentServices? services,
        IApiKeyResolver resolver,
        string? apiKeyValue,
        string? serverName,
        CancellationToken cancellationToken,
        Func<string?, T> body)
    {
        if (apiKeyValue is not null
            && services?.SecretPlaceholderResolver is ISecretPlaceholderResolver secretResolver
            && secretResolver.TryResolve(apiKeyValue, out var retriever))
        {
            using var secure = await retriever.Secret(cancellationToken).ConfigureAwait(false);
            return SecureStringMarshal.Use(secure, body);
        }

        var resolved = await resolver.ResolveApiKeyAsync(apiKeyValue, serverName, cancellationToken).ConfigureAwait(false);
        return body(resolved);
    }

    private static async Task<Model> CreateClientModelWithResolvedSecretPlaceholdersAsync(
        AgentDefinition agent,
        Model model,
        AgentServices? services,
        CancellationToken cancellationToken)
    {
        if (services?.SecretPlaceholderResolver is not ISecretPlaceholderResolver secretResolver
            || model.Options?.AdditionalProperties is not { } additionalProperties)
        {
            return model;
        }

        var clonedAgent = AgentDefinition.FromJson(agent.ToJson())
            ?? throw new InvalidOperationException("Failed to clone the agent definition for secret option resolution.");
        var clonedModel = (clonedAgent as PromptAgent)?.Model
            ?? throw new InvalidOperationException("Cloned agent definition does not specify a model.");
        additionalProperties = clonedModel.Options?.AdditionalProperties;
        if (additionalProperties is null)
        {
            return clonedModel;
        }

        foreach (var key in additionalProperties.Keys.ToArray())
        {
            additionalProperties[key] = (await ResolveModelOptionValueAsync(
                additionalProperties[key],
                secretResolver,
                cancellationToken).ConfigureAwait(false))!;
        }

        return clonedModel;
    }

    private static async Task<object?> ResolveModelOptionValueAsync(
        object? value,
        ISecretPlaceholderResolver secretResolver,
        CancellationToken cancellationToken)
    {
        if (value is string text)
        {
            return await ResolveModelOptionStringAsync(text, secretResolver, cancellationToken).ConfigureAwait(false);
        }

        if (value is IDictionary<string, object> dictionary)
        {
            foreach (var key in dictionary.Keys.ToArray())
            {
                dictionary[key] = (await ResolveModelOptionValueAsync(
                    dictionary[key],
                    secretResolver,
                    cancellationToken).ConfigureAwait(false))!;
            }
        }

        return value;
    }

    private static async Task<string> ResolveModelOptionStringAsync(
        string value,
        ISecretPlaceholderResolver secretResolver,
        CancellationToken cancellationToken)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(value, @"\$\{SECRET:[^}]+\}");
        if (matches.Count == 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        var position = 0;
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            builder.Append(value, position, match.Index - position);
            if (secretResolver.TryResolve(match.Value, out var retriever))
            {
                using var secure = await retriever.Secret(cancellationToken).ConfigureAwait(false);
                builder.Append(SecureStringMarshal.Use(secure, static plain => plain));
            }
            else
            {
                builder.Append(match.Value);
            }

            position = match.Index + match.Length;
        }

        builder.Append(value, position, value.Length - position);
        return builder.ToString();
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

    /// <summary>
    /// Resolves an optional MCP credential value (e.g. an OAuth <c>clientId</c> or <c>clientSecret</c>)
    /// through the same mechanisms used for API keys: a <c>${SECRET:&lt;handle&gt;}</c> token is
    /// materialized via <see cref="ISecretPlaceholderResolver"/> and bounded with
    /// <see cref="SecureStringMarshal.Use{T}(System.Security.SecureString, Func{string, T})"/> to
    /// minimize plaintext lifetime; otherwise the value flows through
    /// <see cref="ResolveApiKeyAsync"/> for <c>${ENV}</c>/<c>${GITHUB_TOKEN}</c>/literal handling.
    /// Unlike <see cref="ResolveApiKeyAsync"/>, a null/blank input returns <see langword="null"/>
    /// rather than throwing, because these OAuth fields are optional (null clientId enables dynamic
    /// registration; null clientSecret enables a public client + PKCE).
    /// </summary>
    internal static async Task<string?> ResolveOptionalSecretOrEnvAsync(
        string? value,
        AgentServices? services,
        string? serverName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (services?.SecretPlaceholderResolver is ISecretPlaceholderResolver secretResolver
            && secretResolver.TryResolve(value, out var retriever))
        {
            using var secure = await retriever.Secret(cancellationToken).ConfigureAwait(false);
            return SecureStringMarshal.Use(secure, static plain => plain);
        }

        return await ResolveApiKeyAsync(value, serverName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a required MCP credential value (e.g. an <see cref="ApiKeyConnection"/> <c>apiKey</c>)
    /// through the secret resolver first: a <c>${SECRET:&lt;handle&gt;}</c> token is materialized via
    /// <see cref="ISecretPlaceholderResolver"/> and bounded with
    /// <see cref="SecureStringMarshal.Use{T}(System.Security.SecureString, Func{string, T})"/> to
    /// minimize plaintext lifetime; otherwise the value flows through <see cref="ResolveApiKeyAsync"/>
    /// for <c>${ENV}</c>/<c>${GITHUB_TOKEN}</c>/literal handling. Mirrors
    /// <see cref="ResolveOptionalSecretOrEnvAsync"/> but, being non-optional, throws the standard
    /// "API key is required" error on a null/blank input (matching the legacy synchronous resolver).
    /// </summary>
    internal static async Task<string> ResolveRequiredSecretOrEnvAsync(
        string? value,
        AgentServices? services,
        string? serverName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"MCP tool '{serverName ?? "unknown"}' API key is required.");
        }

        if (services?.SecretPlaceholderResolver is ISecretPlaceholderResolver secretResolver
            && secretResolver.TryResolve(value, out var retriever))
        {
            using var secure = await retriever.Secret(cancellationToken).ConfigureAwait(false);
            return SecureStringMarshal.Use(secure, static plain => plain);
        }

        return await ResolveApiKeyAsync(value, serverName, cancellationToken).ConfigureAwait(false);
    }
}
