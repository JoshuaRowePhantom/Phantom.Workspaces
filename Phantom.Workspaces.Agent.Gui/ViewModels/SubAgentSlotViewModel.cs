using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// Represents one stable slot in the sub-agents container. Holds a cached
/// <see cref="SubAgentViewModel"/> and an <see cref="IsSelected"/> flag so the
/// AXAML layer can toggle visibility without tearing down the control.
/// </summary>
public sealed class SubAgentSlotViewModel : ViewModelBase
{
    private bool isSelected;

    public SubAgentSlotViewModel(string agentId, AgentViewModel subAgentViewModel, IRunningSubAgent runningSubAgent)
    {
        this.AgentId = agentId;
        this.SubAgentViewModel = subAgentViewModel;
        this.RunningSubAgent = runningSubAgent;
    }

    public string AgentId { get; }

    public AgentViewModel SubAgentViewModel { get; }

    public IRunningSubAgent RunningSubAgent { get; }

    public string DisplayName => this.SubAgentViewModel.DisplayName;

    public string Description => this.SubAgentViewModel.Description;

    public bool IsSelected
    {
        get => this.isSelected;
        internal set => this.SetProperty(ref this.isSelected, value);
    }
}
