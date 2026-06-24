using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ScheduledTools;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Drives the main-window clock / scheduled-tools button so it reflects the persisted
/// <c>scheduled-tools-paused</c> state: a pause glyph while paused, the clock glyph otherwise. The
/// toggle command persists the new pause state on the host profile entity (and, when pausing,
/// stops all in-flight scheduled tool runs via the <see cref="ScheduledToolPauseStateService"/>).
/// </summary>
public sealed class ScheduledToolsPauseIndicatorViewModel : ViewModelBase, IDisposable
{
    private const string ClockGlyph = "⏱";
    private const string PauseGlyph = "⏸";

    private readonly ScheduledToolPauseStateService pauseStateService;
    private readonly EntityId hostEntityId;
    private readonly Action<Action> dispatch;
    private bool isToggleInProgress;

    /// <param name="pauseStateService">The persisted pause-state service to observe and update.</param>
    /// <param name="hostEntityId">The host profile entity whose pause flag is toggled.</param>
    /// <param name="dispatch">
    /// Marshals a state refresh onto the UI thread. Defaults to running synchronously (used in tests);
    /// the GUI passes a dispatcher post so bound properties update on the UI thread.
    /// </param>
    public ScheduledToolsPauseIndicatorViewModel(
        ScheduledToolPauseStateService pauseStateService,
        EntityId hostEntityId,
        Action<Action>? dispatch = null)
    {
        this.pauseStateService = pauseStateService ?? throw new ArgumentNullException(nameof(pauseStateService));
        this.hostEntityId = hostEntityId;
        this.dispatch = dispatch ?? (action => action());
        this.pauseStateService.PauseStateChanged += this.OnPauseStateChanged;
        this.TogglePauseCommand = new RelayCommand(
            _ => _ = this.TogglePauseAsync(),
            _ => !this.isToggleInProgress);
    }

    /// <summary>Whether scheduled tools are currently paused on the host.</summary>
    public bool IsPaused => this.pauseStateService.IsPaused;

    /// <summary>The glyph shown on the scheduled-tools button (pause icon when paused).</summary>
    public string ButtonGlyph => this.IsPaused ? PauseGlyph : ClockGlyph;

    /// <summary>The tooltip for the scheduled-tools button.</summary>
    public string ToolTip => this.IsPaused
        ? "Scheduled tasks (paused)"
        : "Scheduled tasks";

    /// <summary>Toggles the persisted pause state for the host.</summary>
    public RelayCommand TogglePauseCommand { get; }

    /// <summary>Toggles the persisted pause state for the host.</summary>
    public async Task TogglePauseAsync(CancellationToken cancellationToken = default)
    {
        this.isToggleInProgress = true;
        this.TogglePauseCommand.RaiseCanExecuteChanged();
        try
        {
            await this.pauseStateService
                .SetPausedAsync(this.hostEntityId, !this.IsPaused, cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            this.isToggleInProgress = false;
            this.TogglePauseCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnPauseStateChanged(object? sender, EventArgs e) => this.dispatch(this.RaiseStateChanged);

    private void RaiseStateChanged()
    {
        this.RaisePropertyChanged(nameof(this.IsPaused));
        this.RaisePropertyChanged(nameof(this.ButtonGlyph));
        this.RaisePropertyChanged(nameof(this.ToolTip));
    }

    public void Dispose()
    {
        this.pauseStateService.PauseStateChanged -= this.OnPauseStateChanged;
    }
}
