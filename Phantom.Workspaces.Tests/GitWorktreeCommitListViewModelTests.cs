using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorktreeCommitListViewModelTests : IDisposable
{
    private readonly string repoDir;

    public GitWorktreeCommitListViewModelTests()
    {
        this.repoDir = Path.Combine(Path.GetTempPath(), "pw-commit-list-" + Guid.NewGuid().ToString("N"));
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
        await vm.RefreshAsync(this.repoDir, targetBranchName, TestContext.Current.CancellationToken);

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
        await vm.RefreshAsync(this.repoDir, targetBranchName, TestContext.Current.CancellationToken);

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
        await vm.RefreshAsync(this.repoDir, targetBranchName, TestContext.Current.CancellationToken);

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
        await vm.RefreshAsync(this.repoDir, targetBranchName, TestContext.Current.CancellationToken);

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
        await vm.RefreshAsync(this.repoDir, targetBranchName, TestContext.Current.CancellationToken);

        Assert.NotEmpty(vm.Commits);
        var featureCommit = vm.Commits.First(c => !c.IsUnstaged && !c.IsStaged);
        vm.SelectedCommits.Add(featureCommit);

        // Second refresh — selection should be preserved by OID.
        await vm.RefreshAsync(this.repoDir, targetBranchName, TestContext.Current.CancellationToken);

        Assert.Single(vm.SelectedCommits);
        Assert.Equal(featureCommit.Oid, vm.SelectedCommits[0].Oid);
    }
}
