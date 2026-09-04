using System;
using Phantom.Workspaces.Services.Navigation;
using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// #1341: the window-scoped services a <see cref="WorkspacePaneViewModel"/> needs to own its
/// per-pane concerns (the tab→document registry, navigate-to-document Phase-2, close/cycle,
/// populate/restore, and persisted-snapshot building) without reaching back into
/// <see cref="MainWindowViewModel"/>. Constructed once per real workspace pane by
/// <see cref="MainWindowViewModel"/> and passed into the pane view-model's constructor.
/// Placeholder / loading panes are created without an environment.
/// </summary>
public sealed class WorkspacePaneEnvironment
{
    /// <summary>Resolves the current <see cref="EntityBroker"/> (created asynchronously on startup).</summary>
    public required Func<EntityBroker?> GetEntityBroker { get; init; }

    /// <summary>The shared, window-scoped dock factory (activation, focus, dock-state, wiring).</summary>
    public required WorkspaceDockFactory DockFactory { get; init; }

    /// <summary>The window-scoped notification service (mark-read on activation).</summary>
    public required INotificationService NotificationService { get; init; }

    /// <summary>The window-scoped navigation history service.</summary>
    public required NavigationHistoryService NavigationHistory { get; init; }

    /// <summary>Descriptor→tab / entity→tab creation pipeline.</summary>
    public required IWorkspaceTabFactory TabFactory { get; init; }

    /// <summary>True while the given pane is still a live member of <c>WorkspacePanes</c>.</summary>
    public required Func<WorkspacePaneViewModel, bool> IsPaneLive { get; init; }

    /// <summary>Makes the given pane the selected workspace pane.</summary>
    public required Action<WorkspacePaneViewModel> SelectPane { get; init; }

    /// <summary>
    /// Runs window-global MRU navigation after the pane closes its active tab (consults the single
    /// navigation-history service and may switch panes). Kept on <see cref="MainWindowViewModel"/>.
    /// </summary>
    public required Action RunMruNavigationAfterActiveClose { get; init; }
}
