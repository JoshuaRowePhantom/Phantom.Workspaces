using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class GitHubAccountUpsertServiceTests
{
    [Fact]
    public async Task UpsertForTokenAsync_ResolvesUsernameAndUpsertsEntity()
    {
        var dal = await CreateRecordingDataAccessLayerAsync();
        var identity = new FakeIdentityResolver("octocat");
        var service = new GitHubAccountUpsertService(dal, identity);

        await service.UpsertForTokenAsync("ghs_token", CancellationToken.None);

        Assert.Equal(1, dal.UpdateCallCount);
        var change = Assert.Single(dal.LastUpdateRequest!.Changes);
        var data = change.Data!.Value;
        Assert.Equal("https://github.com", data.GetProperty("provider").GetString());
        Assert.Equal("octocat", data.GetProperty("user-name").GetString());
    }

    [Fact]
    public async Task UpsertForTokenAsync_SameTokenTwice_OnlyUpsertsOnce()
    {
        var dal = await CreateRecordingDataAccessLayerAsync();
        var identity = new FakeIdentityResolver("octocat");
        var service = new GitHubAccountUpsertService(dal, identity);
        var ct = CancellationToken.None;

        await service.UpsertForTokenAsync("ghs_token", ct);
        await service.UpsertForTokenAsync("ghs_token", ct);

        Assert.Equal(1, dal.UpdateCallCount);
    }

    [Fact]
    public async Task UpsertForTokenAsync_DifferentTokens_UpsertsBoth()
    {
        var dal = await CreateRecordingDataAccessLayerAsync();
        var callIndex = 0;
        var usernames = new[] { "alice", "bob" };
        var identity = new DelegatingIdentityResolver(token => Task.FromResult<string?>(usernames[callIndex++]));
        var service = new GitHubAccountUpsertService(dal, identity);
        var ct = CancellationToken.None;

        await service.UpsertForTokenAsync("token-a", ct);
        await service.UpsertForTokenAsync("token-b", ct);

        Assert.Equal(2, dal.UpdateCallCount);
    }

    [Fact]
    public async Task UpsertForTokenAsync_UsernameResolutionReturnsNull_DoesNotCallDal()
    {
        var dal = await CreateRecordingDataAccessLayerAsync();
        var identity = new FakeIdentityResolver(null);
        var service = new GitHubAccountUpsertService(dal, identity);

        await service.UpsertForTokenAsync("ghs_token", CancellationToken.None);

        Assert.Equal(0, dal.UpdateCallCount);
    }

    [Fact]
    public async Task UpsertForTokenAsync_UsernameResolutionThrows_DoesNotPropagateException()
    {
        var dal = await CreateRecordingDataAccessLayerAsync();
        var identity = new ThrowingIdentityResolver();
        var service = new GitHubAccountUpsertService(dal, identity);

        // Must not throw.
        await service.UpsertForTokenAsync("ghs_token", CancellationToken.None);

        Assert.Equal(0, dal.UpdateCallCount);
    }

    [Fact]
    public async Task UpsertForTokenAsync_DalThrows_DoesNotPropagateException()
    {
        var dal = new ThrowingDataAccessLayer();
        var identity = new FakeIdentityResolver("octocat");
        var service = new GitHubAccountUpsertService(dal, identity);

        // Must not throw.
        await service.UpsertForTokenAsync("ghs_token", CancellationToken.None);
    }

    [Fact]
    public async Task UpsertForTokenAsync_ExistingEntityWithCorrectData_DoesNotUpsert()
    {
        // Entity already exists with the correct provider + user-name: no update expected.
        var existingData = JsonDocument.Parse("""
            {
              "entity-id": "10000000-0000-4000-8000-000000000009",
              "entity-types": ["entity", "user-account"],
              "names": [["users", "username", "octocat", "user-accounts", "github.com"]],
              "provider": "https://github.com",
              "user-name": "octocat"
            }
            """);
        var dal = await CreateRecordingDataAccessLayerAsync(existingData.RootElement);
        var identity = new FakeIdentityResolver("octocat");
        var service = new GitHubAccountUpsertService(dal, identity);

        await service.UpsertForTokenAsync("ghs_token", CancellationToken.None);

        Assert.Equal(0, dal.UpdateCallCount);
    }

    [Fact]
    public async Task UpsertForTokenAsync_EntityNamed_FollowsSchemaConvention()
    {
        // Entity name should be ["users", "username", "<username>", "user-accounts", "github.com"]
        var dal = await CreateRecordingDataAccessLayerAsync();
        var identity = new FakeIdentityResolver("jrowe");
        var service = new GitHubAccountUpsertService(dal, identity);

        await service.UpsertForTokenAsync("ghs_token", CancellationToken.None);

        var getRequest = dal.LastGetRequest;
        Assert.NotNull(getRequest);
        var getEntity = Assert.Single(getRequest.Entities);
        Assert.NotNull(getEntity.EntityName);
        Assert.Equal(["users", "username", "jrowe", "user-accounts", "github.com"], getEntity.EntityName!.Value.Components);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task<RecordingDataAccessLayer> CreateRecordingDataAccessLayerAsync(
        JsonElement? existingEntity = null)
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        if (existingEntity is { } entity)
        {
            await fixture.SeedValidEntityAsync(entity);
        }

        return new RecordingDataAccessLayer(fixture.DataAccessLayer);
    }

    private sealed class FakeIdentityResolver(string? username) : IGitHubIdentityResolver
    {
        public Task<string?> GetUsernameAsync(string token, CancellationToken cancellationToken = default)
            => Task.FromResult(username);
    }

    private sealed class DelegatingIdentityResolver(Func<string, Task<string?>> resolve) : IGitHubIdentityResolver
    {
        public Task<string?> GetUsernameAsync(string token, CancellationToken cancellationToken = default)
            => resolve(token);
    }

    private sealed class ThrowingIdentityResolver : IGitHubIdentityResolver
    {
        public Task<string?> GetUsernameAsync(string token, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Identity resolution failed.");
    }

    private sealed class RecordingDataAccessLayer(IDataAccessLayer inner) : IDataAccessLayer
    {
        public int UpdateCallCount { get; private set; }
        public UpdateRequest? LastUpdateRequest { get; private set; }
        public GetRequest? LastGetRequest { get; private set; }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            this.LastGetRequest = request;
            return inner.GetAsync(request, cancellationToken);
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        {
            this.UpdateCallCount++;
            this.LastUpdateRequest = request;
            return inner.UpdateAsync(request, cancellationToken);
        }

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => inner.QueryAsync(request, cancellationToken);

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => inner.GetHistoryAsync(request, cancellationToken);

#pragma warning disable CS0618 // Type or member is obsolete
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => inner.ExportAsync(request, cancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => inner.GetChangedEntitiesAsync(request, cancellationToken);
    }

    private sealed class ThrowingDataAccessLayer : IDataAccessLayer
    {
        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GetResult
            {
                Batches = [new TimestampedEntityBatch { Timestamp = null, Entities = [] }],
            });

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("DAL write failed.");

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
