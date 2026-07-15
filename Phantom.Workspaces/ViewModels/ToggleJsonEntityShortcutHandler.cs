using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class ToggleJsonEntityShortcutHandler : ShortcutHandler
{
    public override ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return ValueTask.FromResult(shortcut == Shortcut.Json
            && entityViewModel.CanToggleRawJson);
    }

    public override Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        entityViewModel.ToggleRawJsonVisibility();
        return Task.FromResult(true);
    }
}


