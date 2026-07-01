using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;
using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

public class WorkspaceDocument : Document
{
    private bool hasUnreadNotification;
    private string baseTitle = string.Empty;
    private readonly StatusTabHeaderItemViewModel statusIndicator;
    private readonly TabHeaderViewModel cachedTabHeader;
    private IStatusItem? subscribedTabStatus;

    /// <summary>
    /// Parameterless constructor for JSON deserialization. <see cref="TabViewModel"/> is
    /// null until <see cref="Initialize"/> is called.
    /// </summary>
    public WorkspaceDocument()
    {
        this.statusIndicator = new StatusTabHeaderItemViewModel();
        this.cachedTabHeader = new TabHeaderViewModel { Title = string.Empty };
        this.cachedTabHeader.Items.Add(this.statusIndicator);
    }

    public WorkspaceDocument(WorkspaceTabViewModel tabViewModel)
    {
        this.statusIndicator = new StatusTabHeaderItemViewModel();
        this.cachedTabHeader = new TabHeaderViewModel { Title = string.Empty };

        this.Descriptor = BuildDescriptor(tabViewModel);
        this.InitializeCore(tabViewModel);
    }

    /// <summary>
    /// Wires a deserialized stub document to its tab view model. Called after the dock
    /// layout is restored from JSON and the tab VMs have been recreated from
    /// <see cref="Descriptor"/>.
    /// </summary>
    internal void Initialize(WorkspaceTabViewModel tabViewModel)
    {
        this.InitializeCore(tabViewModel);
    }

    private void InitializeCore(WorkspaceTabViewModel tabViewModel)
    {
        this.TabViewModel = tabViewModel;
        this.Id = tabViewModel.Id;
        this.baseTitle = ComputeBaseTitle(tabViewModel);
        this.Title = this.baseTitle;
        this.CanClose = true;

        this.cachedTabHeader.Title = this.baseTitle;
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
        this.statusIndicator.Status.RunningStatus = this.TabViewModel?.TabStatus?.RunningStatus ?? RunningStatus.Idle;
    }

    private void OnTabViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (this.TabViewModel is not { } tabVm) return;

        if (e.PropertyName is nameof(WorkspaceTabViewModel.Title) or nameof(WorkspaceTabViewModel.TabHeader))
        {
            this.baseTitle = ComputeBaseTitle(tabVm);
            this.RebuildTabHeaderItems();
            this.UpdateTitle();
        }
        else if (e.PropertyName is nameof(WorkspaceTabViewModel.TabStatus))
        {
            this.SubscribeToTabStatus(tabVm.TabStatus);
            this.UpdateStatusRunning();
        }
    }

    /// <summary>
    /// The cached tab header model for this document. Always contains a
    /// <see cref="StatusTabHeaderItemViewModel"/> as the last item,
    /// preceded by any icon items from <see cref="WorkspaceTabViewModel.TabHeader"/>.
    /// </summary>
    [JsonIgnore]
    public TabHeaderViewModel EffectiveTabHeader => this.cachedTabHeader;

    [JsonIgnore]
    public bool HasUnreadNotification
    {
        get => this.hasUnreadNotification;
        set
        {
            if (this.hasUnreadNotification == value) return;
            this.hasUnreadNotification = value;
            this.statusIndicator.Status.ErrorStatus = value ? ErrorStatus.Error : ErrorStatus.None;
            var notificationIndicator = this.cachedTabHeader.Items.OfType<NotificationIndicatorTabHeaderItemViewModel>().FirstOrDefault();
            if (notificationIndicator is not null)
            {
                notificationIndicator.HasUnread = value;
            }
        }
    }

    private void RebuildTabHeaderItems()
    {
        this.cachedTabHeader.Items.Clear();
        if (this.TabViewModel?.TabHeader is { Items: { } items })
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

    [JsonIgnore]
    public WorkspaceTabViewModel? TabViewModel { get; private set; }

    /// <summary>
    /// Serializable descriptor embedded in the dock-layout JSON. Set at construction time
    /// from the tab view model, and read back during restore to recreate the tab VM.
    /// </summary>
    public DockTabDescriptor? Descriptor { get; init; }

    /// <summary>
    /// Builds a <see cref="DockTabDescriptor"/> from a live tab view model, capturing the
    /// identity information needed to recreate the tab on restore.
    /// </summary>
    internal static DockTabDescriptor? BuildDescriptor(WorkspaceTabViewModel tab)
    {
        if (tab.Entity is { } entity)
        {
            if (tab is AgentSessionWorkspaceTabViewModel)
                return new AgentSessionDockTabDescriptor(entity.EntityId.Value.ToString());

            return new EntityDockTabDescriptor(entity.EntityId.Value.ToString(), "Open");
        }

        if (tab is WebViewModel webVm && !string.IsNullOrWhiteSpace(webVm.AddressBarUrl))
            return new BrowserDockTabDescriptor(webVm.AddressBarUrl);

        return null;
    }
}
