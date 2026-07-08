using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// The stable <c>DetailContent</c> object shared by the "Sub-agents (N)" group nav item and every
/// individual sub-agent child nav item. Because the <c>ContentControl</c> never sees a different
/// content reference, Avalonia does not tear down sub-agent controls when the user switches between
/// them — only <see cref="IsShowingBrowser"/> / <see cref="SubAgentSlotViewModel.IsSelected"/>
/// change to toggle visibility.
/// </summary>
public sealed class SubAgentsContainerViewModel : ViewModelBase
{
    private readonly ObservableCollection<SubAgentSlotViewModel> slotSource;
    private bool isShowingBrowser = true;

    public SubAgentsContainerViewModel(SubAgentBrowserViewModel browser)
    {
        this.Browser = browser;
        this.slotSource = [];
        this.Slots = new ReadOnlyObservableCollection<SubAgentSlotViewModel>(this.slotSource);
    }

    public SubAgentBrowserViewModel Browser { get; }

    public ReadOnlyObservableCollection<SubAgentSlotViewModel> Slots { get; }

    public bool IsShowingBrowser
    {
        get => this.isShowingBrowser;
        private set => this.SetProperty(ref this.isShowingBrowser, value);
    }

    /// <summary>Adds a new slot for a sub-agent. Must be called on the UI thread.</summary>
    internal SubAgentSlotViewModel AddSlot(string agentId, AgentViewModel subAgentViewModel)
    {
        var slot = new SubAgentSlotViewModel(agentId, subAgentViewModel);
        this.slotSource.Add(slot);
        return slot;
    }

    /// <summary>Show the browser card, deselecting any sub-agent.</summary>
    public void ShowBrowser()
    {
        this.IsShowingBrowser = true;
        foreach (var slot in this.slotSource)
        {
            slot.IsSelected = false;
        }
    }

    /// <summary>
    /// Show the sub-agent with the given <paramref name="agentId"/>, hiding all other slots
    /// and the browser card.
    /// </summary>
    public void ShowSubAgent(string agentId)
    {
        this.IsShowingBrowser = false;
        foreach (var slot in this.slotSource)
        {
            slot.IsSelected = string.Equals(slot.AgentId, agentId, StringComparison.Ordinal);
        }
    }
}
