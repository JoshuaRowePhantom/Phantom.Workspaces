using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorkspaceEntityDataTests
{
    [Fact]
    public void Build_IncludesAllFields()
    {
        var path = Path.Combine(Path.GetTempPath(), "test-repo");
        var profileNames = new[] { new EntityName("user-computer-profile", "TEST-MACHINE") };
        var metadata = new GitMetadata
        {
            BranchName = "main",
            HeadCommitHash = "abc123",
            OriginRemoteUrl = "https://github.com/test/repo.git",
        };

        var result = GitWorkspaceEntityData.Build(path, profileNames, metadata);

        Assert.NotNull(result);
        Assert.True(result.ContainsKey("entity-types"));
        Assert.True(result.ContainsKey("names"));
        Assert.True(result.ContainsKey("display-name"));
        Assert.True(result.ContainsKey("path"));
        Assert.True(result.ContainsKey("git"));

        var entityTypes = result["entity-types"]!.AsArray();
        Assert.Contains(entityTypes, node => node?.GetValue<string>() == "entity");
        Assert.Contains(entityTypes, node => node?.GetValue<string>() == "git-worktree");
        Assert.Contains(entityTypes, node => node?.GetValue<string>() == "filesystem-path");

        var git = result["git"]!.AsObject();
        Assert.Equal("main", git["branch"]?.GetValue<string>());
        Assert.Equal("abc123", git["head-commit"]?.GetValue<string>());
        Assert.True(git.ContainsKey("remotes"));
    }

    [Fact]
    public void Build_OmitsGit_WhenNoMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), "test-repo");
        var profileNames = new[] { new EntityName("user-computer-profile", "TEST-MACHINE") };

        var result = GitWorkspaceEntityData.Build(path, profileNames, null);

        Assert.NotNull(result);
        Assert.False(result.ContainsKey("git"));
        Assert.True(result.ContainsKey("entity-types"));
        Assert.True(result.ContainsKey("names"));
        Assert.True(result.ContainsKey("display-name"));
        Assert.True(result.ContainsKey("path"));
    }

    [Fact]
    public void Build_NamesAreProfileScoped()
    {
        var path = Path.Combine(Path.GetTempPath(), "test-repo");
        var profileNames = new[] { new EntityName("user-computer-profile", "TEST-MACHINE") };
        var metadata = new GitMetadata { BranchName = "main" };

        var result = GitWorkspaceEntityData.Build(path, profileNames, metadata);

        var names = result["names"]!.AsArray();
        Assert.NotEmpty(names);

        var primaryName = names[0]!.AsArray();
        Assert.Equal("user-computer-profile", primaryName[0]?.GetValue<string>());
        Assert.Equal("TEST-MACHINE", primaryName[1]?.GetValue<string>());
        Assert.Equal("git-workspace", primaryName[2]?.GetValue<string>());
        Assert.NotNull(primaryName[3]); // normalized path

        // Verify no ["git-worktrees", path] entry exists
        Assert.DoesNotContain(names, node =>
        {
            var arr = node?.AsArray();
            return arr != null
                   && arr.Count >= 2
                   && arr[0]?.GetValue<string>() == "git-worktrees";
        });
    }

    [Fact]
    public void Build_NamesUseGitWorkspaceFallback_WhenNoProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), "test-repo");
        var profileNames = Array.Empty<EntityName>();
        var metadata = new GitMetadata { BranchName = "main" };

        var result = GitWorkspaceEntityData.Build(path, profileNames, metadata);

        var names = result["names"]!.AsArray();
        Assert.NotEmpty(names);

        var primaryName = names[0]!.AsArray();
        Assert.Equal("git-workspace", primaryName[0]?.GetValue<string>());
        Assert.NotNull(primaryName[1]); // normalized path
        Assert.Equal(2, primaryName.Count);
    }

    [Fact]
    public void MergePreservingUserEditableFields_KeepsExistingDisplayName()
    {
        var existingJson = """
            {
              "display-name": { "default": "CustomName" },
              "names": [["old", "name"]],
              "path": "/old/path",
              "git": { "branch": "old-branch" }
            }
            """;
        var existing = JsonDocument.Parse(existingJson).RootElement;

        var incoming = new System.Text.Json.Nodes.JsonObject
        {
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "NewName" },
            ["names"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonArray("new", "name")),
            ["path"] = "/new/path",
            ["git"] = new System.Text.Json.Nodes.JsonObject { ["branch"] = "new-branch" },
        };

        var result = GitWorkspaceEntityData.MergePreservingUserEditableFields(existing, incoming);

        Assert.Equal("CustomName", result["display-name"]!["default"]?.GetValue<string>());
        Assert.Equal("/new/path", result["path"]?.GetValue<string>());
        Assert.Equal("new-branch", result["git"]!["branch"]?.GetValue<string>());
    }

    [Fact]
    public void MergePreservingUserEditableFields_UsesIncomingDisplayName_WhenMissing()
    {
        var existingJson = """
            {
              "names": [["old", "name"]],
              "path": "/old/path"
            }
            """;
        var existing = JsonDocument.Parse(existingJson).RootElement;

        var incoming = new System.Text.Json.Nodes.JsonObject
        {
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "NewName" },
            ["names"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonArray("new", "name")),
            ["path"] = "/new/path",
        };

        var result = GitWorkspaceEntityData.MergePreservingUserEditableFields(existing, incoming);

        Assert.Equal("NewName", result["display-name"]!["default"]?.GetValue<string>());
    }

    [Fact]
    public void MergePreservingUserEditableFields_PreservesNames()
    {
        var existingJson = """
            {
              "display-name": { "default": "CustomName" },
              "names": [["preserved", "name", "path"]],
              "path": "/old/path"
            }
            """;
        var existing = JsonDocument.Parse(existingJson).RootElement;

        var incoming = new System.Text.Json.Nodes.JsonObject
        {
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "NewName" },
            ["names"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonArray("new", "name")),
            ["path"] = "/new/path",
        };

        var result = GitWorkspaceEntityData.MergePreservingUserEditableFields(existing, incoming);

        var names = result["names"]!.AsArray();
        Assert.Single(names);
        var preservedName = names[0]!.AsArray();
        Assert.Equal(3, preservedName.Count);
        Assert.Equal("preserved", preservedName[0]?.GetValue<string>());
        Assert.Equal("name", preservedName[1]?.GetValue<string>());
        Assert.Equal("path", preservedName[2]?.GetValue<string>());
    }

    [Fact]
    public void MergePreservingUserEditableFields_UpdatesGit()
    {
        var existingJson = """
            {
              "display-name": { "default": "CustomName" },
              "names": [["old", "name"]],
              "path": "/old/path",
              "git": { "branch": "old-branch", "head-commit": "old123" }
            }
            """;
        var existing = JsonDocument.Parse(existingJson).RootElement;

        var incoming = new System.Text.Json.Nodes.JsonObject
        {
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "NewName" },
            ["names"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonArray("new", "name")),
            ["path"] = "/new/path",
            ["git"] = new System.Text.Json.Nodes.JsonObject
            {
                ["branch"] = "new-branch",
                ["head-commit"] = "new456",
            },
        };

        var result = GitWorkspaceEntityData.MergePreservingUserEditableFields(existing, incoming);

        var git = result["git"]!.AsObject();
        Assert.Equal("new-branch", git["branch"]?.GetValue<string>());
        Assert.Equal("new456", git["head-commit"]?.GetValue<string>());
    }

    [Fact]
    public void Build_IncludesOwningRepository_WhenProvided()
    {
        var path = Path.Combine(Path.GetTempPath(), "linked-worktree");
        var profileNames = new[] { new EntityName("user-computer-profile", "TEST-MACHINE") };
        var metadata = new GitMetadata { BranchName = "feature" };
        var owningRepository = Path.Combine(Path.GetTempPath(), "main-repo");

        var result = GitWorkspaceEntityData.Build(path, profileNames, metadata, owningRepository);

        Assert.True(result.ContainsKey("owning-repository"));
        Assert.Equal(owningRepository, result["owning-repository"]?.GetValue<string>());
    }

    [Fact]
    public void Build_OmitsOwningRepository_WhenNull()
    {
        var path = Path.Combine(Path.GetTempPath(), "standalone-repo");
        var profileNames = new[] { new EntityName("user-computer-profile", "TEST-MACHINE") };
        var metadata = new GitMetadata { BranchName = "main" };

        var result = GitWorkspaceEntityData.Build(path, profileNames, metadata, null);

        Assert.False(result.ContainsKey("owning-repository"));
    }

    [Fact]
    public void Build_WithProfileId_IncludesComputerUserProfileIdField()
    {
        var path = Path.Combine(Path.GetTempPath(), "profile-scoped-repo");
        var profileNames = new[] { new EntityName("user-computer-profile", "TEST-MACHINE") };
        var metadata = new GitMetadata { BranchName = "main" };
        var profileId = new EntityId(Guid.Parse("33333333-3333-3333-3333-333333333333"));

        var result = GitWorkspaceEntityData.Build(
            path,
            profileNames,
            metadata,
            owningRepository: null,
            computerUserProfileId: profileId);

        Assert.True(result.ContainsKey("computer-user-profile-id"));
        Assert.Equal(profileId.ToString(), result["computer-user-profile-id"]?.GetValue<string>());
    }

    [Fact]
    public void Build_WithoutProfileId_OmitsComputerUserProfileIdField()
    {
        var path = Path.Combine(Path.GetTempPath(), "unscoped-repo");
        var profileNames = new[] { new EntityName("user-computer-profile", "TEST-MACHINE") };
        var metadata = new GitMetadata { BranchName = "main" };

        var result = GitWorkspaceEntityData.Build(
            path,
            profileNames,
            metadata,
            owningRepository: null,
            computerUserProfileId: null);

        Assert.False(result.ContainsKey("computer-user-profile-id"));
    }
}
