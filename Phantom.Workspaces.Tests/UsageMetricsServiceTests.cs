using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.UsageProviders;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class UsageMetricsServiceTests
{
    /// <summary>
    /// Deterministic tick gate. <see cref="WaitAsync"/> blocks until <see cref="Release"/> is called.
    /// Mirrors the TickGate pattern used in ScheduledToolRunnerTests.
    /// </summary>
    private sealed class TickGate
    {
        private readonly object gate = new();
        private TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource current;
            lock (this.gate)
            {
                this.waiting.TrySetResult();
                current = this.pending;
            }

            return current.Task.WaitAsync(cancellationToken);
        }

        public Task WaitUntilParkedAsync()
        {
            lock (this.gate)
            {
                return this.waiting.Task;
            }
        }

        public void Release()
        {
            lock (this.gate)
            {
                var toRelease = this.pending;
                this.pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                toRelease.SetResult();
            }
        }
    }

    private static QueryEntitySnapshot MakeUserAccountEntity(string provider, string userName)
    {
        var entityId = Guid.NewGuid().ToString();
        var json = $$"""
            {
              "entity-types": ["entity", "user-account"],
              "provider": "{{provider}}",
              "user-name": "{{userName}}"
            }
            """;
        using var doc = JsonDocument.Parse(json);
        return new QueryEntitySnapshot
        {
            EntityId = new EntityId(entityId),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = doc.RootElement.Clone(),
            Relationships = [],
            MatchingClauseIdentifiers = [],
        };
    }

    private sealed class FakeQueryDataAccessLayer : IDataAccessLayer
    {
        private readonly IReadOnlyList<QueryEntitySnapshot> entities;

        public FakeQueryDataAccessLayer(IReadOnlyList<QueryEntitySnapshot> entities)
        {
            this.entities = entities;
        }

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new QueryResult
            {
                Batches =
                [
                    new TimestampedQueryBatch
                    {
                        Timestamp = null,
                        Entities = this.entities,
                    },
                ],
            });

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeUsageProvider : IUsageProvider
    {
        private readonly Func<UsageAccount, CancellationToken, Task<IReadOnlyList<UsageMetric>>> getMetrics;

        public Uri ProviderUri { get; }

        public FakeUsageProvider(
            string providerHost,
            Func<UsageAccount, CancellationToken, Task<IReadOnlyList<UsageMetric>>> getMetrics)
        {
            this.ProviderUri = new Uri($"https://{providerHost}");
            this.getMetrics = getMetrics;
        }

        public Task<IReadOnlyList<UsageMetric>> GetMetricsAsync(
            UsageAccount account,
            CancellationToken cancellationToken)
            => this.getMetrics(account, cancellationToken);
    }

    [Fact]
    public async Task StartAsync_ImmediatelyRefreshesAccounts()
    {
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TickGate();

        var dal = new FakeQueryDataAccessLayer([MakeUserAccountEntity("https://github.com", "alice")]);
        var metrics = new UsageMetrics();

        var provider = new FakeUsageProvider("github.com", (_, _) =>
        {
            refreshed.TrySetResult();
            return Task.FromResult<IReadOnlyList<UsageMetric>>(
            [
                new UsageMetric { Title = "Included Usage", QuantityUsed = 1m, QuantityTotal = 10m },
            ]);
        });

        await using var service = new UsageMetricsService(dal, metrics, [provider], gate.WaitAsync);

        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await gate.WaitUntilParkedAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Single(metrics.Accounts);
    }

    [Fact]
    public async Task StartAsync_DoesNotAddAccount_WhenProviderReturnsEmpty()
    {
        var gate = new TickGate();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dal = new FakeQueryDataAccessLayer([MakeUserAccountEntity("https://github.com", "alice")]);
        var metrics = new UsageMetrics();

        var provider = new FakeUsageProvider("github.com", (_, _) =>
        {
            called.TrySetResult();
            return Task.FromResult<IReadOnlyList<UsageMetric>>([]);
        });

        await using var service = new UsageMetricsService(dal, metrics, [provider], gate.WaitAsync);

        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await gate.WaitUntilParkedAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Empty(metrics.Accounts);
    }

    [Fact]
    public async Task StartAsync_RefreshesAgainAfterTick()
    {
        var gate = new TickGate();
        var callCount = 0;
        var secondRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dal = new FakeQueryDataAccessLayer([MakeUserAccountEntity("https://github.com", "alice")]);
        var metrics = new UsageMetrics();

        var provider = new FakeUsageProvider("github.com", (_, _) =>
        {
            if (Interlocked.Increment(ref callCount) == 2)
            {
                secondRefresh.TrySetResult();
            }

            return Task.FromResult<IReadOnlyList<UsageMetric>>(
            [
                new UsageMetric { Title = "Included Usage", QuantityUsed = 1m, QuantityTotal = 10m },
            ]);
        });

        await using var service = new UsageMetricsService(dal, metrics, [provider], gate.WaitAsync);

        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        await gate.WaitUntilParkedAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
        gate.Release();
        await secondRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task DisposeAsync_CancelsLoop()
    {
        var gate = new TickGate();
        var dal = new FakeQueryDataAccessLayer([]);
        var metrics = new UsageMetrics();

        var service = new UsageMetricsService(dal, metrics, [], gate.WaitAsync);

        var ct = TestContext.Current.CancellationToken;
        await service.StartAsync(ct);
        await gate.WaitUntilParkedAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
}
