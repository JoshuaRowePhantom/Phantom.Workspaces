using System;
using System.IO;
using Avalonia.Threading;

namespace Phantom.Workspaces.ViewModels;

public sealed class GitWorktreeWatcher : IDisposable
{
    private readonly string repositoryRootPath;
    private FileSystemWatcher? watcher;
    private DispatcherTimer? debounceTimer;
    private bool disposed;

    public GitWorktreeWatcher(string repositoryRootPath)
    {
        this.repositoryRootPath = repositoryRootPath;
    }

    public event EventHandler? Changed;

    public void Start()
    {
        if (this.disposed || !Directory.Exists(this.repositoryRootPath))
        {
            return;
        }

        this.debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        this.debounceTimer.Tick += this.OnDebounceTimerTick;

        this.watcher = new FileSystemWatcher(this.repositoryRootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };
        this.watcher.Changed += this.OnFileSystemChanged;
        this.watcher.Created += this.OnFileSystemChanged;
        this.watcher.Deleted += this.OnFileSystemChanged;
        this.watcher.Renamed += this.OnFileSystemRenamed;
        this.watcher.EnableRaisingEvents = true;
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnorePath(e.FullPath))
        {
            return;
        }

        this.RestartDebounceTimer();
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnorePath(e.FullPath) && ShouldIgnorePath(e.OldFullPath))
        {
            return;
        }

        this.RestartDebounceTimer();
    }

    private static bool ShouldIgnorePath(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/');
        return normalized.Contains("/.git/objects/")
            || normalized.Contains("/bin/")
            || normalized.Contains("/obj/");
    }

    private void RestartDebounceTimer()
    {
        if (this.debounceTimer is null)
        {
            return;
        }

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (this.disposed)
            {
                return;
            }

            this.debounceTimer.Stop();
            this.debounceTimer.Start();
        });
    }

    private void OnDebounceTimerTick(object? sender, EventArgs e)
    {
        this.debounceTimer?.Stop();
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        this.debounceTimer?.Stop();
        this.debounceTimer = null;

        if (this.watcher is not null)
        {
            this.watcher.EnableRaisingEvents = false;
            this.watcher.Dispose();
            this.watcher = null;
        }
    }
}
