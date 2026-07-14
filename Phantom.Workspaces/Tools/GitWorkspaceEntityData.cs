using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// Shared helper for building and merging git-worktree entity data.
/// Used by <see cref="GitWorkspaceDiscoveryTool"/> and <see cref="GitWorkspaceUpdateTool"/>
/// to ensure consistent entity structure and merge behavior.
/// </summary>
internal static class GitWorkspaceEntityData
{
    /// <summary>
    /// Builds the complete entity data object for a git worktree at <paramref name="path"/>.
    /// All fields — structural, path, display-name, owning-repository, and git — are included.
    /// The primary name is composed of the profile name components followed by
    /// <c>"git-workspace"</c> and the normalised path — e.g.
    /// <c>["user-computer-profile", "JROWE-DESKTOP", "git-workspace", normalizedPath]</c>.
    /// Falls back to <c>["git-workspace", normalizedPath]</c> when no profile is available.
    /// No <c>["git-worktrees", path]</c> entry is ever created.
    /// </summary>
    /// <param name="path">The filesystem path to the git worktree.</param>
    /// <param name="profileNames">The collection of profile names for the current user-computer-profile.</param>
    /// <param name="metadata">Git metadata (branch, HEAD commit, remotes); may be null.</param>
    /// <param name="owningRepository">The path to the owning repository for linked worktrees; null for root repos.</param>
    /// <returns>A complete entity data object ready for upsert.</returns>
    public static JsonObject Build(
        string path,
        IReadOnlyCollection<EntityName> profileNames,
        GitMetadata? metadata,
        string? owningRepository = null)
    {
        var fullPath = Path.GetFullPath(path);
        var normalizedPath = NormalizeRepositoryPath(fullPath);
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var names = new JsonArray();

        // Primary name: [...profileComponents, "git-workspace", normalizedPath]
        // or ["git-workspace", normalizedPath] when no profile
        var profileName = profileNames.FirstOrDefault(
            n => n.Components.Length > 0
                 && (string.Equals(n.Components[0], "computer-user-profiles", StringComparison.Ordinal)
                     || string.Equals(n.Components[0], "user-computer-profile", StringComparison.Ordinal)));

        var primaryNameArray = new JsonArray();
        if (profileName.Components.Length > 0)
        {
            foreach (var component in profileName.Components)
            {
                primaryNameArray.Add(component);
            }
        }

        primaryNameArray.Add("git-workspace");
        primaryNameArray.Add(normalizedPath);
        names.Add(primaryNameArray);

        var entityData = new JsonObject
        {
            ["entity-types"] = new JsonArray("entity", "git-worktree", "filesystem-path"),
            ["names"] = names,
            ["display-name"] = new JsonObject
            {
                ["default"] = name,
            },
            ["path"] = fullPath,
        };

        if (!string.IsNullOrWhiteSpace(owningRepository))
        {
            entityData["owning-repository"] = owningRepository;
        }

        if (metadata != null)
        {
            var gitObject = new JsonObject();

            if (!string.IsNullOrWhiteSpace(metadata.BranchName))
            {
                gitObject["branch"] = metadata.BranchName;
            }

            if (!string.IsNullOrWhiteSpace(metadata.HeadCommitHash))
            {
                gitObject["head-commit"] = metadata.HeadCommitHash;
            }

            if (!string.IsNullOrWhiteSpace(metadata.OriginRemoteUrl))
            {
                gitObject["remotes"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "origin",
                        ["url"] = metadata.OriginRemoteUrl,
                    });
            }

            if (gitObject.Count > 0)
            {
                entityData["git"] = gitObject;
            }
        }

        return entityData;
    }

    /// <summary>
    /// Returns a merged entity object where all fields from <paramref name="incoming"/> are applied
    /// to <paramref name="existing"/>, EXCEPT that <c>display-name</c> and <c>names</c> are
    /// always preserved from <paramref name="existing"/> unchanged.
    /// </summary>
    /// <param name="existing">The existing entity data from the DAL.</param>
    /// <param name="incoming">The new entity data to merge in.</param>
    /// <returns>A merged entity data object.</returns>
    public static JsonObject MergePreservingUserEditableFields(JsonElement existing, JsonObject incoming)
    {
        var result = JsonNode.Parse(incoming.ToJsonString())!.AsObject();

        // Preserve display-name from existing if it was set
        if (existing.TryGetProperty("display-name", out var existingDisplayName))
        {
            result["display-name"] = JsonNode.Parse(existingDisplayName.GetRawText());
        }

        // Always preserve names array from existing
        if (existing.TryGetProperty("names", out var existingNames))
        {
            result["names"] = JsonNode.Parse(existingNames.GetRawText());
        }

        return result;
    }

    private static string NormalizeRepositoryPath(string repositoryPath)
    {
        return Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }
}
