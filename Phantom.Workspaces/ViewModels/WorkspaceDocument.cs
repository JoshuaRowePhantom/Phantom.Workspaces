using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;
using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

public class WorkspaceDocument : Document, IAsyncDisposable, IJsonOnDeserialized
{
    private bool hasUnreadNotification;
    private string baseTitle = string.Empty;
    private readonly StatusTabHeaderItemViewModel statusIndicator;
    private TabHeaderViewModel cachedTabHeader;
    private IStatusItem? subscribedTabStatus;
    private int disposed;

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

        this.InitializeCore(tabViewModel);
    }

    /// <summary>
    /// Wires a stub document (from the parameterless constructor) to its tab view model.
    /// Called by <see cref="WorkspaceDocumentGenerator.PrepareDocumentContainer"/> and
    /// after dock layout restore. No-ops if the document is already initialized.
    /// </summary>
    internal void Initialize(WorkspaceTabViewModel tabViewModel)
    {
        if (base.Context is null)
            this.InitializeCore(tabViewModel);
    }

    private void InitializeCore(WorkspaceTabViewModel tabViewModel)
    {
        base.Context = tabViewModel;
        this.Id = tabViewModel.Id;
        this.Descriptor ??= BuildDescriptor(tabViewModel);
        if (this.Descriptor?.IsTitleExplicit == true)
        {
            tabViewModel.IsTitleExplicit = true;
        }

        this.EnsureTabHeaderForDescriptor();
        var incomingTitle = !string.IsNullOrEmpty(tabViewModel.Title)
            ? tabViewModel.Title
            : !string.IsNullOrEmpty(this.Descriptor?.Title)
                ? this.Descriptor!.Title!
                : this.baseTitle;
        this.baseTitle = incomingTitle;
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

            // #1190: keep the persisted descriptor in sync with the live tab title so
            // subsequent WriteBackWorkspaceTabs round-trips the current Title rather
            // than the stale value captured at InitializeCore time. Without this,
            // any Title change after Initialize (async DisplayName load, user rename,
            // entity update, split-and-add) is lost on save/close/reopen and the
            // restored tab header renders blank.
            if (e.PropertyName is nameof(WorkspaceTabViewModel.Title))
            {
                var refreshed = BuildDescriptor(tabVm);
                if (refreshed is not null)
                {
                    this.Descriptor = refreshed;
                }
            }
        }
        else if (e.PropertyName is nameof(WorkspaceTabViewModel.IsTitleExplicit))
        {
            var refreshed = BuildDescriptor(tabVm);
            if (refreshed is not null)
            {
                this.Descriptor = refreshed;
            }
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

    public void OnDeserialized() => this.SyncHeaderFromPersistedTitle();

    internal void SyncHeaderFromPersistedTitle()
    {
        this.EnsureTabHeaderForDescriptor();
        var persistedTitle = !string.IsNullOrEmpty(this.Descriptor?.Title)
            ? this.Descriptor!.Title
            : !string.IsNullOrEmpty(this.Title)
                ? this.Title
                : null;
        if (persistedTitle is null)
        {
            return;
        }

        this.baseTitle = persistedTitle;
        this.Title = persistedTitle;
        this.cachedTabHeader.Title = persistedTitle;
    }

    private void EnsureTabHeaderForDescriptor()
    {
        var desiredType = this.Descriptor is BrowserDockTabDescriptor
            ? typeof(WebTabHeaderViewModel)
            : typeof(TabHeaderViewModel);
        if (this.cachedTabHeader.GetType() == desiredType)
        {
            return;
        }

        var replacement = this.Descriptor is BrowserDockTabDescriptor
            ? new WebTabHeaderViewModel { Title = this.cachedTabHeader.Title }
            : new TabHeaderViewModel { Title = this.cachedTabHeader.Title };
        foreach (var item in this.cachedTabHeader.Items)
        {
            replacement.Items.Add(item);
        }

        this.cachedTabHeader = replacement;
    }

    private void UpdateTitle()
    {
        this.Title = this.baseTitle;
        this.cachedTabHeader.Title = this.baseTitle;
    }

    private static string ComputeBaseTitle(WorkspaceTabViewModel tabViewModel)
    {
        return tabViewModel.Title;
    }

    /// <summary>
    /// The tab view model wired by <see cref="InitializeCore"/> or <see cref="Initialize"/>.
    /// Always null until one of those is called. <c>base.Context</c> holds the same reference
    /// at runtime; <c>Context</c> has <c>[IgnoreDataMember]</c> on the base class so it is
    /// never written to the dock-layout JSON. <c>Owner</c> back-references are serialized
    /// as <c>$ref</c> markers by <c>ReferenceHandler.Preserve</c>; no shadow is needed.
    /// </summary>
    [JsonIgnore]
    public WorkspaceTabViewModel? TabViewModel => base.Context as WorkspaceTabViewModel;

    /// <summary>
    /// Serializable descriptor embedded in the dock-layout JSON. Set by <see cref="InitializeCore"/>
    /// when first wiring a fresh document, preserved during deserialization so that the
    /// JSON-restored value is not overwritten when <see cref="Initialize"/> wires the tab VM.
    /// </summary>
    public DockTabDescriptor? Descriptor { get; set; }

    /// <summary>
    /// Builds a <see cref="DockTabDescriptor"/> from a live tab view model, capturing the
    /// identity information needed to recreate the tab on restore.
    /// </summary>
    internal static DockTabDescriptor? BuildDescriptor(WorkspaceTabViewModel tab)
    {
        var title = string.IsNullOrEmpty(tab.Title) ? null : tab.Title;

        if (tab.Entity is { } entity)
        {
            if (tab is AgentSessionWorkspaceTabViewModel)
                return new AgentSessionDockTabDescriptor(entity.EntityId.Value.ToString()) { Title = title, IsTitleExplicit = tab.IsTitleExplicit };

            return new EntityDockTabDescriptor(entity.EntityId.Value.ToString(), "Open") { Title = title, IsTitleExplicit = tab.IsTitleExplicit };
        }

        if (tab is WebViewModel webVm && !string.IsNullOrWhiteSpace(webVm.AddressBarUrl))
            return new BrowserDockTabDescriptor(webVm.AddressBarUrl) { Title = title, IsTitleExplicit = tab.IsTitleExplicit };

        return null;
    }

    /// <summary>
    /// Unsubscribes from the wrapped <see cref="WorkspaceTabViewModel"/> (and its
    /// <see cref="IStatusItem"/>) and cascades disposal into it. This is the recursive
    /// "document disposes its sub-document" step that guarantees per-tab resources
    /// (e.g. an <c>AgentSessionWorkspaceTabViewModel</c>'s <c>RunningAgentChatLease</c>)
    /// are always released, regardless of which close path is used. See #1198.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        var tabVm = this.TabViewModel;
        if (tabVm is not null)
        {
            tabVm.PropertyChanged -= this.OnTabViewModelPropertyChanged;
        }
        if (this.subscribedTabStatus is not null)
        {
            this.subscribedTabStatus.PropertyChanged -= this.OnTabStatusPropertyChanged;
            this.subscribedTabStatus = null;
        }
        if (tabVm is not null)
        {
            await tabVm.DisposeAsync().ConfigureAwait(false);
        }
    }
}
