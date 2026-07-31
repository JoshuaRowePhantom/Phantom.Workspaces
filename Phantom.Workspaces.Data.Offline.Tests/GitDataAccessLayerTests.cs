using LibGit2Sharp;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Data.Tests;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Data.Offline.Tests;

public abstract class GitDataAccessLayerWithoutRemoteTestsBase : DataAccessLayerNonQueryWithoutHistoryTests, IDisposable
{
    private readonly TempDirectory repository;
    private readonly string repositoryPath;

    protected GitDataAccessLayerWithoutRemoteTestsBase(
        string repositoryPathPrefix)
    {
        this.repository = TestPathFactory.CreateIsolatedTempDirectory(repositoryPathPrefix);
        this.repositoryPath = this.repository.Path;
        Repository.Init(this.repositoryPath);
    }

    protected override IDataAccessLayer CreateDataAccessLayer()
    {
        return this.CreateGitDataAccessLayerForOperation();
    }

    protected abstract IDataAccessLayer CreateGitDataAccessLayerForOperation();

    protected string GetRepositoryPath()
    {
        return this.repositoryPath;
    }

    [Fact]
    public async Task UpdateAsync_DoesNotCommitUnintendedChanges()
    {
        using (var repository = new Repository(this.repositoryPath))
        {
            File.WriteAllText(Path.Combine(this.repositoryPath, "UNINTENDED.txt"), "original");
            Commands.Stage(repository, "UNINTENDED.txt");
            var signature = new Signature("Phantom Workspaces Tests", "noreply@phantom.workspaces", DateTimeOffset.UtcNow);
            repository.Commit("Baseline unintended file", signature, signature);
        }

        File.WriteAllText(Path.Combine(this.repositoryPath, "UNINTENDED.txt"), "modified");

        var dataAccessLayer = this.CreateGitDataAccessLayerForOperation();
        var entityId = new EntityId("5d34dc34-50be-45ef-b408-1648a761f67a");
        using var document = System.Text.Json.JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["tracked-entity"]]
            }
            """);

        var result = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create tracked-entity",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        Data = document.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });
        Assert.Equal(UpdateState.Added, Assert.Single(result.EntityResults).UpdateState);

        using var verificationRepository = new Repository(this.repositoryPath);
        var head = verificationRepository.Head.Tip;
        var parent = Assert.Single(head.Parents);
        Assert.Equal("Create tracked-entity", head.MessageShort);
        Assert.Equal(
            ((Blob)parent["UNINTENDED.txt"]!.Target).Id,
            ((Blob)head["UNINTENDED.txt"]!.Target).Id);
    }

    [Fact]
    public async Task UpdateAsync_WhenEntityFileWasManuallyEdited_AppliesOnlyRequestedChange()
    {
        var dataAccessLayer = this.CreateGitDataAccessLayerForOperation();
        var entityId = new EntityId("d86fb75f-ec39-4553-91f5-9f4948516bb0");

        using var initialDocument = System.Text.Json.JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["initial"]]
            }
            """);
        var createResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create entity for manual edit test",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        Data = initialDocument.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });
        var concurrencyTag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;

        var entityFilePath = Path.Combine(
            FilesystemDataAccessLayer.GetEntityDirectory(this.repositoryPath, entityId),
            $"{entityId}.json");
        File.WriteAllText(
            entityFilePath,
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["manual-edit"]],
              "manual-only": true
            }
            """);

        using var requestedDocument = System.Text.Json.JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["requested-change"]]
            }
            """);
        var updateResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Apply requested change after manual edit",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = concurrencyTag,
                        Data = requestedDocument.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });

        Assert.Equal(UpdateState.Updated, Assert.Single(updateResult.EntityResults).UpdateState);
        var persistedText = File.ReadAllText(entityFilePath);
        Assert.Contains("\"requested-change\"", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"manual-edit\"", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"manual-only\"", persistedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecreateDataAccessLayer_PreservesGitHistory()
    {
        var entityId = new EntityId("17abec8f-fef5-4162-ae7d-1ecf9454d34f");
        var dataAccessLayer = this.CreateGitDataAccessLayerForOperation();
        await VerifyHistoryPreservedAcrossRequestsAsync(dataAccessLayer, entityId, useConcurrencyTag: true);
    }

    private static async Task VerifyHistoryPreservedAcrossRequestsAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        bool useConcurrencyTag)
    {
        using var createDocument = System.Text.Json.JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["v1"]]
            }
            """);
        var createResult = await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create history test entity",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        Data = createDocument.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });
        var concurrencyTag = Assert.Single(createResult.EntityResults).ConcurrencyTag!.Value;

        using var updateDocument = System.Text.Json.JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["v2"]]
            }
            """);
        await RequireUpdateSucceedsAsync(
            dataAccessLayer,
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Update history test entity",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = useConcurrencyTag ? concurrencyTag : null,
                        Data = updateDocument.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });

        var historyResult = await dataAccessLayer.GetHistoryAsync(
            new GetHistoryRequest
            {
                EntityIds = new[] { entityId },
            });

        var history = Assert.Single(historyResult.History);
        Assert.Equal(entityId, history.EntityId);
        Assert.Equal(2, history.UpdateTimes.Count);
    }

    public void Dispose()
    {
        this.repository.Dispose();
    }

    [Fact]
    public void GitDataAccessLayer_AfterTestCompletes_LeavesNoTempDirectoryBehind()
    {
        // Sentinel: dispose a sibling fixture-style temp directory
        // (also seeded via Repository.Init to exercise the read-only
        // .git\objects files libgit2 writes) and confirm that Dispose
        // fully removes it from disk. Regressions in the exception-
        // safe cleanup path fail this assertion.
        string siblingPath;
        using (var sibling = TestPathFactory.CreateIsolatedTempDirectory("sentinel-cleanup"))
        {
            siblingPath = sibling.Path;
            Repository.Init(siblingPath);
            Assert.True(Directory.Exists(siblingPath));
        }

        Assert.False(Directory.Exists(siblingPath));
    }
}

[Trait("Category", "SlowGit")]
public sealed class GitDataAccessLayerWithoutRemoteTests : GitDataAccessLayerWithoutRemoteTestsBase
{
    public GitDataAccessLayerWithoutRemoteTests()
        : base("git-without-remote")
    {
    }

    protected override IDataAccessLayer CreateGitDataAccessLayerForOperation()
    {
        return new GitDataAccessLayer(this.GetRepositoryPath());
    }

    [Fact]
    public void InitializeLocalRepository_WhenRepositoryAlreadyExists_ReturnsFalse()
    {
        var dataAccessLayer = new GitDataAccessLayer(this.GetRepositoryPath());

        var initialized = dataAccessLayer.InitializeLocalRepository();

        Assert.False(initialized);
    }
}

[Trait("Category", "SlowGit")]
public sealed class GitDataAccessLayerWithoutRemotePerInvocationTests : GitDataAccessLayerWithoutRemoteTestsBase
{
    public GitDataAccessLayerWithoutRemotePerInvocationTests()
        : base("git-without-remote-per-invocation")
    {
    }

    protected override IDataAccessLayer CreateGitDataAccessLayerForOperation()
    {
        return new PerInvocationDataAccessLayer(() => new GitDataAccessLayer(this.GetRepositoryPath()));
    }
}

[Trait("Category", "SlowGit")]
public sealed class GitDataAccessLayerInitializationTests : IDisposable
{
    private readonly TempDirectory repository = TestPathFactory.CreateIsolatedTempDirectory("git-init");

    private string repositoryPath => this.repository.Path;

    [Fact]
    public void Constructor_WhenDirectoryIsNotRepository_InitializesRepository()
    {
        var dataAccessLayer = new GitDataAccessLayer(this.repositoryPath);

        var initialized = dataAccessLayer.InitializeLocalRepository();

        Assert.False(initialized);
        Assert.True(Repository.IsValid(this.repositoryPath));
    }

    public void Dispose()
    {
        this.repository.Dispose();
    }
}

public abstract class GitDataAccessLayerWithRemoteTestsBase : DataAccessLayerNonQueryWithoutHistoryTests, IDisposable
{
    private readonly TempDirectory remoteRepository = TestPathFactory.CreateIsolatedTempDirectory("git-remote-bare");
    private readonly TempDirectory localRepository = TestPathFactory.CreateIsolatedTempDirectory("git-with-remote");

    private string remoteRepositoryPath => this.remoteRepository.Path;
    private string localRepositoryPath => this.localRepository.Path;

    protected GitDataAccessLayerWithRemoteTestsBase()
    {
        Repository.Init(this.remoteRepositoryPath, isBare: true);
        Repository.Init(this.localRepositoryPath);

        using var repository = new Repository(this.localRepositoryPath);
        repository.Network.Remotes.Add("origin", this.remoteRepositoryPath);

        File.WriteAllText(Path.Combine(this.localRepositoryPath, "README.txt"), "init");
        Commands.Stage(repository, "README.txt");
        var signature = new Signature("Phantom Workspaces Tests", "noreply@phantom.workspaces", DateTimeOffset.UtcNow);
        repository.Commit("Initialize test repository", signature, signature);

        repository.Branches.Update(
            repository.Head,
            branch =>
            {
                branch.Remote = "origin";
                branch.UpstreamBranch = $"refs/heads/{repository.Head.FriendlyName}";
            });
        repository.Network.Push(repository.Head, new PushOptions());
    }

    protected override IDataAccessLayer CreateDataAccessLayer()
    {
        return this.CreateGitDataAccessLayerForOperation();
    }

    protected abstract IDataAccessLayer CreateGitDataAccessLayerForOperation();

    [Fact]
    public async Task UpdateAsync_WithRemote_PushesCommitToRemoteRepository()
    {
        var dataAccessLayer = this.CreateGitDataAccessLayerForOperation();
        var entityId = new EntityId("13ee2034-e44c-40e3-9b1f-fb6e755e8892");

        using var document = System.Text.Json.JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["remote-check"]]
            }
            """);

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create remote-check entity",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        Data = document.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });

        Assert.Equal(UpdateState.Added, Assert.Single(updateResult.EntityResults).UpdateState);

        using var remoteRepository = new Repository(this.remoteRepositoryPath);
        Assert.Contains(
            remoteRepository.Commits,
            commit => string.Equals(commit.MessageShort, "Create remote-check entity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateAsync_WithRemoteDivergence_FetchResetRetrySucceeds()
    {
        CreateDivergingRemoteCommit(this.remoteRepositoryPath);
        using (var localRepository = new Repository(this.localRepositoryPath))
        {
            Commands.Fetch(
                localRepository,
                "origin",
                Array.Empty<string>(),
                new FetchOptions(),
                null);
        }

        var dataAccessLayer = this.CreateGitDataAccessLayerForOperation();
        var entityId = new EntityId("5237f2f9-cae6-4cb3-bdb0-6724b2646e50");
        using var document = System.Text.Json.JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["remote-divergence"]]
            }
            """);

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Create remote-divergence entity",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        Data = document.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            });

        Assert.Equal(UpdateState.Added, Assert.Single(updateResult.EntityResults).UpdateState);

        using var remoteRepository = new Repository(this.remoteRepositoryPath);
        Assert.Contains(
            remoteRepository.Commits,
            commit => string.Equals(commit.MessageShort, "Create remote-divergence entity", StringComparison.Ordinal));
        Assert.Contains(
            remoteRepository.Commits,
            commit => string.Equals(commit.MessageShort, "Diverging remote commit", StringComparison.Ordinal));
    }

    protected string GetLocalRepositoryPath()
    {
        return this.localRepositoryPath;
    }

    public void Dispose()
    {
        this.localRepository.Dispose();
        this.remoteRepository.Dispose();
    }

    private static void CreateDivergingRemoteCommit(
        string remoteRepositoryPath)
    {
        using var divergingClone = TestPathFactory.CreateIsolatedTempDirectory("git-diverging-clone");
        var divergingClonePath = divergingClone.Path;

        Repository.Clone(remoteRepositoryPath, divergingClonePath);
        using var divergingRepository = new Repository(divergingClonePath);

        File.WriteAllText(Path.Combine(divergingClonePath, "DIVERGING.txt"), "diverging");
        Commands.Stage(divergingRepository, "DIVERGING.txt");
        var signature = new Signature("Phantom Workspaces Tests", "noreply@phantom.workspaces", DateTimeOffset.UtcNow);
        divergingRepository.Commit("Diverging remote commit", signature, signature);
        divergingRepository.Network.Push(divergingRepository.Head, new PushOptions());
    }

    [Trait("Category", "SlowGit")]
    public sealed class GitDataAccessLayerWithRemoteTests : GitDataAccessLayerWithRemoteTestsBase
    {
        protected override IDataAccessLayer CreateGitDataAccessLayerForOperation()
        {
            return new GitDataAccessLayer(this.GetLocalRepositoryPath());
        }
    }

    [Trait("Category", "SlowGit")]
    public sealed class GitDataAccessLayerWithRemotePerInvocationTests : GitDataAccessLayerWithRemoteTestsBase
    {
        protected override IDataAccessLayer CreateGitDataAccessLayerForOperation()
        {
            return new PerInvocationDataAccessLayer(() => new GitDataAccessLayer(this.GetLocalRepositoryPath()));
        }
    }
}
