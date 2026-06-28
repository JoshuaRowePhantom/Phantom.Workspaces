using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>A single git-worktree shown inside a profile group.</summary>
public sealed class GitWorktreeItemViewModel
{
    public GitWorktreeItemViewModel(
        EntityId entityId,
        string displayName,
        string? branch,
        string? headCommit)
    {
        this.EntityId = entityId;
        this.DisplayName = displayName;
        this.Branch = branch;
        this.HeadCommit = headCommit;
    }

    public EntityId EntityId { get; }

    public string DisplayName { get; }

    public string? Branch { get; }

    public string? HeadCommit { get; }

    public bool HasBranch => !string.IsNullOrWhiteSpace(this.Branch);
}

/// <summary>A group of git-worktrees belonging to one user-computer-profile.</summary>
public sealed class GitWorktreeGroupViewModel
{
    public GitWorktreeGroupViewModel(string profileDisplayName, IReadOnlyList<GitWorktreeItemViewModel> worktrees)
    {
        this.ProfileDisplayName = profileDisplayName;
        this.Worktrees = worktrees;
    }

    public string ProfileDisplayName { get; }

    public IReadOnlyList<GitWorktreeItemViewModel> Worktrees { get; }
}

/// <summary>
/// View model for the git workspaces view. Loads all <c>git-worktree</c> entities and groups them
/// by the <c>user-computer-profile</c> whose primary name is embedded as a secondary name in each
/// worktree entity's <c>names</c> array (written by <c>GitWorkspaceDiscoveryTool</c>).
/// </summary>
public sealed class GitWorkspacesViewModel : ViewModelBase
{
    private const string GitWorktreeEntityType = "git-worktree";
    private const string UserComputerProfileEntityType = "user-computer-profile";

    private readonly EntityBroker entityBroker;
    private bool isLoading;

    public GitWorkspacesViewModel(EntityBroker entityBroker)
    {
        this.entityBroker = entityBroker ?? throw new ArgumentNullException(nameof(entityBroker));
    }

    /// <summary>The grouped worktrees, ordered by profile display name.</summary>
    public ObservableCollection<GitWorktreeGroupViewModel> Groups { get; } = [];

    public bool IsLoading
    {
        get => this.isLoading;
        private set => this.SetProperty(ref this.isLoading, value);
    }

    public bool HasGroups => this.Groups.Count > 0;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        this.IsLoading = true;
        try
        {
            var groups = await this.LoadGroupsAsync(cancellationToken).ConfigureAwait(true);
            this.Groups.Clear();
            foreach (var group in groups)
            {
                this.Groups.Add(group);
            }

            this.RaisePropertyChanged(nameof(this.HasGroups));
        }
        finally
        {
            this.IsLoading = false;
        }
    }

    private async Task<IReadOnlyList<GitWorktreeGroupViewModel>> LoadGroupsAsync(CancellationToken cancellationToken)
    {
        var worktrees = await this.QueryWorktreesAsync(cancellationToken).ConfigureAwait(true);
        var profileDisplayNames = await this.LoadProfileDisplayNamesAsync(worktrees, cancellationToken).ConfigureAwait(true);

        // Group worktrees by their profile name key.
        var byProfile = new Dictionary<string, List<GitWorktreeItemViewModel>>(StringComparer.Ordinal);
        foreach (var (item, profileKey) in worktrees)
        {
            if (!byProfile.TryGetValue(profileKey, out var list))
            {
                list = [];
                byProfile[profileKey] = list;
            }

            list.Add(item);
        }

        return byProfile
            .Select(kvp =>
            {
                var displayName = profileDisplayNames.TryGetValue(kvp.Key, out var name) ? name : kvp.Key;
                return new GitWorktreeGroupViewModel(
                    displayName,
                    kvp.Value.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
            })
            .OrderBy(g => g.ProfileDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<(GitWorktreeItemViewModel Item, string ProfileKey)>> QueryWorktreesAsync(
        CancellationToken cancellationToken)
    {
        var queryResult = await this.entityBroker.EntityRepository.DataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier { Value = "git-worktrees" },
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet { Values = [GitWorktreeEntityType] },
                        },
                    },
                ],
                Timestamps = [null],
            },
            cancellationToken).ConfigureAwait(true);

        var result = new List<(GitWorktreeItemViewModel, string)>();
        foreach (var snapshot in queryResult.Batches.SelectMany(b => b.Entities))
        {
            var item = BuildItem(snapshot);
            var profileKey = ReadProfileKey(snapshot);
            result.Add((item, profileKey));
        }

        return result;
    }

    /// <summary>
    /// Loads the display names for all unique profile keys by looking up each profile entity.
    /// Profile keys match the primary name of <c>user-computer-profile</c> entities, encoded as a
    /// pipe-joined string for use as a dictionary key.
    /// </summary>
    private async Task<Dictionary<string, string>> LoadProfileDisplayNamesAsync(
        IReadOnlyList<(GitWorktreeItemViewModel, string ProfileKey)> worktrees,
        CancellationToken cancellationToken)
    {
        var uniqueKeys = worktrees
            .Select(w => w.ProfileKey)
            .Where(k => !string.Equals(k, "(Unknown)", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (uniqueKeys.Length == 0)
        {
            return [];
        }

        // Query all user-computer-profile entities and match by their primary name key.
        var queryResult = await this.entityBroker.EntityRepository.DataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier { Value = "profiles" },
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet { Values = [UserComputerProfileEntityType] },
                        },
                    },
                ],
                Timestamps = [null],
            },
            cancellationToken).ConfigureAwait(true);

        var displayNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var snapshot in queryResult.Batches.SelectMany(b => b.Entities))
        {
            if (snapshot.Data is not { } data)
            {
                continue;
            }

            var primaryKey = ReadPrimaryNameKey(data);
            if (string.IsNullOrEmpty(primaryKey))
            {
                continue;
            }

            var displayName = ReadLocalString(data, "display-name")
                ?? ReadLocalString(data, "title")
                ?? primaryKey;
            displayNames[primaryKey] = displayName;
        }

        return displayNames;
    }

    private static GitWorktreeItemViewModel BuildItem(EntitySnapshot snapshot)
    {
        var displayName = "(unknown)";
        string? branch = null;
        string? headCommit = null;

        if (snapshot.Data is { } data)
        {
            displayName = ReadLocalString(data, "display-name") ?? displayName;

            if (data.TryGetProperty("git", out var git) && git.ValueKind == JsonValueKind.Object)
            {
                branch = git.TryGetProperty("branch", out var branchElement) && branchElement.ValueKind == JsonValueKind.String
                    ? branchElement.GetString()
                    : null;
                headCommit = git.TryGetProperty("head-commit", out var headElement) && headElement.ValueKind == JsonValueKind.String
                    ? headElement.GetString()
                    : null;
            }
        }

        return new GitWorktreeItemViewModel(snapshot.EntityId, displayName, branch, headCommit);
    }

    /// <summary>
    /// Finds the profile key for a git-worktree entity by looking for a secondary name whose first
    /// component is "computer-user-profiles". Returns "(Unknown)" if none found.
    /// </summary>
    private static string ReadProfileKey(EntitySnapshot snapshot)
    {
        if (snapshot.Data is not { } data
            || !data.TryGetProperty("names", out var names)
            || names.ValueKind != JsonValueKind.Array)
        {
            return "(Unknown)";
        }

        foreach (var nameArray in names.EnumerateArray())
        {
            if (nameArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var components = nameArray.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.String)
                .Select(c => c.GetString()!)
                .ToArray();

            if (components.Length > 0 && string.Equals(components[0], "computer-user-profiles", StringComparison.Ordinal))
            {
                return string.Join("|", components);
            }
        }

        return "(Unknown)";
    }

    /// <summary>Reads the primary name key from a user-computer-profile entity's names array.</summary>
    private static string ReadPrimaryNameKey(JsonElement data)
    {
        if (!data.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var nameArray in names.EnumerateArray())
        {
            if (nameArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var components = nameArray.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.String)
                .Select(c => c.GetString()!)
                .ToArray();

            if (components.Length > 0 && string.Equals(components[0], "computer-user-profiles", StringComparison.Ordinal))
            {
                return string.Join("|", components);
            }
        }

        return string.Empty;
    }

    private static string? ReadLocalString(JsonElement data, string propertyName)
    {
        if (!data.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("default", out var defaultValue)
            && defaultValue.ValueKind == JsonValueKind.String)
        {
            return defaultValue.GetString();
        }

        return null;
    }
}
