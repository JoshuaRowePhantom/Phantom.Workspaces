using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;

namespace Phantom.Workspaces.ViewModels;

public sealed class GitWorktreeFileListViewModel : ViewModelBase
{
    public ObservableCollection<GitWorktreeFileEntryViewModel> Files { get; } = new();
    public ObservableCollection<GitWorktreeFileEntryViewModel> SelectedFiles { get; } = new();

    public Task RefreshAsync(string repositoryPath, IReadOnlyList<GitCommitModel> selectedCommits, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var entries = new List<(string path, int added, int removed)>();

        try
        {
            using var repo = new Repository(repositoryPath);
            var commitList = selectedCommits.Count > 0 ? selectedCommits : null;

            foreach (var commit in commitList ?? Array.Empty<GitCommitModel>())
            {
                ct.ThrowIfCancellationRequested();

                if (commit.IsUnstaged)
                {
                    var patch = repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.WorkingDirectory);
                    foreach (var entry in patch)
                    {
                        entries.Add((entry.Path, entry.LinesAdded, entry.LinesDeleted));
                    }
                }
                else if (commit.IsStaged)
                {
                    var patch = repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.Index);
                    foreach (var entry in patch)
                    {
                        entries.Add((entry.Path, entry.LinesAdded, entry.LinesDeleted));
                    }
                }
                else
                {
                    var c = repo.Lookup<Commit>(commit.Oid);
                    if (c?.Parents.FirstOrDefault() is { } parent)
                    {
                        var patch = repo.Diff.Compare<Patch>(parent.Tree, c.Tree);
                        foreach (var entry in patch)
                        {
                            entries.Add((entry.Path, entry.LinesAdded, entry.LinesDeleted));
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

        // Merge by path
        var merged = entries
            .GroupBy(e => e.path)
            .Select(g => new GitWorktreeFileEntryViewModel
            {
                RelativePath = g.Key,
                LinesAdded = g.Sum(x => x.added),
                LinesRemoved = g.Sum(x => x.removed),
            })
            .OrderBy(e => e.RelativePath)
            .ToList();

        // Preserve selection state
        var selectedPaths = new HashSet<string>(
            this.SelectedFiles.Select(f => f.RelativePath),
            StringComparer.Ordinal);

        this.Files.Clear();
        this.SelectedFiles.Clear();

        foreach (var item in merged)
        {
            if (selectedPaths.Contains(item.RelativePath))
            {
                item.IsSelected = true;
                this.SelectedFiles.Add(item);
            }

            this.Files.Add(item);
        }

        return Task.CompletedTask;
    }
}
