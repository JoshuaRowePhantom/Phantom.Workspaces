using System;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class OpenEntityShortcutHandler : ShortcutHandler
{
    public override ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return ValueTask.FromResult(shortcut == Shortcut.Open);
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        if (entityViewModel.IsEntityType("workspace"))
        {
            await mainWindowViewModel.OpenWorkspaceAsync(
                new GetEntityRequest
                {
                    EntityId = entityViewModel.EntityId,
                });
            return true;
        }

        if (entityViewModel.IsEntityType("view"))
        {
            await mainWindowViewModel.OpenViewTabAsync(entityViewModel);
            return true;
        }

        await mainWindowViewModel.OpenEntityTabAsync(
            new GetEntityRequest
            {
                EntityId = entityViewModel.EntityId,
            });
        return true;
    }

    public override async Task<WorkspaceTabViewModel?> TryCreateTabForRestoreAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entityViewModel,
        string? tabId,
        string? title,
        string? dockRegion)
    {
        if (!entityViewModel.IsEntityType("view")
            || tabId?.StartsWith("view-definition-", StringComparison.Ordinal) == true)
        {
            return null;
        }

        return await mainWindowViewModel.CreateViewTabAsync(
            entityViewModel, tabId, title, dockRegion);
    }
}
