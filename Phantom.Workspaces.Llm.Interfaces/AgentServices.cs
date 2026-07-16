using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.AI;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public sealed record AgentServices : IServiceProvider
{
    public bool LogChat { get; init; }

    public bool LogHttpRequests { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Overrides the agent persistence store used by the agent, bypassing the store
    /// configured in the agent definition. Intended for testing.
    /// </summary>
    public IAgentPersistenceStore? AgentPersistenceStoreOverride { get; init; }

    /// <summary>
    /// Overrides the chat client used by the agent. Intended for deterministic tests.
    /// </summary>
    public IChatClient? ChatClientOverride { get; init; }

    /// <summary>
    /// Factory for acquiring leased references to running agent chat sessions.
    /// </summary>
    public IRunningAgentChatFactory? RunningAgentChatFactory { get; init; }

    /// <summary>
    /// Overrides toolset factory resolution for custom tool kinds.
    /// </summary>
    public IToolsetFactory? ToolsetFactory { get; init; }

    /// <summary>
    /// Factory used to resolve the tool resources referenced by an agent manifest into concrete
    /// tools. Used when a <see cref="CreateAgentChatRequest"/> supplies an agent manifest without
    /// its own tool resource factory.
    /// </summary>
    public IToolResourceFactory? ToolResourceFactory { get; init; }

    /// <summary>
    /// Service that auto-creates and persists a <c>user-account</c> entity the first time a GitHub
    /// Copilot session is established for a token. Threaded into <see cref="CopilotSdkChatClient"/>
    /// by the agent factory so account entities actually materialize during normal Copilot use.
    /// </summary>
    public IGitHubAccountUpsertService? AccountUpsertService { get; init; }

    /// <summary>
    /// Optional slash command registry for component self-registration. Typed as <see langword="object"/>
    /// to avoid a reverse project reference from <c>Phantom.Workspaces.Llm.Interfaces</c> to
    /// <c>Phantom.Workspaces.Llm.Core</c>; consuming code casts to <c>ISlashCommandRegistry</c>.
    /// </summary>
    public object? SlashCommandRegistry { get; init; }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(ILoggerFactory))             return LoggerFactory;
        if (serviceType == typeof(IAgentPersistenceStore))     return AgentPersistenceStoreOverride;
        if (serviceType == typeof(IRunningAgentChatFactory))   return RunningAgentChatFactory;
        if (serviceType == typeof(IToolsetFactory))            return ToolsetFactory;
        if (serviceType == typeof(IToolResourceFactory))       return ToolResourceFactory;
        if (serviceType == typeof(IGitHubAccountUpsertService)) return AccountUpsertService;
        return null;
    }
}
