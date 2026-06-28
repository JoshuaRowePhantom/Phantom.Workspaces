using System;
using System.Collections.ObjectModel;
using Phantom.Workspaces.Services.Navigation;

namespace Phantom.Workspaces.ViewModels;

public sealed class NavigationStackPopupViewModel : TransientPopupViewModel
{
    private int selectedIndex;
    private readonly INavigationHistoryService navigationHistoryService;
    private readonly Func<string, string?> getTabTitle;

    public NavigationStackPopupViewModel(
        INavigationHistoryService navigationHistoryService,
        Func<string, string?> getTabTitle)
    {
        this.HoldDuration = TimeSpan.FromMilliseconds(500);
        this.FadeDuration = TimeSpan.FromMilliseconds(500);
        this.Rows = new ObservableCollection<NavigationStackRowViewModel>();
        this.navigationHistoryService = navigationHistoryService;
        this.getTabTitle = getTabTitle;
        this.navigationHistoryService.CanNavigateChanged += this.OnCanNavigateChanged;
    }

    public ObservableCollection<NavigationStackRowViewModel> Rows { get; }

    public int SelectedIndex
    {
        get => this.selectedIndex;
        set => this.SetProperty(ref this.selectedIndex, value);
    }

    /// <summary>
    /// Shows the popup without starting auto-close (for use while Ctrl is held).
    /// Rebuilds rows and sets selection to the current history position.
    /// </summary>
    public void OpenAtCurrentPosition()
    {
        this.RefreshRows();
        this.IsOpen = true;
        this.IsAutoClosing = false;
    }

    /// <summary>Move selection one item toward top of the list (toward newer entries).</summary>
    public void MoveSelectionUp()
    {
        if (this.SelectedIndex > 0)
        {
            this.SelectedIndex--;
        }
    }

    /// <summary>Move selection one item toward bottom of the list (toward older entries).</summary>
    public void MoveSelectionDown()
    {
        if (this.SelectedIndex < this.Rows.Count - 1)
        {
            this.SelectedIndex++;
        }
    }

    /// <summary>
    /// Called when Ctrl is released. Triggers the hold → fade sequence and returns the
    /// history index the caller should navigate to, or -1 if no navigation is needed.
    /// </summary>
    public int CommitAndBeginFade()
    {
        var entries = this.navigationHistoryService.Entries;
        var currentHistoryIndex = this.navigationHistoryService.CurrentIndex;
        int targetHistoryIndex = entries.Count > 0 ? entries.Count - 1 - this.SelectedIndex : -1;

        this.Show();

        return targetHistoryIndex >= 0 && targetHistoryIndex != currentHistoryIndex
            ? targetHistoryIndex
            : -1;
    }

    private void OnCanNavigateChanged(object? sender, EventArgs e)
    {
        if (this.IsOpen)
        {
            this.RefreshRows();
        }
    }

    private void RefreshRows()
    {
        var entries = this.navigationHistoryService.Entries;
        var currentHistoryIndex = this.navigationHistoryService.CurrentIndex;

        this.Rows.Clear();

        // Most-recent first: row 0 = entries[entries.Count-1]
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            this.Rows.Add(new NavigationStackRowViewModel
            {
                TabTitle = this.getTabTitle(entry.TabId) ?? entry.TabId,
            });
        }

        // Map current history position to display index
        this.SelectedIndex = entries.Count > 0
            ? entries.Count - 1 - currentHistoryIndex
            : 0;
    }
}
