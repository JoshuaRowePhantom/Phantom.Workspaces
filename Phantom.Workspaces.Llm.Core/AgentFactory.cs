using AgentSchema;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Factory for creating Agent components from AgentSchema definitions.
/// Converts AgentSchema PromptAgent definitions to ChatClient configuration, model details, and tools.
/// </summary>
public static class AgentFactory
{
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
            "ollama" => CreateOllamaClient(model),
            "openai" => throw new NotImplementedException("OpenAI provider resolution not yet implemented."),
            "azure" => throw new NotImplementedException("Azure provider resolution not yet implemented."),
            _ => throw new InvalidOperationException(
                $"Unknown or unsupported provider: {provider}. Supported: ollama, openai, azure")
        };
    }

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
