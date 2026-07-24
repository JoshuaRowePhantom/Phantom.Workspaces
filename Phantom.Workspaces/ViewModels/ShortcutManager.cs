using System.Collections.Generic;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class ShortcutManager
{
    private readonly List<ShortcutHandler> shortcutHandlers = [];
    // When adding a new Shortcut, also update ResolveShortcut and the entity_invoke_shortcut
    // tool description in WorkspaceGuiContextProvider.EntityInvokeShortcutTool.
    private readonly Shortcut[] shortcuts = [Shortcut.Open, Shortcut.OpenWorkspace, Shortcut.Edit, Shortcut.Clone, Shortcut.Review, Shortcut.VsCode, Shortcut.VsCodeWeb, Shortcut.Delete, Shortcut.StartAgentSession, Shortcut.StartShell];

    public async IAsyncEnumerable<Shortcut> GetShortcutsForAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel)
    {
        foreach (var shortcut in this.shortcuts)
        {
            foreach (var handler in this.shortcutHandlers)
            {
                if (await handler.ShouldApplyTo(mainWindowViewModel, shortcut, entityViewModel))
                {
                    yield return shortcut;
                    break;
                }
            }
        }
    }

    public void AddShortcutHandler(
        ShortcutHandler shortcutHandler)
    {
        this.shortcutHandlers.Add(shortcutHandler);
    }

    /// <summary>
    /// Test-only helper (see <c>InternalsVisibleTo(Phantom.Workspaces.Tests)</c>): replaces
    /// the first registered handler of type <typeparamref name="T"/> with
    /// <paramref name="replacement"/>, preserving the handler's position in the first-wins
    /// dispatch order. Used by #1129 restore/pipeline tests to swap the production shell
    /// handler for a fake-session variant so no real PTY is spawned.
    /// </summary>
    internal void ReplaceShortcutHandlerForTesting<T>(ShortcutHandler replacement)
        where T : ShortcutHandler
    {
        for (var i = 0; i < this.shortcutHandlers.Count; i++)
        {
            if (this.shortcutHandlers[i] is T)
            {
                this.shortcutHandlers[i] = replacement;
                return;
            }
        }

        this.shortcutHandlers.Add(replacement);
    }

    public async Task<bool> HandleShortcutAsync(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        foreach (var shortcutHandler in this.shortcutHandlers)
        {
            if (!await shortcutHandler.ShouldApplyTo(mainWindowViewModel, shortcut, entityViewModel))
            {
                continue;
            }

            if (await shortcutHandler.Handle(mainWindowViewModel, shortcut, entityViewModel))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Restore-aware counterpart to <see cref="HandleShortcutAsync"/>: iterates handlers
    /// first-wins by <see cref="ShortcutHandler.ShouldApplyTo"/> and asks the matching
    /// handler to produce a <see cref="WorkspaceTabViewModel"/> for the persisted tab
    /// metadata (<paramref name="tabId"/>, <paramref name="title"/>,
    /// <paramref name="dockRegion"/>). Returns <see langword="null"/> when no interactive
    /// handler claims the entity so the caller can fall back to the generic entity card.
    /// Introduced in #1129 so the workspace-open/restore path routes through the same
    /// pipeline as the top-level <see cref="Shortcut.Open"/> shortcut (fix for shell
    /// entities restored via saved workspace layouts).
    /// </summary>
    public async Task<WorkspaceTabViewModel?> TryCreateTabForRestoreAsync(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel,
        string? tabId,
        string? title,
        string? dockRegion)
    {
        foreach (var shortcutHandler in this.shortcutHandlers)
        {
            if (!await shortcutHandler.ShouldApplyTo(mainWindowViewModel, shortcut, entityViewModel))
            {
                continue;
            }

            var tab = await shortcutHandler.TryCreateTabForRestoreAsync(
                mainWindowViewModel, entityViewModel, tabId, title, dockRegion);
            if (tab is not null)
            {
                return tab;
            }
        }

        return null;
    }
}


