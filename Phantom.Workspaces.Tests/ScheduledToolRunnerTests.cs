using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.ScheduledTools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduledToolRunnerTests
{
    /// <summary>
    /// A deterministic tick gate: <see cref="WaitAsync"/> blocks until <see cref="Release"/> is
    /// called, letting a test drive the runner loop one poll at a time with no wall-clock timing.
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

        /// <summary>Waits until the runner is parked inside <see cref="WaitAsync"/>.</summary>
        public Task WaitUntilParkedAsync()
        {
            lock (this.gate)
            {
                return this.waiting.Task;
            }
        }

        /// <summary>Releases the parked wait so the loop performs one more evaluation.</summary>
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

    [Fact]
    public async Task Start_RunsImmediatelyOnce_BeforeAnyTick()
    {
        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;
        var gate = new TickGate();

        await using var runner = new ScheduledToolRunner(
            runOnce: _ =>
            {
                if (Interlocked.Increment(ref runCount) == 1)
                {
                    firstRun.SetResult();
                }

                return Task.CompletedTask;
            },
            waitForNextTick: gate.WaitAsync);

        runner.Start();

        await firstRun.Task;
        Assert.Equal(1, Volatile.Read(ref runCount));
    }

    [Fact]
    public async Task EachTick_TriggersExactlyOneAdditionalRun()
    {
        var runCount = 0;
        var runs = new List<TaskCompletionSource>
        {
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var gate = new TickGate();

        await using var runner = new ScheduledToolRunner(
            runOnce: _ =>
            {
                var index = Interlocked.Increment(ref runCount) - 1;
                if (index < runs.Count)
                {
                    runs[index].SetResult();
                }

                return Task.CompletedTask;
            },
            waitForNextTick: gate.WaitAsync);

        runner.Start();

        await runs[0].Task; // immediate run
        await gate.WaitUntilParkedAsync();

        gate.Release();
        await runs[1].Task;
        await gate.WaitUntilParkedAsync();

        gate.Release();
        await runs[2].Task;

        Assert.Equal(3, Volatile.Read(ref runCount));
    }

    [Fact]
    public async Task RunsAreSerialized_NeverOverlapping()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        var secondRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;
        var gate = new TickGate();

        await using var runner = new ScheduledToolRunner(
            runOnce: async _ =>
            {
                var now = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, now);
                await Task.Yield();
                Interlocked.Decrement(ref concurrent);

                if (Interlocked.Increment(ref runCount) == 2)
                {
                    secondRun.SetResult();
                }
            },
            waitForNextTick: gate.WaitAsync);

        runner.Start();
        await gate.WaitUntilParkedAsync();
        gate.Release();
        await secondRun.Task;

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task RunFault_RaisesEvent_AndLoopContinues()
    {
        var faults = new List<Exception>();
        var faultRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;
        var gate = new TickGate();

        await using var runner = new ScheduledToolRunner(
            runOnce: _ =>
            {
                var index = Interlocked.Increment(ref runCount);
                if (index == 1)
                {
                    throw new InvalidOperationException("boom");
                }

                if (index == 2)
                {
                    secondRun.SetResult();
                }

                return Task.CompletedTask;
            },
            waitForNextTick: gate.WaitAsync);

        runner.RunFaulted += (_, exception) =>
        {
            faults.Add(exception);
            faultRaised.TrySetResult();
        };

        runner.Start();
        await faultRaised.Task;
        await gate.WaitUntilParkedAsync();
        gate.Release();
        await secondRun.Task;

        Assert.Single(faults);
        Assert.IsType<InvalidOperationException>(faults[0]);
    }

    [Fact]
    public async Task Dispose_StopsTheLoop()
    {
        var runCount = 0;
        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TickGate();

        var runner = new ScheduledToolRunner(
            runOnce: _ =>
            {
                if (Interlocked.Increment(ref runCount) == 1)
                {
                    firstRun.SetResult();
                }

                return Task.CompletedTask;
            },
            waitForNextTick: gate.WaitAsync);

        runner.Start();
        await firstRun.Task;
        await gate.WaitUntilParkedAsync();

        await runner.DisposeAsync();

        // Releasing after disposal must not produce more runs because the loop has exited.
        gate.Release();
        Assert.Equal(1, Volatile.Read(ref runCount));
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        var runCount = 0;
        var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TickGate();

        await using var runner = new ScheduledToolRunner(
            runOnce: _ =>
            {
                if (Interlocked.Increment(ref runCount) == 1)
                {
                    firstRun.SetResult();
                }

                return Task.CompletedTask;
            },
            waitForNextTick: gate.WaitAsync);

        runner.Start();
        runner.Start();
        runner.Start();

        await firstRun.Task;
        await gate.WaitUntilParkedAsync();

        Assert.Equal(1, Volatile.Read(ref runCount));
    }
}
