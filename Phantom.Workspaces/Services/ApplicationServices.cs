using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.Logging;
using Phantom.Workspaces.Services.Updates;

namespace Phantom.Workspaces.Services;

public sealed class ApplicationServices
{
    public ApplicationServices(
        IRunningAgentChatTable runningAgentChats,
        IAgentPersistenceStoreCache agentPersistenceStoreCache,
        IUpdateController? updateController = null,
        ILoggerFactory? loggerFactory = null,
        ILogDirectoryProvider? logDirectoryProvider = null,
        ConfigurationPersistenceService? configurationPersistence = null)
    {
        this.RunningAgentChats = runningAgentChats;
        this.AgentPersistenceStoreCache = agentPersistenceStoreCache;
        this.UpdateController = updateController;
        this.LoggerFactory = loggerFactory;
        this.LogDirectoryProvider = logDirectoryProvider;
        this.ConfigurationPersistence = configurationPersistence;
    }

    public IRunningAgentChatTable RunningAgentChats { get; }

    public IAgentPersistenceStoreCache AgentPersistenceStoreCache { get; }

    public IUpdateController? UpdateController { get; }

    /// <summary>
    /// The process logger factory backed by the #1086 rolling file provider, or <c>null</c> when no
    /// file logging has been wired (for example in tests), in which case consumers fall back to a
    /// null logger.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; }

    /// <summary>The single log-directory resolver for this process, or <c>null</c> when unwired.</summary>
    public ILogDirectoryProvider? LogDirectoryProvider { get; }

    /// <summary>
    /// The configuration persistence service used to save runtime user preferences (for example,
    /// the pinned AI-usage metric selection). Null in tests that don't exercise persistence.
    /// </summary>
    public ConfigurationPersistenceService? ConfigurationPersistence { get; }
}
