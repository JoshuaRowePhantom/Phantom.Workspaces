using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    private CancellationTokenSource? fileDiffsCts;
    private CancellationTokenSource? selectedCommitsCts;
    private long diffRequestGeneration;
    private long selectedCommitsGeneration;
    private int pendingFileListRefreshes;
    private readonly object diffRequestGate = new();
    private readonly object selectedCommitsGate = new();
    private Task? currentRefresh;
    private readonly GitWorktreeWatcher? watcher;
    private readonly TaskScheduler foregroundScheduler;
    private GitWorktreeCommitListViewModel commitList;
    private GitWorktreeFileListViewModel fileList;
    private ObservableCollection<GitDiffViewModel> fileDiffs;
    private IReadOnlyList<GitDiffRow> diffRows = Array.Empty<GitDiffRow>();
    private GitDiffRow? selectedDiffRow;

    internal static Func<string?, string> DefaultBranchProbeForTests { get; set; } = ProbeRepositoryForDefaultBranch;
    internal Func<CancellationToken, Task>? BeforeDiffBuildAsync { get; set; }

    // #1210: `foregroundScheduler` is a required constructor parameter (mirrors AgentViewModel /
    // #1122). Heavy LibGit2Sharp work runs on the thread pool and only the final snapshot
    // swap marshals back via foregroundScheduler. No blocking git probe runs
    // in the constructor.
    public GitWorktreeReviewWorkspaceTabViewModel(
        SubscribedEntityViewModel entityViewModel,
        TaskScheduler foregroundScheduler)
    {
        this.foregroundScheduler = foregroundScheduler ?? throw new ArgumentNullException(nameof(foregroundScheduler));
        var repositoryPath = GetRepositoryPath(entityViewModel);

        this.RepositoryPath = repositoryPath ?? string.Empty;
        this.commitList = new GitWorktreeCommitListViewModel();
        this.fileList = new GitWorktreeFileListViewModel();
        this.fileDiffs = new ObservableCollection<GitDiffViewModel>();
        this.BranchNames = new ObservableCollection<string>();

        // #1210: seed target branch from entity data ONLY. The main/master repository probe is a
        // blocking LibGit2Sharp call and must not run on the UI thread; it is deferred into
        // InitializeAsync's Task.Run body below.
        var explicitBranch = GetTargetBranchFromEntityData(entityViewModel);
        this.targetBranch = explicitBranch ?? "main";
        var needsDefaultBranchProbe = explicitBranch is null;

        if (repositoryPath is not null)
        {
            this.watcher = new GitWorktreeWatcher(repositoryPath);
            this.watcher.Changed += this.OnWatcherChanged;
            this.watcher.Start();
        }

        this.fileList.SelectedFiles.CollectionChanged += this.OnSelectedFilesChanged;
        this.commitList.SelectedCommits.CollectionChanged += this.OnSelectedCommitsChanged;

        // Start initialization and expose it as CurrentRefresh immediately.
        var initTask = this.InitializeAsync(needsDefaultBranchProbe, Lifetime.Token);
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
                this.RequestFileDiffRebuild();
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
                this.RequestFileDiffRebuild();
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
                this.RequestFileDiffRebuild();
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

    public IReadOnlyList<GitDiffRow> DiffRows
    {
        get => this.diffRows;
        private set => this.SetProperty(ref this.diffRows, value);
    }

    public GitDiffRow? SelectedDiffRow
    {
        get => this.selectedDiffRow;
        set => this.SetProperty(ref this.selectedDiffRow, value);
    }

    /// <summary>Raised before an atomic row replacement, allowing the view to retain its scroll offset.</summary>
    public event EventHandler? DiffRowsReplacing;

    public ObservableCollection<string> BranchNames { get; }

    private async Task InitializeAsync(bool needsDefaultBranchProbe, CancellationToken ct = default)
    {
        // #1210: All blocking LibGit2Sharp work (branch enumeration + optional main/master probe)
        // runs on the thread pool with ConfigureAwait(false).
        var (branchNames, resolvedDefault) = await Task.Run(() =>
        {
            var branches = new System.Collections.Generic.List<string>();
            LoadBranchNames(this.RepositoryPath, branches);
            string? probedDefault = needsDefaultBranchProbe
                ? DefaultBranchProbeForTests(this.RepositoryPath)
                : null;
            return (branches, probedDefault);
        }, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        // #1210: marshal only the final ObservableCollection/property updates to the UI thread.
        await Task.Factory.StartNew(
            () =>
            {
                foreach (var branch in branchNames)
                {
                    this.BranchNames.Add(branch);
                }

                if (resolvedDefault is not null
                    && !string.Equals(this.targetBranch, resolvedDefault, StringComparison.Ordinal))
                {
                    this.targetBranch = resolvedDefault;
                    this.RaisePropertyChanged(nameof(this.TargetBranch));
                    this.RaisePropertyChanged(nameof(this.CommitListHeader));
                }
            },
            ct,
            TaskCreationOptions.None,
            this.foregroundScheduler).ConfigureAwait(false);

        // Call RefreshCoreAsync directly to avoid overwriting currentRefresh
        await this.RefreshCoreAsync(ct).ConfigureAwait(false);
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        return this.currentRefresh = this.RefreshCoreAsync(ct);
    }

    private async Task RefreshCoreAsync(CancellationToken ct = default)
    {
        var requestGeneration = this.CancelFileDiffRebuild();
        this.CancelSelectedCommitRefresh();
        this.refreshCts?.Cancel();
        this.refreshCts?.Dispose();
        this.refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = this.refreshCts.Token;

        try
        {
            this.IsRefreshing = true;

            // Build detached VMs. Their RefreshAsync methods run heavy git work on the thread pool
            // and marshal only the final ObservableCollection mutations to foregroundScheduler.
            var newCommitList = new GitWorktreeCommitListViewModel();
            await newCommitList.RefreshAsync(this.RepositoryPath, this.targetBranch, this.foregroundScheduler, token)
                .ConfigureAwait(false);

            PreserveCommitSelection(this.CommitList, newCommitList);

            var selectedCommits = newCommitList.SelectedCommits.Count > 0
                ? (IReadOnlyList<GitCommitModel>)newCommitList.SelectedCommits
                : (IReadOnlyList<GitCommitModel>)newCommitList.Commits;

            var newFileList = new GitWorktreeFileListViewModel();
            await newFileList.RefreshAsync(this.RepositoryPath, selectedCommits, this.foregroundScheduler, token)
                .ConfigureAwait(false);

            PreserveFileSelection(this.FileList, newFileList);

            var newDiffs = await this.BuildFileDiffsAsync(newFileList, selectedCommits, this.CurrentDiffOptions, token)
                .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            // #1210: marshal the final atomic swap onto foregroundScheduler.
            await Task.Factory.StartNew(
                () =>
                {
                    this.AttachCommitList(newCommitList);
                    this.AttachFileList(newFileList);
                    if (requestGeneration == Interlocked.Read(ref this.diffRequestGeneration))
                    {
                        this.ApplyDiffs(newDiffs);
                    }
                    else
                    {
                        this.RequestFileDiffRebuild();
                    }
                },
                token,
                TaskCreationOptions.None,
                this.foregroundScheduler).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Detached VMs simply go out of scope; visible state is untouched
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                await Task.Factory.StartNew(
                    () => this.IsRefreshing = false,
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    this.foregroundScheduler).ConfigureAwait(false);
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

    private readonly record struct DiffBuildOptions(bool SideBySide, bool FullFile, int ContextLines);

    private sealed record DiffBuildResult(
        ObservableCollection<GitDiffViewModel> FileDiffs, IReadOnlyList<GitDiffRow> Rows);

    private DiffBuildOptions CurrentDiffOptions => new(this.sideBySide, this.fullFile, this.contextLines);

    private Task<DiffBuildResult> BuildFileDiffsAsync(
        GitWorktreeFileListViewModel fileListVm,
        IReadOnlyList<GitCommitModel> selectedCommits,
        DiffBuildOptions options,
        CancellationToken ct)
    {
        var selectedFiles = (fileListVm.SelectedFiles.Count > 0
            ? fileListVm.SelectedFiles
            : fileListVm.Files).ToArray();
        var commits = selectedCommits.ToArray();
        return Task.Run(async () =>
        {
            if (this.BeforeDiffBuildAsync is { } beforeBuild)
            {
                await beforeBuild(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            var newDiffs = new List<GitDiffViewModel>();

            var effectiveContextLines = options.FullFile ? int.MaxValue / 2 : options.ContextLines;

            try
            {
                using var repo = new Repository(this.RepositoryPath);

                foreach (var fileEntry in selectedFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    foreach (var commit in commits)
                    {
                        ct.ThrowIfCancellationRequested();
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
                                newDiffs.Add(GitDiffViewModel.FromPatchEntry(entry, effectiveContextLines, options.SideBySide));
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

            ct.ThrowIfCancellationRequested();
            return new DiffBuildResult(new ObservableCollection<GitDiffViewModel>(newDiffs), GitDiffRow.Flatten(newDiffs));
        }, ct);
    }

    private long CancelFileDiffRebuild()
    {
        lock (this.diffRequestGate)
        {
            this.fileDiffsCts?.Cancel();
            this.fileDiffsCts?.Dispose();
            this.fileDiffsCts = null;
            return Interlocked.Increment(ref this.diffRequestGeneration);
        }
    }

    private void CancelSelectedCommitRefresh()
    {
        lock (this.selectedCommitsGate)
        {
            this.selectedCommitsCts?.Cancel();
            this.selectedCommitsCts?.Dispose();
            this.selectedCommitsCts = null;
            Interlocked.Increment(ref this.selectedCommitsGeneration);
        }
    }

    private void RequestFileDiffRebuild()
    {
        long generation;
        CancellationTokenSource cts;
        lock (this.diffRequestGate)
        {
            this.fileDiffsCts?.Cancel();
            this.fileDiffsCts?.Dispose();
            cts = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            this.fileDiffsCts = cts;
            generation = Interlocked.Increment(ref this.diffRequestGeneration);
        }
        var selectedCommits = this.CommitList.SelectedCommits.Count > 0
            ? this.CommitList.SelectedCommits.ToArray()
            : this.CommitList.Commits.ToArray();
        var rebuildTask = this.RebuildFileDiffsAsync(this.FileList, selectedCommits, this.CurrentDiffOptions, generation, cts.Token);
        this.currentRefresh = rebuildTask;
        this.RaisePropertyChanged(nameof(this.CurrentRefresh));
        Lifetime.Run(_ => rebuildTask);
    }

    private async Task RebuildFileDiffsAsync(
        GitWorktreeFileListViewModel fileList,
        IReadOnlyList<GitCommitModel> selectedCommits,
        DiffBuildOptions options,
        long generation,
        CancellationToken ct)
    {
        var newDiffs = await this.BuildFileDiffsAsync(fileList, selectedCommits, options, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await Task.Factory.StartNew(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                if (generation == Interlocked.Read(ref this.diffRequestGeneration))
                {
                    this.ApplyDiffs(newDiffs);
                }
            },
            ct,
            TaskCreationOptions.None,
            this.foregroundScheduler).ConfigureAwait(false);
    }

    internal void ApplyDiffs(IReadOnlyList<GitDiffViewModel> diffs)
        => this.ApplyDiffs(new DiffBuildResult(
            new ObservableCollection<GitDiffViewModel>(diffs), GitDiffRow.Flatten(diffs)));

    private void ApplyDiffs(DiffBuildResult result)
    {
        var selected = this.SelectedDiffRow;
        this.DiffRowsReplacing?.Invoke(this, EventArgs.Empty);
        this.FileDiffs = result.FileDiffs;
        this.DiffRows = result.Rows;
        if (selected is not null)
        {
            this.SelectedDiffRow = result.Rows.FirstOrDefault(row => SameLocation(row, selected));
        }
    }

    private static bool SameLocation(GitDiffRow row, GitDiffRow selected)
    {
        if (!string.Equals(row.RelativePath, selected.RelativePath, StringComparison.Ordinal))
        {
            return false;
        }

        return (row, selected) switch
        {
            (GitDiffFileRow, GitDiffFileRow) => true,
            (GitDiffHunkRow hunk, GitDiffHunkRow previous) =>
                hunk.OldStart == previous.OldStart && hunk.NewStart == previous.NewStart,
            (GitDiffLineRow line, GitDiffLineRow previous) =>
                line.OldStart == previous.OldStart && line.NewStart == previous.NewStart
                && line.Line.Kind == previous.Line.Kind
                && line.Line.OldLineNumber == previous.Line.OldLineNumber
                && line.Line.NewLineNumber == previous.Line.NewLineNumber,
            _ => false,
        };
    }

    private void OnWatcherChanged(object? sender, EventArgs e)
    {
        Lifetime.Run(this.RefreshAsync);
    }

    private void OnSelectedFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (Volatile.Read(ref this.pendingFileListRefreshes) == 0)
        {
            this.RequestFileDiffRebuild();
        }
    }

    private void OnSelectedCommitsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(this.FileListHeader));

        this.CancelFileDiffRebuild();
        CancellationTokenSource cts;
        long generation;
        lock (this.selectedCommitsGate)
        {
            this.selectedCommitsCts?.Cancel();
            this.selectedCommitsCts?.Dispose();
            cts = this.selectedCommitsCts = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            generation = Interlocked.Increment(ref this.selectedCommitsGeneration);
        }
        var selectedCommits = this.CommitList.SelectedCommits.Count > 0
            ? this.CommitList.SelectedCommits.ToArray()
            : this.CommitList.Commits.ToArray();
        var fileList = this.FileList;
        Interlocked.Increment(ref this.pendingFileListRefreshes);

        Lifetime.Run(async _ =>
        {
            try
            {
                await fileList.RefreshAsync(this.RepositoryPath, selectedCommits, this.foregroundScheduler, cts.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                await Task.Factory.StartNew(
                    () =>
                    {
                        Interlocked.Decrement(ref this.pendingFileListRefreshes);
                        if (!cts.IsCancellationRequested
                            && generation == Interlocked.Read(ref this.selectedCommitsGeneration)
                            && ReferenceEquals(this.FileList, fileList))
                        {
                            this.RequestFileDiffRebuild();
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    this.foregroundScheduler).ConfigureAwait(false);
            }
        });
    }

    public override async ValueTask DisposeAsync()
    {
        this.FileList.SelectedFiles.CollectionChanged -= this.OnSelectedFilesChanged;
        this.CommitList.SelectedCommits.CollectionChanged -= this.OnSelectedCommitsChanged;

        this.refreshCts?.Cancel();
        this.refreshCts?.Dispose();
        this.refreshCts = null;
        this.CancelSelectedCommitRefresh();
        this.CancelFileDiffRebuild();

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
