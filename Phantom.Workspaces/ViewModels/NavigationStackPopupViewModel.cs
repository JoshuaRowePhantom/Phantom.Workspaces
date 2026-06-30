using System;
using System.Collections.ObjectModel;
using Phantom.Workspaces.Services.Navigation;

namespace Phantom.Workspaces.ViewModels;

/// <summary>Tab metadata returned by the tab-info resolver used by <see cref="NavigationStackPopupViewModel"/>.</summary>
public sealed record NavigationTabInfo(string TabTitle, string? WorkspaceName, bool IsRunning, bool IsInteresting);

public sealed class NavigationStackPopupViewModel : TransientPopupViewModel
{
    private int selectedIndex;
    private readonly INavigationHistoryService navigationHistoryService;
    private readonly Func<string, NavigationTabInfo?> getTabInfo;

    public NavigationStackPopupViewModel(
        INavigationHistoryService navigationHistoryService,
        Func<string, NavigationTabInfo?> getTabInfo)
    {
        this.HoldDuration = TimeSpan.FromMilliseconds(500);
        this.FadeDuration = TimeSpan.FromMilliseconds(500);
        this.Rows = new ObservableCollection<NavigationStackRowViewModel>();
        this.navigationHistoryService = navigationHistoryService;
        this.getTabInfo = getTabInfo;
        this.navigationHistoryService.CanNavigateChanged += this.OnCanNavigateChanged;
    }

    public ObservableCollection<NavigationStackRowViewModel> Rows { get; }

    public int SelectedIndex
    {
        get => this.selectedIndex;
        set
        {
            var old = this.selectedIndex;
            if (this.SetProperty(ref this.selectedIndex, value))
            {
                if (old >= 0 && old < this.Rows.Count) this.Rows[old].IsSelected = false;
                if (value >= 0 && value < this.Rows.Count) this.Rows[value].IsSelected = true;
            }
        }
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
        int newSelectedIndex = entries.Count > 0 ? entries.Count - 1 - currentHistoryIndex : 0;

        this.Rows.Clear();

        // Most-recent first: row 0 = entries[entries.Count-1]
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            int displayIndex = entries.Count - 1 - i;
            var info = this.getTabInfo(entry.TabId);
            this.Rows.Add(new NavigationStackRowViewModel
            {
                TabTitle = info?.TabTitle ?? entry.TabId,
                WorkspaceName = info?.WorkspaceName,
                IsRunning = info?.IsRunning ?? false,
                IsInteresting = info?.IsInteresting ?? false,
                IsSelected = displayIndex == newSelectedIndex,
            });
        }

        // Set backing field directly; rows already have IsSelected correct.
        this.selectedIndex = newSelectedIndex;
        this.RaisePropertyChanged(nameof(this.SelectedIndex));
    }
}
