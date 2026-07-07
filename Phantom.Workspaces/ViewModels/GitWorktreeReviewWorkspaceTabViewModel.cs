using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LibGit2Sharp;

namespace Phantom.Workspaces.ViewModels;

public sealed class GitWorktreeReviewWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private string targetBranch = "main";
    private bool sideBySide;
    private bool fullFile;
    private int contextLines = 10;
    private bool isRefreshing;
    private CancellationTokenSource? refreshCts;
    private readonly GitWorktreeWatcher? watcher;

    public GitWorktreeReviewWorkspaceTabViewModel(SubscribedEntityViewModel entityViewModel)
    {
        var repositoryPath = GetRepositoryPath(entityViewModel);

        this.RepositoryPath = repositoryPath ?? string.Empty;
        this.CommitList = new GitWorktreeCommitListViewModel();
        this.FileList = new GitWorktreeFileListViewModel();
        this.FileDiffs = new ObservableCollection<GitDiffViewModel>();

        this.targetBranch = GetDefaultTargetBranch(entityViewModel, repositoryPath);

        if (repositoryPath is not null)
        {
            this.watcher = new GitWorktreeWatcher(repositoryPath);
            this.watcher.Changed += this.OnWatcherChanged;
            this.watcher.Start();
        }

        this.FileList.SelectedFiles.CollectionChanged += this.OnSelectedFilesChanged;
        this.CommitList.SelectedCommits.CollectionChanged += this.OnSelectedCommitsChanged;

        Lifetime.Run(this.RefreshAsync);
    }

    public string RepositoryPath { get; }

    public string TargetBranch
    {
        get => this.targetBranch;
        set
        {
            if (this.SetProperty(ref this.targetBranch, value))
            {
                Lifetime.Run(this.RefreshAsync);
            }
        }
    }

    public bool SideBySide
    {
        get => this.sideBySide;
        set => this.SetProperty(ref this.sideBySide, value);
    }

    public bool FullFile
    {
        get => this.fullFile;
        set => this.SetProperty(ref this.fullFile, value);
    }

    public int ContextLines
    {
        get => this.contextLines;
        set => this.SetProperty(ref this.contextLines, value);
    }

    public bool IsRefreshing
    {
        get => this.isRefreshing;
        private set => this.SetProperty(ref this.isRefreshing, value);
    }

    public GitWorktreeCommitListViewModel CommitList { get; }

    public GitWorktreeFileListViewModel FileList { get; }

    public ObservableCollection<GitDiffViewModel> FileDiffs { get; }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        this.refreshCts?.Cancel();
        this.refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = this.refreshCts.Token;

        try
        {
            this.IsRefreshing = true;
            await this.CommitList.RefreshAsync(this.RepositoryPath, this.targetBranch, token);

            var selectedCommits = this.CommitList.SelectedCommits.Count > 0
                ? (IReadOnlyList<GitCommitModel>)this.CommitList.SelectedCommits
                : (IReadOnlyList<GitCommitModel>)this.CommitList.Commits;

            await this.FileList.RefreshAsync(this.RepositoryPath, selectedCommits, token);
            await this.RebuildFileDiffsAsync(selectedCommits, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                this.IsRefreshing = false;
            }
        }
    }

    private Task RebuildFileDiffsAsync(IReadOnlyList<GitCommitModel> selectedCommits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var selectedFiles = this.FileList.SelectedFiles.Count > 0
            ? (IReadOnlyList<GitWorktreeFileEntryViewModel>)this.FileList.SelectedFiles
            : (IReadOnlyList<GitWorktreeFileEntryViewModel>)this.FileList.Files;

        var newDiffs = new List<GitDiffViewModel>();

        try
        {
            using var repo = new Repository(this.RepositoryPath);

            foreach (var fileEntry in selectedFiles)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var commit in selectedCommits)
                {
                    Patch? patch = null;

                    if (commit.IsUnstaged)
                    {
                        patch = repo.Diff.Compare<Patch>(
                            repo.Head.Tip?.Tree,
                            DiffTargets.WorkingDirectory,
                            new[] { fileEntry.RelativePath },
                            new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false },
                            new CompareOptions { ContextLines = this.contextLines });
                    }
                    else if (commit.IsStaged)
                    {
                        patch = repo.Diff.Compare<Patch>(
                            repo.Head.Tip?.Tree,
                            DiffTargets.Index,
                            new[] { fileEntry.RelativePath },
                            new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false },
                            new CompareOptions { ContextLines = this.contextLines });
                    }
                    else
                    {
                        var c = repo.Lookup<Commit>(commit.Oid);
                        if (c?.Parents.FirstOrDefault() is { } parent)
                        {
                            patch = repo.Diff.Compare<Patch>(
                                parent.Tree,
                                c.Tree,
                                new[] { fileEntry.RelativePath },
                                new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false },
                                new CompareOptions { ContextLines = this.contextLines });
                        }
                    }

                    if (patch is not null)
                    {
                        foreach (var entry in patch)
                        {
                            newDiffs.Add(GitDiffViewModel.FromPatchEntry(entry, this.contextLines));
                        }
                    }
                }
            }
        }
        catch (RepositoryNotFoundException)
        {
        }
        catch (LibGit2SharpException)
        {
        }
        catch (ArgumentException)
        {
        }

        // Update in place
        this.FileDiffs.Clear();
        foreach (var diff in newDiffs)
        {
            this.FileDiffs.Add(diff);
        }

        return Task.CompletedTask;
    }

    private void OnWatcherChanged(object? sender, EventArgs e)
    {
        Lifetime.Run(this.RefreshAsync);
    }

    private void OnSelectedFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        var selectedCommits = this.CommitList.SelectedCommits.Count > 0
            ? (IReadOnlyList<GitCommitModel>)this.CommitList.SelectedCommits
            : (IReadOnlyList<GitCommitModel>)this.CommitList.Commits;

        Lifetime.Run(ct => this.RebuildFileDiffsAsync(selectedCommits, ct));
    }

    private void OnSelectedCommitsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        var selectedCommits = this.CommitList.SelectedCommits.Count > 0
            ? (IReadOnlyList<GitCommitModel>)this.CommitList.SelectedCommits
            : (IReadOnlyList<GitCommitModel>)this.CommitList.Commits;

        Lifetime.Run(async ct =>
        {
            await this.FileList.RefreshAsync(this.RepositoryPath, selectedCommits, ct);
            await this.RebuildFileDiffsAsync(selectedCommits, ct);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        this.FileList.SelectedFiles.CollectionChanged -= this.OnSelectedFilesChanged;
        this.CommitList.SelectedCommits.CollectionChanged -= this.OnSelectedCommitsChanged;

        this.refreshCts?.Cancel();
        this.refreshCts?.Dispose();
        this.refreshCts = null;

        if (this.watcher is not null)
        {
            this.watcher.Changed -= this.OnWatcherChanged;
            this.watcher.Dispose();
        }

        await base.DisposeAsync();
    }

    private static string? GetRepositoryPath(SubscribedEntityViewModel entityViewModel)
    {
        if (entityViewModel.Data is not JsonElement data)
        {
            return null;
        }

        if (data.TryGetProperty("path", out var pathElement)
            && pathElement.ValueKind == JsonValueKind.String)
        {
            return pathElement.GetString();
        }

        return null;
    }

    private static string GetDefaultTargetBranch(SubscribedEntityViewModel entityViewModel, string? repositoryPath)
    {
        // 1. Check entity data for explicit target-branch
        if (entityViewModel.Data is JsonElement data
            && data.TryGetProperty("target-branch", out var targetBranchElement)
            && targetBranchElement.ValueKind == JsonValueKind.String
            && targetBranchElement.GetString() is { Length: > 0 } explicitBranch)
        {
            return explicitBranch;
        }

        // 2. Probe the repository for main/master
        if (repositoryPath is not null)
        {
            try
            {
                using var repo = new Repository(repositoryPath);
                if (repo.Branches["main"] is not null)
                {
                    return "main";
                }

                if (repo.Branches["master"] is not null)
                {
                    return "master";
                }
            }
            catch (RepositoryNotFoundException)
            {
            }
            catch (LibGit2SharpException)
            {
            }
        }

        return "main";
    }
}
