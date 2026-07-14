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
    private Task? currentRefresh;
    private readonly GitWorktreeWatcher? watcher;
    private GitWorktreeCommitListViewModel commitList;
    private GitWorktreeFileListViewModel fileList;
    private ObservableCollection<GitDiffViewModel> fileDiffs;

    public GitWorktreeReviewWorkspaceTabViewModel(SubscribedEntityViewModel entityViewModel)
    {
        var repositoryPath = GetRepositoryPath(entityViewModel);

        this.RepositoryPath = repositoryPath ?? string.Empty;
        this.commitList = new GitWorktreeCommitListViewModel();
        this.fileList = new GitWorktreeFileListViewModel();
        this.fileDiffs = new ObservableCollection<GitDiffViewModel>();
        this.BranchNames = new ObservableCollection<string>();

        // Read target branch synchronously (entity data or repository probe)
        this.targetBranch = GetDefaultTargetBranch(entityViewModel, repositoryPath);

        if (repositoryPath is not null)
        {
            this.watcher = new GitWorktreeWatcher(repositoryPath);
            this.watcher.Changed += this.OnWatcherChanged;
            this.watcher.Start();
        }

        this.fileList.SelectedFiles.CollectionChanged += this.OnSelectedFilesChanged;
        this.commitList.SelectedCommits.CollectionChanged += this.OnSelectedCommitsChanged;

        // Start initialization and expose it as CurrentRefresh immediately
        var initTask = this.InitializeAsync(entityViewModel, Lifetime.Token);
        this.currentRefresh = initTask;
        Lifetime.Run(_ => initTask);
    }

    public string RepositoryPath { get; }

    public string TargetBranch
    {
        get => this.targetBranch;
        set
        {
            if (this.SetProperty(ref this.targetBranch, value))
            {
                this.RaisePropertyChanged(nameof(this.CommitListHeader));
                Lifetime.Run(this.RefreshAsync);
            }
        }
    }

    public string CommitListHeader => $"Commits not in {this.targetBranch}";

    public string FileListHeader
    {
        get
        {
            var selectedCommits = this.CommitList.SelectedCommits;
            if (selectedCommits.Count == 0)
            {
                return "Files changed";
            }

            if (selectedCommits.Count == 1)
            {
                var commit = selectedCommits[0];
                if (!commit.IsUnstaged && !commit.IsStaged)
                {
                    return $"Files changed in {commit.ShortOid}";
                }
            }

            return "Files changed in selected commits";
        }
    }

    public bool SideBySide
    {
        get => this.sideBySide;
        set
        {
            if (this.SetProperty(ref this.sideBySide, value))
            {
                var selectedCommits = this.CommitList.SelectedCommits.Count > 0
                    ? (IReadOnlyList<GitCommitModel>)this.CommitList.SelectedCommits
                    : (IReadOnlyList<GitCommitModel>)this.CommitList.Commits;

                Lifetime.Run(ct => this.RebuildFileDiffsAsync(selectedCommits, ct));
            }
        }
    }

    public bool FullFile
    {
        get => this.fullFile;
        set
        {
            if (this.SetProperty(ref this.fullFile, value))
            {
                var selectedCommits = this.CommitList.SelectedCommits.Count > 0
                    ? (IReadOnlyList<GitCommitModel>)this.CommitList.SelectedCommits
                    : (IReadOnlyList<GitCommitModel>)this.CommitList.Commits;

                Lifetime.Run(ct => this.RebuildFileDiffsAsync(selectedCommits, ct));
            }
        }
    }

    public int ContextLines
    {
        get => this.contextLines;
        set
        {
            if (this.SetProperty(ref this.contextLines, value))
            {
                var selectedCommits = this.CommitList.SelectedCommits.Count > 0
                    ? (IReadOnlyList<GitCommitModel>)this.CommitList.SelectedCommits
                    : (IReadOnlyList<GitCommitModel>)this.CommitList.Commits;

                Lifetime.Run(ct => this.RebuildFileDiffsAsync(selectedCommits, ct));
            }
        }
    }

    public bool IsRefreshing
    {
        get => this.isRefreshing;
        private set => this.SetProperty(ref this.isRefreshing, value);
    }

    public Task? CurrentRefresh => this.currentRefresh;

    public GitWorktreeCommitListViewModel CommitList
    {
        get => this.commitList;
        private set => this.SetProperty(ref this.commitList, value);
    }

    public GitWorktreeFileListViewModel FileList
    {
        get => this.fileList;
        private set => this.SetProperty(ref this.fileList, value);
    }

    public ObservableCollection<GitDiffViewModel> FileDiffs
    {
        get => this.fileDiffs;
        private set => this.SetProperty(ref this.fileDiffs, value);
    }

    public ObservableCollection<string> BranchNames { get; }

    private async Task InitializeAsync(SubscribedEntityViewModel entityViewModel, CancellationToken ct = default)
    {
        var branchNames = await Task.Run(() =>
        {
            var branches = new System.Collections.Generic.List<string>();
            LoadBranchNames(this.RepositoryPath, branches);
            return branches;
        }, ct);

        foreach (var branch in branchNames)
        {
            this.BranchNames.Add(branch);
        }

        // Call RefreshCoreAsync directly to avoid overwriting currentRefresh
        await this.RefreshCoreAsync(ct);
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        return this.currentRefresh = this.RefreshCoreAsync(ct);
    }

    private async Task RefreshCoreAsync(CancellationToken ct = default)
    {
        this.refreshCts?.Cancel();
        this.refreshCts?.Dispose();
        this.refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = this.refreshCts.Token;

        try
        {
            this.IsRefreshing = true;

            // Build detached VMs on background thread
            var newCommitList = new GitWorktreeCommitListViewModel();
            await newCommitList.RefreshAsync(this.RepositoryPath, this.targetBranch, token);

            // Preserve commit selection by OID
            PreserveCommitSelection(this.CommitList, newCommitList);

            var selectedCommits = newCommitList.SelectedCommits.Count > 0
                ? (IReadOnlyList<GitCommitModel>)newCommitList.SelectedCommits
                : (IReadOnlyList<GitCommitModel>)newCommitList.Commits;

            var newFileList = new GitWorktreeFileListViewModel();
            await newFileList.RefreshAsync(this.RepositoryPath, selectedCommits, token);

            // Preserve file selection by path
            PreserveFileSelection(this.FileList, newFileList);

            var newDiffs = await this.BuildFileDiffsAsync(newFileList, selectedCommits, token);

            token.ThrowIfCancellationRequested();

            // Atomic swap - only writes back to visible state
            this.AttachCommitList(newCommitList);
            this.AttachFileList(newFileList);
            this.FileDiffs = newDiffs;
        }
        catch (OperationCanceledException)
        {
            // Detached VMs simply go out of scope; visible state is untouched
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                this.IsRefreshing = false;
            }
        }
    }

    private void AttachCommitList(GitWorktreeCommitListViewModel newList)
    {
        if (this.commitList is { } old)
        {
            old.SelectedCommits.CollectionChanged -= this.OnSelectedCommitsChanged;
        }

        this.CommitList = newList;
        newList.SelectedCommits.CollectionChanged += this.OnSelectedCommitsChanged;
        this.RaisePropertyChanged(nameof(this.FileListHeader));
    }

    private void AttachFileList(GitWorktreeFileListViewModel newList)
    {
        if (this.fileList is { } old)
        {
            old.SelectedFiles.CollectionChanged -= this.OnSelectedFilesChanged;
        }

        this.FileList = newList;
        newList.SelectedFiles.CollectionChanged += this.OnSelectedFilesChanged;
    }

    private static void PreserveCommitSelection(GitWorktreeCommitListViewModel oldList, GitWorktreeCommitListViewModel newList)
    {
        var selectedOids = new HashSet<string>(
            oldList.SelectedCommits.Select(c => c.Oid),
            StringComparer.Ordinal);

        foreach (var commit in newList.Commits)
        {
            if (selectedOids.Contains(commit.Oid))
            {
                newList.SelectedCommits.Add(commit);
            }
        }
    }

    private static void PreserveFileSelection(GitWorktreeFileListViewModel oldList, GitWorktreeFileListViewModel newList)
    {
        var selectedPaths = new HashSet<string>(
            oldList.SelectedFiles.Select(f => f.RelativePath),
            StringComparer.Ordinal);

        foreach (var file in newList.Files)
        {
            if (selectedPaths.Contains(file.RelativePath))
            {
                file.IsSelected = true;
                newList.SelectedFiles.Add(file);
            }
        }
    }

    private Task<ObservableCollection<GitDiffViewModel>> BuildFileDiffsAsync(
        GitWorktreeFileListViewModel fileListVm,
        IReadOnlyList<GitCommitModel> selectedCommits,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var selectedFiles = fileListVm.SelectedFiles.Count > 0
                ? (IReadOnlyList<GitWorktreeFileEntryViewModel>)fileListVm.SelectedFiles
                : (IReadOnlyList<GitWorktreeFileEntryViewModel>)fileListVm.Files;

            var newDiffs = new List<GitDiffViewModel>();

            var effectiveContextLines = this.fullFile ? int.MaxValue / 2 : this.contextLines;

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
                                new CompareOptions { ContextLines = effectiveContextLines });
                        }
                        else if (commit.IsStaged)
                        {
                            patch = repo.Diff.Compare<Patch>(
                                repo.Head.Tip?.Tree,
                                DiffTargets.Index,
                                new[] { fileEntry.RelativePath },
                                new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false },
                                new CompareOptions { ContextLines = effectiveContextLines });
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
                                    new CompareOptions { ContextLines = effectiveContextLines });
                            }
                        }

                        if (patch is not null)
                        {
                            foreach (var entry in patch)
                            {
                                newDiffs.Add(GitDiffViewModel.FromPatchEntry(entry, effectiveContextLines, this.sideBySide));
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

            var result = new ObservableCollection<GitDiffViewModel>();
            foreach (var diff in newDiffs)
            {
                result.Add(diff);
            }

            return result;
        }, ct);
    }

    private async Task RebuildFileDiffsAsync(IReadOnlyList<GitCommitModel> selectedCommits, CancellationToken ct)
    {
        async Task RebuildCore()
        {
            var newDiffs = await this.BuildFileDiffsAsync(this.FileList, selectedCommits, ct);
            ct.ThrowIfCancellationRequested();
            
            // Modify existing collection in-place to fire CollectionChanged
            this.FileDiffs.Clear();
            foreach (var diff in newDiffs)
            {
                this.FileDiffs.Add(diff);
            }
        }

        var rebuildTask = RebuildCore();
        this.currentRefresh = rebuildTask;
        this.RaisePropertyChanged(nameof(this.CurrentRefresh));
        await rebuildTask;
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
        this.RaisePropertyChanged(nameof(this.FileListHeader));

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

    private static string? GetTargetBranchFromEntityData(SubscribedEntityViewModel entityViewModel)
    {
        if (entityViewModel.Data is JsonElement data
            && data.TryGetProperty("target-branch", out var targetBranchElement)
            && targetBranchElement.ValueKind == JsonValueKind.String
            && targetBranchElement.GetString() is { Length: > 0 } explicitBranch)
        {
            return explicitBranch;
        }

        return null;
    }

    private static string ProbeRepositoryForDefaultBranch(string? repositoryPath)
    {
        if (repositoryPath is not null && !string.IsNullOrEmpty(repositoryPath))
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

    private static string GetDefaultTargetBranch(SubscribedEntityViewModel entityViewModel, string? repositoryPath)
    {
        // 1. Check entity data for explicit target-branch
        if (GetTargetBranchFromEntityData(entityViewModel) is { } explicitBranch)
        {
            return explicitBranch;
        }

        // 2. Probe the repository for main/master
        return ProbeRepositoryForDefaultBranch(repositoryPath);
    }

    private static void LoadBranchNames(string? repositoryPath, System.Collections.Generic.List<string> branchNames)
    {
        if (repositoryPath is null || string.IsNullOrEmpty(repositoryPath))
        {
            return;
        }

        try
        {
            using var repo = new Repository(repositoryPath);
            foreach (var branch in repo.Branches)
            {
                branchNames.Add(branch.FriendlyName);
            }
        }
        catch (RepositoryNotFoundException)
        {
        }
        catch (LibGit2SharpException)
        {
        }
    }
}
