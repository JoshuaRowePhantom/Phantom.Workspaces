using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class ReviewWorktreeShortcutHandler : ShortcutHandler
{
    public override ValueTask<bool> ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        return ValueTask.FromResult(shortcut == Shortcut.Review
            && entityViewModel.IsEntityType("git-worktree"));
    }

    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        // #1210: capture the UI-thread scheduler from the shortcut invocation (Handle runs on the
        // Avalonia UI thread) so the review VM can marshal ObservableCollection updates back to it.
        var foregroundScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        var tab = new GitWorktreeReviewWorkspaceTabViewModel(entityViewModel, foregroundScheduler)
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


