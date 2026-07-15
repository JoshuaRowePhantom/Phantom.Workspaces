using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class CloneEntityShortcutHandler : ShortcutHandler
{
    public override ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return ValueTask.FromResult(shortcut == Shortcut.Clone
            && entityViewModel.CanEditEntity);
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        var tab = CloneEntityWorkspaceTabViewModel.Create(entityViewModel, mainWindowViewModel);
        await mainWindowViewModel.OpenTabAsync(tab);
        return true;
    }
}


