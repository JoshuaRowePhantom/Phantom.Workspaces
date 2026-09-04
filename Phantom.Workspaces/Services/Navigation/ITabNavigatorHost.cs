using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.Navigation;

/// <summary>
/// The low-level operations <see cref="MainWindowTabNavigator"/> orchestrates. Implemented by
/// <c>MainWindowViewModel</c>, which already owns the concrete pane-selection / dock-activation /
/// entity-open logic. Kept as a narrow seam so the navigator can be unit-tested against a fake host.
/// </summary>
internal interface ITabNavigatorHost
{
    /// <summary>
    /// Resolves the pane named by <see cref="NavigationRequest.WorkspaceTabId"/> (opening it first if
    /// it is registered but not yet loaded, per #1157), selects it, then activates + focuses the
    /// document named by <see cref="NavigationRequest.DocumentTabId"/>. Returns true when the document
    /// tab was activated.
    /// </summary>
    Task<bool> ActivateTabByRequestAsync(NavigationRequest request);

    /// <summary>
    /// Brain-button fallback: switches to (loading if necessary) the workspace pane the agent session
    /// was started in and opens the agent's entity.
    /// </summary>
    Task OpenAgentForSessionAsync(string sessionKey);

    /// <summary>The id of the currently selected workspace pane, used as a history-push fallback.</summary>
    string? SelectedWorkspacePaneId { get; }

    /// <summary>True while replaying a history entry, so navigation must not re-push history.</summary>
    bool NavigatingViaHistory { get; }

    /// <summary>Brings the main window to the foreground (notifications path).</summary>
    void FocusMainWindow();
}
