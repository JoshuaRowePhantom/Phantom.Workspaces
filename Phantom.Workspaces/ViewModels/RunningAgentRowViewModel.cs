using System.Windows.Input;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Represents a single running-agent entry in the <see cref="RunningAgentBrainViewModel"/> popup.
/// </summary>
public sealed class RunningAgentRowViewModel : ViewModelBase, IDisposable
{
    private readonly TimeProvider timeProvider;
    private readonly AgentChatInterruptState? interruptState;
    private readonly AsyncRelayCommand? interruptCommand;
    private readonly AsyncRelayCommand? terminateCommand;
    private readonly AsyncRelayCommand? setContinueInBackgroundCommand;
    private bool isThinking;
    private bool isRemote;
    private bool continueInBackground;
    private int viewerCount;
    private bool isBackgroundOptionEnabled;
    private bool isInterruptEnabled;
    private bool isTerminateEnabled;
    private bool isInterruptPending;
    private bool isTerminationPending;
    private string? lastOperationError;
    private bool canSetContinueInBackground;
    private bool isConnected = true;
    private bool isTerminal;
    private bool isBackgroundUpdatePending;

    public RunningAgentRowViewModel(
        string sessionKey,
        string workspacePaneTitle,
        string tabTitle,
        bool isThinking,
        ICommand activateCommand,
        TimeProvider? timeProvider = null)
        : this(sessionKey, workspacePaneTitle, tabTitle, null, true, isThinking, activateCommand, timeProvider)
    {
    }

    public RunningAgentRowViewModel(
        string sessionKey,
        string entityName,
        ICommand activateCommand,
        TimeProvider? timeProvider = null)
        : this(sessionKey, string.Empty, string.Empty, entityName, false, false, activateCommand, timeProvider)
    {
    }

    internal RunningAgentRowViewModel(
        RunningAgentChatWithEntityInfo session,
        string? workspacePaneTitle,
        string? tabTitle,
        string? entityName,
        bool hasOpenTab,
        bool isThinking,
        ICommand activateCommand,
        Func<CancellationToken, Task> interruptAsync,
        Func<CancellationToken, Task> terminateAsync,
        Func<bool, CancellationToken, Task> setContinueInBackgroundAsync,
        TimeProvider? timeProvider = null)
        : this(
            session.SessionId.Value,
            workspacePaneTitle ?? string.Empty,
            tabTitle ?? string.Empty,
            entityName,
            hasOpenTab,
            isThinking,
            activateCommand,
            timeProvider)
    {
        this.interruptCommand = new AsyncRelayCommand(
            _ => this.InterruptAsync(interruptAsync),
            _ => this.IsInterruptEnabled,
            allowConcurrentExecutions: false);
        this.interruptState = session.InterruptState;
        this.interruptState.StateChanged += this.OnInterruptStateChanged;
        this.ApplyInterruptState(this.interruptState.Snapshot);
        this.terminateCommand = new AsyncRelayCommand(
            _ => this.TerminateAsync(terminateAsync),
            _ => this.IsTerminateEnabled);
        this.setContinueInBackgroundCommand = new AsyncRelayCommand(
            parameter => parameter is bool value
                ? this.SetContinueInBackgroundAsync(value, setContinueInBackgroundAsync)
                : Task.CompletedTask,
            parameter => parameter is bool && this.IsBackgroundOptionEnabled);
        this.InterruptCommand = this.interruptCommand;
        this.TerminateCommand = this.terminateCommand;
        this.SetContinueInBackgroundCommand = this.setContinueInBackgroundCommand;
        this.UpdateRuntimeMetadata(session, isThinking);
    }

    private RunningAgentRowViewModel(
        string sessionKey,
        string workspacePaneTitle,
        string tabTitle,
        string? entityName,
        bool hasOpenTab,
        bool isThinking,
        ICommand activateCommand,
        TimeProvider? timeProvider)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.SessionKey = sessionKey;
        this.WorkspacePaneTitle = workspacePaneTitle;
        this.TabTitle = tabTitle;
        this.EntityName = entityName;
        this.HasOpenTab = hasOpenTab;
        this.isThinking = isThinking;
        this.ActivateCommand = activateCommand;
        this.InterruptCommand = new RelayCommand(_ => { }, _ => false);
        this.TerminateCommand = new RelayCommand(_ => { }, _ => false);
        this.SetContinueInBackgroundCommand = new RelayCommand(_ => { }, _ => false);
        this.LastActivityAt = this.timeProvider.GetUtcNow().UtcDateTime;
    }

    /// <summary>The session key, used for deduplication across workspace panes.</summary>
    public string SessionKey { get; }
    public string WorkspacePaneTitle { get; }
    public string TabTitle { get; }
    public bool HasOpenTab { get; }
    public string? EntityName { get; }

    public bool IsThinking
    {
        get => this.isThinking;
        internal set => this.SetProperty(ref this.isThinking, value);
    }

    public bool IsRemote
    {
        get => this.isRemote;
        private set => this.SetProperty(ref this.isRemote, value);
    }

    public bool ContinueInBackground
    {
        get => this.continueInBackground;
        private set => this.SetProperty(ref this.continueInBackground, value);
    }

    public int ViewerCount
    {
        get => this.viewerCount;
        private set => this.SetProperty(ref this.viewerCount, value);
    }

    public bool IsBackgroundOptionEnabled
    {
        get => this.isBackgroundOptionEnabled;
        private set
        {
            if (this.SetProperty(ref this.isBackgroundOptionEnabled, value))
            {
                this.setContinueInBackgroundCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsInterruptEnabled
    {
        get => this.isInterruptEnabled;
        private set
        {
            if (this.SetProperty(ref this.isInterruptEnabled, value))
            {
                this.interruptCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsTerminateEnabled
    {
        get => this.isTerminateEnabled;
        private set
        {
            if (this.SetProperty(ref this.isTerminateEnabled, value))
            {
                this.terminateCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True while an explicit interrupt request is awaiting the owner result.</summary>
    public bool IsInterruptPending
    {
        get => this.isInterruptPending;
        private set => this.SetProperty(ref this.isInterruptPending, value);
    }

    /// <summary>True while an explicit terminate request is awaiting the owner result.</summary>
    public bool IsTerminationPending
    {
        get => this.isTerminationPending;
        private set => this.SetProperty(ref this.isTerminationPending, value);
    }

    /// <summary>A stable, non-transport error message for a failed row operation.</summary>
    public string? LastOperationError
    {
        get => this.lastOperationError;
        private set => this.SetProperty(ref this.lastOperationError, value);
    }

    /// <summary>The time of the most recent history activity for this session, used for sorting.</summary>
    internal DateTime LastActivityAt { get; private set; }

    internal void UpdateLastActivityAt(DateTime at) => this.LastActivityAt = at;

    internal void UpdateRuntimeMetadata(RunningAgentChatWithEntityInfo session, bool isActiveInterruptibleTurn)
    {
        this.IsRemote = session.IsRemote;
        this.ContinueInBackground = session.ContinueInBackground;
        this.ViewerCount = session.ViewerCount;
        this.canSetContinueInBackground = session.CanSetContinueInBackground;
        this.isConnected = session.IsConnected;
        this.isTerminal = session.IsTerminal;
        this.IsThinking = isActiveInterruptibleTurn;
        this.UpdateCommandAvailability();
    }

    internal void MarkDisconnected()
    {
        this.IsInterruptEnabled = false;
        this.IsTerminateEnabled = false;
        this.IsBackgroundOptionEnabled = false;
    }

    private async Task TerminateAsync(Func<CancellationToken, Task> terminateAsync)
    {
        this.IsTerminationPending = true;
        this.IsInterruptEnabled = false;
        this.IsTerminateEnabled = false;
        this.IsBackgroundOptionEnabled = false;
        this.LastOperationError = null;
        try
        {
            await terminateAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            this.IsTerminationPending = false;
            this.UpdateCommandAvailability();
        }
        catch
        {
            this.LastOperationError = "Unable to terminate agent session.";
            this.IsTerminationPending = false;
            this.UpdateCommandAvailability();
        }
    }

    private async Task InterruptAsync(Func<CancellationToken, Task> interruptAsync)
    {
        try
        {
            await this.interruptState!.InterruptAsync(interruptAsync, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Cancellation leaves authoritative row state unchanged.
        }
        catch
        {
            // The shared state publishes the failure before releasing ownership.
        }
    }

    private void OnInterruptStateChanged(object? sender, EventArgs e) =>
        this.ApplyInterruptState(this.interruptState!.Snapshot);

    private void ApplyInterruptState(AgentChatInterruptSnapshot snapshot)
    {
        this.IsInterruptPending = snapshot.IsPending;
        this.LastOperationError = snapshot.Outcome switch
        {
            AgentChatInterruptOutcome.Failed => "Unable to interrupt agent.",
            _ => null,
        };
        this.UpdateCommandAvailability();
    }

    private async Task SetContinueInBackgroundAsync(
        bool value,
        Func<bool, CancellationToken, Task> setContinueInBackgroundAsync)
    {
        this.isBackgroundUpdatePending = true;
        this.IsBackgroundOptionEnabled = false;
        // A ToggleButton changes its visual state before ICommand executes. Reassert the
        // authoritative source value immediately so a pending/rejected request never appears
        // checked optimistically.
        this.RaisePropertyChanged(nameof(this.ContinueInBackground));
        this.LastOperationError = null;
        try
        {
            await setContinueInBackgroundAsync(value, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            this.LastOperationError = "Unable to update agent session.";
        }
        finally
        {
            this.isBackgroundUpdatePending = false;
            this.UpdateCommandAvailability();
            this.RaisePropertyChanged(nameof(this.ContinueInBackground));
        }
    }

    private void UpdateCommandAvailability()
    {
        this.IsInterruptEnabled =
            !this.IsInterruptPending
            && !this.IsTerminationPending
            && this.IsThinking
            && this.isConnected
            && !this.isTerminal;
        this.IsTerminateEnabled =
            !this.IsTerminationPending && this.isConnected && !this.isTerminal;
        this.IsBackgroundOptionEnabled =
            !this.IsTerminationPending
            &&
            !this.isBackgroundUpdatePending
            && this.canSetContinueInBackground
            && this.isConnected
            && !this.isTerminal;
    }

    /// <summary>Navigates to the agent tab or opens a new one when clicked.</summary>
    public ICommand ActivateCommand { get; }
    public ICommand InterruptCommand { get; }
    public ICommand TerminateCommand { get; }
    public ICommand SetContinueInBackgroundCommand { get; }

    public void Dispose()
    {
        if (this.interruptState is not null)
        {
            this.interruptState.StateChanged -= this.OnInterruptStateChanged;
        }
    }
}
