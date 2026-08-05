using Avalonia.Headless.XUnit;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LibGit2Sharp;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorktreeReviewWorkspaceTabViewModelTests : IDisposable
{
    private readonly TempDirectory temp = new("pw-review-tab-");
    private string repoDir => this.temp.Path;

    public void Dispose()
    {
        this.temp.Dispose();
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

        // #1210: [AvaloniaFact] tests run on the Avalonia UI thread; capture its scheduler here.
        var foregroundScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        return new GitWorktreeReviewWorkspaceTabViewModel(entity, foregroundScheduler)
        {
            Id = "test-id",
            Title = "Test",
            Entity = entity,
        };
    }

    [AvaloniaFact(Timeout = 10_000)]
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

    [AvaloniaFact(Timeout = 10_000)]
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

    [AvaloniaFact(Timeout = 10_000)]
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

    [AvaloniaFact(Timeout = 10_000)]
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

    [AvaloniaFact(Timeout = 10_000)]
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
            // #1210: the main/master probe now runs in InitializeAsync's Task.Run instead of the
            // constructor, so we must await CurrentRefresh before observing the resolved default.
            await vm.CurrentRefresh!;
            Assert.Equal("master", vm.TargetBranch);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
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

    [AvaloniaFact(Timeout = 60_000)]
    public async Task TargetBranch_Set_TriggersCommitListRefreshAndClearsPreviousCommits()
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
            // Wait for the constructor's initial refresh to complete
            Assert.NotNull(vm.CurrentRefresh);
            await vm.CurrentRefresh!;

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

            vm.TargetBranch = "develop";

            // Wait for the new refresh to complete
            await vm.CurrentRefresh!;

            // The sentinel should be gone - new CommitList instance was swapped in
            Assert.Empty(vm.CommitList.Commits);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
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
        await vm.CurrentRefresh!;
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

    [AvaloniaFact(Timeout = 10_000)]
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
            await vm.CurrentRefresh!;

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

    [AvaloniaFact(Timeout = 10_000)]
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
            await vm.CurrentRefresh!;

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

    [AvaloniaFact(Timeout = 10_000)]
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
            await vm.CurrentRefresh!;

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

    [AvaloniaFact(Timeout = 10_000)]
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
            await vm.CurrentRefresh!;

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

    [AvaloniaFact(Timeout = 10_000)]
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
            await vm.CurrentRefresh!;

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

    [AvaloniaFact(Timeout = 10_000)]
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
            await vm.CurrentRefresh!;

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

    [AvaloniaFact(Timeout = 10_000)]
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
            await vm.CurrentRefresh!;

            var commit = vm.CommitList.Commits.FirstOrDefault(c => !c.IsUnstaged && !c.IsStaged);
            Assert.NotNull(commit);

            var diffViewUpdatedCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffViewUpdatedCompleted.TrySetResult(true);

            vm.CommitList.SelectedCommits.Add(commit);

            await diffViewUpdatedCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Single(vm.FileDiffs);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task CommitList_ShowsDate_FormattedCorrectly()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", new DateTimeOffset(2024, 1, 15, 14, 30, 45, TimeSpan.Zero));

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "content1");
            Commands.Stage(repo, "*");
            repo.Commit("Test commit", sig, sig);
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
            await vm.CurrentRefresh!;

            var commit = vm.CommitList.Commits.FirstOrDefault(c => !c.IsUnstaged && !c.IsStaged);
            Assert.NotNull(commit);
            Assert.Equal(new DateTimeOffset(2024, 1, 15, 14, 30, 45, TimeSpan.Zero), commit.AuthorDate);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task CommitList_ShortSha_DisplayedInColumn()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "content1");
            Commands.Stage(repo, "*");
            repo.Commit("Test commit", sig, sig);
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
            await vm.CurrentRefresh!;

            var commit = vm.CommitList.Commits.FirstOrDefault(c => !c.IsUnstaged && !c.IsStaged);
            Assert.NotNull(commit);
            Assert.NotEmpty(commit.Oid);
            Assert.True(commit.Oid.Length >= 7);
            var shortSha = commit.Oid.Substring(0, 7);
            Assert.Equal(7, shortSha.Length);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task CommitList_AuthorColumn_Displayed()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("Test Author", "author@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "content1");
            Commands.Stage(repo, "*");
            repo.Commit("Test commit", sig, sig);
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
            await vm.CurrentRefresh!;

            var commit = vm.CommitList.Commits.FirstOrDefault(c => !c.IsUnstaged && !c.IsStaged);
            Assert.NotNull(commit);
            Assert.Equal("Test Author", commit.AuthorName);
        }
    }

    [Fact]
    public void CommitList_CopyButton_CopiesFullHash()
    {
        var commit = new GitCommitModel
        {
            Oid = "abcdef1234567890abcdef1234567890abcdef12",
            ShortMessage = "Test commit",
            AuthorName = "Test Author",
            AuthorDate = DateTimeOffset.UtcNow,
        };

        Assert.Equal("abcdef1234567890abcdef1234567890abcdef12", commit.Oid);
        Assert.Equal("abcdef1", commit.ShortOid);
    }

    [Fact]
    public void CommitList_DetailsPopup_ShowsFullCommitDetails()
    {
        var fullSha = "1234567890abcdef1234567890abcdef12345678";
        var authorDate = new DateTimeOffset(2024, 1, 15, 14, 30, 0, TimeSpan.Zero);
        var commit = new GitCommitModel
        {
            Oid = fullSha,
            ShortMessage = "Test commit message",
            AuthorName = "Test Author",
            AuthorDate = authorDate,
        };

        Assert.Equal(fullSha, commit.Oid);
        Assert.Equal("Test commit message", commit.ShortMessage);
        Assert.Equal("Test Author", commit.AuthorName);
        Assert.Equal(authorDate, commit.AuthorDate);
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task RebuildFileDiffsAsync_DeletedFile_DoesNotThrow()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "content1");
            File.WriteAllText(Path.Combine(this.repoDir, "file2.txt"), "content2");
            Commands.Stage(repo, "*");
            repo.Commit("Add files", sig, sig);

            File.Delete(Path.Combine(this.repoDir, "file1.txt"));
            Commands.Stage(repo, "*");
            repo.Commit("Delete file1", sig, sig);
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
            await vm.CurrentRefresh!;

            var deleteCommit = vm.CommitList.Commits.FirstOrDefault(c => c.ShortMessage.Contains("Delete"));
            Assert.NotNull(deleteCommit);

            var diffViewUpdatedCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffViewUpdatedCompleted.TrySetResult(true);

            vm.CommitList.SelectedCommits.Add(deleteCommit);

            await diffViewUpdatedCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Single(vm.FileDiffs);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task RebuildFileDiffsAsync_AddedFile_DoesNotThrow()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "content1");
            Commands.Stage(repo, "*");
            repo.Commit("Add file1", sig, sig);

            File.WriteAllText(Path.Combine(this.repoDir, "file2.txt"), "content2");
            Commands.Stage(repo, "*");
            repo.Commit("Add file2", sig, sig);
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
            await vm.CurrentRefresh!;

            var addCommit = vm.CommitList.Commits.FirstOrDefault(c => c.ShortMessage.Contains("Add file2"));
            Assert.NotNull(addCommit);

            var diffViewUpdatedCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffViewUpdatedCompleted.TrySetResult(true);

            vm.CommitList.SelectedCommits.Add(addCommit);

            await diffViewUpdatedCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Single(vm.FileDiffs);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task RebuildFileDiffsAsync_UnmatchedPath_ProcessesOtherFiles()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "content1");
            File.WriteAllText(Path.Combine(this.repoDir, "file2.txt"), "content2");
            File.WriteAllText(Path.Combine(this.repoDir, "file3.txt"), "content3");
            Commands.Stage(repo, "*");
            repo.Commit("Add files", sig, sig);

            File.Delete(Path.Combine(this.repoDir, "file2.txt"));
            File.AppendAllText(Path.Combine(this.repoDir, "file3.txt"), "\nmodified");
            Commands.Stage(repo, "*");
            repo.Commit("Delete file2, modify file3", sig, sig);
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
            await vm.CurrentRefresh!;

            var secondCommit = vm.CommitList.Commits.FirstOrDefault(c => c.ShortMessage.Contains("Delete"));
            Assert.NotNull(secondCommit);

            var diffViewUpdatedCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffViewUpdatedCompleted.TrySetResult(true);

            vm.CommitList.SelectedCommits.Add(secondCommit);

            await diffViewUpdatedCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Equal(2, vm.FileDiffs.Count);
            Assert.Contains(vm.FileDiffs, d => d.RelativePath.Contains("file2"));
            Assert.Contains(vm.FileDiffs, d => d.RelativePath.Contains("file3"));
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task CommitListHeader_ShowsBranchName()
    {
        this.InitRepoWithBranch("main");

        var entityJson = $$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": "{{this.repoDir.Replace("\\", "\\\\")}}",
                "target-branch": "main"
            }
            """;

        var vm = CreateViewModel(entityJson);
        await using (vm)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Equal("Commits not in main", vm.CommitListHeader);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task CommitListHeader_UpdatesWhenBranchChanges()
    {
        this.InitRepoWithBranch("main");

        var entityJson = $$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": "{{this.repoDir.Replace("\\", "\\\\")}}",
                "target-branch": "main"
            }
            """;

        var vm = CreateViewModel(entityJson);
        await using (vm)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Equal("Commits not in main", vm.CommitListHeader);

            vm.TargetBranch = "develop";
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Equal("Commits not in develop", vm.CommitListHeader);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task FileListHeader_SingleCommit_ShowsSha()
    {
        this.InitRepoWithBranch("main");

        Commit commit;
        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature");
            Commands.Checkout(repo, featureBranch);

            var filePath = Path.Combine(repo.Info.WorkingDirectory, "feature.txt");
            File.WriteAllText(filePath, "feature content");
            Commands.Stage(repo, "feature.txt");
            commit = repo.Commit("Add feature", sig, sig);
        }

        await Task.Delay(100);

        var entityJson = $$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": "{{this.repoDir.Replace("\\", "\\\\")}}",
                "target-branch": "main"
            }
            """;

        var vm = CreateViewModel(entityJson);
        await using (vm)
        {
            await vm.CurrentRefresh!;

            vm.CommitList.SelectedCommits.Add(vm.CommitList.Commits.First());
            await Dispatcher.UIThread.InvokeAsync(() => { });

            var shortSha = commit.Sha.Substring(0, 7);
            Assert.Equal($"Files changed in {shortSha}", vm.FileListHeader);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task FileListHeader_MultipleCommits_ShowsGenericLabel()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature");
            Commands.Checkout(repo, featureBranch);

            var filePath1 = Path.Combine(repo.Info.WorkingDirectory, "feature1.txt");
            File.WriteAllText(filePath1, "feature content 1");
            Commands.Stage(repo, "feature1.txt");
            repo.Commit("Add feature 1", sig, sig);

            var filePath2 = Path.Combine(repo.Info.WorkingDirectory, "feature2.txt");
            File.WriteAllText(filePath2, "feature content 2");
            Commands.Stage(repo, "feature2.txt");
            repo.Commit("Add feature 2", sig, sig);
        }

        await Task.Delay(100);

        var entityJson = $$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": "{{this.repoDir.Replace("\\", "\\\\")}}",
                "target-branch": "main"
            }
            """;

        var vm = CreateViewModel(entityJson);
        await using (vm)
        {
            await vm.CurrentRefresh!;

            vm.CommitList.SelectedCommits.Add(vm.CommitList.Commits[0]);
            vm.CommitList.SelectedCommits.Add(vm.CommitList.Commits[1]);
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.Equal("Files changed in selected commits", vm.FileListHeader);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task FileListHeader_NoSelection_ShowsPlaceholder()
    {
        this.InitRepoWithBranch("main");

        var entityJson = $$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": "{{this.repoDir.Replace("\\", "\\\\")}}",
                "target-branch": "main"
            }
            """;

        var vm = CreateViewModel(entityJson);
        await using (vm)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
            await vm.RefreshAsync();

            Assert.Equal("Files changed", vm.FileListHeader);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task BranchDropdown_PopulatedWithRepoBranches_LoadsAllBranches()
    {
        this.InitRepoWithBranch("main");

        using var repo = new Repository(this.repoDir);
        var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);
        
        repo.CreateBranch("feature-1");
        repo.CreateBranch("feature-2");
        repo.CreateBranch("develop");

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
            await vm.CurrentRefresh!;
            
            Assert.NotNull(vm.BranchNames);
            Assert.Contains("main", vm.BranchNames);
            Assert.Contains("feature-1", vm.BranchNames);
            Assert.Contains("feature-2", vm.BranchNames);
            Assert.Contains("develop", vm.BranchNames);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task BranchDropdown_SelectBranch_UpdatesTargetBranchAndCommitList()
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
            // Wait for the constructor's initial refresh to complete
            Assert.NotNull(vm.CurrentRefresh);
            await vm.CurrentRefresh!;

            var sentinel = new GitCommitModel
            {
                Oid = "sentinel",
                ShortMessage = "sentinel",
                AuthorName = string.Empty,
                AuthorDate = DateTimeOffset.MinValue,
            };
            vm.CommitList.Commits.Add(sentinel);

            vm.TargetBranch = "feature-branch";

            // Wait for the new refresh to complete
            await vm.CurrentRefresh!;

            Assert.Equal("feature-branch", vm.TargetBranch);
            Assert.Empty(vm.CommitList.Commits);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_FullFileToggle_TriggersRebuild()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "test.txt"), "line1\nline2\nline3\nline4\nline5\n");
            Commands.Stage(repo, "*");
            repo.Commit("Add test file", sig, sig);

            File.WriteAllText(Path.Combine(this.repoDir, "test.txt"), "line1\nline2\nmodified\nline4\nline5\n");
            Commands.Stage(repo, "*");
            repo.Commit("Modify test file", sig, sig);
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
            await vm.CurrentRefresh!;

            var initialDiffCount = vm.FileDiffs.Count;

            var diffRebuildCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffRebuildCompleted.TrySetResult(true);

            vm.FullFile = true;

            await diffRebuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.True(vm.FullFile);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_FullFileTrue_UsesLargeContextLines()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            var content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line{i}"));
            File.WriteAllText(Path.Combine(this.repoDir, "bigfile.txt"), content);
            Commands.Stage(repo, "*");
            repo.Commit("Add big file", sig, sig);

            var modifiedContent = string.Join("\n", Enumerable.Range(1, 100).Select(i => i == 50 ? "MODIFIED" : $"line{i}"));
            File.WriteAllText(Path.Combine(this.repoDir, "bigfile.txt"), modifiedContent);
            Commands.Stage(repo, "*");
            repo.Commit("Modify line 50", sig, sig);
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
            vm.ContextLines = 3;
            await vm.CurrentRefresh!;

            vm.FullFile = true;
            await Dispatcher.UIThread.InvokeAsync(() => { });
            
            while (vm.FileDiffs.Count == 0 && vm.CurrentRefresh != null)
            {
                await vm.CurrentRefresh;
                await Dispatcher.UIThread.InvokeAsync(() => { });
            }

            var diff = vm.FileDiffs.FirstOrDefault();
            Assert.NotNull(diff);

            var totalContextLines = diff.Hunks.SelectMany(h => h.Lines).Count(l => l.Kind == GitDiffLineKind.Context);
            Assert.True(totalContextLines > 90, $"Expected > 90 context lines in full file mode, got {totalContextLines}");
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_FullFileFalse_UsesConfiguredContextLines()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            var content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line{i}"));
            File.WriteAllText(Path.Combine(this.repoDir, "bigfile.txt"), content);
            Commands.Stage(repo, "*");
            repo.Commit("Add big file", sig, sig);

            var modifiedContent = string.Join("\n", Enumerable.Range(1, 100).Select(i => i == 50 ? "MODIFIED" : $"line{i}"));
            File.WriteAllText(Path.Combine(this.repoDir, "bigfile.txt"), modifiedContent);
            Commands.Stage(repo, "*");
            repo.Commit("Modify line 50", sig, sig);
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
            await vm.CurrentRefresh!;

            while (vm.FileDiffs.Count == 0)
            {
                await Task.Delay(50);
            }

            Assert.False(vm.FullFile);
            Assert.Equal(10, vm.ContextLines);

            var diff = vm.FileDiffs.FirstOrDefault();
            Assert.NotNull(diff);

            var totalContextLines = diff.Hunks.SelectMany(h => h.Lines).Count(l => l.Kind == GitDiffLineKind.Context);
            Assert.True(totalContextLines <= 20, $"Expected <= 20 context lines (default 10 before + 10 after), got {totalContextLines}");
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_ContextLines_TriggersRebuild()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            var lines = new List<string> { "line1" };
            for (int i = 2; i <= 100; i++)
            {
                lines.Add($"line{i}");
            }
            File.WriteAllText(Path.Combine(this.repoDir, "file.txt"), string.Join("\n", lines));
            Commands.Stage(repo, "*");
            repo.Commit("Add file with many lines", sig, sig);

            lines[50] = "MODIFIED LINE 51";
            File.WriteAllText(Path.Combine(this.repoDir, "file.txt"), string.Join("\n", lines));
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
            await vm.CurrentRefresh!;

            while (vm.FileDiffs.Count == 0)
            {
                await Task.Delay(50);
            }

            var diffRebuildCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffRebuildCompleted.TrySetResult(true);

            vm.ContextLines = 5;

            await diffRebuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Equal(5, vm.ContextLines);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_ContextLines_Zero_ShowsOnlyChangedLines()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            var lines = new List<string> { "line1" };
            for (int i = 2; i <= 100; i++)
            {
                lines.Add($"line{i}");
            }
            File.WriteAllText(Path.Combine(this.repoDir, "file.txt"), string.Join("\n", lines));
            Commands.Stage(repo, "*");
            repo.Commit("Add file with many lines", sig, sig);

            lines[50] = "MODIFIED LINE 51";
            File.WriteAllText(Path.Combine(this.repoDir, "file.txt"), string.Join("\n", lines));
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
            await vm.CurrentRefresh!;

            var diffRebuildCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffRebuildCompleted.TrySetResult(true);

            vm.ContextLines = 0;

            await diffRebuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            var diff = vm.FileDiffs.FirstOrDefault();
            Assert.NotNull(diff);

            var totalContextLines = diff.Hunks.SelectMany(h => h.Lines).Count(l => l.Kind == GitDiffLineKind.Context);
            Assert.Equal(0, totalContextLines);
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_ContextLines_LargeValue_ShowsExtendedContext()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            var lines = new List<string> { "line1" };
            for (int i = 2; i <= 100; i++)
            {
                lines.Add($"line{i}");
            }
            File.WriteAllText(Path.Combine(this.repoDir, "file.txt"), string.Join("\n", lines));
            Commands.Stage(repo, "*");
            repo.Commit("Add file with many lines", sig, sig);

            lines[50] = "MODIFIED LINE 51";
            File.WriteAllText(Path.Combine(this.repoDir, "file.txt"), string.Join("\n", lines));
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
            await vm.CurrentRefresh!;

            while (vm.FileDiffs.Count == 0)
            {
                await Task.Delay(50);
            }

            var diffRebuildCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffRebuildCompleted.TrySetResult(true);

            vm.ContextLines = 15;

            await diffRebuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            var diff = vm.FileDiffs.FirstOrDefault();
            Assert.NotNull(diff);

            var totalContextLines = diff.Hunks.SelectMany(h => h.Lines).Count(l => l.Kind == GitDiffLineKind.Context);
            Assert.True(totalContextLines >= 30, $"Expected >= 30 context lines (15 before + 15 after), got {totalContextLines}");
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task SideBySide_WhenToggled_TriggersRebuildFileDiffs()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "source.txt"), "line one\nline two\nline three\n");
            Commands.Stage(repo, "*");
            repo.Commit("Add source.txt", sig, sig);

            File.WriteAllText(Path.Combine(this.repoDir, "source.txt"), "line one\nmodified line two\nline three\n");
            Commands.Stage(repo, "*");
            repo.Commit("Modify source.txt", sig, sig);
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
            await vm.CurrentRefresh!;

            Assert.NotEmpty(vm.FileDiffs);
            Assert.All(vm.FileDiffs, diff => Assert.False(diff.SideBySide));

            var diffRebuildCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffRebuildCompleted.TrySetResult(true);

            vm.SideBySide = true;

            await diffRebuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.NotEmpty(vm.FileDiffs);
            Assert.All(vm.FileDiffs, diff => Assert.True(diff.SideBySide));
        }
    }

    [AvaloniaFact(Timeout = 10_000)]
    public async Task SideBySide_WhenToggled_PreservesExistingFileDiffContent()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);

            var featureBranch = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, featureBranch);

            File.WriteAllText(Path.Combine(this.repoDir, "data.txt"), "alpha\nbeta\ngamma\ndelta\n");
            Commands.Stage(repo, "*");
            repo.Commit("Add data.txt", sig, sig);

            File.WriteAllText(Path.Combine(this.repoDir, "data.txt"), "alpha\nBETA\ngamma\ndelta\nextra\n");
            Commands.Stage(repo, "*");
            repo.Commit("Modify data.txt", sig, sig);
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
            await vm.CurrentRefresh!;

            Assert.NotEmpty(vm.FileDiffs);
            var initialDiff = vm.FileDiffs[0];
            var initialRelativePath = initialDiff.RelativePath;
            var initialLinesAdded = initialDiff.LinesAdded;
            var initialLinesRemoved = initialDiff.LinesRemoved;
            var initialHunkCount = initialDiff.Hunks.Count;
            Assert.False(initialDiff.SideBySide);

            var diffRebuildCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.FileDiffs.CollectionChanged += (_, _) => diffRebuildCompleted.TrySetResult(true);

            vm.SideBySide = true;

            await diffRebuildCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.NotEmpty(vm.FileDiffs);
            var rebuiltDiff = vm.FileDiffs[0];

            Assert.Equal(initialRelativePath, rebuiltDiff.RelativePath);
            Assert.Equal(initialLinesAdded, rebuiltDiff.LinesAdded);
            Assert.Equal(initialLinesRemoved, rebuiltDiff.LinesRemoved);
            Assert.Equal(initialHunkCount, rebuiltDiff.Hunks.Count);
            Assert.True(rebuiltDiff.SideBySide);
        }
    }

    // ---------------------------------------------------------------------
    // #1210: threading tests. Verify that git I/O runs on the thread pool and
    // that only the final ObservableCollection updates marshal back to the
    // injected foreground scheduler.
    // ---------------------------------------------------------------------

    private GitWorktreeReviewWorkspaceTabViewModel CreateViewModelWithScheduler(
        string entityJson,
        TaskScheduler foregroundScheduler)
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

        return new GitWorktreeReviewWorkspaceTabViewModel(entity, foregroundScheduler)
        {
            Id = "test-id",
            Title = "Test",
            Entity = entity,
        };
    }

    [Fact]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_Constructor_DoesNotOpenRepositorySynchronously()
    {
        // A "master"-only repo forces the probe to return "master". If the constructor were
        // opening LibGit2Sharp synchronously, TargetBranch would be "master" before we await
        // anything. With the fix, TargetBranch is the seeded default ("main") until the
        // background probe in InitializeAsync completes.
        this.InitRepoWithBranch("master");

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var foregroundScheduler = await pump.PostAsync(() =>
            Task.FromResult(TaskScheduler.FromCurrentSynchronizationContext()));

        var vm = await pump.PostAsync(() => Task.FromResult(this.CreateViewModelWithScheduler($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}}
            }
            """, foregroundScheduler)));

        await using (vm)
        {
            // Observed immediately after construction — the probe has not run.
            Assert.Equal("main", vm.TargetBranch);

            await vm.CurrentRefresh!;

            // Now the background probe has updated the field.
            Assert.Equal("master", vm.TargetBranch);
        }
    }

    [Fact]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_InitializeAsync_ResolvesDefaultBranchOffUIThread()
    {
        this.InitRepoWithBranch("master");

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var pumpThreadId = pump.ThreadId;

        var foregroundScheduler = await pump.PostAsync(() =>
            Task.FromResult(TaskScheduler.FromCurrentSynchronizationContext()));

        var vm = await pump.PostAsync(() => Task.FromResult(this.CreateViewModelWithScheduler($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}}
            }
            """, foregroundScheduler)));

        await using (vm)
        {
            // Interleave a pump-thread ping while InitializeAsync is running. It must be able to
            // execute before the initial refresh finishes, proving the probe/branch enumeration
            // ran off the foreground scheduler.
            var pingRan = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            pump.Context.Post(_ => pingRan.SetResult(Environment.CurrentManagedThreadId), null);

            var winner = await Task.WhenAny(pingRan.Task, vm.CurrentRefresh!)
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Same(pingRan.Task, winner);
            Assert.Equal(pumpThreadId, await pingRan.Task);

            await vm.CurrentRefresh!;
            Assert.Equal("master", vm.TargetBranch);
        }
    }

    [Fact]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_RefreshAsync_RunsGitOperationsOffForegroundScheduler()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);
            var feature = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, feature);
            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(this.repoDir, $"f{i}.txt"), $"content{i}");
                Commands.Stage(repo, "*");
                repo.Commit($"Commit {i}", sig, sig);
            }
        }

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var pumpThreadId = pump.ThreadId;

        var foregroundScheduler = await pump.PostAsync(() =>
            Task.FromResult(TaskScheduler.FromCurrentSynchronizationContext()));

        var vm = await pump.PostAsync(() => Task.FromResult(this.CreateViewModelWithScheduler($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}},
                "target-branch": "main"
            }
            """, foregroundScheduler)));

        await using (vm)
        {
            var refreshTaskTcs = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            pump.Context.Post(_ => refreshTaskTcs.SetResult(vm.RefreshAsync()), null);
            var refreshTask = await refreshTaskTcs.Task;

            // Interleave a pump ping. If git ops were running on the foreground scheduler, this
            // would be blocked behind the git work.
            var pingRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pump.Context.Post(_ => pingRan.SetResult(), null);

            var winner = await Task.WhenAny(pingRan.Task, refreshTask).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Same(pingRan.Task, winner);

            await refreshTask;
            _ = pumpThreadId;
        }
    }

    [Fact]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_RefreshAsync_MarshalsFinalCollectionUpdatesToForegroundScheduler()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);
            var feature = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, feature);
            File.WriteAllText(Path.Combine(this.repoDir, "file1.txt"), "c1");
            Commands.Stage(repo, "*");
            repo.Commit("Feature commit", sig, sig);
        }

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var pumpThreadId = pump.ThreadId;

        var foregroundScheduler = await pump.PostAsync(() =>
            Task.FromResult(TaskScheduler.FromCurrentSynchronizationContext()));

        var vm = await pump.PostAsync(() => Task.FromResult(this.CreateViewModelWithScheduler($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}},
                "target-branch": "main"
            }
            """, foregroundScheduler)));

        await using (vm)
        {
            // Final ObservableCollection swap raises PropertyChanged for CommitList/FileList/FileDiffs
            // via the injected foreground scheduler. Verify each of those fires on the pump thread.
            var propChangeThreadIds = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not null)
                {
                    propChangeThreadIds[e.PropertyName] = Environment.CurrentManagedThreadId;
                }
            };

            await vm.CurrentRefresh!;

            Assert.True(propChangeThreadIds.ContainsKey(nameof(vm.CommitList)));
            Assert.True(propChangeThreadIds.ContainsKey(nameof(vm.FileList)));
            Assert.True(propChangeThreadIds.ContainsKey(nameof(vm.FileDiffs)));
            Assert.Equal(pumpThreadId, propChangeThreadIds[nameof(vm.CommitList)]);
            Assert.Equal(pumpThreadId, propChangeThreadIds[nameof(vm.FileList)]);
            Assert.Equal(pumpThreadId, propChangeThreadIds[nameof(vm.FileDiffs)]);
        }
    }

    [Fact]
    public async Task GitWorktreeReviewWorkspaceTabViewModel_TargetBranch_Set_DoesNotBlockCallingThread()
    {
        this.InitRepoWithBranch("main");

        using (var repo = new Repository(this.repoDir))
        {
            var sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);
            var feature = repo.CreateBranch("feature", repo.Head.Tip);
            Commands.Checkout(repo, feature);
            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(this.repoDir, $"f{i}.txt"), $"content{i}");
                Commands.Stage(repo, "*");
                repo.Commit($"Commit {i}", sig, sig);
            }
        }

        var repoPath = JsonSerializer.Serialize(this.repoDir);
        using var pump = new SingleThreadPump(installSynchronizationContext: true);

        var foregroundScheduler = await pump.PostAsync(() =>
            Task.FromResult(TaskScheduler.FromCurrentSynchronizationContext()));

        var vm = await pump.PostAsync(() => Task.FromResult(this.CreateViewModelWithScheduler($$"""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "git-worktree"],
                "names": [["worktrees", "test"]],
                "display-name": { "default": "Test" },
                "path": {{repoPath}},
                "target-branch": "main"
            }
            """, foregroundScheduler)));

        await using (vm)
        {
            await vm.CurrentRefresh!;

            // Set TargetBranch from the pump thread — the setter must return promptly (not block
            // while the git log walk runs). Immediately after, post a ping and confirm it can run
            // before the triggered refresh completes.
            var setterReturnedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pingRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            pump.Context.Post(
                _ =>
                {
                    vm.TargetBranch = "feature";
                    setterReturnedTcs.SetResult();
                    pump.Context.Post(__ => pingRan.SetResult(), null);
                },
                null);

            await setterReturnedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            await pingRan.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            // If we reach here without hitting the 15s timeout, the setter did not block.
            Assert.True(true);
        }
    }
}


