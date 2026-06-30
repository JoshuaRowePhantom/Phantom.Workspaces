namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A snapshot of an open agent session tab and its containing workspace pane,
/// used by <see cref="RunningAgentBrainViewModel"/> to build popup rows.
/// </summary>
internal readonly struct AgentTabInfo(string paneId, string paneTitle, AgentSessionWorkspaceTabViewModel tab)
{
    public string PaneId { get; } = paneId;
    public string PaneTitle { get; } = paneTitle;
    public AgentSessionWorkspaceTabViewModel Tab { get; } = tab;
}
