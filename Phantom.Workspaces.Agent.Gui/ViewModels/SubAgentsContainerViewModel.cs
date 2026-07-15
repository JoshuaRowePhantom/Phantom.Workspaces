using System.Collections.ObjectModel;
using Phantom.Workspaces.Llm;

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
    private CancellationTokenSource? resumeSortCts;
    private bool isShowingBrowser = true;
    private bool isSortSuppressed;
    private bool hasDeferredSort;

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
        var slot = new SubAgentSlotViewModel(agentId, subAgentViewModel, subAgentViewModel.AgentChat);
        this.slotSource.Add(slot);
        return slot;
    }

    internal SubAgentSlotViewModel AddSlot(string agentId, AgentViewModel subAgentViewModel, IRunningSubAgent runningSubAgent)
    {
        var slot = new SubAgentSlotViewModel(agentId, subAgentViewModel, runningSubAgent);
        this.slotSource.Add(slot);
        this.ApplySortOrDefer();
        return slot;
    }

    public void SuppressSort()
    {
        this.resumeSortCts?.Cancel();
        this.IsSortSuppressed = true;
    }

    public void ScheduleResumeSort()
    {
        this.resumeSortCts?.Cancel();
        var cts = new CancellationTokenSource();
        this.resumeSortCts = cts;
        _ = ResumeSortAfterDelayAsync(cts);
    }

    public bool IsSortSuppressed
    {
        get => this.isSortSuppressed;
        private set => this.SetProperty(ref this.isSortSuppressed, value);
    }

    internal void NotifySubAgentUpdated()
        => this.ApplySortOrDefer();

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

    private async Task ResumeSortAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            this.IsSortSuppressed = false;
            if (this.hasDeferredSort)
            {
                this.hasDeferredSort = false;
                this.SortSlots();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplySortOrDefer()
    {
        if (this.IsSortSuppressed)
        {
            this.hasDeferredSort = true;
            return;
        }

        this.SortSlots();
    }

    private void SortSlots()
    {
        var ordered = this.slotSource
            .OrderByDescending(s => s.RunningSubAgent.LastUpdatedAt)
            .ToList();

        for (var targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            var currentIndex = this.slotSource.IndexOf(ordered[targetIndex]);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                this.slotSource.Move(currentIndex, targetIndex);
            }
        }
    }
}
