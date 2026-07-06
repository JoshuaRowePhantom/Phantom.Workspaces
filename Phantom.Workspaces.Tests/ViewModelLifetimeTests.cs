using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Gui.Shared.Utilities;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ViewModelLifetimeTests
{
    // ── ViewModelLifetime tests ────────────────────────────────────────────────

    [Fact]
    public async Task ViewModelLifetime_Run_CancelledOnDispose_WorkIsCancelled()
    {
        var lifetime = new ViewModelLifetime();
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();

        lifetime.Run(async ct =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
            }
        });

        await started.Task;
        await lifetime.DisposeAsync();

        Assert.True(cancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ViewModelLifetime_Run_CompletesNormally_NoException()
    {
        var lifetime = new ViewModelLifetime();
        var completed = new TaskCompletionSource();

        lifetime.Run(ct =>
        {
            completed.SetResult();
            return Task.CompletedTask;
        });

        await lifetime.DisposeAsync();

        Assert.True(completed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ViewModelLifetime_Run_OperationCanceledException_IsSwallowed()
    {
        var lifetime = new ViewModelLifetime();
        var started = new TaskCompletionSource();

        lifetime.Run(ct =>
        {
            started.SetResult();
            throw new OperationCanceledException();
        });

        await started.Task;
        // Should not throw
        await lifetime.DisposeAsync();
    }

    [Fact]
    public async Task ViewModelLifetime_Run_OtherException_IsUnobserved()
    {
        var lifetime = new ViewModelLifetime();
        var started = new TaskCompletionSource();

        lifetime.Run(ct =>
        {
            started.SetResult();
            throw new InvalidOperationException("test");
        });

        await started.Task;
        // DisposeAsync should complete without propagating the exception
        await lifetime.DisposeAsync();
    }

    [Fact]
    public async Task ViewModelLifetime_Dispose_CancelsToken()
    {
        var lifetime = new ViewModelLifetime();
        var token = lifetime.Token;
        Assert.False(token.IsCancellationRequested);

        await lifetime.DisposeAsync();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task ViewModelLifetime_Dispose_IsIdempotent()
    {
        var lifetime = new ViewModelLifetime();
        await lifetime.DisposeAsync();
        // Second call should not throw
        await lifetime.DisposeAsync();
    }

    [Fact]
    public async Task ViewModelLifetime_Run_AfterDispose_WorkCancelledImmediately()
    {
        var lifetime = new ViewModelLifetime();
        await lifetime.DisposeAsync();

        var workSawCancelled = new TaskCompletionSource();

        lifetime.Run(async ct =>
        {
            if (ct.IsCancellationRequested)
            {
                workSawCancelled.SetResult();
            }
            else
            {
                // Wait briefly and check again — token may not yet be observed
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { workSawCancelled.SetResult(); }
            }
        });

        // Awaiting again flushes pending async work started by Run
        await lifetime.DisposeAsync();

        Assert.True(workSawCancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void ViewModelLifetime_Token_IsTheSameAsCtsToken()
    {
        var lifetime = new ViewModelLifetime();
        var token1 = lifetime.Token;
        var token2 = lifetime.Token;
        Assert.Equal(token1, token2);
    }

    [Fact]
    public async Task ViewModelLifetime_DisposeAsync_AwaitsRunningTasksToCompletion()
    {
        var lifetime = new ViewModelLifetime();
        var gate = new TaskCompletionSource();
        var workCompleted = false;

        lifetime.Run(async ct =>
        {
            await gate.Task.ConfigureAwait(false);
            workCompleted = true;
        });

        var disposeTask = lifetime.DisposeAsync();
        Assert.False(workCompleted);

        gate.SetResult();
        await disposeTask;

        Assert.True(workCompleted);
    }

    [Fact]
    public async Task ViewModelLifetime_DisposeAsync_IsIdempotent()
    {
        var lifetime = new ViewModelLifetime();
        lifetime.Run(ct => Task.CompletedTask);
        await lifetime.DisposeAsync();
        // Should not throw
        await lifetime.DisposeAsync();
    }

    // ── ViewModelBase tests ────────────────────────────────────────────────────

    [Fact]
    public async Task ViewModelBase_Dispose_CancelsLifetime()
    {
        var vm = new TestViewModel();
        var token = vm.LifetimeToken;
        Assert.False(token.IsCancellationRequested);

        await vm.DisposeAsync();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void ViewModelBase_Lifetime_IsAvailableInConstructor()
    {
        CancellationToken tokenDuringCtor = default;
        var vm = new TestViewModelWithCtorCapture(t => tokenDuringCtor = t);

        Assert.False(tokenDuringCtor.IsCancellationRequested);
        Assert.Equal(vm.LifetimeToken, tokenDuringCtor);
    }

    [Fact]
    public async Task ViewModelBase_DisposeAsync_CancelsLifetime()
    {
        var vm = new TestViewModel();
        var token = vm.LifetimeToken;

        await vm.DisposeAsync();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task ViewModelBase_DisposeAsync_AwaitsLifetimeCompletion()
    {
        var vm = new TestViewModel();
        var gate = new TaskCompletionSource();
        var workDone = false;

        vm.RunWork(async ct =>
        {
            await gate.Task.ConfigureAwait(false);
            workDone = true;
        });

        var disposeTask = vm.DisposeAsync();
        Assert.False(workDone);

        gate.SetResult();
        await disposeTask;

        Assert.True(workDone);
    }

    [Fact]
    public async Task ViewModelBase_DerivedClass_CanOverrideDisposeAsync_AndCallBase()
    {
        var derivedDisposed = false;
        var vm = new TestViewModelWithOverride(() => derivedDisposed = true);
        var token = vm.LifetimeToken;

        await vm.DisposeAsync();

        Assert.True(derivedDisposed);
        Assert.True(token.IsCancellationRequested);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed class TestViewModel : ViewModelBase
    {
        public CancellationToken LifetimeToken => Lifetime.Token;
        public void RunWork(Func<CancellationToken, Task> work) => Lifetime.Run(work);
    }

    private sealed class TestViewModelWithCtorCapture : ViewModelBase
    {
        public CancellationToken LifetimeToken => Lifetime.Token;

        public TestViewModelWithCtorCapture(Action<CancellationToken> captureToken)
        {
            captureToken(Lifetime.Token);
        }
    }

    private sealed class TestViewModelWithOverride : ViewModelBase
    {
        public CancellationToken LifetimeToken => Lifetime.Token;
        private readonly Action onDispose;

        public TestViewModelWithOverride(Action onDispose)
        {
            this.onDispose = onDispose;
        }

        public override async ValueTask DisposeAsync()
        {
            onDispose();
            await base.DisposeAsync();
        }
    }
}
