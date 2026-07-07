using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dock.Model.Controls;
using Dock.Model.Core;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ReviewWorktreeShortcutHandlerTests
{
    [Fact]
    public async Task ShouldApplyTo_ReturnsTrueForReviewShortcutOnGitWorktreeEntity()
    {
        var handler = new ReviewWorktreeShortcutHandler();
        await using var vm = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateGitWorktreeEntity();

        Assert.True(handler.ShouldApplyTo(vm, Shortcut.Review, entity));
    }

    [Fact]
    public async Task ShouldApplyTo_ReturnsFalseForOpenShortcutOnGitWorktreeEntity()
    {
        var handler = new ReviewWorktreeShortcutHandler();
        await using var vm = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateGitWorktreeEntity();

        Assert.False(handler.ShouldApplyTo(vm, Shortcut.Open, entity));
    }

    [Fact]
    public async Task ShouldApplyTo_ReturnsFalseForReviewShortcutOnOtherEntityType()
    {
        var handler = new ReviewWorktreeShortcutHandler();
        await using var vm = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateNoteEntity();

        Assert.False(handler.ShouldApplyTo(vm, Shortcut.Review, entity));
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OpensGitWorktreeReviewTabWithCorrectId()
    {
        await using var vm = new MainWindowViewModel(new UnknownRepositorySource());
        await vm.InitializeAsync();

        var handler = new ReviewWorktreeShortcutHandler();
        var entity = CreateGitWorktreeEntity();

        await handler.Handle(vm, Shortcut.Review, entity);

        var documentDock = FindDocumentDock(vm);
        var reviewTab = documentDock?.VisibleDockables
            ?.OfType<WorkspaceDocument>()
            .Select(d => d.TabViewModel)
            .OfType<GitWorktreeReviewWorkspaceTabViewModel>()
            .FirstOrDefault();

        Assert.NotNull(reviewTab);
        Assert.Equal($"git-review-{entity.EntityId}", reviewTab.Id);
        Assert.Same(entity, reviewTab.Entity);

        await reviewTab.DisposeAsync();
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task Handle_DeduplicatesTabWhenAlreadyOpen()
    {
        await using var vm = new MainWindowViewModel(new UnknownRepositorySource());
        await vm.InitializeAsync();

        var handler = new ReviewWorktreeShortcutHandler();
        var entity = CreateGitWorktreeEntity();

        await handler.Handle(vm, Shortcut.Review, entity);
        await handler.Handle(vm, Shortcut.Review, entity);

        var documentDock = FindDocumentDock(vm);
        var reviewTabs = documentDock?.VisibleDockables
            ?.OfType<WorkspaceDocument>()
            .Select(d => d.TabViewModel)
            .OfType<GitWorktreeReviewWorkspaceTabViewModel>()
            .ToList();

        Assert.NotNull(reviewTabs);
        Assert.Single(reviewTabs);

        await reviewTabs[0].DisposeAsync();
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task Handle_WhenRepositoryPathMissing_StillOpensTabWithEmptyPath()
    {
        await using var vm = new MainWindowViewModel(new UnknownRepositorySource());
        await vm.InitializeAsync();

        var handler = new ReviewWorktreeShortcutHandler();
        var entity = CreateGitWorktreeEntityWithoutPath();

        await handler.Handle(vm, Shortcut.Review, entity);

        var documentDock = FindDocumentDock(vm);
        var reviewTab = documentDock?.VisibleDockables
            ?.OfType<WorkspaceDocument>()
            .Select(d => d.TabViewModel)
            .OfType<GitWorktreeReviewWorkspaceTabViewModel>()
            .FirstOrDefault();

        Assert.NotNull(reviewTab);
        Assert.Equal(string.Empty, reviewTab.RepositoryPath);

        await reviewTab.DisposeAsync();
    }

    private static SubscribedEntityViewModel CreateGitWorktreeEntity()
    {
        using var document = JsonDocument.Parse("""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "my-worktree"]],
                "display-name": { "default": "My Worktree" }
            }
            """);

        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
    }

    private static SubscribedEntityViewModel CreateGitWorktreeEntityWithoutPath()
    {
        using var document = JsonDocument.Parse("""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "my-worktree"]],
                "display-name": { "default": "My Worktree" }
            }
            """);

        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
    }

    private static SubscribedEntityViewModel CreateNoteEntity()
    {
        using var document = JsonDocument.Parse("""
            {
                "entity-id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "entity-types": ["entity", "note"],
                "names": [["notes", "test"]],
                "display-name": { "default": "Test Note" }
            }
            """);

        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
    }

    private static IDocumentDock? FindDocumentDock(MainWindowViewModel viewModel)
    {
        var contentLayout = viewModel.SelectedWorkspacePane?.ContentLayout;
        if (contentLayout is null)
        {
            return null;
        }

        return FindDocumentDockIn(contentLayout);
    }

    private static IDocumentDock? FindDocumentDockIn(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = FindDocumentDockIn(child);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }
}
