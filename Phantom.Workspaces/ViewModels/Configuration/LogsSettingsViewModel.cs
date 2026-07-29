using System;
using Phantom.Workspaces.Install;
using Phantom.Workspaces.Services.Logging;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Logs settings section for the unified settings dialog. Surfaces the process's single
/// log-directory source of truth (<see cref="ILogDirectoryProvider.LogDirectory"/>) as a
/// read-only value and offers an "Open folder" command that launches the OS file browser at
/// that path via the injectable <see cref="IProcessLauncher"/> abstraction.
/// </summary>
public sealed class LogsSettingsViewModel : ViewModelBase
{
    private readonly ILogDirectoryProvider logDirectoryProvider;
    private readonly IProcessLauncher processLauncher;

    public LogsSettingsViewModel(
        ILogDirectoryProvider logDirectoryProvider,
        IProcessLauncher processLauncher)
    {
        ArgumentNullException.ThrowIfNull(logDirectoryProvider);
        ArgumentNullException.ThrowIfNull(processLauncher);
        this.logDirectoryProvider = logDirectoryProvider;
        this.processLauncher = processLauncher;
        this.OpenLogDirectoryCommand = new RelayCommand(_ => this.OpenLogDirectory());
    }

    /// <summary>Read-only path where rolling log files are written.</summary>
    public string LogDirectory => this.logDirectoryProvider.LogDirectory;

    /// <summary>Opens <see cref="LogDirectory"/> in the OS file browser.</summary>
    public RelayCommand OpenLogDirectoryCommand { get; }

    private void OpenLogDirectory()
    {
        // Reading the property creates the directory on demand (LogDirectoryProvider is lazy),
        // so the folder always exists before we ask the OS to open it.
        var directory = this.logDirectoryProvider.LogDirectory;
        this.processLauncher.Start(new ProcessStartRequest
        {
            FileName = "explorer.exe",
            Arguments = new[] { directory },
        });
    }
}
