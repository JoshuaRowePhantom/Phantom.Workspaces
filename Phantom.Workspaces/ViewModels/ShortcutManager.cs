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
}


