using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Llm.Remote;
using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Tests;

public sealed class DataAccessAgentSessionRuntimeHostFactoryTests
{
    private const string Owner = "11111111-1111-1111-1111-111111111111";
    private const string NewOwner = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task LoadIntentAsync_PersistedEntity_HydratesRealRuntimeContext()
    {
        var layer = Layer(Entity());
        var factory = Factory(layer.Object);

        var intent = await factory.LoadIntentAsync("session", TestContext.Current.CancellationToken);

        Assert.NotNull(intent);
        Assert.Equal("session", intent.AgentSessionId);
        Assert.Equal(Owner, intent.OwningProfileEntityId);
        Assert.Equal(3, intent.OwnershipGeneration);
        Assert.True(intent.ContinueInBackground);
    }

    [Fact]
    public async Task TryTakeOverAsync_UnexpiredPersistedLease_FailsClosedWithoutMutation()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var layer = Layer(Entity(time.GetUtcNow() + TimeSpan.FromMinutes(1)));
        var factory = Factory(layer.Object, time);

        Assert.False(await factory.TryTakeOverAsync(Takeover(), TestContext.Current.CancellationToken));
        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTakeOverAsync_StaleExpectedOwner_FailsClosedWithoutMutation()
    {
        var layer = Layer(Entity());
        var factory = Factory(layer.Object);
        var request = Takeover() with
        {
            ExpectedOwningProfileEntityId = "55555555-5555-5555-5555-555555555555",
        };

        Assert.False(await factory.TryTakeOverAsync(
            request, TestContext.Current.CancellationToken));

        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTakeOverAsync_StaleExpectedGeneration_FailsClosedWithoutMutation()
    {
        var layer = Layer(Entity());
        var factory = Factory(layer.Object);
        var request = Takeover() with { ExpectedOwnershipGeneration = 2 };

        Assert.False(await factory.TryTakeOverAsync(
            request, TestContext.Current.CancellationToken));

        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTakeOverAsync_MalformedPersistedLease_FailsClosedWithoutMutation()
    {
        var entity = EntityWithLeaseValue("not-an-authoritative-timestamp");
        var layer = Layer(entity);
        var factory = Factory(layer.Object);

        Assert.False(await factory.TryTakeOverAsync(
            Takeover(), TestContext.Current.CancellationToken));

        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTakeOverAsync_PersistedLeaseWithoutEpoch_FailsClosedWithoutMutation()
    {
        var values = Entity().Data!.Value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone(),
            StringComparer.Ordinal);
        values["runtime-lease-expiry"] = "2026-09-08T00:00:00Z";
        var entity = Entity() with { Data = JsonSerializer.SerializeToElement(values) };
        var layer = Layer(entity, DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var factory = Factory(layer.Object);

        Assert.False(await factory.TryTakeOverAsync(
            Takeover(), TestContext.Current.CancellationToken));

        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTakeOverAsync_PersistedEpochWithoutLease_FailsClosedWithoutMutation()
    {
        var entity = EntityWithLeaseValue(null);
        var layer = Layer(entity);
        var factory = Factory(layer.Object);

        Assert.False(await factory.TryTakeOverAsync(
            Takeover(), TestContext.Current.CancellationToken));

        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTakeOverAsync_MalformedPersistedEpoch_FailsClosedWithoutMutation()
    {
        var entity = EntityWithRuntimeValues(
            "not-an-epoch", "2026-09-08T00:00:00Z");
        var layer = Layer(entity);
        var factory = Factory(layer.Object);

        Assert.False(await factory.TryTakeOverAsync(
            Takeover(), TestContext.Current.CancellationToken));

        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTakeOverAsync_UsesAuthoritativeDatastoreTimeInsteadOfHostClock()
    {
        var datastoreTime = DateTimeOffset.Parse("2026-09-09T00:00:00Z");
        var hostTime = new FakeTimeProvider(datastoreTime + TimeSpan.FromDays(1));
        var layer = Layer(Entity(datastoreTime + TimeSpan.FromSeconds(1)), datastoreTime);
        var factory = Factory(layer.Object, hostTime);

        Assert.False(await factory.TryTakeOverAsync(
            Takeover(), TestContext.Current.CancellationToken));

        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTakeOverAsync_CompetingCompareExchange_AllowsOneWinner()
    {
        var time = DateTimeOffset.Parse("2026-09-09T00:00:00Z");
        var entity = Entity(time - TimeSpan.FromSeconds(1));
        var layer = Layer(entity, time);
        var bothUpdating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        layer.Setup(value => value.UpdateAsync(
                It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (UpdateRequest request, CancellationToken ct) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 2) bothUpdating.SetResult();
                await bothUpdating.Task.WaitAsync(ct);
                var data = Assert.Single(request.Changes).Data!.Value;
                return call == 1
                    ? SuccessfulUpdate(entity, data, "winner")
                    : FailedConcurrencyUpdate(entity);
            });
        var factory = Factory(layer.Object, new FakeTimeProvider(time));

        var first = factory.TryTakeOverAsync(Takeover(), TestContext.Current.CancellationToken).AsTask();
        var second = factory.TryTakeOverAsync(Takeover(), TestContext.Current.CancellationToken).AsTask();

        Assert.Equal([false, true], new[] { await first, await second }.Order());
    }

    [Fact]
    public async Task StartAsync_StaleIntentGeneration_FailsBeforeAcquiringRuntimeOrLease()
    {
        var layer = Layer(Entity());
        var running = new Mock<IRunningAgentChatTable>();
        var factory = new DataAccessAgentSessionRuntimeHostFactory(
            layer.Object, running.Object, new AgentSessionRuntimeContextFactory(null));
        var stale = (await factory.LoadIntentAsync(
            "session", TestContext.Current.CancellationToken))! with
        {
            OwnershipGeneration = 2,
        };

        await Assert.ThrowsAsync<AgentSessionUnavailableException>(
            () => factory.StartAsync(stale, TestContext.Current.CancellationToken));

        running.Verify(value => value.AcquireAsync(
            It.IsAny<AcquireAgentChatRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_AbandonedEpochWithoutAuthoritativeExpiry_FailsClosed()
    {
        var entity = EntityWithLeaseValue(null);
        var layer = Layer(entity);
        var running = new Mock<IRunningAgentChatTable>();
        var factory = new DataAccessAgentSessionRuntimeHostFactory(
            layer.Object, running.Object, new AgentSessionRuntimeContextFactory(null));
        var intent = (await factory.LoadIntentAsync(
            "session", TestContext.Current.CancellationToken))!;

        await Assert.ThrowsAsync<AgentSessionTakeoverBlockedException>(
            () => factory.StartAsync(intent, TestContext.Current.CancellationToken));

        running.Verify(value => value.AcquireAsync(
            It.IsAny<AcquireAgentChatRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_PersistedLeaseWithoutEpoch_FailsClosed()
    {
        var entity = EntityWithRuntimeValues(null, "2026-09-08T00:00:00Z");
        var layer = Layer(entity);
        var running = new Mock<IRunningAgentChatTable>();
        var factory = new DataAccessAgentSessionRuntimeHostFactory(
            layer.Object, running.Object, new AgentSessionRuntimeContextFactory(null));
        var intent = (await factory.LoadIntentAsync(
            "session", TestContext.Current.CancellationToken))!;

        await Assert.ThrowsAsync<AgentSessionTakeoverBlockedException>(
            () => factory.StartAsync(intent, TestContext.Current.CancellationToken));

        running.Verify(value => value.AcquireAsync(
            It.IsAny<AcquireAgentChatRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_MalformedPersistedEpoch_FailsClosed()
    {
        var entity = EntityWithRuntimeValues(
            "not-an-epoch", "2026-09-08T00:00:00Z");
        var layer = Layer(entity);
        var running = new Mock<IRunningAgentChatTable>();
        var factory = new DataAccessAgentSessionRuntimeHostFactory(
            layer.Object, running.Object, new AgentSessionRuntimeContextFactory(null));
        var intent = (await factory.LoadIntentAsync(
            "session", TestContext.Current.CancellationToken))!;

        await Assert.ThrowsAsync<AgentSessionTakeoverBlockedException>(
            () => factory.StartAsync(intent, TestContext.Current.CancellationToken));

        running.Verify(value => value.AcquireAsync(
            It.IsAny<AcquireAgentChatRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        layer.Verify(value => value.UpdateAsync(
            It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryTakeOverAsync_ExpiredPersistedLease_AdvancesOwnerAndGeneration()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var entity = Entity(time.GetUtcNow() - TimeSpan.FromSeconds(1));
        var layer = Layer(entity, time.GetUtcNow());
        UpdateRequest? update = null;
        layer.Setup(value => value.UpdateAsync(It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateRequest, CancellationToken>((request, _) => update = request)
            .ReturnsAsync(new UpdateResult
            {
                EntityResults =
                [
                    new EntityUpdateResult
                    {
                        UpdateState = UpdateState.Updated,
                        RequestedEntityId = entity.EntityId,
                        ResultingEntityId = entity.EntityId,
                        ConcurrencyTag = new ConcurrencyTag("next"),
                        ConcurrencyMatchState = ConcurrencyMatchState.Matched,
                        Errors = [],
                    },
                ],
            });
        var factory = Factory(layer.Object, time);

        Assert.True(await factory.TryTakeOverAsync(Takeover(), TestContext.Current.CancellationToken));
        var persisted = Assert.Single(update!.Changes).Data!.Value;
        Assert.Equal(NewOwner, persisted.GetProperty("host-profile-entity-id").GetString());
        Assert.Equal(4, persisted.GetProperty("ownership-generation").GetInt64());
        Assert.Equal("stopped", persisted.GetProperty("runtime-state").GetString());
        Assert.True(persisted.GetProperty("continue-in-background").GetBoolean());
    }

    [Fact]
    public async Task HostCrash_Restart_RecordsInterruptedEpochStoppedAndPreservesPreference()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var entity = Entity(time.GetUtcNow() - TimeSpan.FromSeconds(1));
        var layer = Layer(entity);
        var writes = new List<JsonElement>();
        layer.Setup(value => value.UpdateAsync(It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UpdateRequest request, CancellationToken _) =>
            {
                var data = Assert.Single(request.Changes).Data!.Value.Clone();
                writes.Add(data);
                return SuccessfulUpdate(entity, data, $"revision-{writes.Count}");
            });
        var chatFactory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(),
            new AgentServices(),
            TaskScheduler.Default);
        var running = new RecordingRunningAgentChatTable(new RunningAgentChatTable(chatFactory));
        var factory = new DataAccessAgentSessionRuntimeHostFactory(
            layer.Object, running, new AgentSessionRuntimeContextFactory(null), time);
        var intent = (await factory.LoadIntentAsync("session", TestContext.Current.CancellationToken))!;

        await using var runtime = await factory.StartAsync(intent, TestContext.Current.CancellationToken);

        Assert.NotNull(running.LastRequest);
        Assert.Equal(AgentChatAcquisitionMode.Local, running.LastRequest.AcquisitionMode);
        Assert.NotNull(running.LastRequest.AgentSessionEntity);
        Assert.Equal("session", running.LastRequest.AgentSessionEntity.Value
            .GetProperty("agent-session-id").GetString());
        Assert.Single(running.RunningSessions);
        Assert.Equal("interrupted", writes[0].GetProperty("runtime-state").GetString());
        Assert.Equal("33333333-3333-3333-3333-333333333333",
            writes[0].GetProperty("last-stopped-runtime-epoch").GetString());
        Assert.Equal("running", writes[1].GetProperty("runtime-state").GetString());
        Assert.True(writes[1].GetProperty("continue-in-background").GetBoolean());
        Assert.NotEqual(
            "33333333-3333-3333-3333-333333333333",
            writes[1].GetProperty("runtime-epoch").GetString());
        var context = Assert.IsType<CurrentSessionContext>(
            running.LastRequest.AgentServices!.CurrentSessionContext);
        Assert.Equal("session", context.AgentSessionId);
        Assert.Equal(Owner, context.OwningProfileEntityId);
        Assert.Equal(3, context.OwnershipGeneration);
        Assert.Equal(
            writes[1].GetProperty("runtime-epoch").GetString(),
            context.RuntimeEpoch?.Value.ToString("D"));
    }

    [Fact]
    public async Task OwnershipLease_AcquisitionAndRenewalUseAuthoritativeDatastoreTime()
    {
        var hostTime = new FakeTimeProvider(DateTimeOffset.Parse("2040-01-01T00:00:00Z"));
        var datastoreTime = DateTimeOffset.Parse("2026-09-09T00:00:00Z");
        var authoritativeTime = datastoreTime;
        EntitySnapshot current = Entity();
        var layer = new Mock<IDataAccessLayer>();
        layer.Setup(value => value.QueryAsync(
                It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new QueryResult
            {
                AuthoritativeTimestamp = new Timestamp(authoritativeTime, "query"),
                Batches =
                [
                    new TimestampedQueryBatch
                    {
                        Timestamp = null,
                        Entities = [(QueryEntitySnapshot)current],
                    },
                ],
            });
        var expiries = new List<DateTimeOffset>();
        var renewalPersisted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = 0;
        layer.Setup(value => value.UpdateAsync(
                It.IsAny<UpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UpdateRequest request, CancellationToken _) =>
            {
                var data = Assert.Single(request.Changes).Data!.Value.Clone();
                if (data.TryGetProperty("runtime-lease-expiry", out var expiry))
                    expiries.Add(expiry.GetDateTimeOffset());
                var result = SuccessfulUpdate(current, data, $"revision-{++revision}");
                current = result.EntityResults.Single().CurrentEntity!;
                if (request.UpdateMetadata.Comment.Text == "Renew agent session runtime lease")
                    renewalPersisted.TrySetResult();
                return result;
            });
        var chatFactory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(),
            new AgentServices(),
            TaskScheduler.Default);
        var factory = new DataAccessAgentSessionRuntimeHostFactory(
            layer.Object,
            new RecordingRunningAgentChatTable(new RunningAgentChatTable(chatFactory)),
            new AgentSessionRuntimeContextFactory(null),
            hostTime);
        var intent = (await factory.LoadIntentAsync(
            "session", TestContext.Current.CancellationToken))!;

        var runtime = await factory.StartAsync(intent, TestContext.Current.CancellationToken);
        Assert.Equal(datastoreTime + TimeSpan.FromSeconds(30), Assert.Single(expiries));

        authoritativeTime += TimeSpan.FromSeconds(10);
        hostTime.Advance(TimeSpan.FromSeconds(10));
        await renewalPersisted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(datastoreTime + TimeSpan.FromSeconds(40), expiries[1]);
        await runtime.DisposeAsync();
        await chatFactory.DisposeAsync();
    }

    private static DataAccessAgentSessionRuntimeHostFactory Factory(
        IDataAccessLayer layer, TimeProvider? timeProvider = null)
        => new(
            layer,
            Mock.Of<IRunningAgentChatTable>(),
            new AgentSessionRuntimeContextFactory(null),
            timeProvider);

    private static Mock<IDataAccessLayer> Layer(
        QueryEntitySnapshot entity,
        DateTimeOffset? authoritativeTime = null)
    {
        var layer = new Mock<IDataAccessLayer>();
        layer.Setup(value => value.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResult
            {
                AuthoritativeTimestamp = new Timestamp(
                    authoritativeTime ?? DateTimeOffset.Parse("2026-09-09T00:00:00Z"),
                    "query"),
                Batches =
                [
                    new TimestampedQueryBatch
                    {
                        Timestamp = new Timestamp(DateTimeOffset.UnixEpoch, "change"),
                        Entities = [entity],
                    },
                ],
            });
        return layer;
    }

    private static UpdateResult FailedConcurrencyUpdate(EntitySnapshot current)
        => new()
        {
            EntityResults =
            [
                new EntityUpdateResult
                {
                    UpdateState = UpdateState.Updated,
                    RequestedEntityId = current.EntityId,
                    ResultingEntityId = current.EntityId,
                    ConcurrencyTag = current.ConcurrencyTag,
                    ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
                    CurrentEntity = current,
                    Errors = [],
                },
            ],
        };

    private static QueryEntitySnapshot Entity(DateTimeOffset? leaseExpiry = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["agent-session-id"] = "session",
            ["host-profile-entity-id"] = Owner,
            ["ownership-generation"] = 3,
            ["continue-in-background"] = true,
            ["definition"] = new Dictionary<string, object?>
            {
                ["kind"] = "prompt",
                ["name"] = "non-gui-owner-pipeline",
                ["model"] = new Dictionary<string, object?>
                {
                    ["id"] = "echo",
                    ["provider"] = "echo",
                    ["apiType"] = "Echo",
                },
                ["tools"] = Array.Empty<object>(),
            },
        };
        if (leaseExpiry is not null)
        {
            data["runtime-epoch"] = "33333333-3333-3333-3333-333333333333";
            data["runtime-lease-expiry"] = leaseExpiry.Value.ToString("O");
        }

        return new QueryEntitySnapshot
        {
            EntityId = new EntityId("44444444-4444-4444-4444-444444444444"),
            ConcurrencyTag = new ConcurrencyTag("revision"),
            ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "change"),
            Data = JsonSerializer.SerializeToElement(data),
            Relationships = [],
            MatchingClauseIdentifiers = [],
        };
    }

    private static QueryEntitySnapshot EntityWithLeaseValue(string? leaseExpiry)
        => EntityWithRuntimeValues(
            "33333333-3333-3333-3333-333333333333", leaseExpiry);

    private static QueryEntitySnapshot EntityWithRuntimeValues(
        string? epoch,
        string? leaseExpiry)
    {
        var entity = Entity();
        var values = entity.Data!.Value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone(),
            StringComparer.Ordinal);
        if (epoch is not null)
            values["runtime-epoch"] = epoch;
        if (leaseExpiry is not null)
            values["runtime-lease-expiry"] = leaseExpiry;
        return entity with { Data = JsonSerializer.SerializeToElement(values) };
    }

    private static UpdateResult SuccessfulUpdate(
        EntitySnapshot previous, JsonElement data, string concurrencyTag)
        => new()
        {
            EntityResults =
            [
                new EntityUpdateResult
                {
                    UpdateState = UpdateState.Updated,
                    RequestedEntityId = previous.EntityId,
                    ResultingEntityId = previous.EntityId,
                    ConcurrencyTag = new ConcurrencyTag(concurrencyTag),
                    ConcurrencyMatchState = ConcurrencyMatchState.Matched,
                    CurrentEntity = previous with
                    {
                        Data = data,
                        ConcurrencyTag = new ConcurrencyTag(concurrencyTag),
                    },
                    Errors = [],
                },
            ],
        };

    private static AgentSessionTakeoverRequest Takeover() => new()
    {
        AgentSessionId = "session",
        ExpectedOwningProfileEntityId = Owner,
        ExpectedOwnershipGeneration = 3,
        NewOwningProfileEntityId = NewOwner,
        CorrelationId = Guid.NewGuid(),
    };

    private sealed class RecordingRunningAgentChatTable(IRunningAgentChatTable inner)
        : IRunningAgentChatTable
    {
        internal AcquireAgentChatRequest? LastRequest { get; private set; }
        public ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions
            => inner.RunningSessions;

        public Task<RunningAgentChatLease> AcquireAsync(
            AcquireAgentChatRequest request,
            CancellationToken ct = default)
        {
            this.LastRequest = request;
            return inner.AcquireAsync(request, ct);
        }

        public Task<bool> TerminateAsync(AgentSessionId sessionId, CancellationToken ct = default)
            => inner.TerminateAsync(sessionId, ct);

        public Task SetContinueInBackgroundAsync(
            AgentSessionId sessionId,
            bool continueInBackground,
            CancellationToken ct = default)
            => inner.SetContinueInBackgroundAsync(sessionId, continueInBackground, ct);
    }
}
