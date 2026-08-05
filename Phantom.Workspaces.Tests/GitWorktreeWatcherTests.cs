using Avalonia.Headless.XUnit;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Phantom.Workspaces.Testing;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorktreeWatcherTests : IDisposable
{
    private readonly TempDirectory temp = new("pw-watcher-");
    private string tempDir => this.temp.Path;

    public void Dispose()
    {
        this.temp.Dispose();
    }

    [AvaloniaFact(Timeout = 10_000)]
    public void GitWorktreeWatcher_AfterTestCompletes_LeavesNoTempDirectoryBehind()
    {
        // Sentinel: dispose a sibling TempDirectory and assert the
        // directory it allocated is fully removed. Regressions in the
        // exception-safe cleanup path fail this test.
        string siblingPath;
        using (var sibling = new TempDirectory("pw-watcher-sentinel-"))
        {
            siblingPath = sibling.Path;
            File.WriteAllText(Path.Combine(siblingPath, "some-file.txt"), "content");
            Assert.True(Directory.Exists(siblingPath));
        }

        Assert.False(Directory.Exists(siblingPath));
    }

    [AvaloniaFact(Timeout = 10_000)]
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

    [AvaloniaFact(Timeout = 10_000)]
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

    [AvaloniaFact(Timeout = 10_000)]
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

    [AvaloniaFact(Timeout = 10_000)]
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
