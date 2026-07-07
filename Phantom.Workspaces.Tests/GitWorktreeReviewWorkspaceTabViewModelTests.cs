using System;
using System.IO;
using System.Linq;
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

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task FileList_SelectFile_DiffViewUpdatesToSelectedFile()
    {
        this.InitRepoWithBranch("main");

        using var repo = new Repository(this.repoDir);
        var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

        File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "file1 content");
        File.WriteAllText(Path.Combine(this.repoDir, "file2.txt"), "file2 content");
        Commands.Stage(repo, "*");
        repo.Commit("Add files", sig, sig);

        File.AppendAllText(Path.Combine(this.repoDir, "file1.txt"), "\nfile1 change");
        File.AppendAllText(Path.Combine(this.repoDir, "file2.txt"), "\nfile2 change");

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
            await Task.Yield();

            var diffRebuildCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffRebuildCompleted.TrySetResult(true);

            var file1 = vm.FileList.Files.FirstOrDefault(f => f.RelativePath.Contains("file1"));
            Assert.NotNull(file1);
            vm.FileList.SelectedFiles.Add(file1);

            await diffRebuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Single(vm.FileDiffs);
            Assert.Contains("file1", vm.FileDiffs[0].RelativePath);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task FileList_SelectSecondFile_DiffViewChanges()
    {
        this.InitRepoWithBranch("main");

        using var repo = new Repository(this.repoDir);
        var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

        File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "file1 content");
        File.WriteAllText(Path.Combine(this.repoDir, "file2.txt"), "file2 content");
        Commands.Stage(repo, "*");
        repo.Commit("Add files", sig, sig);

        File.AppendAllText(Path.Combine(this.repoDir, "file1.txt"), "\nfile1 change");
        File.AppendAllText(Path.Combine(this.repoDir, "file2.txt"), "\nfile2 change");

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
            await Task.Yield();

            var file1 = vm.FileList.Files.FirstOrDefault(f => f.RelativePath.Contains("file1"));
            Assert.NotNull(file1);

            var diffRebuildCompleted1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffRebuildCompleted1.TrySetResult(true);
            vm.FileList.SelectedFiles.Add(file1);
            await diffRebuildCompleted1.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Single(vm.FileDiffs);
            Assert.Contains("file1", vm.FileDiffs[0].RelativePath);

            var file2 = vm.FileList.Files.FirstOrDefault(f => f.RelativePath.Contains("file2"));
            Assert.NotNull(file2);

            var diffRebuildCompleted2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffRebuildCompleted2.TrySetResult(true);
            vm.FileList.SelectedFiles.Clear();
            vm.FileList.SelectedFiles.Add(file2);
            await diffRebuildCompleted2.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Single(vm.FileDiffs);
            Assert.Contains("file2", vm.FileDiffs[0].RelativePath);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task FileList_NoFileSelected_DiffViewShowsAllFiles()
    {
        this.InitRepoWithBranch("main");

        using var repo = new Repository(this.repoDir);
        var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

        File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "file1 content");
        File.WriteAllText(Path.Combine(this.repoDir, "file2.txt"), "file2 content");
        Commands.Stage(repo, "*");
        repo.Commit("Add files", sig, sig);

        File.AppendAllText(Path.Combine(this.repoDir, "file1.txt"), "\nfile1 change");
        File.AppendAllText(Path.Combine(this.repoDir, "file2.txt"), "\nfile2 change");

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
            await Task.Yield();

            var file1 = vm.FileList.Files.FirstOrDefault(f => f.RelativePath.Contains("file1"));
            Assert.NotNull(file1);

            var diffRebuildCompleted1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffRebuildCompleted1.TrySetResult(true);
            vm.FileList.SelectedFiles.Add(file1);
            await diffRebuildCompleted1.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Single(vm.FileDiffs);

            var diffRebuildCompleted2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffRebuildCompleted2.TrySetResult(true);
            vm.FileList.SelectedFiles.Clear();
            await diffRebuildCompleted2.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Equal(2, vm.FileDiffs.Count);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task CommitList_SelectCommit_FileListUpdates()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "content1");
            Commands.Stage(repo, "*");
            repo.Commit("Commit 1", sig, sig);

            File.WriteAllText(Path.Combine(this.repoDir, "file2.txt"), "content2");
            Commands.Stage(repo, "*");
            repo.Commit("Commit 2", sig, sig);
        }

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        var vm = CreateViewModel($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}},
                "target-branch": "main"
            }
            """);

        await using (vm)
        {
            await Task.Yield();

            Assert.Equal(2, vm.FileList.Files.Count);

            var commitToSelect = vm.CommitList.Commits.FirstOrDefault(c => !c.IsUnstaged && !c.IsStaged);
            Assert.NotNull(commitToSelect);

            var fileListRefreshCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileList.Files.CollectionChanged += (_, _) => fileListRefreshCompleted.TrySetResult(true);

            vm.CommitList.SelectedCommits.Add(commitToSelect);

            await fileListRefreshCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Single(vm.FileList.Files);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task CommitList_SelectMultipleCommits_FileListShowsUnion()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "content1");
            Commands.Stage(repo, "*");
            repo.Commit("Commit 1", sig, sig);

            File.WriteAllText(Path.Combine(this.repoDir, "file2.txt"), "content2");
            Commands.Stage(repo, "*");
            repo.Commit("Commit 2", sig, sig);
        }

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        var vm = CreateViewModel($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}},
                "target-branch": "main"
            }
            """);

        await using (vm)
        {
            await Task.Yield();

            var commits = vm.CommitList.Commits.Where(c => !c.IsUnstaged && !c.IsStaged).ToList();
            Assert.Equal(2, commits.Count);

            var fileListRefreshCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileList.Files.CollectionChanged += (_, _) => fileListRefreshCompleted.TrySetResult(true);

            vm.CommitList.SelectedCommits.Add(commits[0]);
            vm.CommitList.SelectedCommits.Add(commits[1]);

            await fileListRefreshCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Equal(2, vm.FileList.Files.Count);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task CommitList_DeselectAll_FileListShowsAllCommits()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "content1");
            Commands.Stage(repo, "*");
            repo.Commit("Commit 1", sig, sig);

            File.WriteAllText(Path.Combine(this.repoDir, "file2.txt"), "content2");
            Commands.Stage(repo, "*");
            repo.Commit("Commit 2", sig, sig);
        }

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        var vm = CreateViewModel($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}},
                "target-branch": "main"
            }
            """);

        await using (vm)
        {
            await Task.Yield();

            var commit = vm.CommitList.Commits.FirstOrDefault(c => !c.IsUnstaged && !c.IsStaged);
            Assert.NotNull(commit);

            var fileListRefreshCompleted1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileList.Files.CollectionChanged += (_, _) => fileListRefreshCompleted1.TrySetResult(true);
            vm.CommitList.SelectedCommits.Add(commit);
            await fileListRefreshCompleted1.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Single(vm.FileList.Files);

            var fileListRefreshCompleted2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileList.Files.CollectionChanged += (_, _) => fileListRefreshCompleted2.TrySetResult(true);
            vm.CommitList.SelectedCommits.Clear();
            await fileListRefreshCompleted2.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Equal(2, vm.FileList.Files.Count);
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task CommitList_SelectionChange_DiffViewUpdates()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "content1");
            Commands.Stage(repo, "*");
            repo.Commit("Commit 1", sig, sig);

            File.WriteAllText(Path.Combine(this.repoDir, "file2.txt"), "content2");
            Commands.Stage(repo, "*");
            repo.Commit("Commit 2", sig, sig);
        }

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        var vm = CreateViewModel($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}},
                "target-branch": "main"
            }
            """);

        await using (vm)
        {
            await Task.Yield();

            var commit = vm.CommitList.Commits.FirstOrDefault(c => !c.IsUnstaged && !c.IsStaged);
            Assert.NotNull(commit);

            var diffViewUpdatedCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffViewUpdatedCompleted.TrySetResult(true);

            vm.CommitList.SelectedCommits.Add(commit);

            await diffViewUpdatedCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Single(vm.FileDiffs);
        }
    }
}
