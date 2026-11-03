using System.Diagnostics;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Data.Offline.Tests;

public sealed class FilesystemSnapshotPublicationTests
{
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

    private static Process StartWriter(string path, EntityId id, string tag, string text)
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
        foreach (var argument in new[] { "--snapshot-update", path, id.ToString(), tag, text })
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
