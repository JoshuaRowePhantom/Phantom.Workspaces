using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class ShortcutManager
{
    private readonly List<ShortcutHandler> shortcutHandlers = [];
    // When adding a new Shortcut, also update ResolveShortcut and the entity_invoke_shortcut
    // tool description in WorkspaceGuiContextProvider.EntityInvokeShortcutTool.
    private readonly Shortcut[] shortcuts = [Shortcut.Open, Shortcut.OpenWorkspace, Shortcut.Edit, Shortcut.Clone, Shortcut.Review, Shortcut.VsCode, Shortcut.VsCodeWeb, Shortcut.Delete, Shortcut.StartAgentSession, Shortcut.StartShell];

    public IEnumerable<Shortcut> GetShortcutsFor(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel)
    {
        foreach (var shortcut in this.shortcuts)
        {
            foreach (var handler in this.shortcutHandlers)
            {
                if (handler.ShouldApplyTo(mainWindowViewModel, shortcut, entityViewModel).AsTask().GetAwaiter().GetResult())
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


