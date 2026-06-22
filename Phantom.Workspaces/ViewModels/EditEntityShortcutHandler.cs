using System;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Shortcut handler for the Edit (✎) action. Applies only to entities that are editable
/// (<see cref="SubscribedEntityViewModel.CanEditEntity"/>) and, when handled, puts the owning
/// card node into edit mode via the supplied node locator.
/// </summary>
public sealed class EditEntityShortcutHandler : ShortcutHandler
{
    private readonly Func<SubscribedEntityViewModel, EntityListNodeViewModel?>? cardNodeLocator;

    public EditEntityShortcutHandler(
        Func<SubscribedEntityViewModel, EntityListNodeViewModel?>? cardNodeLocator = null)
    {
        this.cardNodeLocator = cardNodeLocator;
    }

    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return shortcut == Shortcut.Edit && entityViewModel.CanEditEntity;
    }

    public override Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        var cardNode = this.cardNodeLocator?.Invoke(entityViewModel);
        if (cardNode is null)
        {
            return Task.FromResult(false);
        }

        cardNode.EnterEditMode();
        return Task.FromResult(true);
    }
}
