using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.ViewModels;

public sealed record CreateAgentSessionTabForRestoreRequest
{
    public required MainWindowViewModel MainWindowViewModel { get; init; }
    public required SubscribedEntityViewModel AgentSessionEntity { get; init; }
    public string? TabId { get; init; } = null;
    public string? Title { get; init; } = null;
    public string? DockRegion { get; init; } = null;
}

public sealed record CreateAgentSessionTabRequest
{
    public required MainWindowViewModel MainWindowViewModel { get; init; }
    public required SubscribedEntityViewModel AgentSessionEntity { get; init; }
    public required IAgentChat AgentChat { get; init; }
    public string? RemoteProfileDisplayName { get; init; }
}

public sealed record ComposeSessionAgentViewModelOptions
{
    public required MainWindowViewModel MainWindowViewModel { get; init; }
    public required ObservableLoggerFactory LoggerFactory { get; init; }
    public required IAgentChat AgentChat { get; init; }
    public required SubscribedEntityViewModel AgentSessionEntity { get; init; }
    public required AgentSessionWorkspaceTabViewModel Tab { get; init; }
    public required TaskScheduler ForegroundScheduler { get; init; }
}
