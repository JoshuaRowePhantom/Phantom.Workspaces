using System.Diagnostics;
using System.Text.Json;
using LibGit2Sharp;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Data.Offline.Tests;

public sealed class FilesystemSnapshotPublicationTests
{
    [Fact(Timeout = 60000)]
    [Trait("Category", "SlowGit")]
    public async Task CloneAfterLastEntityDeletion_RetainsPublishedRevisionAndRejectsStaleTag()
    {
        using var source = TestPathFactory.CreateIsolatedTempDirectory("snapshot-revision-source");
        using var clone = TestPathFactory.CreateIsolatedTempDirectory("snapshot-revision-clone");
        var id = new EntityId("c5788404-0940-4b5c-950a-a8483c7c34ca");
        var store = new GitDataAccessLayer(source.Path);
        var original = Assert.Single((await store.UpdateAsync(Request(id, "old"))).EntityResults).ConcurrencyTag;
        await store.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Delete last entity" } },
            Changes = [new EntityChange { EntityId = id, ConcurrencyTag = original, EntityChangeMode = EntityChangeMode.Replace }],
        });
        Repository.Clone(source.Path, clone.Path);
        // Only the tracked snapshot was cloned; the private reset-resistant floor is local.
        Assert.False(File.Exists(System.IO.Path.Combine(clone.Path, ".git", "phantom-snapshot-revision")));
        var clonedStore = new GitDataAccessLayer(clone.Path);
        // Keep this a local snapshot transaction; no changes are pushed back to the source fixture.
        using (var repository = new Repository(clone.Path))
            repository.Network.Remotes.Remove("origin");
        var replacement = Assert.Single((await clonedStore.UpdateAsync(Request(id, "replacement"))).EntityResults);
        Assert.NotEqual(original, replacement.ConcurrencyTag);
        Assert.Equal(UpdateState.Failed, Assert.Single((await clonedStore.UpdateAsync(Request(id, "stale") with
        {
            Changes = [Request(id, "stale").Changes.Single() with { ConcurrencyTag = original }],
        })).EntityResults).UpdateState);
    }

    [Theory(Timeout = 60000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GitAdministrativeReference_ResolvesLinkedFloorOrFailsClosed(bool valid)
    {
        using var directory = TestPathFactory.CreateIsolatedTempDirectory("snapshot-linked-revision");
        var administrative = System.IO.Path.Combine(directory.Path, "administrative");
        Directory.CreateDirectory(administrative);
        File.WriteAllText(System.IO.Path.Combine(directory.Path, ".git"), valid ? "gitdir: administrative\n" : "not a gitdir reference");
        var store = new FilesystemDataAccessLayer(directory.Path);
        var id = new EntityId("7afc405c-0608-433e-932f-47ff625c6303");
        if (!valid)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(Request(id, "new")));
            return;
        }
        File.WriteAllText(System.IO.Path.Combine(administrative, "phantom-snapshot-revision"), "90");
        var result = Assert.Single((await store.UpdateAsync(Request(id, "new"))).EntityResults);
        Assert.Equal("91", result.ConcurrencyTag!.Value.Value);
        Assert.Equal("91", File.ReadAllText(System.IO.Path.Combine(directory.Path, ".snapshot-revision")));
        Assert.Equal("91", File.ReadAllText(System.IO.Path.Combine(administrative, "phantom-snapshot-revision")));
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SlowGit")]
    public async Task GitResetToEarlierSnapshot_ReopenedWriterNeverReissuesDiscardedRevision()
    {
        using var directory = TestPathFactory.CreateIsolatedTempDirectory("snapshot-git-reset-revision");
        var id = new EntityId("2da152a7-6d3e-454d-bf5d-6343e25ecb89");
        var store = new GitDataAccessLayer(directory.Path);
        var original = Assert.Single((await store.UpdateAsync(Request(id, "old"))).EntityResults).ConcurrencyTag;
        var later = Assert.Single((await store.UpdateAsync(Request(id, "discarded") with
        {
            Changes = [Request(id, "discarded").Changes.Single() with { ConcurrencyTag = original }],
        })).EntityResults).ConcurrencyTag;
        using (var repository = new Repository(directory.Path))
            repository.Reset(ResetMode.Hard, repository.Head.Tip.Parents.Single());
        var reopened = new GitDataAccessLayer(directory.Path);
        var fresh = Assert.Single((await reopened.UpdateAsync(Request(id, "replacement") with
        {
            Changes = [Request(id, "replacement").Changes.Single() with { ConcurrencyTag = original }],
        })).EntityResults);
        Assert.Equal(UpdateState.Updated, fresh.UpdateState);
        Assert.NotEqual(later, fresh.ConcurrencyTag);
        Assert.Equal(UpdateState.Failed, Assert.Single((await reopened.UpdateAsync(Request(id, "stale") with
        {
            Changes = [Request(id, "stale").Changes.Single() with { ConcurrencyTag = later }],
        })).EntityResults).UpdateState);
    }

    [Fact(Timeout = 60000)]
    public async Task IndependentRecreation_AfterLastEntityDeletion_RetainsRevisionAcrossProcesses()
    {
        using var directory = TestPathFactory.CreateIsolatedTempDirectory("snapshot-process-revision");
        var id = new EntityId("6ed76d49-175b-4430-8d15-09bd9be0f804");
        var store = new FilesystemDataAccessLayer(directory.Path);
        var original = Assert.Single((await store.UpdateAsync(Request(id, "old"))).EntityResults).ConcurrencyTag;
        Assert.Equal(UpdateState.Removed, Assert.Single((await store.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Delete last entity" } },
            Changes = [new EntityChange { EntityId = id, ConcurrencyTag = original, EntityChangeMode = EntityChangeMode.Replace }],
        })).EntityResults).UpdateState);
        using var child = StartWriter(directory.Path, id, "-", "replacement");
        try
        {
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync());
            await child.StandardInput.WriteLineAsync("UPDATE");
            Assert.Contains("Added", await child.StandardOutput.ReadLineAsync());
            await child.WaitForExitAsync();
            Assert.Equal(0, child.ExitCode);
            Assert.Equal(UpdateState.Failed, Assert.Single((await store.UpdateAsync(Request(id, "stale") with
            {
                Changes = [Request(id, "stale").Changes.Single() with { ConcurrencyTag = original }],
            })).EntityResults).UpdateState);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }

    [Theory(Timeout = 60000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeleteRecreate_AcrossInstancesAndGitReset_RejectsOriginalTag(bool git, bool reopen)
    {
        using var directory = TestPathFactory.CreateIsolatedTempDirectory("snapshot-revision");
        IDataAccessLayer store = git ? new GitDataAccessLayer(directory.Path) : new FilesystemDataAccessLayer(directory.Path);
        var id = new EntityId("95d7f0af-f7a6-42ea-bc4c-f0df767aaf06");
        var original = Assert.Single((await store.UpdateAsync(Request(id, "old"))).EntityResults).ConcurrencyTag;
        var deletion = await store.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Delete" } },
            Changes = [new EntityChange { EntityId = id, ConcurrencyTag = original, EntityChangeMode = EntityChangeMode.Replace }],
        });
        Assert.Equal(UpdateState.Removed, Assert.Single(deletion.EntityResults).UpdateState);
        if (reopen)
            store = git ? new GitDataAccessLayer(directory.Path) : new FilesystemDataAccessLayer(directory.Path);
        var replacement = Assert.Single((await store.UpdateAsync(Request(id, "replacement"))).EntityResults);
        Assert.Equal(UpdateState.Added, replacement.UpdateState);
        Assert.NotEqual(original, replacement.ConcurrencyTag);
        var stale = Request(id, "stale") with
        {
            Changes = [Request(id, "stale").Changes.Single() with { ConcurrencyTag = original }],
        };
        Assert.Equal(UpdateState.Failed, Assert.Single((await store.UpdateAsync(stale)).EntityResults).UpdateState);
        Assert.Equal("replacement", Assert.Single(Assert.Single((await store.GetAsync(new GetRequest
            { Entities = [new GetEntityRequest { EntityId = id }] })).Batches).Entities)
            .Data!.Value.GetProperty("display-name").GetString());
    }

    [Theory(Timeout = 60000)]
    [InlineData("get")]
    [InlineData("query")]
    [InlineData("export")]
    public async Task PublicReader_HeldBetweenDataAndMetadata_BlocksIndependentWriter(string operation)
    {
        using var directory = TestPathFactory.CreateIsolatedTempDirectory("snapshot-public-reader");
        var id = new EntityId("034c21d1-fb2b-4c72-85cd-61f3091ae57e");
        var store = new FilesystemDataAccessLayer(directory.Path);
        var oldTag = Assert.Single((await store.UpdateAsync(Request(id, "old"))).EntityResults).ConcurrencyTag!.Value;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEvent(false);
        store.SnapshotDataRead = () => { entered.TrySetResult(); release.WaitOne(); };
        using var child = StartWriter(directory.Path, id, oldTag.Value, "new", observeWait: true);
        try
        {
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync());
            var read = ReadAsync(store, id, operation);
            await entered.Task;
            await child.StandardInput.WriteLineAsync("UPDATE");
            Assert.Equal("WAITING", await child.StandardOutput.ReadLineAsync());
            Assert.False(read.IsCompleted);
            release.Set();
            var snapshot = await read;
            Assert.Equal(oldTag, snapshot.ConcurrencyTag);
            Assert.Equal("old", snapshot.Data!.Value.GetProperty("display-name").GetString());
            Assert.Contains("Updated", await child.StandardOutput.ReadLineAsync());
            await child.WaitForExitAsync();
            Assert.Equal(0, child.ExitCode);
            store.SnapshotDataRead = null;
            var updated = await ReadAsync(store, id, operation);
            Assert.NotEqual(oldTag, updated.ConcurrencyTag);
            Assert.Equal("new", updated.Data!.Value.GetProperty("display-name").GetString());
        }
        finally
        {
            release.Set();
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SlowGit")]
    public async Task GitWriters_HeldBeforeCommit_SerializeResetStageCommitWithoutLostOrForeignChanges()
    {
        using var directory = TestPathFactory.CreateIsolatedTempDirectory("snapshot-git-writers");
        var firstId = new EntityId("f14aa451-bcbb-4e16-8789-17fd7f84d467");
        var secondId = new EntityId("ce7cb8fc-3c83-4fe6-8eb9-888bb3169fb0");
        var store = new GitDataAccessLayer(directory.Path);
        var seed = await store.UpdateAsync(Request(firstId, "old-first") with
        {
            Changes = [Request(firstId, "old-first").Changes.Single(), Request(secondId, "old-second").Changes.Single()],
        });
        var firstTag = seed.EntityResults.Single(result => result.ResultingEntityId == firstId).ConcurrencyTag!.Value;
        var secondTag = seed.EntityResults.Single(result => result.ResultingEntityId == secondId).ConcurrencyTag!.Value;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEvent(false);
        store.BeforeCommit = () => { entered.SetResult(); release.WaitOne(); };
        using var child = StartWriter(directory.Path, secondId, secondTag.Value, "second", git: true, observeWait: true);
        Task<UpdateResult>? first = null;
        try
        {
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync());
            first = store.UpdateAsync(Request(firstId, "first") with
            {
                Changes = [Request(firstId, "first").Changes.Single() with { ConcurrencyTag = firstTag }],
            });
            await entered.Task;
            await child.StandardInput.WriteLineAsync("UPDATE");
            Assert.Equal("WAITING", await child.StandardOutput.ReadLineAsync());
            release.Set();
            Assert.Equal(UpdateState.Updated, Assert.Single((await first).EntityResults).UpdateState);
            Assert.Contains("Updated", await child.StandardOutput.ReadLineAsync());
            await child.WaitForExitAsync();
            Assert.Equal(0, child.ExitCode);
            using var repository = new LibGit2Sharp.Repository(directory.Path);
            var latest = repository.Head.Tip;
            var parent = Assert.Single(latest.Parents);
            var seedCommit = Assert.Single(parent.Parents);
            Assert.Empty(seedCommit.Parents);
            var commits = new[] { latest, parent, seedCommit };
            Assert.Equal("Snapshot fixture", commits[1].MessageShort);
            Assert.Equal("Process snapshot regression", commits[0].MessageShort);
            string Relative(EntityId entity) => System.IO.Path.GetRelativePath(directory.Path,
                System.IO.Path.Combine(FilesystemDataAccessLayer.GetEntityDirectory(directory.Path, entity), $"{entity}.json"))
                .Replace('\\', '/');
            string Name(LibGit2Sharp.Commit commit, EntityId entity)
            {
                using var content = new StreamReader(((LibGit2Sharp.Blob)commit[Relative(entity)].Target).GetContentStream());
                using var json = JsonDocument.Parse(content.ReadToEnd());
                return json.RootElement.GetProperty("display-name").GetString()!;
            }
            Assert.Equal("first", Name(commits[1], firstId));
            Assert.Equal("old-second", Name(commits[1], secondId));
            Assert.Equal("first", Name(commits[0], firstId));
            Assert.Equal("second", Name(commits[0], secondId));
            Assert.False(repository.RetrieveStatus().IsDirty);
        }
        finally
        {
            release.Set();
            if (first is not null) await first;
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }

    private static async Task<EntitySnapshot> ReadAsync(FilesystemDataAccessLayer store, EntityId id, string operation)
    {
        if (operation == "get")
            return Assert.Single(Assert.Single((await store.GetAsync(new GetRequest
            { Entities = [new GetEntityRequest { EntityId = id }] })).Batches).Entities);
        if (operation == "query")
        {
            var result = await store.QueryAsync(new QueryRequest
            {
                Clauses = [new TopLevelQueryClause { ClauseIdentifier = new QueryClauseIdentifier("all"),
                    Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["entity"]) } }],
            });
            return Assert.Single(Assert.Single(result.Batches).Entities);
        }
        return Assert.Single(Assert.Single((await store.ExportAsync(new ExportRequest())).ChangeBatches).Entities);
    }

    [Fact(Timeout = 60000)]
    public async Task IndependentWriter_WithOpenReader_PublishesWholeSnapshotAndPreservesOldRead()
    {
        using var directory = TestPathFactory.CreateIsolatedTempDirectory("snapshot-publication");
        var id = new EntityId("5db8865d-a9a7-42fa-95f5-19c858ba6483");
        var store = new FilesystemDataAccessLayer(directory.Path);
        var initial = await store.UpdateAsync(Request(id, "old"));
        var tag = Assert.Single(initial.EntityResults).ConcurrencyTag!.Value;
        var path = System.IO.Path.Combine(FilesystemDataAccessLayer.GetEntityDirectory(directory.Path, id), $"{id}.json");
        using var oldReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var child = StartWriter(directory.Path, id, tag.Value, "new");
        try
        {
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync());
            await child.StandardInput.WriteLineAsync("UPDATE");
            var result = await child.StandardOutput.ReadLineAsync();
            await child.WaitForExitAsync();
            Assert.True(child.ExitCode == 0, await child.StandardError.ReadToEndAsync());
            Assert.Contains("Updated", result);
            using var oldDocument = await JsonDocument.ParseAsync(oldReader);
            Assert.Equal("old", oldDocument.RootElement.GetProperty("display-name").GetString());
            var current = await store.ExportAsync(new ExportRequest());
            var snapshot = Assert.Single(Assert.Single(current.ChangeBatches).Entities);
            Assert.Equal("new", snapshot.Data!.Value.GetProperty("display-name").GetString());
            Assert.NotEqual(tag, snapshot.ConcurrencyTag);
            Assert.Empty(Directory.GetFiles(directory.Path, "*.staging", SearchOption.AllDirectories));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
    }

    [Fact(Timeout = 60000)]
    public async Task IndependentWriters_WithSameOriginalTag_OnlyOneCommits()
    {
        using var directory = TestPathFactory.CreateIsolatedTempDirectory("snapshot-writers");
        var id = new EntityId("11e915c8-8dc0-4b6f-9ada-a62f9939626b");
        var store = new FilesystemDataAccessLayer(directory.Path);
        var initial = await store.UpdateAsync(Request(id, "initial"));
        var tag = Assert.Single(initial.EntityResults).ConcurrencyTag!.Value;
        using var first = StartWriter(directory.Path, id, tag.Value, "first");
        using var second = StartWriter(directory.Path, id, tag.Value, "second");
        try
        {
            Assert.Equal("READY", await first.StandardOutput.ReadLineAsync());
            Assert.Equal("READY", await second.StandardOutput.ReadLineAsync());
            await Task.WhenAll(first.StandardInput.WriteLineAsync("UPDATE"), second.StandardInput.WriteLineAsync("UPDATE"));
            var results = await Task.WhenAll(first.StandardOutput.ReadLineAsync(), second.StandardOutput.ReadLineAsync());
            await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync());
            Assert.Equal(0, first.ExitCode);
            Assert.Equal(0, second.ExitCode);
            Assert.Single(results, result => result!.Contains("Updated", StringComparison.Ordinal));
            Assert.Single(results, result => result!.Contains("Failed", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var child in new[] { first, second })
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
        }
    }

    private static Process StartWriter(string path, EntityId id, string tag, string text, bool git = false, bool observeWait = false)
    {
        var start = new ProcessStartInfo
        {
            FileName = System.IO.Path.Combine(AppContext.BaseDirectory, "Phantom.Workspaces.Test.WindowsProcessProbe.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "--snapshot-update", path, id.ToString(), tag, text,
            git ? "git" : "filesystem", observeWait ? "observe-wait" : "silent" })
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static UpdateRequest Request(EntityId id, string text) => new()
    {
        UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Snapshot fixture" } },
        Changes =
        [
            new EntityChange
            {
                EntityId = id, EntityChangeMode = EntityChangeMode.Replace,
                Data = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["entity-id"] = id.ToString(), ["entity-types"] = new[] { "entity" }, ["display-name"] = text,
                }),
            },
        ],
    };
}
