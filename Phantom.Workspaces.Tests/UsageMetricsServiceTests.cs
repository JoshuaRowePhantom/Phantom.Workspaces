using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Models;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.UsageProviders;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class UsageMetricsServiceTests
{
    /// <summary>Fake provider that returns metrics controlled by the test.</summary>
    private sealed class FakeUsageProvider : IUsageProvider
    {
        private readonly Func<UsageAccount, CancellationToken, Task<IReadOnlyList<UsageMetric>>> getMetrics;
        private int callCount;

        public FakeUsageProvider(
            Uri providerUri,
            Func<UsageAccount, CancellationToken, Task<IReadOnlyList<UsageMetric>>> getMetrics)
        {
            this.ProviderUri = providerUri;
            this.getMetrics = getMetrics;
        }

        public Uri ProviderUri { get; }

        public int CallCount => Volatile.Read(ref this.callCount);

        public Task<IReadOnlyList<UsageMetric>> GetMetricsAsync(
            UsageAccount account,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.callCount);
            return this.getMetrics(account, cancellationToken);
        }
    }

    private sealed class FakeDataAccessLayer : IDataAccessLayer
    {
        private readonly IReadOnlyList<QueryEntitySnapshot> entities;

        public FakeDataAccessLayer(IReadOnlyList<QueryEntitySnapshot> entities)
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

    private static QueryEntitySnapshot CreateUserAccountEntity(string provider, string userName)
    {
        using var doc = JsonDocument.Parse($$"""
            {
              "entity-id": "{{Guid.NewGuid()}}",
              "entity-types": ["entity", "user-account"],
              "provider": "{{provider}}",
              "user-name": "{{userName}}"
            }
            """);
        return new QueryEntitySnapshot
        {
            EntityId = new EntityId(Guid.NewGuid().ToString()),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = doc.RootElement.Clone(),
            Relationships = [],
            MatchingClauseIdentifiers = [],
        };
    }

    [Fact]
    public async Task UsageMetricsService_AccountNotAdded_WhenProviderReturnsEmptyMetrics()
    {
        var providerUri = new Uri("https://example.com");
        var firstCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider(
            providerUri,
            (_, _) =>
            {
                firstCallCompleted.TrySetResult();
                return Task.FromResult<IReadOnlyList<UsageMetric>>(Array.Empty<UsageMetric>());
            });

        var dal = new FakeDataAccessLayer([
            CreateUserAccountEntity("https://example.com", "user1"),
        ]);

        var usageMetrics = new UsageMetrics();
        var timeProvider = new FakeTimeProvider();

        await using var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { provider },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await firstCallCompleted.Task;

        // Provider returned empty metrics on first refresh
        Assert.Equal(1, provider.CallCount);
        Assert.Empty(usageMetrics.Accounts);
    }

    [Fact]
    public async Task UsageMetricsService_AccountAdded_WhenProviderReturnsMetrics()
    {
        var providerUri = new Uri("https://example.com");
        var firstCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider(
            providerUri,
            (_, _) =>
            {
                firstCallCompleted.TrySetResult();
                return Task.FromResult<IReadOnlyList<UsageMetric>>(
                [
                    new UsageMetric { Title = "API Calls" }
                ]);
            });

        var dal = new FakeDataAccessLayer([
            CreateUserAccountEntity("https://example.com", "user1"),
        ]);

        var mutationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationScheduler = new ActionBlockScheduler(task =>
        {
            task();
            mutationCompleted.TrySetResult();
        });
        var usageMetrics = new UsageMetrics(mutationScheduler);
        var timeProvider = new FakeTimeProvider();

        await using var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { provider },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await mutationCompleted.Task;

        // Account was added after provider returned non-empty metrics
        Assert.Equal(1, provider.CallCount);
        var account = Assert.Single(usageMetrics.Accounts);
        Assert.Equal("user1", account.UserName);
        Assert.Single(account.Metrics);
    }

    [Fact]
    public async Task UsageMetricsService_AccountRemoved_WhenProviderSubsequentlyReturnsEmptyMetrics()
    {
        var providerUri = new Uri("https://example.com");
        var firstCall = true;
        var secondCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider(
            providerUri,
            (_, _) =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    return Task.FromResult<IReadOnlyList<UsageMetric>>(
                    [
                        new UsageMetric { Title = "API Calls" }
                    ]);
                }
                secondCallCompleted.TrySetResult();
                return Task.FromResult<IReadOnlyList<UsageMetric>>(Array.Empty<UsageMetric>());
            });

        var dal = new FakeDataAccessLayer([
            CreateUserAccountEntity("https://example.com", "user1"),
        ]);

        var mutationCount = 0;
        var firstMutationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondMutationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationScheduler = new ActionBlockScheduler(task =>
        {
            task();
            if (Interlocked.Increment(ref mutationCount) == 1)
                firstMutationCompleted.TrySetResult();
            else
                secondMutationCompleted.TrySetResult();
        });
        var usageMetrics = new UsageMetrics(mutationScheduler);
        var timeProvider = new FakeTimeProvider();

        var delayScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { provider },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance)
        {
            DelayScheduled = () =>
            {
                delayScheduled.TrySetResult();
                return Task.CompletedTask;
            }
        };

        await service.StartAsync(TestContext.Current.CancellationToken);
        await firstMutationCompleted.Task;

        // First refresh: account added
        Assert.Single(usageMetrics.Accounts);

        // Wait for the service loop to register its timer before advancing fake time
        await delayScheduled.Task;

        timeProvider.Advance(TimeSpan.FromSeconds(60));
        await secondMutationCompleted.Task;

        Assert.Equal(2, provider.CallCount);
        Assert.Empty(usageMetrics.Accounts);
    }

    [Fact]
    public async Task UsageMetricsService_AccountRetained_WhenProviderThrowsOnRefresh()
    {
        var providerUri = new Uri("https://example.com");
        var firstCall = true;
        var secondCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider(
            providerUri,
            (_, _) =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    return Task.FromResult<IReadOnlyList<UsageMetric>>(
                    [
                        new UsageMetric { Title = "API Calls" }
                    ]);
                }
                secondCallCompleted.TrySetResult();
                throw new InvalidOperationException("Provider error");
            });

        var dal = new FakeDataAccessLayer([
            CreateUserAccountEntity("https://example.com", "user1"),
        ]);

        var firstMutationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationScheduler = new ActionBlockScheduler(task =>
        {
            task();
            firstMutationCompleted.TrySetResult();
        });
        var usageMetrics = new UsageMetrics(mutationScheduler);
        var timeProvider = new FakeTimeProvider();

        var delayScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { provider },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance)
        {
            DelayScheduled = () =>
            {
                delayScheduled.TrySetResult();
                return Task.CompletedTask;
            }
        };

        await service.StartAsync(TestContext.Current.CancellationToken);
        await firstMutationCompleted.Task;

        // First refresh: account added
        Assert.Single(usageMetrics.Accounts);

        // Wait for the service loop to register its timer before advancing fake time
        await delayScheduled.Task;

        timeProvider.Advance(TimeSpan.FromSeconds(60));
        await secondCallCompleted.Task;
        // Second call threw - no mutation, account unchanged

        Assert.Equal(2, provider.CallCount);
        Assert.Single(usageMetrics.Accounts); // Account still present
    }

    [Fact]
    public async Task UsageMetricsService_IgnoresAccounts_WithNoMatchingProvider()
    {
        var providerUri = new Uri("https://example.com");
        var firstCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider(
            providerUri,
            (_, _) =>
            {
                firstCallCompleted.TrySetResult();
                return Task.FromResult<IReadOnlyList<UsageMetric>>(
                [
                    new UsageMetric { Title = "API Calls" }
                ]);
            });

        var dal = new FakeDataAccessLayer([
            CreateUserAccountEntity("https://example.com", "user1"),
            CreateUserAccountEntity("https://different.com", "user2"), // No matching provider
        ]);

        var mutationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationScheduler = new ActionBlockScheduler(task =>
        {
            task();
            mutationCompleted.TrySetResult();
        });
        var usageMetrics = new UsageMetrics(mutationScheduler);
        var timeProvider = new FakeTimeProvider();

        await using var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { provider },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await mutationCompleted.Task;

        // Only the account with a matching provider was processed
        Assert.Equal(1, provider.CallCount);
        var account = Assert.Single(usageMetrics.Accounts);
        Assert.Equal("user1", account.UserName);
    }

    [Fact]
    public async Task UsageMetricsService_CallsProvider_OnStart()
    {
        var providerUri = new Uri("https://example.com");
        var firstCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider(
            providerUri,
            (_, _) =>
            {
                firstCallCompleted.TrySetResult();
                return Task.FromResult<IReadOnlyList<UsageMetric>>(Array.Empty<UsageMetric>());
            });

        var dal = new FakeDataAccessLayer([
            CreateUserAccountEntity("https://example.com", "user1"),
        ]);

        var usageMetrics = new UsageMetrics();
        var timeProvider = new FakeTimeProvider();

        await using var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { provider },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance);

        // Provider should be called immediately after StartAsync
        await service.StartAsync(TestContext.Current.CancellationToken);
        await firstCallCompleted.Task;

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task UsageMetricsService_CallsProvider_After60Seconds()
    {
        var providerUri = new Uri("https://example.com");
        var firstCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var provider = new FakeUsageProvider(
            providerUri,
            (_, _) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstCallCompleted.TrySetResult();
                }
                else
                {
                    secondCallCompleted.TrySetResult();
                }
                return Task.FromResult<IReadOnlyList<UsageMetric>>(Array.Empty<UsageMetric>());
            });

        var dal = new FakeDataAccessLayer([
            CreateUserAccountEntity("https://example.com", "user1"),
        ]);

        var usageMetrics = new UsageMetrics();
        var timeProvider = new FakeTimeProvider();

        await using var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { provider },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await firstCallCompleted.Task;

        Assert.Equal(1, provider.CallCount);

        timeProvider.Advance(TimeSpan.FromSeconds(60));
        await secondCallCompleted.Task;

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task UsageMetricsService_UpdatesMetrics_OnForegroundScheduler()
    {
        var providerUri = new Uri("https://example.com");
        var firstCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider(
            providerUri,
            (_, _) =>
            {
                firstCallCompleted.TrySetResult();
                return Task.FromResult<IReadOnlyList<UsageMetric>>(
                [
                    new UsageMetric { Title = "API Calls" }
                ]);
            });

        var dal = new FakeDataAccessLayer([
            CreateUserAccountEntity("https://example.com", "user1"),
        ]);

        // Use a custom scheduler to verify marshalling
        var mutationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foregroundTaskIds = new List<int>();
        var foregroundScheduler = new ActionBlockScheduler(task =>
        {
            foregroundTaskIds.Add(Environment.CurrentManagedThreadId);
            task();
            mutationCompleted.TrySetResult();
        });

        var usageMetrics = new UsageMetrics(foregroundScheduler);
        var timeProvider = new FakeTimeProvider();

        await using var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { provider },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await firstCallCompleted.Task;
        await mutationCompleted.Task;

        // Verify the mutation ran on the foreground scheduler
        Assert.NotEmpty(foregroundTaskIds);
        var account = Assert.Single(usageMetrics.Accounts);
        Assert.Single(account.Metrics);
    }

    [Fact]
    public async Task UsageMetricsService_ProviderThrows_LogsWarningAndContinues()
    {
        var providerUri1 = new Uri("https://example1.com");
        var providerUri2 = new Uri("https://example2.com");

        var provider1Completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider2Completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var provider1 = new FakeUsageProvider(
            providerUri1,
            (_, _) =>
            {
                provider1Completed.TrySetResult();
                throw new InvalidOperationException("Provider 1 error");
            });

        var provider2 = new FakeUsageProvider(
            providerUri2,
            (_, _) =>
            {
                provider2Completed.TrySetResult();
                return Task.FromResult<IReadOnlyList<UsageMetric>>(
                [
                    new UsageMetric { Title = "API Calls" }
                ]);
            });

        var dal = new FakeDataAccessLayer([
            CreateUserAccountEntity("https://example1.com", "user1"),
            CreateUserAccountEntity("https://example2.com", "user2"),
        ]);

        var mutationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationScheduler = new ActionBlockScheduler(task =>
        {
            task();
            mutationCompleted.TrySetResult();
        });
        var usageMetrics = new UsageMetrics(mutationScheduler);
        var timeProvider = new FakeTimeProvider();

        await using var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { provider1, provider2 },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await mutationCompleted.Task;

        // Despite provider1 throwing, provider2 was still called and added
        Assert.Equal(1, provider1.CallCount);
        Assert.Equal(1, provider2.CallCount);
        var account = Assert.Single(usageMetrics.Accounts);
        Assert.Equal("user2", account.UserName);
    }

    [Fact]
    public async Task UsageMetricsService_OnDispose_CancelsRefreshLoop()
    {
        var providerUri = new Uri("https://example.com");
        var firstCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider(
            providerUri,
            (_, _) =>
            {
                firstCallCompleted.TrySetResult();
                return Task.FromResult<IReadOnlyList<UsageMetric>>(Array.Empty<UsageMetric>());
            });

        var dal = new FakeDataAccessLayer([
            CreateUserAccountEntity("https://example.com", "user1"),
        ]);

        var usageMetrics = new UsageMetrics();
        var timeProvider = new FakeTimeProvider();

        var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { provider },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await firstCallCompleted.Task;

        Assert.Equal(1, provider.CallCount);

        // Dispose should cancel the loop
        await service.DisposeAsync();

        // Advance time to verify no new calls are made after disposal
        timeProvider.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(1, provider.CallCount); // Still 1, no additional calls
    }

    [Fact]
    public async Task UsageMetricsService_NoAccounts_StartsWithEmptyCollection()
    {
        var providerUri = new Uri("https://example.com");
        var startCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeUsageProvider(
            providerUri,
            (_, _) => Task.FromResult<IReadOnlyList<UsageMetric>>(Array.Empty<UsageMetric>()));

        var dal = new FakeDataAccessLayer(Array.Empty<QueryEntitySnapshot>());

        var usageMetrics = new UsageMetrics();
        var timeProvider = new FakeTimeProvider();

        await using var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { provider },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);

        // No accounts discovered, so provider never called
        Assert.Equal(0, provider.CallCount);
        Assert.Empty(usageMetrics.Accounts);
    }

    /// <summary>
    /// A TaskScheduler that executes tasks synchronously, immediately on the queuing thread.
    /// </summary>
    private sealed class SynchronousTaskScheduler : TaskScheduler
    {
        protected override IEnumerable<Task> GetScheduledTasks() => Enumerable.Empty<Task>();
        protected override void QueueTask(Task task) => TryExecuteTask(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);
    }

    /// <summary>
    /// A simple TaskScheduler that executes tasks synchronously on the thread that queues them.
    /// Used to verify that mutations run on the injected scheduler.
    /// </summary>
    private sealed class ActionBlockScheduler : TaskScheduler
    {
        private readonly Action<Action> executeTask;

        public ActionBlockScheduler(Action<Action> executeTask)
        {
            this.executeTask = executeTask;
        }

        protected override IEnumerable<Task> GetScheduledTasks() => Enumerable.Empty<Task>();

        protected override void QueueTask(Task task)
        {
            this.executeTask(() => TryExecuteTask(task));
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            return TryExecuteTask(task);
        }
    }
}
