using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorktreeWatcherTests : IDisposable
{
    private readonly string tempDir;

    public GitWorktreeWatcherTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "pw-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.tempDir))
            {
                Directory.Delete(this.tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task ChangedEventFiredAfterFileModification()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new GitWorktreeWatcher(this.tempDir, TimeSpan.FromMilliseconds(1));
        watcher.Changed += (_, _) => tcs.TrySetResult(true);
        watcher.Start();

        File.WriteAllText(Path.Combine(this.tempDir, "test.txt"), "hello");

        var fired = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(fired);
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task ChangedEventDebouncedOnRapidWrites()
    {
        var count = 0;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new GitWorktreeWatcher(this.tempDir, TimeSpan.FromMilliseconds(50));
        watcher.Changed += (_, _) =>
        {
            Interlocked.Increment(ref count);
            tcs.TrySetResult(true);
        };
        watcher.Start();

        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(this.tempDir, $"file{i}.txt"), "content");
        }

        // Wait for the debounce to fire at least once.
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));

        // Allow one extra dispatcher round-trip to catch any additional firings.
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(1, Volatile.Read(ref count));
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task DisposeUnsubscribesWatcher()
    {
        var refreshCount = 0;

        var watcher = new GitWorktreeWatcher(this.tempDir, TimeSpan.FromMilliseconds(1));
        watcher.Changed += (_, _) => Interlocked.Increment(ref refreshCount);
        watcher.Start();
        watcher.Dispose();

        File.WriteAllText(Path.Combine(this.tempDir, "after-dispose.txt"), "content");

        // Yield to the dispatcher twice so any already-queued work can complete.
        await Dispatcher.UIThread.InvokeAsync(() => { });
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(0, Volatile.Read(ref refreshCount));
    }

    [PhantomAvaloniaFact(Timeout = 10_000)]
    public async Task ChangedEventRaisedOnUIThread()
    {
        var onUiThread = false;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new GitWorktreeWatcher(this.tempDir, TimeSpan.FromMilliseconds(1));
        watcher.Changed += (_, _) =>
        {
            onUiThread = Dispatcher.UIThread.CheckAccess();
            tcs.TrySetResult(true);
        };
        watcher.Start();

        File.WriteAllText(Path.Combine(this.tempDir, "ui-thread-check.txt"), "content");

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(onUiThread);
    }
}
