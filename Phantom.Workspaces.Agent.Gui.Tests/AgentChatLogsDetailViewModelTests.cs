using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatLogsDetailViewModelTests
{
    [Fact]
    public async Task AgentViewModel_LogsDetail_SeedAndLiveReconnect_HasNoGapsOrDuplicates()
    {
        using var memory = new ObservableLoggerFactory();
        var logger = memory.CreateLogger("SafeLifecycle");
        logger.LogInformation("before-navigation");
        using var detail = new AgentChatLogsDetailViewModel(memory, TaskScheduler.Default);
        Assert.Single(detail.Entries);

        var observed = ObserveEntryAsync(detail, "after-navigation");
        logger.LogInformation("after-navigation");
        await observed;

        Assert.Equal(2, detail.Entries.Count);
        Assert.Equal(1, detail.Entries.Count(item => item.Contains("before-navigation", StringComparison.Ordinal)));
        Assert.Equal(1, detail.Entries.Count(item => item.Contains("after-navigation", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AgentViewModel_LogsDetail_ConcurrentWritesAcrossSubscription_HasNoGapsOrDuplicates()
    {
        using var memory = new ObservableLoggerFactory();
        var logger = memory.CreateLogger("SafeLifecycle");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            started.TrySetResult();
            await proceed.Task;
            for (var i = 0; i < 500; i++)
                logger.LogInformation("entry-{Number:D4}", i);
        }, TestContext.Current.CancellationToken);
        await started.Task;
        using var detail = new AgentChatLogsDetailViewModel(memory, TaskScheduler.Default);
        var receivedLast = ObserveEntryAsync(detail, "entry-0499");
        proceed.TrySetResult();
        await writer;
        await receivedLast;

        Assert.Equal(500, detail.Entries.Count);
        Assert.Equal(500, detail.Entries.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task AgentChatLogsDetail_Burst_RemainsBounded()
    {
        using var memory = new ObservableLoggerFactory();
        var logger = memory.CreateLogger("SafeLifecycle");
        for (var i = 0; i < ObservableLoggerFactory.RecentEntryLimit + 100; i++)
            logger.LogInformation("entry-{Number:D4}", i);
        using var detail = new AgentChatLogsDetailViewModel(memory, TaskScheduler.Default);

        Assert.Equal(ObservableLoggerFactory.RecentEntryLimit, memory.Entries.Count);
        Assert.Equal(ObservableLoggerFactory.RecentEntryLimit, detail.Entries.Count);
        Assert.Contains("entry-0100", detail.Entries[0], StringComparison.Ordinal);
        Assert.DoesNotContain(detail.Entries, item => item.Contains("entry-0000", StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AgentViewModel_LogsDetail_Disposed_UnsubscribesAndStopsUpdates()
    {
        using var memory = new ObservableLoggerFactory();
        using var detail = new AgentChatLogsDetailViewModel(memory, TaskScheduler.Default);
        var count = detail.Entries.Count;
        detail.Dispose();
        memory.CreateLogger("SafeLifecycle").LogInformation("after-dispose");

        Assert.Equal(count, detail.Entries.Count);
        await Task.CompletedTask;
    }

    internal static Task ObserveEntryAsync(AgentChatLogsDetailViewModel detail, string marker)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = (System.Collections.Specialized.INotifyCollectionChanged)detail.Entries;
        void OnChanged(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __)
        {
            if (detail.Entries.Any(entry => entry.Contains(marker, StringComparison.Ordinal)))
            {
                changes.CollectionChanged -= OnChanged;
                completion.TrySetResult();
            }
        }
        changes.CollectionChanged += OnChanged;
        OnChanged(null, new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        return completion.Task.WaitAsync(TestContext.Current.CancellationToken);
    }
}
