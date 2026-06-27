using Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// Receives live status-field updates produced by <see cref="Visualization.StatusUpdate"/> results
/// from <see cref="Visualization.IToolVisualizerFactory.Visualize"/>. Implemented by
/// <see cref="Controls.AgentChatOutputControl"/> and forwarded to
/// <see cref="AgentChatStatusLineViewModel"/> on the UI thread.
/// </summary>
public interface IAgentStatusSink
{
    void UpdateStatus(AgentStatusField field, string? value);
}
