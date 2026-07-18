using System.Text.Json;
using LibGit2Sharp;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Offline.Tests;

/// <summary>
/// Verifies the offline DALs stamp entity/commit timestamps from an injected
/// <see cref="TimeProvider"/> rather than the wall clock, enabling deterministic assertions.
/// </summary>
public sealed class OfflineDataAccessLayerTimeProviderTests : IDisposable
{
    private readonly List<string> createdDirectories = new();

    private string NewDirectory(string prefix)
    {
        var path = TestPathFactory.CreateIsolatedDirectory(prefix);
        this.createdDirectories.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var directory in this.createdDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Git object file handles can still be releasing at test teardown.
            }
            catch (IOException)
            {
                // Best-effort cleanup; leftover temp directories are harmless.
            }
        }
    }

    private static UpdateRequest BuildUpsertRequest(EntityId entityId, string name)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": [["{{name}}"]]
            }
            """);

        return new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "time-provider test" },
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
        };
    }

    [Fact]
    public async Task FilesystemDataAccessLayer_Write_StampsModifiedTimeFromTimeProvider()
    {
        var instant = new DateTimeOffset(2024, 6, 7, 8, 9, 10, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(instant);
        var dal = new FilesystemDataAccessLayer(this.NewDirectory("fs-timeprovider"), timeProvider);
        var entityId = new EntityId();

        await dal.UpdateAsync(BuildUpsertRequest(entityId, "one"));

        var export = await dal.ExportAsync(new ExportRequest());
        Assert.Equal(instant, export.FinalSnapshotTime.DateTime);
    }

    [Fact]
    public async Task FilesystemDataAccessLayer_TwoWritesAtSameFakeInstant_ProduceDistinctTimestampsViaSequenceNumber()
    {
        var instant = new DateTimeOffset(2024, 6, 7, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(instant);
        var dal = new FilesystemDataAccessLayer(this.NewDirectory("fs-tie-break"), timeProvider);

        await dal.UpdateAsync(BuildUpsertRequest(new EntityId(), "first"));
        await dal.UpdateAsync(BuildUpsertRequest(new EntityId(), "second"));

        var export = await dal.ExportAsync(new ExportRequest());
        var changeTimes = export.ChangeBatches.Select(static batch => batch.ChangeTime).ToArray();

        Assert.Equal(2, changeTimes.Length);
        Assert.All(changeTimes, ct => Assert.Equal(instant, ct.DateTime));
        Assert.Equal(2, changeTimes.Select(static ct => ct.ChangeId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task InMemoryDataAccessLayer_Write_StampsTimestampFromTimeProvider()
    {
        var instant = new DateTimeOffset(2024, 6, 7, 11, 12, 13, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(instant);
        var dal = new InMemoryDataAccessLayer(embeddingsProvider: null, timeProvider: timeProvider);
        var entityId = new EntityId();

        await dal.UpdateAsync(BuildUpsertRequest(entityId, "one"));

        var export = await dal.ExportAsync(new ExportRequest());
        Assert.Equal(instant, export.FinalSnapshotTime.DateTime);
    }

    [Fact]
    public async Task GitDataAccessLayer_Commit_StampsSignatureWhenFromTimeProvider()
    {
        var instant = new DateTimeOffset(2024, 6, 7, 14, 15, 16, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(instant);
        var repositoryPath = this.NewDirectory("git-timeprovider");
        var dal = new GitDataAccessLayer(repositoryPath, timeProvider);

        var result = await dal.UpdateAsync(BuildUpsertRequest(new EntityId(), "one"));
        Assert.Equal(UpdateState.Added, Assert.Single(result.EntityResults).UpdateState);

        using var repository = new Repository(repositoryPath);
        var commit = repository.Head.Tip;
        Assert.Equal(instant, commit.Author.When);
        Assert.Equal(instant, commit.Committer.When);
    }
}
