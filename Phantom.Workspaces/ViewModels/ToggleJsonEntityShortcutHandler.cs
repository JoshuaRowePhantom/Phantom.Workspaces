using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class ToggleJsonEntityShortcutHandler : ShortcutHandler
{
    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return shortcut == Shortcut.Json
            && entityViewModel.CanToggleRawJson;
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
