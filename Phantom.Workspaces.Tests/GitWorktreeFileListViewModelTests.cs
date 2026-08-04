using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Testing.Gui;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorktreeFileListViewModelTests : IDisposable
{
    private readonly TempDirectory temp = new("pw-file-list-");
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

    private static GitCommitModel ToModel(Commit c) =>
        new() { Oid = c.Sha, ShortMessage = c.MessageShort, AuthorName = c.Author.Name, AuthorDate = c.Author.When };

    [Fact]
    public async Task FileListReflectsAllCommitsWhenNoneSelected()
    {
        GitCommitModel commit1;
        GitCommitModel commit2;

        using (var repo = this.InitRepo(out var sig))
        {
            MakeCommit(repo, sig, "base.txt", "base", "Base commit");
            commit1 = ToModel(MakeCommit(repo, sig, "alpha.txt", "a", "Add alpha"));
            commit2 = ToModel(MakeCommit(repo, sig, "beta.txt", "b", "Add beta"));
        }

        var vm = new GitWorktreeFileListViewModel();
        await vm.RefreshAsync(this.repoDir, [commit1, commit2], TaskScheduler.Default, TestContext.Current.CancellationToken);

        var paths = vm.Files.Select(f => f.RelativePath).ToHashSet();
        Assert.Contains("alpha.txt", paths);
        Assert.Contains("beta.txt", paths);
    }

    [Fact]
    public async Task FileListReflectsOnlyFilesInSelectedCommits()
    {
        GitCommitModel commit2;

        using (var repo = this.InitRepo(out var sig))
        {
            MakeCommit(repo, sig, "base.txt", "base", "Base commit");
            MakeCommit(repo, sig, "alpha.txt", "a", "Add alpha");
            commit2 = ToModel(MakeCommit(repo, sig, "beta.txt", "b", "Add beta"));
        }

        var vm = new GitWorktreeFileListViewModel();
        // Pass only the second commit; only beta.txt should appear.
        await vm.RefreshAsync(this.repoDir, [commit2], TaskScheduler.Default, TestContext.Current.CancellationToken);

        var paths = vm.Files.Select(f => f.RelativePath).ToHashSet();
        Assert.DoesNotContain("alpha.txt", paths);
        Assert.Contains("beta.txt", paths);
    }

    [Fact]
    public async Task FileListShowsCorrectAddedRemovedCounts()
    {
        GitCommitModel modifyCommit;

        using (var repo = this.InitRepo(out var sig))
        {
            MakeCommit(repo, sig, "base.txt", "base", "Base commit");
            MakeCommit(repo, sig, "counter.txt", "line1\nline2\nline3\n", "Add counter");
            modifyCommit = ToModel(MakeCommit(repo, sig, "counter.txt", "line1\nline2\nline3\nline4\n", "Append line"));
        }

        var vm = new GitWorktreeFileListViewModel();
        await vm.RefreshAsync(this.repoDir, [modifyCommit], TaskScheduler.Default, TestContext.Current.CancellationToken);

        var entry = Assert.Single(vm.Files);
        Assert.Equal("counter.txt", entry.RelativePath);
        Assert.True(entry.LinesAdded > 0);
    }

    [Fact]
    public async Task UnstagedCommitSelectionIncludesWorkingDirectoryDiff()
    {
        using (var repo = this.InitRepo(out var sig))
        {
            MakeCommit(repo, sig, "file.txt", "original content\n", "Base commit");
            // Modify without staging.
            File.WriteAllText(Path.Combine(repo.Info.WorkingDirectory, "file.txt"), "modified content\nextra line\n");
        }

        var vm = new GitWorktreeFileListViewModel();
        await vm.RefreshAsync(this.repoDir, [GitCommitModel.CreateUnstaged()], TaskScheduler.Default, TestContext.Current.CancellationToken);

        var paths = vm.Files.Select(f => f.RelativePath).ToHashSet();
        Assert.Contains("file.txt", paths);
    }

    [Fact]
    public async Task StagedCommitSelectionIncludesIndexDiff()
    {
        using (var repo = this.InitRepo(out var sig))
        {
            MakeCommit(repo, sig, "file.txt", "original content\n", "Base commit");
            // Stage a new file.
            var newFilePath = Path.Combine(repo.Info.WorkingDirectory, "staged-new.txt");
            File.WriteAllText(newFilePath, "new staged content\n");
            Commands.Stage(repo, "staged-new.txt");
        }

        var vm = new GitWorktreeFileListViewModel();
        await vm.RefreshAsync(this.repoDir, [GitCommitModel.CreateStaged()], TaskScheduler.Default, TestContext.Current.CancellationToken);

        var paths = vm.Files.Select(f => f.RelativePath).ToHashSet();
        Assert.Contains("staged-new.txt", paths);
    }

    // ---------------------------------------------------------------------
    // #1210: threading tests
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_RunsDiffCompareOffForegroundScheduler()
    {
        var commits = new System.Collections.Generic.List<GitCommitModel>();
        using (var repo = this.InitRepo(out var sig))
        {
            MakeCommit(repo, sig, "base.txt", "base", "Base");
            for (var i = 0; i < 5; i++)
            {
                commits.Add(ToModel(MakeCommit(repo, sig, $"f{i}.txt", $"v{i}", $"Commit {i}")));
            }
        }

        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var foregroundScheduler = await pump.PostAsync(() =>
            Task.FromResult(TaskScheduler.FromCurrentSynchronizationContext()));
        var vm = new GitWorktreeFileListViewModel();

        var refreshTaskTcs = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Context.Post(_ => refreshTaskTcs.SetResult(
            vm.RefreshAsync(this.repoDir, commits, foregroundScheduler, TestContext.Current.CancellationToken)), null);
        var refreshTask = await refreshTaskTcs.Task;

        var pingRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Context.Post(_ => pingRan.SetResult(), null);

        var winner = await Task.WhenAny(pingRan.Task, refreshTask).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Same(pingRan.Task, winner);

        await refreshTask;
        Assert.NotEmpty(vm.Files);
    }

    [Fact]
    public async Task RefreshAsync_MergesEntriesAndAppliesFilesMutationsOnForegroundScheduler()
    {
        GitCommitModel commit;
        using (var repo = this.InitRepo(out var sig))
        {
            MakeCommit(repo, sig, "base.txt", "base", "Base");
            commit = ToModel(MakeCommit(repo, sig, "alpha.txt", "a", "Alpha"));
        }

        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var pumpThreadId = pump.ThreadId;
        var foregroundScheduler = await pump.PostAsync(() =>
            Task.FromResult(TaskScheduler.FromCurrentSynchronizationContext()));
        var vm = new GitWorktreeFileListViewModel();

        var observedThreadIds = new System.Collections.Concurrent.ConcurrentBag<int>();
        vm.Files.CollectionChanged += (_, _) => observedThreadIds.Add(Environment.CurrentManagedThreadId);
        vm.SelectedFiles.CollectionChanged += (_, _) => observedThreadIds.Add(Environment.CurrentManagedThreadId);

        var refreshTaskTcs = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Context.Post(_ => refreshTaskTcs.SetResult(
            vm.RefreshAsync(this.repoDir, [commit], foregroundScheduler, TestContext.Current.CancellationToken)), null);
        await (await refreshTaskTcs.Task);

        Assert.NotEmpty(observedThreadIds);
        Assert.All(observedThreadIds, id => Assert.Equal(pumpThreadId, id));
    }
}


