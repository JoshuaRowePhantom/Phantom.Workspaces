using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Testing.Gui;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorktreeCommitListViewModelTests : IDisposable
{
    private readonly TempDirectory temp = new("pw-commit-list-");
    private string repoDir => this.temp.Path;

    public void Dispose()
    {
        this.temp.Dispose();
    }

    private Repository InitRepo(out Signature sig)
    {
        Repository.Init(this.repoDir);
        var repo = new Repository(this.repoDir);
        sig = new Signature("tester", "tester@example.com", DateTimeOffset.UtcNow);
        return repo;
    }

    private static Commit MakeCommit(Repository repo, Signature sig, string fileName, string content, string message)
    {
        var filePath = Path.Combine(repo.Info.WorkingDirectory, fileName);
        File.WriteAllText(filePath, content);
        Commands.Stage(repo, fileName);
        return repo.Commit(message, sig, sig, new CommitOptions { AllowEmptyCommit = true });
    }

    [Fact]
    public async Task PopulatesCommitsNotInTargetBranch()
    {
        string targetBranchName;
        using (var repo = this.InitRepo(out var sig))
        {
            var initial = MakeCommit(repo, sig, "readme.txt", "initial", "Initial commit");
            // Use the default branch (e.g. "main") as the target.
            targetBranchName = repo.Head.FriendlyName;

            // Branch off from the initial commit and add feature commits.
            var featureBranch = repo.CreateBranch("feature", initial);
            Commands.Checkout(repo, featureBranch);
            MakeCommit(repo, sig, "feature1.txt", "a", "Feature commit 1");
            MakeCommit(repo, sig, "feature2.txt", "b", "Feature commit 2");
        }

        var vm = new GitWorktreeCommitListViewModel();
        await vm.RefreshAsync(this.repoDir, targetBranchName, TaskScheduler.Default, TestContext.Current.CancellationToken);

        var regularCommits = vm.Commits.Where(c => !c.IsUnstaged && !c.IsStaged).ToList();
        Assert.Equal(2, regularCommits.Count);
        Assert.Equal("Feature commit 2", regularCommits[0].ShortMessage);
        Assert.Equal("Feature commit 1", regularCommits[1].ShortMessage);
    }

    [Fact]
    public async Task UnstagedChangesAppearsAsFirstEntry()
    {
        string targetBranchName;
        using (var repo = this.InitRepo(out var sig))
        {
            MakeCommit(repo, sig, "readme.txt", "initial", "Initial commit");
            targetBranchName = repo.Head.FriendlyName;
            // Modify without staging to create an unstaged change.
            File.WriteAllText(Path.Combine(repo.Info.WorkingDirectory, "readme.txt"), "modified");
        }

        var vm = new GitWorktreeCommitListViewModel();
        await vm.RefreshAsync(this.repoDir, targetBranchName, TaskScheduler.Default, TestContext.Current.CancellationToken);

        Assert.NotEmpty(vm.Commits);
        Assert.True(vm.Commits[0].IsUnstaged);
    }

    [Fact]
    public async Task StagedChangesAppearsAsSecondEntry()
    {
        string targetBranchName;
        using (var repo = this.InitRepo(out var sig))
        {
            // Initial commit with two tracked files.
            var dir = repo.Info.WorkingDirectory;
            File.WriteAllText(Path.Combine(dir, "file1.txt"), "original1\n");
            File.WriteAllText(Path.Combine(dir, "file2.txt"), "original2\n");
            Commands.Stage(repo, "file1.txt");
            Commands.Stage(repo, "file2.txt");
            repo.Commit("Initial commit", sig, sig, new CommitOptions { AllowEmptyCommit = true });
            targetBranchName = repo.Head.FriendlyName;

            // Unstaged: new untracked file (detected via status.Untracked).
            File.WriteAllText(Path.Combine(dir, "untracked.txt"), "untracked content");

            // Staged: modify an existing tracked file and stage it.
            File.WriteAllText(Path.Combine(dir, "file2.txt"), "modified2\n");
            Commands.Stage(repo, "file2.txt");
        }

        var vm = new GitWorktreeCommitListViewModel();
        await vm.RefreshAsync(this.repoDir, targetBranchName, TaskScheduler.Default, TestContext.Current.CancellationToken);

        Assert.True(vm.Commits.Count >= 2);
        Assert.True(vm.Commits[0].IsUnstaged);
        Assert.True(vm.Commits[1].IsStaged);
    }

    [Fact]
    public async Task NeitherStagedNorUnstagedEntryWhenWorktreeIsClean()
    {
        string targetBranchName;
        using (var repo = this.InitRepo(out var sig))
        {
            MakeCommit(repo, sig, "readme.txt", "initial", "Initial commit");
            targetBranchName = repo.Head.FriendlyName;
        }

        var vm = new GitWorktreeCommitListViewModel();
        await vm.RefreshAsync(this.repoDir, targetBranchName, TaskScheduler.Default, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(vm.Commits, c => c.IsUnstaged);
        Assert.DoesNotContain(vm.Commits, c => c.IsStaged);
    }

    [Fact]
    public async Task RefreshAsyncUpdatesCommitsInPlaceWithoutClearingSelection()
    {
        string targetBranchName;
        using (var repo = this.InitRepo(out var sig))
        {
            var initial = MakeCommit(repo, sig, "readme.txt", "initial", "Initial commit");
            targetBranchName = repo.Head.FriendlyName;

            // Create a feature branch with one additional commit.
            var featureBranch = repo.CreateBranch("feature", initial);
            Commands.Checkout(repo, featureBranch);
            MakeCommit(repo, sig, "feature.txt", "f", "Feature commit");
        }

        var vm = new GitWorktreeCommitListViewModel();
        await vm.RefreshAsync(this.repoDir, targetBranchName, TaskScheduler.Default, TestContext.Current.CancellationToken);

        Assert.NotEmpty(vm.Commits);
        var featureCommit = vm.Commits.First(c => !c.IsUnstaged && !c.IsStaged);
        vm.SelectedCommits.Add(featureCommit);

        // Second refresh — selection should be preserved by OID.
        await vm.RefreshAsync(this.repoDir, targetBranchName, TaskScheduler.Default, TestContext.Current.CancellationToken);

        Assert.Single(vm.SelectedCommits);
        Assert.Equal(featureCommit.Oid, vm.SelectedCommits[0].Oid);
    }

    // ---------------------------------------------------------------------
    // #1210: threading tests
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_RunsGitStatusAndLogOffForegroundScheduler()
    {
        string targetBranchName;
        using (var repo = this.InitRepo(out var sig))
        {
            var initial = MakeCommit(repo, sig, "readme.txt", "initial", "Initial commit");
            targetBranchName = repo.Head.FriendlyName;
            var featureBranch = repo.CreateBranch("feature", initial);
            Commands.Checkout(repo, featureBranch);
            for (var i = 0; i < 5; i++)
            {
                MakeCommit(repo, sig, $"f{i}.txt", $"v{i}", $"Commit {i}");
            }
        }

        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var pumpThreadId = pump.ThreadId;
        var foregroundScheduler = await pump.PostAsync(() =>
            Task.FromResult(TaskScheduler.FromCurrentSynchronizationContext()));
        var vm = new GitWorktreeCommitListViewModel();

        // Capture the managed thread id of the Task.Run-scheduled git work via the test hook.
        // Asserting this differs from the pump thread proves the git status + log did not run on
        // the foreground scheduler — the actual behaviour the test name claims — without relying
        // on the pump queue's FIFO ordering between an externally-timed continuation and a
        // locally-posted ping. See #1284.
        int? gitWorkThreadId = null;
        GitWorktreeCommitListViewModel.GitWorkStartedForTests = () =>
            gitWorkThreadId = Environment.CurrentManagedThreadId;
        try
        {
            var refreshTask = await pump.PostAsync(() => Task.FromResult(
                vm.RefreshAsync(this.repoDir, targetBranchName, foregroundScheduler, TestContext.Current.CancellationToken)));
            await refreshTask.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        finally
        {
            GitWorktreeCommitListViewModel.GitWorkStartedForTests = null;
        }

        Assert.NotNull(gitWorkThreadId);
        Assert.NotEqual(pumpThreadId, gitWorkThreadId!.Value);
        Assert.NotEmpty(vm.Commits);
    }

    [Fact]
    public async Task RefreshAsync_AppliesCollectionMutationsOnForegroundScheduler()
    {
        string targetBranchName;
        using (var repo = this.InitRepo(out var sig))
        {
            var initial = MakeCommit(repo, sig, "readme.txt", "initial", "Initial commit");
            targetBranchName = repo.Head.FriendlyName;
            var featureBranch = repo.CreateBranch("feature", initial);
            Commands.Checkout(repo, featureBranch);
            MakeCommit(repo, sig, "feature.txt", "f", "Feature commit");
        }

        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var pumpThreadId = pump.ThreadId;
        var foregroundScheduler = await pump.PostAsync(() =>
            Task.FromResult(TaskScheduler.FromCurrentSynchronizationContext()));
        var vm = new GitWorktreeCommitListViewModel();

        var observedThreadIds = new System.Collections.Concurrent.ConcurrentBag<int>();
        vm.Commits.CollectionChanged += (_, _) => observedThreadIds.Add(Environment.CurrentManagedThreadId);
        vm.SelectedCommits.CollectionChanged += (_, _) => observedThreadIds.Add(Environment.CurrentManagedThreadId);

        var refreshTaskTcs = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Context.Post(_ => refreshTaskTcs.SetResult(
            vm.RefreshAsync(this.repoDir, targetBranchName, foregroundScheduler, TestContext.Current.CancellationToken)), null);
        await (await refreshTaskTcs.Task);

        Assert.NotEmpty(observedThreadIds);
        Assert.All(observedThreadIds, id => Assert.Equal(pumpThreadId, id));
    }
}


