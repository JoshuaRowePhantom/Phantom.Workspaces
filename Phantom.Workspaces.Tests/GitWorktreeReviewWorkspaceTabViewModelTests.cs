using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LibGit2Sharp;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorktreeReviewWorkspaceTabViewModelTests : IDisposable
{
    private readonly string repoDir;

    public GitWorktreeReviewWorkspaceTabViewModelTests()
    {
        this.repoDir = Path.Combine(Path.GetTempPath(), "pw-review-tab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.repoDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.repoDir))
            {
                ForceDeleteDirectory(this.repoDir);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void ForceDeleteDirectory(string path)
    {
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }

    private void InitRepoWithBranch(string branchName)
    {
        Repository.Init(this.repoDir);
        using var repo = new Repository(this.repoDir);
        var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);
        var filePath = Path.Combine(repo.Info.WorkingDirectory, "readme.txt");
        File.WriteAllText(filePath, "initial");
        Commands.Stage(repo, "readme.txt");
        repo.Commit("Initial commit", sig, sig, new CommitOptions { AllowEmptyCommit = true });

        // Rename the current branch to the desired name if it is different.
        var currentBranchName = repo.Head.FriendlyName;
        if (!string.Equals(currentBranchName, branchName, StringComparison.Ordinal))
        {
            repo.Branches.Rename(currentBranchName, branchName);
        }
    }

    private static GitWorktreeReviewWorkspaceTabViewModel CreateViewModel(string entityJson)
    {
        using var document = JsonDocument.Parse(entityJson);
        var entity = new SubscribedEntityViewModel(new EntitySnapshot
        {
            EntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        });

        return new GitWorktreeReviewWorkspaceTabViewModel(entity)
        {
            Id = "test-id",
            Title = "Test",
            Entity = entity,
        };
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_ReadsCorrectRepositoryPathField()
    {
        var vm = CreateViewModel("""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": "/some/repo/path"
            }
            """);

        await using (vm)
        {
            Assert.Equal("/some/repo/path", vm.RepositoryPath);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_ReadsCorrectTargetBranchField()
    {
        var vm = CreateViewModel("""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "target-branch": "develop"
            }
            """);

        await using (vm)
        {
            Assert.Equal("develop", vm.TargetBranch);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_WhenPathMissing_DoesNotThrow()
    {
        var vm = CreateViewModel("""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" }
            }
            """);

        await using (vm)
        {
            Assert.Equal(string.Empty, vm.RepositoryPath);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task TargetBranchDefaultsToMainWhenBranchExists()
    {
        this.InitRepoWithBranch("main");

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        var vm = CreateViewModel($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}}
            }
            """);

        await using (vm)
        {
            Assert.Equal("main", vm.TargetBranch);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task TargetBranchDefaultsToMasterWhenMainAbsent()
    {
        this.InitRepoWithBranch("master");

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        var vm = CreateViewModel($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}}
            }
            """);

        await using (vm)
        {
            Assert.Equal("master", vm.TargetBranch);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task TargetBranchReadFromEntityDataWhenPresent()
    {
        var vm = CreateViewModel("""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "target-branch": "develop"
            }
            """);

        await using (vm)
        {
            Assert.Equal("develop", vm.TargetBranch);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task ChangingTargetBranchTriggersCommitListRefresh()
    {
        // Use no valid repo path so RefreshAsync completes quickly.
        var vm = CreateViewModel("""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" }
            }
            """);

        await using (vm)
        {
            // Allow the constructor's fire-and-forget RefreshAsync to complete.
            await Task.Yield();

            // Plant a sentinel commit to verify it is cleared when CommitList refreshes.
            var sentinel = new GitCommitModel
            {
                Oid = "sentinel",
                ShortMessage = "sentinel",
                AuthorName = string.Empty,
                AuthorDate = DateTimeOffset.MinValue,
            };
            vm.CommitList.Commits.Add(sentinel);
            Assert.Single(vm.CommitList.Commits);

            // Wait for IsRefreshing to complete its true→false cycle (event-driven sync).
            var refreshCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var wasRefreshing = false;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.IsRefreshing))
                {
                    if (vm.IsRefreshing)
                    {
                        wasRefreshing = true;
                    }
                    else if (wasRefreshing)
                    {
                        refreshCompleted.TrySetResult(true);
                    }
                }
            };

            vm.TargetBranch = "develop";

            await refreshCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            // Commits.Clear() is called during every RefreshAsync; the sentinel should be gone.
            Assert.Empty(vm.CommitList.Commits);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task DisposeAsyncStopsWatcher()
    {
        this.InitRepoWithBranch("main");

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        var vm = CreateViewModel($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}}
            }
            """);

        // Allow the constructor's initial refresh to complete.
        await Task.Yield();
        var refreshCountBeforeDispose = vm.CommitList.Commits.Count;

        await vm.DisposeAsync();

        // Any new refresh triggered after dispose should not happen.
        var refreshAfterDispose = 0;
        vm.CommitList.Commits.CollectionChanged += (_, _) => Interlocked.Increment(ref refreshAfterDispose);

        File.WriteAllText(Path.Combine(this.repoDir, "post-dispose.txt"), "should not trigger refresh");

        // Yield twice so any dispatcher-queued continuations could run (they shouldn't).
        await Dispatcher.UIThread.InvokeAsync(() => { });
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(0, refreshAfterDispose);
        _ = refreshCountBeforeDispose;
    }
}
