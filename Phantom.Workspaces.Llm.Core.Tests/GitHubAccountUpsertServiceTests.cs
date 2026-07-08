using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class GitHubAccountUpsertServiceTests
{
    [Fact]
    public async Task UpsertForTokenAsync_ResolvesUsernameAndUpsertsEntity()
    {
        var dal = new FakeDataAccessLayer();
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
        var dal = new FakeDataAccessLayer();
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
        var dal = new FakeDataAccessLayer();
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
        var dal = new FakeDataAccessLayer();
        var identity = new FakeIdentityResolver(null);
        var service = new GitHubAccountUpsertService(dal, identity);

        await service.UpsertForTokenAsync("ghs_token", CancellationToken.None);

        Assert.Equal(0, dal.UpdateCallCount);
    }

    [Fact]
    public async Task UpsertForTokenAsync_UsernameResolutionThrows_DoesNotPropagateException()
    {
        var dal = new FakeDataAccessLayer();
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
              "provider": "https://github.com",
              "user-name": "octocat"
            }
            """);
        var dal = new FakeDataAccessLayer(existingEntity: MakeSnapshot(existingData.RootElement));
        var identity = new FakeIdentityResolver("octocat");
        var service = new GitHubAccountUpsertService(dal, identity);

        await service.UpsertForTokenAsync("ghs_token", CancellationToken.None);

        Assert.Equal(0, dal.UpdateCallCount);
    }

    [Fact]
    public async Task UpsertForTokenAsync_EntityNamed_FollowsSchemaConvention()
    {
        // Entity name should be ["users", "username", "<username>", "user-accounts", "github.com"]
        var dal = new FakeDataAccessLayer();
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

    private static EntitySnapshot MakeSnapshot(JsonElement data) =>
        new()
        {
            EntityId = new EntityId(),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            ConcurrencyTag = new ConcurrencyTag("ct1"),
            Data = data,
            Relationships = [],
        };

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

    private sealed class FakeDataAccessLayer(EntitySnapshot? existingEntity = null) : IDataAccessLayer
    {
        public int UpdateCallCount { get; private set; }
        public UpdateRequest? LastUpdateRequest { get; private set; }
        public GetRequest? LastGetRequest { get; private set; }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            this.LastGetRequest = request;
            var entities = existingEntity is not null ? (IReadOnlyCollection<EntitySnapshot>)[existingEntity] : [];
            return Task.FromResult(new GetResult
            {
                Batches =
                [
                    new TimestampedEntityBatch
                    {
                        Timestamp = null,
                        Entities = entities,
                    },
                ],
            });
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        {
            this.UpdateCallCount++;
            this.LastUpdateRequest = request;
            var change = Assert.Single(request.Changes);
            return Task.FromResult(new UpdateResult
            {
                EntityResults =
                [
                    new EntityUpdateResult
                    {
                        RequestedEntityId = change.EntityId!.Value,
                        ResultingEntityId = change.EntityId!.Value,
                        UpdateState = UpdateState.Added,
                        ConcurrencyMatchState = ConcurrencyMatchState.Matched,
                        Errors = [],
                    },
                ],
            });
        }

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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
