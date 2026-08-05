using System.ComponentModel;
using System.Text.Json.Serialization;
using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Dock document wrapper for WorkspacePaneViewModel (workspace-level tab).
/// </summary>
public class WorkspacePaneDocument : Document, IAsyncDisposable
{
    private readonly TabHeaderViewModel cachedTabHeader;
    private readonly AgentRunningIndicatorTabHeaderItemViewModel runningIndicator;
    private readonly NotificationIndicatorTabHeaderItemViewModel notificationIndicator;
    private int disposed;

    public WorkspacePaneDocument(WorkspacePaneViewModel workspacePane)
    {
        this.WorkspacePane = workspacePane;
        this.cachedTabHeader = new TabHeaderViewModel { Title = workspacePane.Title };

        this.runningIndicator = new AgentRunningIndicatorTabHeaderItemViewModel
        {
            IsRunning = workspacePane.AnyTabIsRunning,
        };
        this.notificationIndicator = new NotificationIndicatorTabHeaderItemViewModel
        {
            HasUnread = workspacePane.AnyTabHasUnreadNotification,
        };
        this.cachedTabHeader.Items.Add(this.runningIndicator);
        this.cachedTabHeader.Items.Add(this.notificationIndicator);

        workspacePane.PropertyChanged += this.OnWorkspacePanePropertyChanged;
    }

    private void OnWorkspacePanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(WorkspacePaneViewModel.Title), StringComparison.Ordinal))
        {
            this.cachedTabHeader.Title = this.WorkspacePane.Title;
            this.Title = this.WorkspacePane.Title;
        }
        else if (string.Equals(e.PropertyName, nameof(WorkspacePaneViewModel.AnyTabIsRunning), StringComparison.Ordinal))
        {
            this.runningIndicator.IsRunning = this.WorkspacePane.AnyTabIsRunning;
        }
        else if (string.Equals(e.PropertyName, nameof(WorkspacePaneViewModel.AnyTabHasUnreadNotification), StringComparison.Ordinal))
        {
            this.notificationIndicator.HasUnread = this.WorkspacePane.AnyTabHasUnreadNotification;
        }
    }

    [JsonIgnore]
    public WorkspacePaneViewModel WorkspacePane { get; }

    /// <summary>
    /// Header model for this workspace-pane tab. Contains running and notification
    /// indicator items that mirror the pane's aggregated tab state.
    /// </summary>
    [JsonIgnore]
    public TabHeaderViewModel EffectiveTabHeader => this.cachedTabHeader;

    /// <summary>
    /// Unsubscribes from the wrapped <see cref="WorkspacePaneViewModel"/> and cascades
    /// disposal into it — which in turn disposes each child <see cref="WorkspaceTabViewModel"/>
    /// (releasing any running-agent leases). See #1198.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;
        this.WorkspacePane.PropertyChanged -= this.OnWorkspacePanePropertyChanged;
        await this.WorkspacePane.DisposeAsync().ConfigureAwait(false);
    }
}
