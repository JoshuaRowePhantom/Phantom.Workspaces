using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Phantom.Workspaces.Llm.Echo;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Factory for creating Agent components from AgentSchema definitions.
/// Converts AgentSchema PromptAgent definitions to ChatClient configuration, model details, and tools.
/// </summary>
public static class AgentFactory
{
    /// <summary>
    /// Applies agent-definition-specific settings to chat options.
    /// </summary>
    /// <param name="agent">The agent definition to apply.</param>
    /// <param name="chatOptions">The chat options to update.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="chatOptions"/> is null.</exception>
    public static void ConfigureChatOptions(AgentDefinition agent, ChatOptions chatOptions)
    {
        ArgumentNullException.ThrowIfNull(chatOptions);

        chatOptions.Reasoning = new ReasoningOptions
        {
            Effort = ResolveReasoningEffort(agent),
        };

        StoreAgentDefinition(agent, chatOptions);

        var instructions = GetSystemInstructions(agent);
        if (!string.IsNullOrEmpty(instructions) && chatOptions.AdditionalProperties != null)
        {
            chatOptions.AdditionalProperties["system_instructions"] = instructions;
        }
    }

    /// <summary>
    /// Stores an AgentSchema definition in ChatOptions additional properties for later reference.
    /// </summary>
    /// <param name="agent">The agent definition to store.</param>
    /// <param name="chatOptions">The ChatOptions to update with agent metadata.</param>
    /// <exception cref="InvalidOperationException">If the agent is not a PromptAgent.</exception>
    public static void StoreAgentDefinition(AgentDefinition agent, ChatOptions chatOptions)
    {
        if (agent.Kind != "prompt")
        {
            throw new InvalidOperationException(
                $"Only PromptAgent (kind: 'prompt') is currently supported. Got: {agent.Kind}");
        }

        var promptAgent = agent as PromptAgent
            ?? throw new InvalidOperationException("Failed to cast agent to PromptAgent.");

        // Store the agent definition itself for later access
        if (chatOptions.AdditionalProperties != null)
        {
            chatOptions.AdditionalProperties["agent_definition"] = agent;
        }
    }

    /// <summary>
    /// Extracts tool definitions from a PromptAgent for registration with ChatClient.
    /// </summary>
    /// <param name="agent">The PromptAgent to extract tools from.</param>
    /// <returns>List of tools defined in the agent.</returns>
    /// <exception cref="InvalidOperationException">If the agent is not a PromptAgent.</exception>
    public static IList<Tool>? ExtractTools(AgentDefinition agent)
    {
        if (agent.Kind != "prompt")
        {
            throw new InvalidOperationException(
                $"Only PromptAgent (kind: 'prompt') is currently supported. Got: {agent.Kind}");
        }

        var promptAgent = agent as PromptAgent
            ?? throw new InvalidOperationException("Failed to cast agent to PromptAgent.");

        return promptAgent.Tools;
    }

    /// <summary>
    /// Gets the system instructions from a PromptAgent.
    /// </summary>
    /// <param name="agent">The PromptAgent to extract instructions from.</param>
    /// <returns>The system instructions, or empty string if not defined.</returns>
    /// <exception cref="InvalidOperationException">If the agent is not a PromptAgent.</exception>
    public static string GetSystemInstructions(AgentDefinition agent)
    {
        if (agent.Kind != "prompt")
        {
            throw new InvalidOperationException(
                $"Only PromptAgent (kind: 'prompt') is currently supported. Got: {agent.Kind}");
        }

        var promptAgent = agent as PromptAgent
            ?? throw new InvalidOperationException("Failed to cast agent to PromptAgent.");

        return promptAgent.Instructions ?? string.Empty;
    }

    /// <summary>
    /// Gets the model ID from a PromptAgent.
    /// </summary>
    /// <param name="agent">The PromptAgent to extract the model from.</param>
    /// <returns>The model ID, or null if not defined.</returns>
    /// <exception cref="InvalidOperationException">If the agent is not a PromptAgent.</exception>
    public static string? GetModelId(AgentDefinition agent)
    {
        if (agent.Kind != "prompt")
        {
            throw new InvalidOperationException(
                $"Only PromptAgent (kind: 'prompt') is currently supported. Got: {agent.Kind}");
        }

        var promptAgent = agent as PromptAgent
            ?? throw new InvalidOperationException("Failed to cast agent to PromptAgent.");

        return promptAgent.Model?.Id;
    }

    /// <summary>
    /// Gets the model definition from a PromptAgent.
    /// </summary>
    /// <param name="agent">The PromptAgent to extract the model from.</param>
    /// <returns>The model definition containing ID, provider, API type, connection, and options.</returns>
    /// <exception cref="InvalidOperationException">If the agent is not a PromptAgent or has no model defined.</exception>
    public static Model GetModel(AgentDefinition agent)
    {
        if (agent.Kind != "prompt")
        {
            throw new InvalidOperationException(
                $"Only PromptAgent (kind: 'prompt') is currently supported. Got: {agent.Kind}");
        }

        var promptAgent = agent as PromptAgent
            ?? throw new InvalidOperationException("Failed to cast agent to PromptAgent.");

        return promptAgent.Model
            ?? throw new InvalidOperationException("Agent definition does not specify a model.");
    }

    /// <summary>
    /// Creates a ChatClient from an AgentDefinition, resolving the provider and connection.
    /// </summary>
    /// <param name="agent">The agent definition to create a client from.</param>
    /// <returns>A tuple of (ChatClient, display name).</returns>
    /// <exception cref="InvalidOperationException">If the agent is invalid or provider is unsupported.</exception>
    public static (IChatClient client, string displayName) CreateChatClient(AgentDefinition agent)
    {
        var model = GetModel(agent);
        if (string.IsNullOrEmpty(model.Id))
        {
            throw new InvalidOperationException("Agent definition does not specify a model ID.");
        }

        var provider = model.Provider?.ToLowerInvariant() ?? "unknown";

        return provider switch
        {
            "echo" => (new EchoChatClient(), "Echo Chat Client"),
            "ollama" => CreateOllamaClient(model),
            "openai" => throw new NotImplementedException("OpenAI provider resolution not yet implemented."),
            "azure" => throw new NotImplementedException("Azure provider resolution not yet implemented."),
            _ => throw new InvalidOperationException(
                $"Unknown or unsupported provider: {provider}. Supported: echo, ollama, openai, azure")
        };
    }

    /// <summary>
    /// Creates a <see cref="ChatClientAgent"/> and underlying client from an agent definition.
    /// </summary>
    /// <param name="agent">Agent definition to materialize.</param>
    /// <returns>Tuple of created agent, underlying chat client, and display name.</returns>
    public static (ChatClientAgent Agent, IChatClient Client, string DisplayName) CreateAgent(
        AgentDefinition agent)
    {
        var chatOptions = new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions(),
        };
        ConfigureChatOptions(agent, chatOptions.ChatOptions);

        var clientInfo = CreateChatClient(agent);

        var createdAgent = new ChatClientAgent(clientInfo.client, chatOptions);
        return (createdAgent, clientInfo.client, clientInfo.displayName);
    }

    private static ReasoningEffort ResolveReasoningEffort(AgentDefinition agent)
    {
        var model = GetModel(agent);
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

    private static (IChatClient client, string displayName) CreateOllamaClient(Model model)
    {
        var connection = model.Connection as AgentSchema.AnonymousConnection
            ?? throw new InvalidOperationException("Ollama model requires an AnonymousConnection.");

        var endpoint = connection.Endpoint
            ?? throw new InvalidOperationException("Ollama connection requires an endpoint URL.");

        var modelId = model.Id ?? "mistral";

        try
        {
            var client = new OllamaApiClient(new Uri(endpoint), modelId);
            var displayName = $"Ollama ({modelId} at {endpoint})";
            return (client, displayName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to create Ollama client for model '{modelId}' at '{endpoint}': {ex.Message}", ex);
        }
    }
}
