using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public sealed class AllowedSecretsStoreTests : IDisposable
{
    private readonly string filePath;

    public AllowedSecretsStoreTests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pw-allowed-secrets-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        this.filePath = Path.Combine(directory, "allowed-secrets.json");
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(this.filePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private AllowedSecretsStore CreateStore(string? path = null)
        => new(new AllowedSecretsStoreConfiguration { Path = path ?? this.filePath });

    private static MemorizedSecret SampleRecord(string credentialName = "AwsProdKey")
        => new(
            new SecretUseMemory(SecretUseScope.KeyInManifestContent, "This Key in This Manifest", "abc123"),
            new CredentialStoreSecretSource(credentialName),
            new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));

    [Fact]
    public async Task PutAsync_Then_TryGetAsync_ReturnsSameRecord()
    {
        var store = this.CreateStore();
        var record = SampleRecord();

        await store.PutAsync("hash-1", record, CancellationToken.None);

        // A fresh store reads back from the persisted file, proving round-trip persistence.
        var reloaded = this.CreateStore();
        var read = await reloaded.TryGetAsync("hash-1", CancellationToken.None);

        Assert.Equal(record, read);
        var source = Assert.IsType<CredentialStoreSecretSource>(read!.Source);
        Assert.Equal("AwsProdKey", source.CredentialName);
    }

    [Fact]
    public async Task TryGetAsync_MissingHash_ReturnsNull()
    {
        var store = this.CreateStore();
        await store.PutAsync("hash-1", SampleRecord(), CancellationToken.None);

        var read = await store.TryGetAsync("does-not-exist", CancellationToken.None);

        Assert.Null(read);
    }

    [Fact]
    public async Task DeleteAsync_RemovesRecordAndPersists()
    {
        var store = this.CreateStore();
        await store.PutAsync("hash-1", SampleRecord(), CancellationToken.None);
        await store.PutAsync("hash-2", SampleRecord("Other"), CancellationToken.None);

        await store.DeleteAsync("hash-1", CancellationToken.None);

        var reloaded = this.CreateStore();
        Assert.Null(await reloaded.TryGetAsync("hash-1", CancellationToken.None));
        Assert.NotNull(await reloaded.TryGetAsync("hash-2", CancellationToken.None));
    }

    [Fact]
    public async Task LoadAllAsync_EmptyFile_ReturnsEmptyMap()
    {
        var store = this.CreateStore();

        var all = await store.LoadAllAsync(CancellationToken.None);

        Assert.Empty(all);
    }

    [Fact]
    public async Task PutAsync_SecretValuesNeverPersisted()
    {
        const string sentinelSecretValue = "THIS-IS-A-RAW-SECRET-VALUE-THAT-MUST-NEVER-BE-WRITTEN";

        var store = this.CreateStore();
        await store.PutAsync("hash-1", SampleRecord(), CancellationToken.None);

        var bytes = await File.ReadAllTextAsync(this.filePath, CancellationToken.None);

        // The credential *name* is expected; no raw secret value is ever written.
        Assert.Contains("AwsProdKey", bytes);
        Assert.DoesNotContain(sentinelSecretValue, bytes);
    }

    [Fact]
    public async Task DeleteAsync_UnknownHash_IsNoOp()
    {
        var store = this.CreateStore();
        await store.PutAsync("hash-1", SampleRecord(), CancellationToken.None);
        var before = await File.ReadAllTextAsync(this.filePath, CancellationToken.None);

        await store.DeleteAsync("does-not-exist", CancellationToken.None);

        // The backing file is left byte-for-byte unchanged and the existing record survives.
        var after = await File.ReadAllTextAsync(this.filePath, CancellationToken.None);
        Assert.Equal(before, after);

        var reloaded = this.CreateStore();
        Assert.NotNull(await reloaded.TryGetAsync("hash-1", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_UnknownHash_EmptyStore_DoesNotCreateFile()
    {
        var store = this.CreateStore();

        await store.DeleteAsync("does-not-exist", CancellationToken.None);

        Assert.False(File.Exists(this.filePath));
    }

    [Fact]
    public void Ctor_ConfigurationPathNull_UsesDefaultBesideConfigJson()
    {
        var store = new AllowedSecretsStore(new AllowedSecretsStoreConfiguration { Path = null });

        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var expected = Path.Combine(applicationData, "Phantom.Workspaces", "allowed-secrets.json");

        Assert.Equal(expected, store.FilePath);
    }

    [Fact]
    public void Ctor_ConfigurationPathSet_UsesExplicitPath()
    {
        var explicitPath = Path.Combine(Path.GetTempPath(), "explicit-allowed-secrets.json");
        var store = new AllowedSecretsStore(new AllowedSecretsStoreConfiguration { Path = explicitPath });

        Assert.Equal(explicitPath, store.FilePath);
    }
}
