using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorktreeFileListViewModelTests : IDisposable
{
    private readonly string repoDir;

    public GitWorktreeFileListViewModelTests()
    {
        this.repoDir = Path.Combine(Path.GetTempPath(), "pw-file-list-" + Guid.NewGuid().ToString("N"));
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
        await vm.RefreshAsync(this.repoDir, [commit1, commit2], TestContext.Current.CancellationToken);

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
        await vm.RefreshAsync(this.repoDir, [commit2], TestContext.Current.CancellationToken);

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
        await vm.RefreshAsync(this.repoDir, [modifyCommit], TestContext.Current.CancellationToken);

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
        await vm.RefreshAsync(this.repoDir, [GitCommitModel.CreateUnstaged()], TestContext.Current.CancellationToken);

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
        await vm.RefreshAsync(this.repoDir, [GitCommitModel.CreateStaged()], TestContext.Current.CancellationToken);

        var paths = vm.Files.Select(f => f.RelativePath).ToHashSet();
        Assert.Contains("staged-new.txt", paths);
    }
}
