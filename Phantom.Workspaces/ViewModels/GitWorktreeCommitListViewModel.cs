using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;

namespace Phantom.Workspaces.ViewModels;

public sealed class GitWorktreeCommitListViewModel : ViewModelBase
{
    public ObservableCollection<GitCommitModel> Commits { get; } = new();
    public ObservableCollection<GitCommitModel> SelectedCommits { get; } = new();

    // Test-only hook (see InternalsVisibleTo(Phantom.Workspaces.Tests)): invoked at the top of the
    // Task.Run-scheduled build task in RefreshAsync so tests can capture the managed thread id on
    // which git status + log actually runs, without depending on pump-queue enqueue ordering.
    // See #1284.
    internal static Action? GitWorkStartedForTests { get; set; }

    // #1210: `foregroundScheduler` is required so that git I/O runs on the thread pool and only the
    // final ObservableCollection mutations are marshalled back to the UI thread. See AgentViewModel
    // (#1122) for the reference pattern.
    public Task RefreshAsync(
        string repositoryPath,
        string targetBranch,
        TaskScheduler foregroundScheduler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(foregroundScheduler);
        ct.ThrowIfCancellationRequested();

        var buildTask = Task.Run(() =>
        {
            GitWorkStartedForTests?.Invoke();

            var commitsList = new System.Collections.Generic.List<GitCommitModel>();
            bool unstaged = false;
            bool staged = false;

            try
            {
                using var repo = new Repository(repositoryPath);

                var status = repo.RetrieveStatus(new StatusOptions());
                staged = status.Staged.Any();
                unstaged = status.Modified.Any() || status.Untracked.Any() || status.Missing.Any();

                var targetCommit = repo.Branches[targetBranch]?.Tip ?? repo.Lookup<Commit>(targetBranch);
                if (targetCommit is not null && repo.Head.Tip is not null)
                {
                    var filter = new CommitFilter
                    {
                        IncludeReachableFrom = repo.Head.Tip,
                        ExcludeReachableFrom = targetCommit,
                    };

                    foreach (var commit in repo.Commits.QueryBy(filter))
                    {
                        ct.ThrowIfCancellationRequested();
                        commitsList.Add(new GitCommitModel
                        {
                            Oid = commit.Sha,
                            ShortMessage = commit.MessageShort,
                            AuthorName = commit.Author.Name,
                            AuthorDate = commit.Author.When,
                        });
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

            return (commitsList, unstaged, staged);
        }, ct);

        return buildTask.ContinueWith(
            t =>
            {
                var (commits, hasUnstaged, hasStaged) = t.GetAwaiter().GetResult();
                ct.ThrowIfCancellationRequested();

                var selectedOids = new System.Collections.Generic.HashSet<string>(
                    this.SelectedCommits.Select(c => c.Oid),
                    StringComparer.Ordinal);

                this.Commits.Clear();
                this.SelectedCommits.Clear();

                if (hasUnstaged)
                {
                    var unstagedModel = GitCommitModel.CreateUnstaged();
                    this.Commits.Add(unstagedModel);
                    if (selectedOids.Contains(unstagedModel.Oid))
                    {
                        this.SelectedCommits.Add(unstagedModel);
                    }
                }

                if (hasStaged)
                {
                    var stagedModel = GitCommitModel.CreateStaged();
                    this.Commits.Add(stagedModel);
                    if (selectedOids.Contains(stagedModel.Oid))
                    {
                        this.SelectedCommits.Add(stagedModel);
                    }
                }

                foreach (var commit in commits)
                {
                    this.Commits.Add(commit);
                    if (selectedOids.Contains(commit.Oid))
                    {
                        this.SelectedCommits.Add(commit);
                    }
                }
            },
            ct,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            foregroundScheduler);
    }
}
