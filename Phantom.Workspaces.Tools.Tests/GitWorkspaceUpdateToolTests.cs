using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class GitWorkspaceUpdateToolTests : IDisposable
{
    private readonly TempDirectory temporaryRoot = new("git-workspace-update-");
    private string temporaryRootPath => this.temporaryRoot.Path;

    public void Dispose()
    {
        this.temporaryRoot.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesGitFieldsOnExistingGitEntity()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "real-repo"));
        var remoteUrl = "https://example.com/repo.git";
        InitializeGitRepository(repoPath, remoteUrl);

        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var entityId = DeterministicEntityId.Create("git-workspace", normalizedPath);
        await UpsertEntityAsync(
            dataAccessLayer,
            entityId,
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "git"],
              "names": [["git", "{{EscapeForJsonString(repoPath)}}"]],
              "display-name": { "default": "real-repo" },
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = await CreateContextAsync(fixture);
        var tool = new GitWorkspaceUpdateTool();

        var result = await tool.ExecuteAsync(context);

        var updatedEntity = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.NotNull(updatedEntity?.Data);
        var rawData = updatedEntity.Data!.Value.GetRawText();
        var entityObject = JsonNode.Parse(rawData)!.AsObject();
        var git = entityObject["git"]?.AsObject();
        Assert.NotNull(git);
        Assert.False(string.IsNullOrWhiteSpace(git["branch"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(git["head-commit"]?.GetValue<string>()));

        Assert.NotNull(result.ResultContent);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEntitiesWithNoPath()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await UpsertEntityAsync(
            dataAccessLayer,
            entityId,
            """
            {
              "entity-id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "entity-types": ["entity", "git"],
              "names": [["git", "no-path"]],
              "display-name": { "default": "no-path" }
            }
            """,
            concurrencyTag: null);

        var context = await CreateContextAsync(fixture);
        var tool = new GitWorkspaceUpdateTool();

        var result = await tool.ExecuteAsync(context);

        var entityAfterNoPath = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.NotNull(entityAfterNoPath);
        var gitSubObjectNoPath = JsonNode.Parse(entityAfterNoPath!.Data!.Value.GetRawText())?["git"];
        Assert.Null(gitSubObjectNoPath);
        Assert.NotNull(result.ResultContent);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEntitiesWithInvalidPath()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var missingPath = Path.Combine(this.temporaryRootPath, "does-not-exist");
        await UpsertEntityAsync(
            dataAccessLayer,
            entityId,
            $$"""
            {
              "entity-id": "cccccccc-cccc-cccc-cccc-cccccccccccc",
              "entity-types": ["entity", "git"],
              "names": [["git", "{{EscapeForJsonString(missingPath)}}"]],
              "display-name": { "default": "missing" },
              "path": "{{EscapeForJsonString(missingPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = await CreateContextAsync(fixture);
        var tool = new GitWorkspaceUpdateTool();

        var result = await tool.ExecuteAsync(context);

        var entityAfterInvalidPath = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.NotNull(entityAfterInvalidPath);
        var gitSubObjectInvalidPath = JsonNode.Parse(entityAfterInvalidPath!.Data!.Value.GetRawText())?["git"];
        Assert.Null(gitSubObjectInvalidPath);
        Assert.NotNull(result.ResultContent);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsResultContentWithSummary()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "summary-repo"));
        InitializeGitRepository(repoPath, "https://example.com/summary.git");
        var missingPath = Path.Combine(this.temporaryRootPath, "summary-missing");

        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            $$"""
            {
              "entity-id": "dddddddd-dddd-dddd-dddd-dddddddddddd",
              "entity-types": ["entity", "git"],
              "names": [["git", "{{EscapeForJsonString(repoPath)}}"]],
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);
        await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            $$"""
            {
              "entity-id": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
              "entity-types": ["entity", "git"],
              "names": [["git", "{{EscapeForJsonString(missingPath)}}"]],
              "path": "{{EscapeForJsonString(missingPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = await CreateContextAsync(fixture);
        var tool = new GitWorkspaceUpdateTool();

        var result = await tool.ExecuteAsync(context);

        Assert.NotNull(result.ResultContent);
        Assert.Contains("changed: 2", result.ResultContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("errors: 0", result.ResultContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_PreservesDisplayName()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "refresh-display"));
        InitializeGitRepository(repoPath, "https://example.com/refresh-display.git");

        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

        await UpsertEntityAsync(
            dataAccessLayer,
            deterministicId,
            $$"""
            {
              "entity-id": "{{deterministicId}}",
              "entity-types": ["entity", "filesystem-path", "git-worktree"],
              "names": [["git-worktrees", "{{EscapeForJsonString(repoPath)}}"]],
              "display-name": {"default": "CustomRefreshName"},
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = await CreateContextAsync(fixture);
        var tool = new GitWorkspaceUpdateTool();

        await tool.ExecuteAsync(context);

        var refreshedEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(refreshedEntity);
        var displayName = refreshedEntity.Data?.GetProperty("display-name").GetProperty("default").GetString();
        Assert.Equal("CustomRefreshName", displayName);
    }

    [Fact]
    public async Task Refresh_PreservesNames()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "refresh-names"));
        InitializeGitRepository(repoPath, "https://example.com/refresh-names.git");

        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

        await UpsertEntityAsync(
            dataAccessLayer,
            deterministicId,
            $$"""
            {
              "entity-id": "{{deterministicId}}",
              "entity-types": ["entity", "filesystem-path", "git-worktree"],
              "names": [["custom-refresh-name", "preserved"], ["another", "name"]],
              "display-name": {"default": "repo"},
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = await CreateContextAsync(fixture);
        var tool = new GitWorkspaceUpdateTool();

        await tool.ExecuteAsync(context);

        var refreshedEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(refreshedEntity);
        var names = refreshedEntity.Data?.GetProperty("names").EnumerateArray().ToList();
        Assert.Equal(2, names?.Count);
        Assert.Equal("custom-refresh-name", names?[0].EnumerateArray().First().GetString());
        Assert.Equal("preserved", names?[0].EnumerateArray().Skip(1).First().GetString());
    }

    [Fact]
    public async Task Refresh_UpdatesAllGitFields()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "refresh-git-fields"));
        InitializeGitRepository(repoPath, "https://example.com/refresh-git-fields.git");

        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

        await UpsertEntityAsync(
            dataAccessLayer,
            deterministicId,
            $$"""
            {
              "entity-id": "{{deterministicId}}",
              "entity-types": ["entity", "filesystem-path", "git-worktree"],
              "names": [["git-worktrees", "{{EscapeForJsonString(repoPath)}}"]],
              "display-name": {"default": "repo"},
              "path": "{{EscapeForJsonString(repoPath)}}",
              "git": {"branch": "old-branch", "head-commit": "old-commit"}
            }
            """,
            concurrencyTag: null);

        var context = await CreateContextAsync(fixture);
        var tool = new GitWorkspaceUpdateTool();

        await tool.ExecuteAsync(context);

        var refreshedEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(refreshedEntity);
        var git = refreshedEntity.Data?.GetProperty("git");
        Assert.True(git.HasValue);
        var hasBranch = git.Value.TryGetProperty("branch", out var branch);
        var hasHeadCommit = git.Value.TryGetProperty("head-commit", out var headCommit);
        Assert.True(hasBranch);
        Assert.True(hasHeadCommit);
        Assert.False(string.IsNullOrWhiteSpace(branch.GetString()));
        Assert.False(string.IsNullOrWhiteSpace(headCommit.GetString()));
    }

    [Fact]
    public async Task Refresh_UsesDeterministicId_PreservesEntityId()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "refresh-id"));
        InitializeGitRepository(repoPath, "https://example.com/refresh-id.git");

        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

        await UpsertEntityAsync(
            dataAccessLayer,
            deterministicId,
            $$"""
            {
              "entity-id": "{{deterministicId}}",
              "entity-types": ["entity", "filesystem-path", "git-worktree"],
              "names": [["git-worktrees", "{{EscapeForJsonString(repoPath)}}"]],
              "display-name": {"default": "repo"},
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = await CreateContextAsync(fixture);
        var tool = new GitWorkspaceUpdateTool();

        await tool.ExecuteAsync(context);

        var refreshedEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(refreshedEntity);
        Assert.Equal(deterministicId, refreshedEntity.EntityId);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatedWorktree_SetsComputerUserProfileIdToCurrentProfileEntityId()
    {
        var repoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "update-profile-id"));
        InitializeGitRepository(repoPath, "https://example.com/update-profile-id.git");

        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

        // Pre-existing entity WITHOUT computer-user-profile-id — simulating a worktree
        // written by a pre-fix scanner version.
        await UpsertEntityAsync(
            dataAccessLayer,
            deterministicId,
            $$"""
            {
              "entity-id": "{{deterministicId}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["git-worktrees", "{{EscapeForJsonString(repoPath)}}"]],
              "display-name": {"default": "update-profile-id"},
              "path": "{{EscapeForJsonString(repoPath)}}"
            }
            """,
            concurrencyTag: null);

        var context = await CreateContextAsync(fixture);
        var tool = new GitWorkspaceUpdateTool();

        await tool.ExecuteAsync(context);

        var refreshedEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(refreshedEntity);
        Assert.True(refreshedEntity.Data!.Value.TryGetProperty("computer-user-profile-id", out var profileIdElement));
        Assert.Equal(context.CurrentComputerUserProfileEntity.EntityId.ToString(), profileIdElement.GetString());
    }

    [Fact]
    public async Task ExecuteAsync_QueriesAllExistingGitAndGitWorktreeEntities_NotJustNewlyDiscoveredOnes()
    {
        var firstPath = Path.Combine(this.temporaryRootPath, "query-all-first");
        var secondPath = Path.Combine(this.temporaryRootPath, "query-all-second");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var firstId = new EntityId(Guid.NewGuid());
        var secondId = new EntityId(Guid.NewGuid());
        await SeedGitWorkspaceAsync(dataAccessLayer, firstId, firstPath, entityType: "git");
        await SeedGitWorkspaceAsync(dataAccessLayer, secondId, secondPath);
        var tool = CreateTool(directoryExists: _ => true);

        await tool.ExecuteAsync(await CreateContextAsync(fixture));

        Assert.True((await GetEntityByIdAsync(dataAccessLayer, firstId))!.Data!.Value
            .GetProperty("exists-on-filesystem").GetBoolean());
        Assert.True((await GetEntityByIdAsync(dataAccessLayer, secondId))!.Data!.Value
            .GetProperty("exists-on-filesystem").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_WorktreeFolderDeleted_SetsExistsOnFilesystemFalseAndHidesEntity()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId(Guid.NewGuid());
        await SeedGitWorkspaceAsync(
            dataAccessLayer,
            entityId,
            Path.Combine(this.temporaryRootPath, "deleted-worktree"),
            existsOnFilesystem: true);
        var tool = CreateTool(directoryExists: _ => false);

        await tool.ExecuteAsync(await CreateContextAsync(fixture));

        var entity = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.False(entity!.Data!.Value.GetProperty("exists-on-filesystem").GetBoolean());
        var hideRelationship = await GetEntityByIdAsync(dataAccessLayer, MissingRelationshipId(entityId));
        Assert.NotNull(hideRelationship?.Data);
        Assert.Equal(
            entityId.ToString(),
            hideRelationship.Data.Value.GetProperty("participants").GetProperty("target").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_WorktreeFolderDeleted_AddsGenericNoteToHideRelationship_WithoutLeakingPath()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId(Guid.NewGuid());
        var missingPath = Path.Combine(this.temporaryRootPath, "private", "deleted-worktree");
        await SeedGitWorkspaceAsync(dataAccessLayer, entityId, missingPath, existsOnFilesystem: true);

        await CreateTool(directoryExists: _ => false).ExecuteAsync(await CreateContextAsync(fixture));

        var relationship = await GetEntityByIdAsync(dataAccessLayer, MissingRelationshipId(entityId));
        var note = relationship!.Data!.Value.GetProperty("note").GetString();
        Assert.Equal("This Git workspace does not exist on the filesystem.", note);
        Assert.DoesNotContain(missingPath, note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_NewlyMissingWorktree_DoesNotRefreshGitMetadata()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId(Guid.NewGuid());
        await SeedGitWorkspaceAsync(
            dataAccessLayer,
            entityId,
            Path.Combine(this.temporaryRootPath, "missing-no-metadata"),
            existsOnFilesystem: true,
            gitJson: """{"branch":"preserved","head-commit":"abc"}""");
        var metadataReadCount = 0;
        var tool = new GitWorkspaceUpdateTool(
            metadataReader: (_, _) =>
            {
                metadataReadCount++;
                return new GitMetadata { BranchName = "unexpected" };
            },
            directoryExists: _ => false);

        await tool.ExecuteAsync(await CreateContextAsync(fixture));

        var entity = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.Equal(0, metadataReadCount);
        Assert.Equal("preserved", entity!.Data!.Value.GetProperty("git").GetProperty("branch").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_WorktreeFolderRestored_SetsExistsOnFilesystemTrueAndUnhidesEntity()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId(Guid.NewGuid());
        await SeedGitWorkspaceAsync(
            dataAccessLayer,
            entityId,
            Path.Combine(this.temporaryRootPath, "restored-worktree"),
            existsOnFilesystem: false);
        await SeedMissingRelationshipAsync(dataAccessLayer, entityId);

        await CreateTool(directoryExists: _ => true).ExecuteAsync(await CreateContextAsync(fixture));

        var entity = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.True(entity!.Data!.Value.GetProperty("exists-on-filesystem").GetBoolean());
        var hideRelationship = await GetEntityByIdAsync(dataAccessLayer, MissingRelationshipId(entityId));
        Assert.True(hideRelationship is null || hideRelationship.Data is null);
    }

    [Fact]
    public async Task ExecuteAsync_WorktreeFolderRestored_PreservesIndependentUserHiddenState()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId(Guid.NewGuid());
        var userRelationshipId = new EntityId(Guid.NewGuid());
        var userId = new EntityId(Guid.NewGuid());
        await fixture.SeedValidEntityAsync(
            JsonDocument.Parse(
                $$"""
                {
                  "entity-id": "{{userId}}",
                  "entity-types": ["entity", "user"],
                  "names": [["users", "username", "independent-hidden-state"]]
                }
                """).RootElement.Clone());
        await SeedGitWorkspaceAsync(
            dataAccessLayer,
            entityId,
            Path.Combine(this.temporaryRootPath, "restored-user-hidden"),
            existsOnFilesystem: false);
        await SeedMissingRelationshipAsync(dataAccessLayer, entityId);
        await SeedNotInterestingRelationshipAsync(
            dataAccessLayer,
            userRelationshipId,
            entityId,
            "Hidden by the user.",
            userId);

        await CreateTool(directoryExists: _ => true).ExecuteAsync(await CreateContextAsync(fixture));

        var systemRelationship = await GetEntityByIdAsync(dataAccessLayer, MissingRelationshipId(entityId));
        var userRelationship = await GetEntityByIdAsync(dataAccessLayer, userRelationshipId);
        Assert.True(systemRelationship is null || systemRelationship.Data is null);
        Assert.NotNull(userRelationship?.Data);
        Assert.Equal(
            "Hidden by the user.",
            userRelationship.Data.Value.GetProperty("note").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_StillMissingWorktree_RepeatedRuns_DoesNotDuplicateHideRelationshipOrNote()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId(Guid.NewGuid());
        await SeedGitWorkspaceAsync(
            dataAccessLayer,
            entityId,
            Path.Combine(this.temporaryRootPath, "still-missing"),
            existsOnFilesystem: true);
        var tool = CreateTool(directoryExists: _ => false);
        var context = await CreateContextAsync(fixture);
        await tool.ExecuteAsync(context);
        var firstRelationship = await GetEntityByIdAsync(dataAccessLayer, MissingRelationshipId(entityId));

        var secondResult = await tool.ExecuteAsync(context);

        var secondRelationship = await GetEntityByIdAsync(dataAccessLayer, MissingRelationshipId(entityId));
        Assert.Equal(firstRelationship?.ConcurrencyTag, secondRelationship?.ConcurrencyTag);
        Assert.Contains("unchanged: 1", secondResult.ResultContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_StillMissingWorktree_UserRemovedHideRelationship_DoesNotReHideOnNextRun()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId(Guid.NewGuid());
        await SeedGitWorkspaceAsync(
            dataAccessLayer,
            entityId,
            Path.Combine(this.temporaryRootPath, "missing-user-unhidden"),
            existsOnFilesystem: true);
        var tool = CreateTool(directoryExists: _ => false);
        var context = await CreateContextAsync(fixture);
        await tool.ExecuteAsync(context);
        var hideRelationship = await GetEntityByIdAsync(dataAccessLayer, MissingRelationshipId(entityId));
        Assert.NotNull(hideRelationship);
        await DeleteEntityAsync(dataAccessLayer, hideRelationship!);

        await tool.ExecuteAsync(context);

        var afterSecondRun = await GetEntityByIdAsync(dataAccessLayer, MissingRelationshipId(entityId));
        Assert.True(afterSecondRun is null || afterSecondRun.Data is null);
    }

    [Fact]
    public async Task ExecuteAsync_PreExistingEntityMissingExistsOnFilesystemProperty_BackfillsAuthoritativeValueFromDisk()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId(Guid.NewGuid());
        await SeedGitWorkspaceAsync(
            dataAccessLayer,
            entityId,
            Path.Combine(this.temporaryRootPath, "backfilled-worktree"));

        await CreateTool(directoryExists: _ => true).ExecuteAsync(await CreateContextAsync(fixture));

        var entity = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.True(entity!.Data!.Value.GetProperty("exists-on-filesystem").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_EntityOwnedByDifferentComputerUserProfile_DoesNotEvaluateOrChangeFilesystemExistence()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId(Guid.NewGuid());
        await SeedGitWorkspaceAsync(
            dataAccessLayer,
            entityId,
            Path.Combine(this.temporaryRootPath, "foreign-worktree"),
            existsOnFilesystem: true,
            computerUserProfileId: new EntityId(Guid.NewGuid()));
        var existenceCheckCount = 0;
        var tool = CreateTool(
            directoryExists: _ =>
            {
                existenceCheckCount++;
                return false;
            });

        var result = await tool.ExecuteAsync(await CreateContextAsync(fixture));

        var entity = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.Equal(0, existenceCheckCount);
        Assert.True(entity!.Data!.Value.GetProperty("exists-on-filesystem").GetBoolean());
        Assert.Contains("foreign: 1", result.ResultContent, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await GetEntityByIdAsync(dataAccessLayer, MissingRelationshipId(entityId)));
    }

    [Fact]
    public async Task ExecuteAsync_EntityWithNoComputerUserProfileId_TreatsAsLocalAndReconciles()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var entityId = new EntityId(Guid.NewGuid());
        await SeedGitWorkspaceAsync(
            dataAccessLayer,
            entityId,
            Path.Combine(this.temporaryRootPath, "legacy-missing"));

        var context = await CreateContextAsync(fixture);
        await CreateTool(directoryExists: _ => false).ExecuteAsync(context);

        var entity = await GetEntityByIdAsync(dataAccessLayer, entityId);
        Assert.False(entity!.Data!.Value.GetProperty("exists-on-filesystem").GetBoolean());
        Assert.Equal(
            context.CurrentComputerUserProfileEntity.EntityId.ToString(),
            entity.Data.Value.GetProperty("computer-user-profile-id").GetString());
        Assert.NotNull((await GetEntityByIdAsync(dataAccessLayer, MissingRelationshipId(entityId)))?.Data);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrencyConflictOnOneEntity_ReportsErrorAndProcessesRemainingEntities()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var inner = fixture.DataAccessLayer;
        var failingEntityId = new EntityId(Guid.NewGuid());
        var succeedingEntityId = new EntityId(Guid.NewGuid());
        await SeedGitWorkspaceAsync(
            inner,
            failingEntityId,
            Path.Combine(this.temporaryRootPath, "conflicting-worktree"));
        await SeedGitWorkspaceAsync(
            inner,
            succeedingEntityId,
            Path.Combine(this.temporaryRootPath, "successful-worktree"));
        var dataAccessLayer = new FailingEntityUpdateDataAccessLayer(inner, failingEntityId);

        var result = await CreateTool(directoryExists: _ => true)
            .ExecuteAsync(await CreateContextAsync(fixture, dataAccessLayer));

        Assert.Contains("errors: 1", result.ResultContent, StringComparison.OrdinalIgnoreCase);
        var succeeded = await GetEntityByIdAsync(inner, succeedingEntityId);
        Assert.True(succeeded!.Data!.Value.GetProperty("exists-on-filesystem").GetBoolean());
    }

    private static GitWorkspaceUpdateTool CreateTool(Func<string, bool> directoryExists)
        => new(
            metadataReader: (_, _) => new GitMetadata
            {
                BranchName = "main",
                HeadCommitHash = "abc123",
            },
            directoryExists: directoryExists);

    private static EntityId MissingRelationshipId(EntityId targetEntityId)
        => DeterministicEntityId.Create("git-workspace-missing", targetEntityId.ToString());

    private static async Task SeedGitWorkspaceAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        string path,
        bool? existsOnFilesystem = null,
        EntityId? computerUserProfileId = null,
        string? gitJson = null,
        string entityType = "git-worktree")
    {
        var entityTypes = entityType == "git"
            ? new JsonArray("entity", "git")
            : new JsonArray("entity", "git-worktree", "filesystem-path");
        var data = new JsonObject
        {
            ["entity-id"] = entityId.ToString(),
            ["entity-types"] = entityTypes,
            ["names"] = new JsonArray(new JsonArray("git-worktrees", path)),
            ["display-name"] = new JsonObject { ["default"] = "worktree" },
            ["path"] = path,
        };
        if (existsOnFilesystem is { } exists)
        {
            data["exists-on-filesystem"] = exists;
        }

        if (computerUserProfileId is { } profileId)
        {
            data["computer-user-profile-id"] = profileId.ToString();
        }

        if (gitJson is not null)
        {
            data["git"] = JsonNode.Parse(gitJson);
        }

        await UpsertEntityAsync(
            dataAccessLayer,
            entityId,
            data.ToJsonString(),
            concurrencyTag: null);
    }

    private static Task SeedMissingRelationshipAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId targetEntityId)
        => SeedNotInterestingRelationshipAsync(
            dataAccessLayer,
            MissingRelationshipId(targetEntityId),
            targetEntityId,
            "This Git workspace does not exist on the filesystem.");

    private static async Task SeedNotInterestingRelationshipAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId relationshipId,
        EntityId targetEntityId,
        string note,
        EntityId? userId = null)
    {
        var userParticipant = userId is { } user
            ? $", \"user\": \"{user}\""
            : string.Empty;
        await UpsertEntityAsync(
            dataAccessLayer,
            relationshipId,
            $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "not-interesting", "relationship"],
              "participants": { "target": "{{targetEntityId}}"{{userParticipant}} },
              "note": "{{note}}"
            }
            """,
            concurrencyTag: null);
    }

    private static async Task DeleteEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntitySnapshot entity)
    {
        var result = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Delete test relationship." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entity.EntityId,
                        ConcurrencyTag = entity.ConcurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = null,
                    },
                ],
            });
        Assert.DoesNotContain(result.EntityResults, static item => item.UpdateState == UpdateState.Failed);
    }

    private static async Task<WorkspaceToolExecutionContext> CreateContextAsync(
        ValidatingEntitySeedFixture fixture,
        IDataAccessLayer? dataAccessLayer = null)
    {
        var placeholderId = new EntityId();
        var toolId = new EntityId();
        await fixture.SeedManyValidAsync(
            [
                JsonDocument.Parse(
                    $$"""
                    {
                      "entity-id": "{{placeholderId}}",
                      "entity-types": ["entity", "task"],
                      "names": [["tasks", "git-workspace-update-context"]]
                    }
                    """).RootElement.Clone(),
                JsonDocument.Parse(
                    $$"""
                    {
                      "entity-id": "{{toolId}}",
                      "entity-types": ["entity", "tool"],
                      "names": [["tools", "git-workspace-update"]],
                      "tool-type": "git-workspace-update"
                    }
                    """).RootElement.Clone(),
            ]);
        var result = await fixture.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest { EntityId = placeholderId },
                    new GetEntityRequest { EntityId = toolId },
                ],
                Timestamps = [null],
            });
        var entities = result.Batches.SelectMany(static batch => batch.Entities).ToArray();
        var placeholder = Assert.Single(entities, entity => entity.EntityId == placeholderId);
        var tool = Assert.Single(entities, entity => entity.EntityId == toolId);
        return new WorkspaceToolExecutionContext
        {
            DataAccessLayer = dataAccessLayer ?? fixture.DataAccessLayer,
            CancellationToken = CancellationToken.None,
            CurrentComputerEntity = placeholder,
            CurrentUserEntity = placeholder,
            CurrentComputerUserProfileEntity = placeholder,
            ToolRelationship = placeholder,
            Participants = [placeholder],
            Tool = tool,
            Schedule = placeholder,
        };
    }

    private static async Task<EntitySnapshot?> GetEntityByIdAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId)
    {
        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = entityId,
                    },
                ],
            });
        return getResult.Batches.SelectMany(static b => b.Entities).FirstOrDefault();
    }

    private static async Task<EntitySnapshot> UpsertEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        string json,
        ConcurrencyTag? concurrencyTag)
    {
        using var document = JsonDocument.Parse(json);
        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "GitWorkspaceUpdateTool test upsert." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = concurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });

        var entityResult = Assert.Single(updateResult.EntityResults, r => r.RequestedEntityId == entityId);
        Assert.Empty(entityResult.Errors);
        return Assert.IsType<EntitySnapshot>(entityResult.CurrentEntity);
    }

    private static void InitializeGitRepository(string repositoryPath, string remoteUrl)
    {
        Directory.CreateDirectory(repositoryPath);
        File.WriteAllText(Path.Combine(repositoryPath, "README.md"), "# test");
        Repository.Init(repositoryPath);

        using var repository = new Repository(repositoryPath);
        repository.Config.Set("user.name", "test-user");
        repository.Config.Set("user.email", "test@example.com");
        Commands.Stage(repository, "*");
        var signature = new Signature("test-user", "test@example.com", DateTimeOffset.UtcNow);
        repository.Commit("initial", signature, signature);
        repository.Network.Remotes.Add("origin", remoteUrl);
    }

    private static string EscapeForJsonString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private sealed class FailingEntityUpdateDataAccessLayer(
        IDataAccessLayer inner,
        EntityId failingEntityId) : IDataAccessLayer
    {
        public Task<UpdateResult> UpdateAsync(
            UpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            var failedChange = request.Changes.SingleOrDefault(change => change.EntityId == failingEntityId);
            if (failedChange is null)
            {
                return inner.UpdateAsync(request, cancellationToken);
            }

            return Task.FromResult(
                new UpdateResult
                {
                    EntityResults =
                    [
                        new EntityUpdateResult
                        {
                            UpdateState = UpdateState.Failed,
                            RequestedEntityId = failingEntityId,
                            ResultingEntityId = failingEntityId,
                            ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
                            Errors = [new UpdateError { Message = "Simulated concurrency conflict." }],
                        },
                    ],
                });
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
            => inner.GetAsync(request, cancellationToken);

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => inner.QueryAsync(request, cancellationToken);

        public Task<GetHistoryResult> GetHistoryAsync(
            GetHistoryRequest request,
            CancellationToken cancellationToken = default)
            => inner.GetHistoryAsync(request, cancellationToken);

#pragma warning disable CS0618
        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            CancellationToken cancellationToken = default)
            => inner.ExportAsync(request, cancellationToken);
#pragma warning restore CS0618

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
            GetChangedEntitiesRequest request,
            CancellationToken cancellationToken = default)
            => inner.GetChangedEntitiesAsync(request, cancellationToken);
    }
}
