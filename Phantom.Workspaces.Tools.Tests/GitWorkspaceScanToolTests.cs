using System.Text.Json;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class GitWorkspaceScanToolTests : IDisposable
{
    private readonly TempDirectory temporaryRoot = new("git-workspace-scan-");
    private string temporaryRootPath => this.temporaryRoot.Path;

    [Fact]
    public async Task ExecuteAsync_ScansOnlyCurrentComputerUserProfileParticipantsAndUpsertsGitWorktreeEntities()
    {
        var currentProfileRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "current-profile-root"));
        var otherProfileRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other-profile-root"));
        var currentProfileRepositoryPath = Path.GetFullPath(Path.Combine(currentProfileRoot, "repo-a"));
        var otherProfileRepositoryPath = Path.GetFullPath(Path.Combine(otherProfileRoot, "repo-b"));

        InitializeGitRepository(currentProfileRepositoryPath, "https://example.com/current.git");
        InitializeGitRepository(otherProfileRepositoryPath, "https://example.com/other.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            currentProfileRoot,
            otherProfileRoot);
        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([currentProfileRoot]));

        await tool.ExecuteAsync(context);

        var discoveredCurrentWorktree = await GetEntityByNameAsync(
            dataAccessLayer,
            new EntityName("git-worktrees", currentProfileRepositoryPath));
        Assert.NotNull(discoveredCurrentWorktree);
        var currentRawData = discoveredCurrentWorktree.Data?.GetRawText() ?? string.Empty;
        Assert.Contains("\"git-worktree\"", currentRawData, StringComparison.Ordinal);
        Assert.Contains($"\"path\":\"{EscapeForJsonString(currentProfileRepositoryPath)}\"", currentRawData, StringComparison.Ordinal);
        Assert.DoesNotContain(EscapeForJsonString(otherProfileRepositoryPath), currentRawData, StringComparison.Ordinal);

        await tool.ExecuteAsync(context);
        Assert.Equal(
            1,
            await CountEntitiesByNameAsync(dataAccessLayer, new EntityName("git-worktrees", currentProfileRepositoryPath)));
        Assert.Equal(
            0,
            await CountEntitiesByNameAsync(dataAccessLayer, new EntityName("git-worktrees", otherProfileRepositoryPath)));
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunningAgainstComputerUserProfile_ScansProvidedLocalDrives()
    {
        var currentProfileRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "local-drive-root"));
        var repositoryPath = Path.GetFullPath(Path.Combine(currentProfileRoot, "repo-drive-scan"));
        InitializeGitRepository(repositoryPath, "https://example.com/drive.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            currentProfileRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other-profile-root")));
        context = context with
        {
            Participants = [context.CurrentComputerUserProfileEntity],
        };

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([currentProfileRoot]));
        await tool.ExecuteAsync(context);

        var discoveredWorktree = await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", repositoryPath));
        Assert.NotNull(discoveredWorktree);
    }

    [Fact]
    public async Task ExecuteAsync_RootRepo_DoesNotHaveOwningRepositorySet()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "root-no-owning"));
        var rootRepoPath = Path.GetFullPath(Path.Combine(scanRoot, "root-repo"));
        InitializeGitRepository(rootRepoPath, "https://example.com/root.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));
        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", rootRepoPath));
        Assert.NotNull(entity);
        var rawData = entity.Data?.GetRawText() ?? string.Empty;
        Assert.DoesNotContain("owning-repository", rawData, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_LinkedWorktree_HasOwningRepositorySet()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "linked-owning"));
        var rootRepoPath = Path.GetFullPath(Path.Combine(scanRoot, "root-repo"));
        var linkedWorktreePath = Path.GetFullPath(Path.Combine(scanRoot, "linked-wt"));
        InitializeGitRepository(rootRepoPath, "https://example.com/root.git");
        AddLinkedWorktree(rootRepoPath, "linked-wt", linkedWorktreePath);

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));
        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", linkedWorktreePath));
        Assert.NotNull(entity);
        var rawData = entity.Data?.GetRawText() ?? string.Empty;
        Assert.Contains("\"owning-repository\"", rawData, StringComparison.Ordinal);
        Assert.Contains(EscapeForJsonString(rootRepoPath), rawData, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_LinkedWorktreeOutsideRootDirectory_StillDiscovered()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "outside-scan"));
        var outsideRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "outside-linked-wt"));
        var rootRepoPath = Path.GetFullPath(Path.Combine(scanRoot, "root-repo"));
        var linkedWorktreePath = Path.GetFullPath(Path.Combine(outsideRoot, "wt"));
        Directory.CreateDirectory(outsideRoot);
        InitializeGitRepository(rootRepoPath, "https://example.com/outside.git");
        AddLinkedWorktree(rootRepoPath, "outside-wt", linkedWorktreePath);

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));
        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", linkedWorktreePath));
        Assert.NotNull(entity);
    }

    [Fact]
    public async Task ExecuteAsync_LinkedWorktreeOutsideRootDirectory_HasOwningRepositorySet()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "outside-owning"));
        var outsideRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "outside-owning-wt"));
        var rootRepoPath = Path.GetFullPath(Path.Combine(scanRoot, "root-repo"));
        var linkedWorktreePath = Path.GetFullPath(Path.Combine(outsideRoot, "wt"));
        Directory.CreateDirectory(outsideRoot);
        InitializeGitRepository(rootRepoPath, "https://example.com/outside-owning.git");
        AddLinkedWorktree(rootRepoPath, "outside-owning-wt", linkedWorktreePath);

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));
        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", linkedWorktreePath));
        Assert.NotNull(entity);
        var rawData = entity.Data?.GetRawText() ?? string.Empty;
        Assert.Contains("\"owning-repository\"", rawData, StringComparison.Ordinal);
        Assert.Contains(EscapeForJsonString(rootRepoPath), rawData, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_LinkedWorktreeInsideRootDirectory_NotDuplicated()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "dedup-inside"));
        var rootRepoPath = Path.GetFullPath(Path.Combine(scanRoot, "root-repo"));
        var linkedWorktreePath = Path.GetFullPath(Path.Combine(scanRoot, "linked-inside"));
        InitializeGitRepository(rootRepoPath, "https://example.com/dedup.git");
        AddLinkedWorktree(rootRepoPath, "linked-inside", linkedWorktreePath);

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));
        await tool.ExecuteAsync(context);

        Assert.Equal(
            1,
            await CountEntitiesByNameAsync(dataAccessLayer, new EntityName("git-worktrees", linkedWorktreePath)));

        var entity = await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", linkedWorktreePath));
        Assert.NotNull(entity);
        var rawData = entity.Data?.GetRawText() ?? string.Empty;
        Assert.Contains("\"owning-repository\"", rawData, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_OwningRepositoryPath_IsNormalized()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "normalized-owning"));
        var rootRepoPath = Path.GetFullPath(Path.Combine(scanRoot, "norm-repo"));
        var linkedWorktreePath = Path.GetFullPath(Path.Combine(scanRoot, "norm-linked"));
        InitializeGitRepository(rootRepoPath, "https://example.com/norm.git");
        AddLinkedWorktree(rootRepoPath, "norm-linked", linkedWorktreePath);

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));
        await tool.ExecuteAsync(context);

        var entity = await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", linkedWorktreePath));
        Assert.NotNull(entity);
        var rawData = entity.Data?.GetRawText() ?? string.Empty;
        // Verify owning-repository equals the normalized path (Path.GetFullPath form, no trailing separator)
        Assert.Contains($"\"owning-repository\":\"{EscapeForJsonString(rootRepoPath)}\"", rawData, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ExcludesConfigured_SkipsRepositoriesUnderExcludedPath()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "excludes-configured"));
        var excludedDirectory = Path.GetFullPath(Path.Combine(scanRoot, "excluded"));
        var includedRepoPath = Path.GetFullPath(Path.Combine(scanRoot, "included-repo"));
        var excludedRepoPath = Path.GetFullPath(Path.Combine(excludedDirectory, "excluded-repo"));
        InitializeGitRepository(includedRepoPath, "https://example.com/included.git");
        InitializeGitRepository(excludedRepoPath, "https://example.com/excluded.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var excludesJson = JsonSerializer.Serialize(new[] { excludedDirectory });
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")),
            toolExcludesJson: excludesJson);

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));
        await tool.ExecuteAsync(context);

        Assert.NotNull(await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", includedRepoPath)));
        Assert.Null(await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", excludedRepoPath)));
    }

    [Fact]
    public async Task ExecuteAsync_ParticipantPathContainsEnvironmentVariable_ScanRootIsExpanded()
    {
        var variableName = "PW_TEST_PARTICIPANT_" + Guid.NewGuid().ToString("N");
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "env-participant"));
        var repoPath = Path.GetFullPath(Path.Combine(scanRoot, "env-repo"));
        InitializeGitRepository(repoPath, "https://example.com/env-participant.git");

        Environment.SetEnvironmentVariable(variableName, scanRoot);
        try
        {
            var dataAccessLayer = new InMemoryDataAccessLayer();
            var context = await CreateExecutionContextAsync(
                dataAccessLayer,
                "%" + variableName + "%",
                Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));

            var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider(Array.Empty<string>()));
            await tool.ExecuteAsync(context);

            Assert.NotNull(await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", repoPath)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task ExecuteAsync_HomeDirectoryContainsEnvironmentVariable_ScanRootIsExpanded()
    {
        var variableName = "PW_TEST_HOME_" + Guid.NewGuid().ToString("N");
        var homeDirectory = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "env-home"));
        var repoPath = Path.GetFullPath(Path.Combine(homeDirectory, "home-repo"));
        InitializeGitRepository(repoPath, "https://example.com/env-home.git");

        Environment.SetEnvironmentVariable(variableName, homeDirectory);
        try
        {
            var dataAccessLayer = new InMemoryDataAccessLayer();
            // Point currentProfileRoot at the env-var reference so the profile's home-directory
            // is stored as "%VAR%" (not the expanded path).
            var context = await CreateExecutionContextAsync(
                dataAccessLayer,
                "%" + variableName + "%",
                Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));
            // Restrict participants to just the current profile so the profile's home-directory
            // (rather than a filesystem-folder participant) is the sole scan root.
            context = context with
            {
                Participants = [context.CurrentComputerUserProfileEntity],
            };

            var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider(Array.Empty<string>()));
            await tool.ExecuteAsync(context);

            Assert.NotNull(await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", repoPath)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ExcludeContainsEnvironmentVariable_ExpandedBeforeMatching()
    {
        var variableName = "PW_TEST_EXCLUDE_" + Guid.NewGuid().ToString("N");
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "env-exclude"));
        var excludedDirectory = Path.GetFullPath(Path.Combine(scanRoot, "excluded"));
        var excludedRepoPath = Path.GetFullPath(Path.Combine(excludedDirectory, "excluded-repo"));
        var keptRepoPath = Path.GetFullPath(Path.Combine(scanRoot, "kept-repo"));
        InitializeGitRepository(excludedRepoPath, "https://example.com/env-excluded.git");
        InitializeGitRepository(keptRepoPath, "https://example.com/env-kept.git");

        Environment.SetEnvironmentVariable(variableName, excludedDirectory);
        try
        {
            var dataAccessLayer = new InMemoryDataAccessLayer();
            var excludesJson = JsonSerializer.Serialize(new[] { "%" + variableName + "%" });
            var context = await CreateExecutionContextAsync(
                dataAccessLayer,
                scanRoot,
                Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")),
                toolExcludesJson: excludesJson);

            var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));
            await tool.ExecuteAsync(context);

            Assert.NotNull(await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", keptRepoPath)));
            Assert.Null(await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", excludedRepoPath)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DefaultExcludes_SkipTempDirectory()
    {
        // Redirect %TEMP% to an isolated sub-directory of the fixture's root so both the
        // scan root and the "temp" directory are known, and the default %TEMP% exclude
        // can be validated deterministically without touching the real temp directory.
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "default-excludes-scan"));
        var fakeTempDirectory = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "default-excludes-temp"));
        Directory.CreateDirectory(scanRoot);
        Directory.CreateDirectory(fakeTempDirectory);
        var keptRepoPath = Path.GetFullPath(Path.Combine(scanRoot, "kept-repo"));
        var tempRepoPath = Path.GetFullPath(Path.Combine(fakeTempDirectory, "temp-repo"));
        InitializeGitRepository(keptRepoPath, "https://example.com/kept.git");
        InitializeGitRepository(tempRepoPath, "https://example.com/temp.git");

        var originalTemp = Environment.GetEnvironmentVariable("TEMP");
        var originalTmp = Environment.GetEnvironmentVariable("TMP");
        Environment.SetEnvironmentVariable("TEMP", fakeTempDirectory);
        Environment.SetEnvironmentVariable("TMP", fakeTempDirectory);
        try
        {
            var dataAccessLayer = new InMemoryDataAccessLayer();
            // Pass toolExcludesJson: null so the tool entity has no `excludes` property and
            // the in-code DefaultExcludes ([%TEMP%]) is applied.
            var context = await CreateExecutionContextAsync(
                dataAccessLayer,
                scanRoot,
                Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")),
                toolExcludesJson: null);

            // Also add the fake temp directory as a scan root so we would find its repo
            // if the default exclude were absent.
            var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot, fakeTempDirectory]));
            await tool.ExecuteAsync(context);

            Assert.NotNull(await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", keptRepoPath)));
            Assert.Null(await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", tempRepoPath)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", originalTemp);
            Environment.SetEnvironmentVariable("TMP", originalTmp);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RepositoryDirectlyAtExcludedPathRoot_NotDiscovered()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "exclude-at-root"));
        var excludedRepoPath = Path.GetFullPath(Path.Combine(scanRoot, "exact-repo"));
        InitializeGitRepository(excludedRepoPath, "https://example.com/at-root.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var excludesJson = JsonSerializer.Serialize(new[] { excludedRepoPath });
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")),
            toolExcludesJson: excludesJson);

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));
        await tool.ExecuteAsync(context);

        Assert.Null(await GetEntityByNameAsync(dataAccessLayer, new EntityName("git-worktrees", excludedRepoPath)));
    }

    private static void AddLinkedWorktree(string rootRepoPath, string worktreeName, string worktreePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        using var repository = new Repository(rootRepoPath);
        repository.Worktrees.Add(worktreeName, worktreePath, isLocked: false);
    }

    public void Dispose()
    {
        this.temporaryRoot.Dispose();
    }

    private static void InitializeGitRepository(
        string repositoryPath,
        string remoteUrl)
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

    private static async Task<WorkspaceToolExecutionContext> CreateExecutionContextAsync(
        IDataAccessLayer dataAccessLayer,
        string currentProfileRoot,
        string otherProfileRoot,
        string? toolExcludesJson = "[]")
    {
        var currentComputerEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("11111111-1111-1111-1111-111111111111"),
            """
            {
              "entity-id": "11111111-1111-1111-1111-111111111111",
              "entity-types": ["entity", "computer"],
              "names": [["computers", "hostname", "test-computer"]]
            }
            """,
            concurrencyTag: null);
        var currentUserEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("22222222-2222-2222-2222-222222222222"),
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity", "user"],
              "names": [["users", "username", "test-user"]]
            }
            """,
            concurrencyTag: null);
        var currentComputerUserProfileEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("33333333-3333-3333-3333-333333333333"),
            $$"""
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["entity", "user-computer-profile"],
              "names": [["computer-user-profiles", "users", "username", "test-user", "computers", "hostname", "test-computer"]],
              "computer-reference": ["computers", "hostname", "test-computer"],
              "user-reference": ["users", "username", "test-user"],
              "home-directory": "{{EscapeForJsonString(currentProfileRoot)}}"
            }
            """,
            concurrencyTag: null);
        var otherComputerUserProfileParticipant = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("44444444-4444-4444-4444-444444444444"),
            $$"""
            {
              "entity-id": "44444444-4444-4444-4444-444444444444",
              "entity-types": ["entity", "user-computer-profile"],
              "names": [["computer-user-profiles", "users", "username", "other-user", "computers", "hostname", "other-computer"]],
              "computer-reference": ["computers", "hostname", "other-computer"],
              "user-reference": ["users", "username", "other-user"],
              "home-directory": "{{EscapeForJsonString(otherProfileRoot)}}"
            }
            """,
            concurrencyTag: null);
        var currentFilesystemFolderParticipant = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("55555555-5555-5555-5555-555555555555"),
            $$"""
            {
              "entity-id": "55555555-5555-5555-5555-555555555555",
              "entity-types": ["entity", "filesystem-folder", "filesystem-path"],
              "names": [
                ["filesystem-folders", "current-profile-root"],
                ["computer-user-profiles", "users", "username", "test-user", "computers", "hostname", "test-computer"]
              ],
              "path": "{{EscapeForJsonString(currentProfileRoot)}}"
            }
            """,
            concurrencyTag: null);
        var otherFilesystemFolderParticipant = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("66666666-6666-6666-6666-666666666666"),
            $$"""
            {
              "entity-id": "66666666-6666-6666-6666-666666666666",
              "entity-types": ["entity", "filesystem-folder", "filesystem-path"],
              "names": [
                ["filesystem-folders", "other-profile-root"],
                ["computer-user-profiles", "users", "username", "other-user", "computers", "hostname", "other-computer"]
              ],
              "path": "{{EscapeForJsonString(otherProfileRoot)}}"
            }
            """,
            concurrencyTag: null);

        var toolExcludesProperty = toolExcludesJson is null
            ? string.Empty
            : $",\n              \"excludes\": {toolExcludesJson}";
        var toolEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("77777777-7777-7777-7777-777777777777"),
            $$"""
            {
              "entity-id": "77777777-7777-7777-7777-777777777777",
              "entity-types": ["entity", "tool"],
              "names": [["tools", "git-workspace-scan"]],
              "tool-type": "git-workspace-scan"{{toolExcludesProperty}}
            }
            """,
            concurrencyTag: null);

        return new WorkspaceToolExecutionContext
        {
            DataAccessLayer = dataAccessLayer,
            CancellationToken = CancellationToken.None,
            CurrentComputerEntity = currentComputerEntity,
            CurrentUserEntity = currentUserEntity,
            CurrentComputerUserProfileEntity = currentComputerUserProfileEntity,
            ToolRelationship = currentComputerUserProfileEntity,
            Participants =
            [
                currentComputerUserProfileEntity,
                otherComputerUserProfileParticipant,
                currentFilesystemFolderParticipant,
                otherFilesystemFolderParticipant,
            ],
            Tool = toolEntity,
            Schedule = currentComputerEntity,
        };
    }

    private static async Task<int> CountEntitiesByNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName)
    {
        // After migration to deterministic IDs, entities are keyed by path, not name.
        // Extract the path from the entity name and compute the deterministic ID.
        if (entityName.Components.Length >= 2 && entityName.Components[0] == "git-worktrees")
        {
            var path = entityName.Components[1];
            var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
            var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);
            var entity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
            return entity != null ? 1 : 0;
        }

        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = entityName,
                    },
                ],
            });

        return getResult.Batches.SelectMany(static batch => batch.Entities).Count();
    }

    private static async Task<EntitySnapshot?> GetEntityByNameAsync(
        IDataAccessLayer dataAccessLayer,
        EntityName entityName)
    {
        // After migration to deterministic IDs, entities are keyed by path, not name.
        // Extract the path from the entity name and compute the deterministic ID.
        if (entityName.Components.Length >= 2 && entityName.Components[0] == "git-worktrees")
        {
            var path = entityName.Components[1];
            var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
            var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);
            return await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        }

        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = entityName,
                    },
                ],
            });
        return getResult.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault();
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
                    Comment = new Markdown
                    {
                        Text = "Git workspace discovery test upsert.",
                    },
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

        var entityResult = Assert.Single(updateResult.EntityResults, entityResult => entityResult.RequestedEntityId == entityId);
        Assert.Empty(entityResult.Errors);
        return Assert.IsType<EntitySnapshot>(entityResult.CurrentEntity);
    }

    private static string EscapeForJsonString(
        string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    private static void TryDeleteDirectory(
        string directoryPath)
    {
        TempDirectory.ForceDelete(directoryPath);
    }

    private sealed class FixedLocalDriveRootProvider(
        IReadOnlyCollection<string> localDriveRoots) : ILocalDriveRootProvider
    {
        public IReadOnlyCollection<string> GetLocalDriveRoots()
        {
            return localDriveRoots;
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenGetGitMetadataFails_LogsDebug()
    {
        // A directory with a .git sub-directory but no valid git internals — LibGit2Sharp will throw
        // RepositoryNotFoundException when opened.
        var invalidRepoPath = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "invalid-repo"));
        Directory.CreateDirectory(Path.Combine(invalidRepoPath, ".git"));

        var logger = new TestLogger<GitWorkspaceScanTool>();
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            this.temporaryRootPath,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other-profile-root")));

        var tool = new GitWorkspaceScanTool(
            new FixedLocalDriveRootProvider([this.temporaryRootPath]),
            logger);

        await tool.ExecuteAsync(context);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task ExecuteAsync_BeforeEachScanRoot_LogsInformationWithPath()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "profile-root"));
        Directory.CreateDirectory(scanRoot);

        var logger = new TestLogger<GitWorkspaceScanTool>();
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other-profile-root")));

        var tool = new GitWorkspaceScanTool(
            new FixedLocalDriveRootProvider([scanRoot]),
            logger);

        await tool.ExecuteAsync(context);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains(scanRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_WhenRepositoryFound_LogsDebugWithRepoPath()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "drive-root-debug"));
        var repoPath = Path.GetFullPath(Path.Combine(scanRoot, "my-repo"));
        InitializeGitRepository(repoPath, "https://example.com/debug.git");

        var logger = new TestLogger<GitWorkspaceScanTool>();
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other-profile-root")));

        var tool = new GitWorkspaceScanTool(
            new FixedLocalDriveRootProvider([scanRoot]),
            logger);

        await tool.ExecuteAsync(context);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Debug
            && e.Message.Contains(repoPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_AfterEachScanRoot_LogsInformationSummaryWithCountAndPath()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "drive-root-summary"));
        var repoPath1 = Path.GetFullPath(Path.Combine(scanRoot, "repo-one"));
        var repoPath2 = Path.GetFullPath(Path.Combine(scanRoot, "repo-two"));
        InitializeGitRepository(repoPath1, "https://example.com/one.git");
        InitializeGitRepository(repoPath2, "https://example.com/two.git");

        var logger = new TestLogger<GitWorkspaceScanTool>();
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other-profile-root")));

        var tool = new GitWorkspaceScanTool(
            new FixedLocalDriveRootProvider([scanRoot]),
            logger);

        await tool.ExecuteAsync(context);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("2", StringComparison.Ordinal)
            && e.Message.Contains(scanRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rediscovery_PreservesDisplayName()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "rediscovery-display"));
        var repoPath = Path.GetFullPath(Path.Combine(scanRoot, "my-repo"));
        InitializeGitRepository(repoPath, "https://example.com/preserve-display.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));

        // First run: create entity
        await tool.ExecuteAsync(context);

        // Manually customize display-name
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);
        var entity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(entity);

        var customizedJson = $$"""
            {
              "entity-id": "{{entity.EntityId}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": {{JsonSerializer.Serialize(entity.Data?.GetProperty("names"))}},
              "display-name": {"default": "CustomDisplayName"},
              "path": "{{EscapeForJsonString(repoPath)}}",
              "git": {{JsonSerializer.Serialize(entity.Data?.GetProperty("git"))}}
            }
            """;
        await UpsertEntityAsync(dataAccessLayer, entity.EntityId, customizedJson, entity.ConcurrencyTag);

        // Second run: rediscover — should preserve custom display-name
        await tool.ExecuteAsync(context);

        var rediscoveredEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(rediscoveredEntity);
        var displayName = rediscoveredEntity.Data?.GetProperty("display-name").GetProperty("default").GetString();
        Assert.Equal("CustomDisplayName", displayName);
    }

    [Fact]
    public async Task Rediscovery_PreservesNames()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "rediscovery-names"));
        var repoPath = Path.GetFullPath(Path.Combine(scanRoot, "my-repo"));
        InitializeGitRepository(repoPath, "https://example.com/preserve-names.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));

        // First run: create entity
        await tool.ExecuteAsync(context);

        // Manually add custom name
        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);
        var entity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(entity);

        var customizedJson = $$"""
            {
              "entity-id": "{{entity.EntityId}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["custom-name", "preserved"], ["another", "name"]],
              "display-name": {"default": "repo"},
              "path": "{{EscapeForJsonString(repoPath)}}",
              "git": {{JsonSerializer.Serialize(entity.Data?.GetProperty("git"))}}
            }
            """;
        await UpsertEntityAsync(dataAccessLayer, entity.EntityId, customizedJson, entity.ConcurrencyTag);

        // Second run: rediscover — should preserve custom names array
        await tool.ExecuteAsync(context);

        var rediscoveredEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(rediscoveredEntity);
        var names = rediscoveredEntity.Data?.GetProperty("names").EnumerateArray().ToList();
        Assert.Equal(2, names?.Count);
        Assert.Equal("custom-name", names?[0].EnumerateArray().First().GetString());
        Assert.Equal("preserved", names?[0].EnumerateArray().Skip(1).First().GetString());
    }

    [Fact]
    public async Task Rediscovery_UpdatesGitSection()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "rediscovery-git"));
        var repoPath = Path.GetFullPath(Path.Combine(scanRoot, "my-repo"));
        InitializeGitRepository(repoPath, "https://example.com/update-git.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));

        // First run: create entity
        await tool.ExecuteAsync(context);

        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);
        var entity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(entity);
        var firstGitBranch = entity.Data?.GetProperty("git").GetProperty("branch").GetString();

        // Make a commit to change HEAD
        using (var repository = new Repository(repoPath))
        {
            File.WriteAllText(Path.Combine(repoPath, "file.txt"), "content");
            Commands.Stage(repository, "*");
            var signature = new Signature("test-user", "test@example.com", DateTimeOffset.UtcNow);
            repository.Commit("second commit", signature, signature);
        }

        // Second run: rediscover — should update git section
        await tool.ExecuteAsync(context);

        var rediscoveredEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(rediscoveredEntity);
        var secondGitBranch = rediscoveredEntity.Data?.GetProperty("git").GetProperty("branch").GetString();
        var secondHeadCommit = rediscoveredEntity.Data?.GetProperty("git").GetProperty("head-commit").GetString();

        Assert.Equal(firstGitBranch, secondGitBranch);
        Assert.NotNull(secondHeadCommit);
    }

    [Fact]
    public async Task Rediscovery_UsesDeterministicId_PreservesEntityId()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "rediscovery-id"));
        var repoPath = Path.GetFullPath(Path.Combine(scanRoot, "my-repo"));
        InitializeGitRepository(repoPath, "https://example.com/preserve-id.git");

        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other")));

        var tool = new GitWorkspaceScanTool(new FixedLocalDriveRootProvider([scanRoot]));

        // First run: create entity
        await tool.ExecuteAsync(context);

        var normalizedPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);
        var firstEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(firstEntity);

        // Second run: rediscover — entity ID should be identical
        await tool.ExecuteAsync(context);

        var secondEntity = await GetEntityByIdAsync(dataAccessLayer, deterministicId);
        Assert.NotNull(secondEntity);
        Assert.Equal(firstEntity.EntityId, secondEntity.EntityId);
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
        return getResult.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault();
    }

    [Fact]
    public async Task GitWorkspaceScanTool_WhenScanStarts_LogsScanningTopLevelDirectory()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "scan-starts"));
        Directory.CreateDirectory(scanRoot);

        var logger = new TestLogger<GitWorkspaceScanTool>();
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other-profile-root")));

        var tool = new GitWorkspaceScanTool(
            new FixedLocalDriveRootProvider([scanRoot]),
            logger);

        await tool.ExecuteAsync(context);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("Scanning top-level directory", StringComparison.Ordinal)
            && e.Message.Contains(scanRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GitWorkspaceScanTool_WhenScanCompletes_LogsRepositoryCountSummary()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "summary-scan"));
        var repoPath = Path.GetFullPath(Path.Combine(scanRoot, "summary-repo"));
        InitializeGitRepository(repoPath, "https://example.com/summary.git");

        var logger = new TestLogger<GitWorkspaceScanTool>();
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other-profile-root")));

        var tool = new GitWorkspaceScanTool(
            new FixedLocalDriveRootProvider([scanRoot]),
            logger);

        await tool.ExecuteAsync(context);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("Scanned", StringComparison.Ordinal)
            && e.Message.Contains("repositories", StringComparison.Ordinal)
            && e.Message.Contains("worktree", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GitWorkspaceScanTool_WhenScanCompletes_ReturnsResultSummaryContent()
    {
        var scanRoot = Path.GetFullPath(Path.Combine(this.temporaryRootPath, "result-scan"));
        var repoPath = Path.GetFullPath(Path.Combine(scanRoot, "result-repo"));
        InitializeGitRepository(repoPath, "https://example.com/result.git");

        var logger = new TestLogger<GitWorkspaceScanTool>();
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = await CreateExecutionContextAsync(
            dataAccessLayer,
            scanRoot,
            Path.GetFullPath(Path.Combine(this.temporaryRootPath, "other-profile-root")));

        var tool = new GitWorkspaceScanTool(
            new FixedLocalDriveRootProvider([scanRoot]),
            logger);

        var result = await tool.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ResultContent);
        Assert.Contains("Scanned", result.ResultContent, StringComparison.Ordinal);
        Assert.Contains("repositories", result.ResultContent, StringComparison.Ordinal);
    }

    [Fact]
    public void GitWorkspaceScanTool_ToolType_MatchesSeededDefaultEntity()
    {
        // Regression for #1161: the seeded default tool entity's tool-type must match the tool's ToolType,
        // otherwise ScheduledToolHost cannot resolve the tool and scheduled scans silently no-op.
        var tool = new GitWorkspaceScanTool();
        var assembly = typeof(SchemaPopulator).Assembly;
        const string resourceName = "Phantom.Workspaces.Data.JsonEntities.defaults.tools.git-workspace-scan-tool.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream!);
        var seededToolType = document.RootElement.GetProperty("tool-type").GetString();

        Assert.Equal(seededToolType, tool.ToolType);
    }
}

internal sealed class TestLogger<T> : ILogger<T>
{
    public sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        this.Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
    }
}
