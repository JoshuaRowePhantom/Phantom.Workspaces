using System.Security;
using ModelContextProtocol.Authentication;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Mcp;

namespace Phantom.Workspaces.Tests.Mcp;

public sealed class McpOAuthCacheAdministrationTests
{
    [Fact]
    public async Task List_IsMetadataOnly_AndObservesLateRegistrationWithStableOpaqueIdentifiers()
    {
        await using var fixture = new AdministrationFixture();
        Assert.Empty(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        var configuration = fixture.Register('a');
        Assert.Empty(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        var generation = await fixture.SeedAsync(configuration);

        var first = Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        var repeated = Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(first.Identifier, repeated.Identifier);
        Assert.NotEqual(generation.Value, first.Identifier.Value);
        Assert.Equal(generation, first.Generation);
        Assert.Equal(configuration.Source, first.SourceIdentifier);
        Assert.Equal(CredentialProviderKind.McpOAuth, first.Provider);
        Assert.Equal(CredentialKind.AccessToken, first.Kind);
        Assert.NotNull(first.ExpiresOn);
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Store.Enumerations);
        Assert.Equal(0, fixture.Browser.Opens);
        Assert.DoesNotContain("private", first.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False((object)fixture.Executor is IProviderGrantAdministration);
    }

    [Fact]
    public async Task List_ColdPersistedMaterialRemainsUnknownWithoutAValueRead()
    {
        await using var fixture = new AdministrationFixture();
        fixture.Register('a');
        using var material = new SecureString();
        foreach (var character in "private-persisted-entry-that-must-not-be-read")
            material.AppendChar(character);
        material.MakeReadOnly();
        await fixture.Store.WriteAsync("mcp-oauth:v2:private-a", material, TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Store.Enumerations);
        Assert.Equal(0, fixture.Store.Deletes);
    }

    [Fact]
    public async Task Identifiers_AreUniqueAcrossConfigurationsEvenWithEqualBackendGenerationIds()
    {
        await using var fixture = new AdministrationFixture();
        var first = fixture.Register('a');
        var second = fixture.Register('b');
        var generation = new CredentialMaterialGenerationIdentifier { Value = Guid.NewGuid() };
        await fixture.SeedAsync(first, generation);
        await fixture.SeedAsync(second, generation);
        var descriptors = await fixture.Executor.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, descriptors.Count);
        Assert.NotEqual(descriptors[0].Identifier, descriptors[1].Identifier);

        await fixture.Executor.DeleteAsync(descriptors.Single(row => row.SourceIdentifier == first.Source).Identifier,
            TestContext.Current.CancellationToken);
        Assert.Null(await ((ICredentialGenerationCache)first.Cache).InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.Equal(generation, (await ((ICredentialGenerationCache)second.Cache)
            .InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation);
        Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Store.Deletes);
    }

    [Fact]
    public async Task Delete_OldIdentifierNeverResolvesToReplacement_AndMissingIsNoLongerCurrent()
    {
        await using var fixture = new AdministrationFixture();
        var configuration = fixture.Register('a');
        await fixture.SeedAsync(configuration);
        var old = Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        var replacementGeneration = await fixture.SeedAsync(configuration);
        var replacement = Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(old.Identifier, replacement.Identifier);

        var stale = await Assert.ThrowsAsync<CredentialAdministrationException>(
            () => fixture.Executor.DeleteAsync(old.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, stale.Failure);
        Assert.Equal(0, fixture.Store.Deletes);
        Assert.Equal(replacementGeneration, (await ((ICredentialGenerationCache)configuration.Cache)
            .InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation);
        await fixture.Executor.DeleteAsync(replacement.Identifier, TestContext.Current.CancellationToken);
        var missing = await Assert.ThrowsAsync<CredentialAdministrationException>(
            () => fixture.Executor.DeleteAsync(replacement.Identifier, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.NoLongerCurrent, missing.Failure);
        Assert.Equal(1, fixture.Store.Deletes);
    }

    [Fact]
    public async Task Delete_UnknownIdentifierIsUnavailableWithoutBackendAccess()
    {
        await using var fixture = new AdministrationFixture();
        var error = await Assert.ThrowsAsync<CredentialAdministrationException>(() => fixture.Executor.DeleteAsync(
            new() { Value = Guid.NewGuid() }, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.Unavailable, error.Failure);
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Store.Deletes);
    }

    [Fact]
    public async Task CancelledOperations_DoNotInspectOrDelete()
    {
        await using var fixture = new AdministrationFixture();
        var configuration = fixture.Register('a');
        var generation = await fixture.SeedAsync(configuration);
        var row = Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Executor.ListAsync(cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Executor.DeleteAsync(row.Identifier, cancellation.Token).AsTask());
        Assert.Equal(generation, (await ((ICredentialGenerationCache)configuration.Cache)
            .InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation);
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Equal(0, fixture.Store.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_SanitizesExpectedBackendErrors_ButDoesNotSwallowUnknownBugs(bool unknownBug)
    {
        await using var fixture = new AdministrationFixture();
        var configuration = fixture.Register('a');
        await fixture.SeedAsync(configuration);
        var row = Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        fixture.Store.ReadFailure = unknownBug
            ? new InvalidOperationException("private-backend-details")
            : new IOException("private-backend-details");
        if (unknownBug)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Executor.DeleteAsync(row.Identifier, TestContext.Current.CancellationToken).AsTask());
            Assert.Same(fixture.Store.ReadFailure, failure);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<CredentialAdministrationException>(
                () => fixture.Executor.DeleteAsync(row.Identifier, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(CredentialAdministrationFailure.Failed, failure.Failure);
            Assert.DoesNotContain("private", failure.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Null(failure.InnerException);
        }
        Assert.Equal(0, fixture.Store.Deletes);
    }

    [Fact]
    public async Task Shutdown_CancelsAndJoinsAdmittedDelete_WithoutDisposingBackend()
    {
        await using var fixture = new AdministrationFixture();
        var configuration = fixture.Register('a');
        var generation = await fixture.SeedAsync(configuration);
        var row = Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        fixture.Store.BlockRead = true;
        var deleting = fixture.Executor.DeleteAsync(row.Identifier, CancellationToken.None).AsTask();
        await fixture.Store.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var shutdown = fixture.Executor.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        fixture.Store.ContinueRead.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deleting);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var unavailable = await Assert.ThrowsAsync<CredentialAdministrationException>(
            () => fixture.Executor.ListAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(CredentialAdministrationFailure.Unavailable, unavailable.Failure);
        Assert.Equal(0, fixture.Store.Disposals);
        Assert.Equal(0, fixture.Store.Deletes);
        Assert.Equal(generation, (await ((ICredentialGenerationCache)configuration.Cache)
            .InspectGenerationAsync(TestContext.Current.CancellationToken))!.Generation);
    }

    [Fact]
    public async Task RegisteredExecutorFacet_DeleteInvalidatesRealBrokerLeaseWithoutChangingReleaseRows()
    {
        await using var fixture = new McpOAuthCredentialAccessExecutorTests.Fixture();
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var result = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await acquisition.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        var rows = fixture.Registry.GetSnapshot().ToArray();
        var releaseEvents = fixture.Events.Count(entry => entry == "release");
        var reads = fixture.Store.Reads;
        ICredentialCacheAdministration facet = fixture.Executor;
        var row = Assert.Single(await facet.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(reads, fixture.Store.Reads);
        await facet.DeleteAsync(row.Identifier, TestContext.Current.CancellationToken);
        Assert.Equal(rows, fixture.Registry.GetSnapshot().ToArray());
        Assert.Equal(releaseEvents, fixture.Events.Count(entry => entry == "release"));
        var invoked = false;
        var use = await result.Use.UseAsync(_ =>
        {
            invoked = true;
            return ValueTask.FromResult(1);
        }, TestContext.Current.CancellationToken);
        Assert.IsType<CredentialUseReacquisitionRequired<int>>(use);
        Assert.False(invoked);
        var afterUse = fixture.Registry.GetSnapshot();
        Assert.Equal(rows[0], afterUse.Single(snapshot => snapshot.Identifier == rows[0].Identifier));
        Assert.Equal(UserAuthorizationState.Pending,
            Assert.Single(afterUse, snapshot => snapshot.Identifier != rows[0].Identifier).State);
        Assert.Equal(releaseEvents, fixture.Events.Count(entry => entry == "release"));
        Assert.Equal(1, fixture.Browser.OpenCount);
    }

    private sealed class AdministrationFixture : IAsyncDisposable
    {
        internal AdministrativeStore Store { get; } = new();
        internal NoBrowser Browser { get; } = new();
        internal McpOAuthCredentialRequestCatalog Catalog { get; } = new();
        internal CredentialAccessExecutorRegistry Executors { get; } = new();
        internal McpOAuthCredentialAccessExecutor Executor { get; }

        internal AdministrationFixture() =>
            this.Executor = McpOAuthComposition.RegisterExecutor(this.Executors, this.Catalog, this.Browser);

        internal McpOAuthProviderConfiguration Register(char partition)
        {
            var configuration = new McpOAuthProviderConfiguration
            {
                Identifier = new() { Digest = new string(partition, 64) },
                Endpoint = new Uri($"https://private-{partition}.test/mcp"),
                ClientAuthentication = new PublicOAuthClient { Client = new() { Value = "private-client" } },
                Scopes = [],
                Source = new() { Digest = new string(partition, 64) },
                Cache = new CredentialManagerTokenCache(this.Store, "mcp-oauth:", $"private-{partition}"),
            };
            this.Catalog.RegisterConfiguration(configuration);
            return configuration;
        }

        internal async Task<CredentialMaterialGenerationIdentifier> SeedAsync(
            McpOAuthProviderConfiguration configuration, CredentialMaterialGenerationIdentifier? generation = null)
        {
            generation ??= new() { Value = Guid.NewGuid() };
            await configuration.Cache.ReserveGenerationAsync(generation, TestContext.Current.CancellationToken);
            await configuration.Cache.StoreGenerationAsync(new TokenContainer
            {
                AccessToken = "private-material",
                TokenType = "Bearer",
                ObtainedAt = DateTimeOffset.UtcNow,
                ExpiresIn = 3600,
            }, generation, TestContext.Current.CancellationToken);
            return generation;
        }

        public async ValueTask DisposeAsync()
        {
            await this.Executors.DisposeAsync();
            this.Store.Dispose();
        }
    }

    private sealed class NoBrowser : ISystemBrowserLauncher
    {
        internal int Opens { get; private set; }
        public void Open(Uri uri) => this.Opens++;
    }

    private sealed class AdministrativeStore : IPlatformSecretStore, IDisposable
    {
        private readonly Dictionary<string, SecureString> values = [];
        internal int Reads { get; private set; }
        internal int Deletes { get; private set; }
        internal int Enumerations { get; private set; }
        internal int Disposals { get; private set; }
        internal bool BlockRead { get; set; }
        internal Exception? ReadFailure { get; set; }
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SecureString?> ReadAsync(string name, CancellationToken cancellationToken)
        {
            this.Reads++;
            if (this.BlockRead)
            {
                this.ReadEntered.TrySetResult();
                await this.ContinueRead.Task;
            }
            if (this.ReadFailure is { } failure) { throw failure; }
            return this.values.TryGetValue(name, out var value) ? value.Copy() : null;
        }
        public Task WriteAsync(string name, SecureString value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.values.Remove(name, out var old)) { old.Dispose(); }
            this.values.Add(name, value.Copy());
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Deletes++;
            if (this.values.Remove(name, out var value)) { value.Dispose(); }
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken cancellationToken)
        {
            this.Enumerations++;
            throw new InvalidOperationException("Enumeration is not metadata inspection.");
        }
        public void Dispose()
        {
            this.Disposals++;
            foreach (var value in this.values.Values) { value.Dispose(); }
            this.values.Clear();
        }
    }
}
