using System;
using System.Collections.ObjectModel;
using System.Linq;
using Phantom.Workspaces.ScheduledTools;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A single currently-running scheduled tool, projected for display in the running-tools list.
/// </summary>
public sealed class RunningScheduledToolViewModel
{
    public RunningScheduledToolViewModel(string toolType, string host, DateTimeOffset startedAt)
    {
        this.ToolType = toolType;
        this.Host = host;
        this.StartedAt = startedAt;
    }

    /// <summary>The tool type that is running.</summary>
    public string ToolType { get; }

    /// <summary>A display label for the host the tool is running on.</summary>
    public string Host { get; }

    /// <summary>When the run started (UTC).</summary>
    public DateTimeOffset StartedAt { get; }
}

/// <summary>
/// Surfaces the scheduled tools currently running on a <see cref="ScheduledToolHost"/> for the
/// scheduled-tools runtime display. It refreshes live from the host's
/// <see cref="ScheduledToolHost.RunningExecutionsChanged"/> event.
/// </summary>
public sealed class ScheduledToolsRunningViewModel : ViewModelBase, IDisposable
{
    private readonly ScheduledToolHost host;
    private readonly Action<Action> dispatch;

    /// <param name="host">The host whose running tools are displayed.</param>
    /// <param name="dispatch">
    /// Marshals a refresh onto the UI thread. Defaults to running synchronously (used in tests); the
    /// GUI passes a dispatcher post so the observable collection is updated on the UI thread.
    /// </param>
    public ScheduledToolsRunningViewModel(ScheduledToolHost host, Action<Action>? dispatch = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.dispatch = dispatch ?? (action => action());
        this.host.RunningExecutionsChanged += this.OnRunningExecutionsChanged;
        this.Refresh();
    }

    /// <summary>The scheduled tools currently running, ordered by start time.</summary>
    public ObservableCollection<RunningScheduledToolViewModel> RunningTools { get; } = new();

    /// <summary>Whether any scheduled tool is currently running.</summary>
    public bool HasRunningTools => this.RunningTools.Count > 0;

    private void OnRunningExecutionsChanged(object? sender, EventArgs e) => this.dispatch(this.Refresh);

    private void Refresh()
    {
        this.RunningTools.Clear();
        foreach (var running in this.host.GetRunningExecutions().OrderBy(execution => execution.StartedAt))
        {
            this.RunningTools.Add(new RunningScheduledToolViewModel(
                running.ToolType,
                string.Join(" / ", running.HostNameComponents),
                running.StartedAt));
        }

        this.RaisePropertyChanged(nameof(this.HasRunningTools));
    }

    public void Dispose()
    {
        this.host.RunningExecutionsChanged -= this.OnRunningExecutionsChanged;
    }
}
