using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowStartupTests
{
    [AvaloniaFact]
    public async Task MainWindowViewModel_RepositoryStartupIsBlocked_DispatcherRemainsResponsive()
    {
        using var probe = new StartupProbe();
        await using var viewModel = CreateViewModel();
        try
        {
            await probe.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(probe.RepositoryStartedOnDispatcher);
            var initialization = viewModel.InitializeAsync();
            var heartbeat = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() => heartbeat.TrySetResult(Dispatcher.UIThread.CheckAccess()));
            Assert.True(await heartbeat.Task.WaitAsync(TestContext.Current.CancellationToken));
            Assert.False(initialization.IsCompleted);
            probe.Release.Set();
            await initialization;
            Assert.True(probe.BrokerConstructedOnDispatcher);
            Assert.NotEmpty(viewModel.TopLevelViews);
        }
        finally
        {
            probe.Release.Set();
        }
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_DisposedDuringRepositoryStartup_CancelsAndObservesLateCompletion()
    {
        using var probe = new StartupProbe();
        var viewModel = CreateViewModel();
        try
        {
            await probe.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(probe.RepositoryStartedOnDispatcher);
            var initialization = viewModel.InitializeAsync();
            var disposal = viewModel.DisposeAsync().AsTask();
            var concurrentDisposal = viewModel.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(concurrentDisposal.IsCompleted);
            probe.Release.Set();
            await disposal;
            await concurrentDisposal;
            await viewModel.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => viewModel.InitializeAsync());
            Assert.False(probe.BrokerConstructedOnDispatcher);
            Assert.Empty(viewModel.TopLevelViews);
        }
        finally
        {
            probe.Release.Set();
            await viewModel.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_DisposedAfterRepositoryCreated_DisposesLateRepositoryExactlyOnce()
    {
        using var probe = new StartupProbe(gateCompletedRepository: true);
        var viewModel = CreateViewModel();
        try
        {
            await probe.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(probe.RepositoryStartedOnDispatcher);
            var disposal = viewModel.DisposeAsync().AsTask();
            var concurrentDisposal = viewModel.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            probe.Release.Set();
            await disposal;
            await concurrentDisposal;
            await viewModel.DisposeAsync();
            Assert.Equal(1, probe.RepositoryDisposals);
            Assert.Empty(viewModel.TopLevelViews);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => viewModel.InitializeAsync());
        }
        finally
        {
            probe.Release.Set();
            await viewModel.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_DisposedDuringFailingStartup_ObservesSameFailureAndCompletesCleanup()
    {
        var startupFailure = new InvalidOperationException("Controlled repository startup failure.");
        using var probe = new StartupProbe(failureAfterRelease: startupFailure);
        var viewModel = CreateViewModel();
        try
        {
            await probe.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(probe.RepositoryStartedOnDispatcher);
            var initialization = viewModel.InitializeAsync();
            var disposal = viewModel.DisposeAsync().AsTask();
            var concurrentDisposal = viewModel.DisposeAsync().AsTask();
            probe.Release.Set();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => disposal);
            Assert.Same(startupFailure, Assert.Single(failure.InnerExceptions));
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => concurrentDisposal));
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => viewModel.DisposeAsync().AsTask()));
            Assert.Same(startupFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => initialization));
            Assert.Empty(viewModel.TopLevelViews);
        }
        finally
        {
            probe.Release.Set();
            try { await viewModel.DisposeAsync(); }
            catch (AggregateException) { }
        }
    }

    private static MainWindowViewModel CreateViewModel() => new(
        new UnknownRepositorySource(),
        new WorkspacesConfiguration { SkipStartupWorkspace = true });

    private sealed class StartupProbe : IDisposable
    {
        private readonly ActivityListener listener;
        private int entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public bool RepositoryStartedOnDispatcher { get; private set; }
        public bool BrokerConstructedOnDispatcher { get; private set; }
        public int RepositoryDisposals { get; private set; }

        public StartupProbe(bool gateCompletedRepository = false, Exception? failureAfterRelease = null)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            void Gate()
            {
                if (Interlocked.Exchange(ref this.entered, 1) != 0)
                    return;
                this.RepositoryStartedOnDispatcher = Dispatcher.UIThread.CheckAccess();
                this.Entered.TrySetResult();
                if (!this.RepositoryStartedOnDispatcher)
                {
                    // Activity callbacks are synchronous. Gate only the repository worker, never the dispatcher.
                    this.Release.Wait(cancellationToken);
                    if (failureAfterRelease is not null)
                        throw failureAfterRelease;
                }
            }
            this.listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == RepositoryStartupDiagnostics.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.OperationName == "broker-construction")
                        this.BrokerConstructedOnDispatcher = Dispatcher.UIThread.CheckAccess();
                    if (!gateCompletedRepository && activity.OperationName == "repository-schema-population")
                        Gate();
                },
                ActivityStopped = activity =>
                {
                    if (activity.OperationName == "repository-disposal")
                        this.RepositoryDisposals++;
                    if (gateCompletedRepository && activity.OperationName == "repository-initialization")
                        Gate();
                },
            };
            ActivitySource.AddActivityListener(this.listener);
        }

        public void Dispose()
        {
            this.Release.Set();
            this.listener.Dispose();
            this.Release.Dispose();
        }
    }
}
