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

    public Task RefreshAsync(string repositoryPath, string targetBranch, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var commits = new System.Collections.Generic.List<GitCommitModel>();
        bool hasUnstaged = false;
        bool hasStaged = false;

        try
        {
            using var repo = new Repository(repositoryPath);

            // Check for uncommitted changes
            var status = repo.RetrieveStatus(new StatusOptions());
            hasStaged = status.Staged.Any();
            hasUnstaged = status.Modified.Any() || status.Untracked.Any() || status.Missing.Any();

            // Get commits not in target branch
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
                    commits.Add(new GitCommitModel
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

        // Preserve selection state by OID
        var selectedOids = new System.Collections.Generic.HashSet<string>(
            this.SelectedCommits.Select(c => c.Oid),
            StringComparer.Ordinal);

        this.Commits.Clear();
        this.SelectedCommits.Clear();

        if (hasUnstaged)
        {
            var unstaged = GitCommitModel.CreateUnstaged();
            this.Commits.Add(unstaged);
            if (selectedOids.Contains(unstaged.Oid))
            {
                this.SelectedCommits.Add(unstaged);
            }
        }

        if (hasStaged)
        {
            var staged = GitCommitModel.CreateStaged();
            this.Commits.Add(staged);
            if (selectedOids.Contains(staged.Oid))
            {
                this.SelectedCommits.Add(staged);
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

        return Task.CompletedTask;
    }
}
