using System.ComponentModel;
using System.Linq;
using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

public class WorkspaceDocument : Document
{
    private bool hasUnreadNotification;
    private string baseTitle = string.Empty;
    private readonly NotificationIndicatorTabHeaderItemViewModel notificationIndicator;
    private readonly TabHeaderViewModel cachedTabHeader;

    public WorkspaceDocument(WorkspaceTabViewModel tabViewModel)
    {
        this.TabViewModel = tabViewModel;
        this.Id = tabViewModel.Id;
        this.baseTitle = ComputeBaseTitle(tabViewModel);
        this.Title = this.baseTitle;
        this.CanClose = true;

        this.notificationIndicator = new NotificationIndicatorTabHeaderItemViewModel();
        this.cachedTabHeader = new TabHeaderViewModel { Title = this.baseTitle };
        this.RebuildTabHeaderItems();

        tabViewModel.PropertyChanged += OnTabViewModelPropertyChanged;
    }

    private void OnTabViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceTabViewModel.Title) or nameof(WorkspaceTabViewModel.TabHeader))
        {
            this.baseTitle = ComputeBaseTitle(this.TabViewModel);
            this.RebuildTabHeaderItems();
            this.UpdateTitle();
        }
    }

    /// <summary>
    /// The cached tab header model for this document. Always contains a
    /// <see cref="NotificationIndicatorTabHeaderItemViewModel"/> as the last item,
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
            this.notificationIndicator.HasUnread = value;
        }
    }

    private void RebuildTabHeaderItems()
    {
        this.cachedTabHeader.Items.Clear();
        if (this.TabViewModel.TabHeader is { Items: { } items })
        {
            foreach (var item in items.Where(i => i is not NotificationIndicatorTabHeaderItemViewModel))
            {
                this.cachedTabHeader.Items.Add(item);
            }
        }
        this.cachedTabHeader.Items.Add(this.notificationIndicator);
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
