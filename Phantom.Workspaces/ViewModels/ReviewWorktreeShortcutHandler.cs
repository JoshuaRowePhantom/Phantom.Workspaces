using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class ReviewWorktreeShortcutHandler : ShortcutHandler
{
    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return shortcut == Shortcut.Review
            && entityViewModel.IsEntityType("git-worktree");
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        var tab = new GitWorktreeReviewWorkspaceTabViewModel(entityViewModel)
        {
            Id = $"git-review-{entityViewModel.EntityId}",
            Title = $"Review — {entityViewModel.DisplayName}",
            DockRegion = "full",
            Entity = entityViewModel,
            TabHeader = TabHeaderViewModel.WithIcon("±", $"Review — {entityViewModel.DisplayName}"),
        };
        await mainWindowViewModel.OpenTabAsync(tab);
        return true;
    }
}
