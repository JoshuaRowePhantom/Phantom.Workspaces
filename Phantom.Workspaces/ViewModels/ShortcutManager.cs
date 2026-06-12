using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class ShortcutManager
{
    private readonly List<ShortcutHandler> shortcutHandlers = [];
    private readonly Shortcut[] shortcuts = [Shortcut.Open];

    public IEnumerable<Shortcut> GetShortcutsFor(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel)
    {
        return this.shortcuts.Where(shortcut =>
            this.shortcutHandlers.Any(handler => handler.ShouldApplyTo(mainWindowViewModel, shortcut, entityViewModel)));
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
            if (!shortcutHandler.ShouldApplyTo(mainWindowViewModel, shortcut, entityViewModel))
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
