using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
        await firstCallCompleted.Task;

        Assert.Equal(1, provider.CallCount);

        // Wait for the service loop to register its timer before advancing fake time
        await delayScheduled.Task;

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

    [Fact]
    public async Task UsageMetricsService_LoopDelay_RegistersTimerOnFakeTimeProvider()
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
        await firstCallCompleted.Task;

        // Wait for the loop to schedule the timer
        await Task.WhenAny(delayScheduled.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // DelayScheduled hook should have been called, confirming timer registration
        Assert.True(delayScheduled.Task.IsCompleted, "DelayScheduled hook should have been called");
    }

    [Fact]
    public async Task UsageMetricsService_AdvanceBeforeTimerRegistered_DoesNotSilentlyStall()
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
        await firstCallCompleted.Task;

        // Wait for timer registration
        await delayScheduled.Task;

        // Advance time should trigger second call
        timeProvider.Advance(TimeSpan.FromSeconds(60));

        // Use bounded wait to detect silent stall
        var completed = await Task.WhenAny(secondCallCompleted.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(secondCallCompleted.Task, completed);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task UsageMetricsService_DisposeAsync_CancelsLoop_WhenBlockedOnDelay()
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

        var delayScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new UsageMetricsService(
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
        await firstCallCompleted.Task;

        // Wait for the loop to be blocked on delay
        await delayScheduled.Task;

        // DisposeAsync should unblock the loop that is parked on Task.Delay
        var disposeTask = service.DisposeAsync();
        var completed = await Task.WhenAny(disposeTask.AsTask(), Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(disposeTask.AsTask(), completed);

        Assert.Equal(1, provider.CallCount); // Only first call, no new calls after disposal
    }

    [Fact]
    public async Task UsageMetricsService_GitHubAccount_RoutesToBothCopilotAndActionsProviders()
    {
        // A single account stamped "https://github.com" must fan out to both providers registered
        // for the github.com host (Copilot at .../copilot and Actions at github.com), each producing
        // its own UsageAccount. Exact-URI routing previously dropped Copilot entirely (issue #1041).
        var copilotCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionsCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var copilotProvider = new FakeUsageProvider(
            new Uri("https://github.com/copilot"),
            (_, _) =>
            {
                copilotCalled.TrySetResult();
                return Task.FromResult<IReadOnlyList<UsageMetric>>([new UsageMetric { Title = "Copilot Usage" }]);
            });

        var actionsProvider = new FakeUsageProvider(
            new Uri("https://github.com"),
            (_, _) =>
            {
                actionsCalled.TrySetResult();
                return Task.FromResult<IReadOnlyList<UsageMetric>>([new UsageMetric { Title = "Actions Minutes" }]);
            });

        var dal = new FakeDataAccessLayer([
            CreateUserAccountEntity("https://github.com", "octocat"),
        ]);

        var mutationCount = 0;
        var bothMutationsCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationScheduler = new ActionBlockScheduler(task =>
        {
            task();
            if (Interlocked.Increment(ref mutationCount) == 2)
            {
                bothMutationsCompleted.TrySetResult();
            }
        });
        var usageMetrics = new UsageMetrics(mutationScheduler);
        var timeProvider = new FakeTimeProvider();

        await using var service = new UsageMetricsService(
            dal,
            usageMetrics,
            new[] { copilotProvider, actionsProvider },
            timeProvider,
            NullLogger<UsageMetricsService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await copilotCalled.Task;
        await actionsCalled.Task;
        await bothMutationsCompleted.Task;

        Assert.Equal(1, copilotProvider.CallCount);
        Assert.Equal(1, actionsProvider.CallCount);
        Assert.Equal(2, usageMetrics.Accounts.Count);
        Assert.Contains(usageMetrics.Accounts, a => a.SettingsUrl == new Uri("https://github.com/copilot"));
        Assert.Contains(usageMetrics.Accounts, a => a.SettingsUrl == new Uri("https://github.com"));
    }

    [Fact]
    public async Task UsageMetricsService_AccountCreatedAfterStartup_IsDiscoveredOnNextPoll()
    {
        // The account is created lazily after the service has started; re-discovering accounts on
        // every poll (rather than once at startup) must pick it up without an app restart (issue #1041).
        var provider = new FakeUsageProvider(
            new Uri("https://example.com"),
            (_, _) => Task.FromResult<IReadOnlyList<UsageMetric>>([new UsageMetric { Title = "API Calls" }]));

        var dal = new MutableFakeDataAccessLayer();

        var mutationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationScheduler = new ActionBlockScheduler(task =>
        {
            task();
            mutationCompleted.TrySetResult();
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

        // Startup discovery found no accounts; the loop is now parked on the delay.
        await delayScheduled.Task;
        Assert.Empty(usageMetrics.Accounts);

        // Account appears after startup; the next poll must discover and refresh it.
        dal.Entities = [CreateUserAccountEntity("https://example.com", "user1")];
        timeProvider.Advance(TimeSpan.FromSeconds(60));

        await mutationCompleted.Task;

        Assert.Equal(1, provider.CallCount);
        var account = Assert.Single(usageMetrics.Accounts);
        Assert.Equal("user1", account.UserName);
    }

    /// <summary>
    /// A fake DAL whose returned entity set can be changed after construction, so tests can simulate
    /// accounts being created after the service has started.
    /// </summary>
    private sealed class MutableFakeDataAccessLayer : IDataAccessLayer
    {
        private volatile IReadOnlyList<QueryEntitySnapshot> entities = Array.Empty<QueryEntitySnapshot>();

        public IReadOnlyList<QueryEntitySnapshot> Entities
        {
            get => this.entities;
            set => this.entities = value;
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

    /// <summary>Capturing logger that lets a test await a specific log entry deterministically.</summary>
    private sealed class SignalingLogger<T> : ILogger<T>
    {
        private readonly object gate = new();
        private readonly List<(LogLevel Level, string Message)> entries = new();
        private readonly List<(Func<(LogLevel Level, string Message), bool> Predicate, TaskCompletionSource Tcs)> waiters = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (this.gate) { return this.entries.ToList(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = (logLevel, formatter(state, exception));
            lock (this.gate)
            {
                this.entries.Add(entry);
                for (var i = this.waiters.Count - 1; i >= 0; i--)
                {
                    if (this.waiters[i].Predicate(entry))
                    {
                        this.waiters[i].Tcs.TrySetResult();
                        this.waiters.RemoveAt(i);
                    }
                }
            }
        }

        public Task WaitForAsync(Func<(LogLevel Level, string Message), bool> predicate)
        {
            lock (this.gate)
            {
                if (this.entries.Any(predicate))
                {
                    return Task.CompletedTask;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.waiters.Add((predicate, tcs));
                return tcs.Task;
            }
        }
    }

    [Fact]
    public async Task RefreshAccountAsync_WhenMetricsEmpty_LogsWarningWithAccountAndReason()
    {
        var provider = new FakeUsageProvider(
            new Uri("https://example.com"),
            (_, _) => Task.FromResult<IReadOnlyList<UsageMetric>>(Array.Empty<UsageMetric>()));
        var dal = new FakeDataAccessLayer([CreateUserAccountEntity("https://example.com", "user1")]);
        var usageMetrics = new UsageMetrics();
        var logger = new SignalingLogger<UsageMetricsService>();

        await using var service = new UsageMetricsService(
            dal, usageMetrics, new[] { provider }, new FakeTimeProvider(), logger);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForAsync(e => e.Level == LogLevel.Warning
            && e.Message.Contains("empty", StringComparison.OrdinalIgnoreCase));

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("user1", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAccountAsync_WhenAccountSkipped_LogsSkipReason()
    {
        var provider = new FakeUsageProvider(
            new Uri("https://example.com"),
            (_, _) => Task.FromResult<IReadOnlyList<UsageMetric>>(Array.Empty<UsageMetric>()));
        var dal = new FakeDataAccessLayer([CreateUserAccountEntity("https://example.com", "user1")]);
        var usageMetrics = new UsageMetrics();
        var logger = new SignalingLogger<UsageMetricsService>();

        await using var service = new UsageMetricsService(
            dal, usageMetrics, new[] { provider }, new FakeTimeProvider(), logger);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForAsync(e => e.Level == LogLevel.Warning
            && e.Message.Contains("skipping", StringComparison.OrdinalIgnoreCase));

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("skipping", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(usageMetrics.Accounts);
    }

    [Fact]
    public async Task RefreshAccountAsync_WhenMetricsReturned_LogsInformationAccountAdded()
    {
        var provider = new FakeUsageProvider(
            new Uri("https://example.com"),
            (_, _) => Task.FromResult<IReadOnlyList<UsageMetric>>([new UsageMetric { Title = "API Calls" }]));
        var dal = new FakeDataAccessLayer([CreateUserAccountEntity("https://example.com", "user1")]);
        var mutationScheduler = new ActionBlockScheduler(task => task());
        var usageMetrics = new UsageMetrics(mutationScheduler);
        var logger = new SignalingLogger<UsageMetricsService>();

        await using var service = new UsageMetricsService(
            dal, usageMetrics, new[] { provider }, new FakeTimeProvider(), logger);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForAsync(e => e.Level == LogLevel.Information
            && e.Message.Contains("added/updated", StringComparison.OrdinalIgnoreCase));

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("user1", entry.Message, StringComparison.Ordinal);
        Assert.Contains("1", entry.Message, StringComparison.Ordinal);
    }

    // #1188 — When a subsequent poll returns a different BillingPeriodStart than what
    // is currently cached on the account, the service must replace all previously
    // cached metrics for that account with the new-period set (no stale carry-over).
    [Fact]
    public async Task UsageMetricsService_CachedPreviousPeriodUsage_InvalidatedOnPeriodChange()
    {
        var providerUri = new Uri("https://example.com");
        var augStart = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var sepStart = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

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
                        new UsageMetric
                        {
                            Title = "Copilot AI Credits",
                            QuantityUsed = 15000m,
                            QuantityTotal = 20000m,
                            BillingPeriodStart = augStart,
                            ResetsAt = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero),
                        },
                    ]);
                }
                secondCallCompleted.TrySetResult();
                return Task.FromResult<IReadOnlyList<UsageMetric>>(
                [
                    new UsageMetric
                    {
                        Title = "Copilot AI Credits",
                        QuantityUsed = 250m,
                        QuantityTotal = 20000m,
                        BillingPeriodStart = sepStart,
                        ResetsAt = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
                    },
                ]);
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

        var account = Assert.Single(usageMetrics.Accounts);
        Assert.Equal(augStart, account.BillingPeriodStart);
        var firstMetric = Assert.Single(account.Metrics);
        Assert.Equal(15000m, firstMetric.QuantityUsed);

        await delayScheduled.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(60));
        await secondCallCompleted.Task;
        await secondMutationCompleted.Task;

        // Period rolled: prior 15,000 AI-credit metric must have been dropped; only the
        // new-period metric (250 credits) remains, and the account's cached period start
        // is updated to reflect September.
        Assert.Equal(sepStart, account.BillingPeriodStart);
        var secondMetric = Assert.Single(account.Metrics);
        Assert.Equal(250m, secondMetric.QuantityUsed);
        Assert.DoesNotContain(account.Metrics, m => m.QuantityUsed == 15000m);
    }
}
