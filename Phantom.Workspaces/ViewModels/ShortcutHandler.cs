using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public abstract class ShortcutHandler
{
    public abstract bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel);

    public abstract Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel);
}
