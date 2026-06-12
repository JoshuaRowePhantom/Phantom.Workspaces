using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class DeleteEntityShortcutHandler : ShortcutHandler
{
    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return shortcut == Shortcut.Delete
            && entityViewModel.CanDeleteEntity;
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        await entityViewModel.DeleteEntityAsync();
        return true;
    }
}
