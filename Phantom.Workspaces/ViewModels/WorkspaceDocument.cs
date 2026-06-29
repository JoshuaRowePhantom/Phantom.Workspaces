using System.ComponentModel;
using System.Linq;
using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

public class WorkspaceDocument : Document
{
    private bool hasUnreadNotification;
    private string baseTitle = string.Empty;
    private readonly StatusTabHeaderItemViewModel statusIndicator;
    private readonly TabHeaderViewModel cachedTabHeader;
    private IStatusItem? subscribedTabStatus;

    public WorkspaceDocument(WorkspaceTabViewModel tabViewModel)
    {
        this.TabViewModel = tabViewModel;
        this.Id = tabViewModel.Id;
        this.baseTitle = ComputeBaseTitle(tabViewModel);
        this.Title = this.baseTitle;
        this.CanClose = true;

        this.statusIndicator = new StatusTabHeaderItemViewModel();
        this.cachedTabHeader = new TabHeaderViewModel { Title = this.baseTitle };
        this.RebuildTabHeaderItems();
        this.UpdateStatusRunning();

        tabViewModel.PropertyChanged += OnTabViewModelPropertyChanged;
        this.SubscribeToTabStatus(tabViewModel.TabStatus);
    }

    private void SubscribeToTabStatus(IStatusItem? tabStatus)
    {
        if (this.subscribedTabStatus is not null)
            this.subscribedTabStatus.PropertyChanged -= this.OnTabStatusPropertyChanged;
        this.subscribedTabStatus = tabStatus;
        if (this.subscribedTabStatus is not null)
            this.subscribedTabStatus.PropertyChanged += this.OnTabStatusPropertyChanged;
    }

    private void OnTabStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IStatusItem.RunningStatus))
            this.UpdateStatusRunning();
    }

    private void UpdateStatusRunning()
    {
        this.statusIndicator.Status.RunningStatus = this.TabViewModel.TabStatus?.RunningStatus ?? RunningStatus.Idle;
    }

    private void OnTabViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceTabViewModel.Title) or nameof(WorkspaceTabViewModel.TabHeader))
        {
            this.baseTitle = ComputeBaseTitle(this.TabViewModel);
            this.RebuildTabHeaderItems();
            this.UpdateTitle();
        }
        else if (e.PropertyName is nameof(WorkspaceTabViewModel.TabStatus))
        {
            this.SubscribeToTabStatus(this.TabViewModel.TabStatus);
            this.UpdateStatusRunning();
        }
    }

    /// <summary>
    /// The cached tab header model for this document. Always contains a
    /// <see cref="StatusTabHeaderItemViewModel"/> as the last item,
    /// preceded by any icon items from <see cref="WorkspaceTabViewModel.TabHeader"/>.
    /// </summary>
    public TabHeaderViewModel EffectiveTabHeader => this.cachedTabHeader;

    public bool HasUnreadNotification
    {
        get => this.hasUnreadNotification;
        set
        {
            if (this.hasUnreadNotification == value) return;
            this.hasUnreadNotification = value;
            this.statusIndicator.Status.ErrorStatus = value ? ErrorStatus.Error : ErrorStatus.None;
        }
    }

    private void RebuildTabHeaderItems()
    {
        this.cachedTabHeader.Items.Clear();
        if (this.TabViewModel.TabHeader is { Items: { } items })
        {
            foreach (var item in items.Where(i => i is not StatusTabHeaderItemViewModel))
            {
                this.cachedTabHeader.Items.Add(item);
            }
        }
        this.cachedTabHeader.Items.Add(this.statusIndicator);
    }

    private void UpdateTitle()
    {
        this.Title = this.baseTitle;
        this.cachedTabHeader.Title = this.baseTitle;
    }

    private static string ComputeBaseTitle(WorkspaceTabViewModel tabViewModel)
    {
        return TruncateTitle(tabViewModel.Title);
    }

    private static string TruncateTitle(string title)
    {
        return title.Length > 20 ? title[..17] + "..." : title;
    }

    public WorkspaceTabViewModel TabViewModel { get; }
}
