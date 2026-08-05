using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public abstract class ShortcutHandler
{
    public abstract ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel);

    public abstract Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel);

    /// <summary>
    /// Restore-aware factory used by the workspace-open/restore path to reconstruct an
    /// interactive tab for <paramref name="entityViewModel"/> from a saved workspace layout,
    /// preserving the persisted <paramref name="tabId"/>, <paramref name="title"/> and
    /// <paramref name="dockRegion"/>. Handlers that produce an interactive tab (shell,
    /// agent-session, external browser, …) override this to return a fully-typed
    /// <see cref="WorkspaceTabViewModel"/>; handlers that do not participate in workspace
    /// restore return <see langword="null"/> (the default) so the pipeline moves on to the
    /// next candidate — and ultimately to the generic entity card. Introduced in #1129 to
    /// route <see cref="MainWindowViewModel.CreateTabFromEntityAsync"/> and
    /// <see cref="MainWindowViewModel.TryReadWorkspaceTabContentAsync"/> through the
    /// shortcut pipeline instead of the previous hard-coded external / agent-session /
    /// default cascade.
    /// </summary>
    public virtual Task<WorkspaceTabViewModel?> TryCreateTabForRestoreAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel,
        string? tabId,
        string? title,
        string? dockRegion)
        => Task.FromResult<WorkspaceTabViewModel?>(null);
}

