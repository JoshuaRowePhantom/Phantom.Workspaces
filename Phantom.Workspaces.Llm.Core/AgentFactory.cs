using AgentSchema;
using Microsoft.Extensions.AI;

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
}
