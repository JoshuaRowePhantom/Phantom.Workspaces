using System.Windows.Input;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Represents a single running-agent entry in the <see cref="RunningAgentBrainViewModel"/> popup.
/// </summary>
public sealed class RunningAgentRowViewModel : ViewModelBase
{
    private bool isThinking;

    public RunningAgentRowViewModel(
        string sessionKey,
        string workspacePaneTitle,
        string tabTitle,
        bool isThinking,
        ICommand activateCommand)
    {
        this.SessionKey = sessionKey;
        this.WorkspacePaneTitle = workspacePaneTitle;
        this.TabTitle = tabTitle;
        this.isThinking = isThinking;
        this.HasOpenTab = true;
        this.ActivateCommand = activateCommand;
    }

    public RunningAgentRowViewModel(
        string sessionKey,
        string entityName,
        ICommand activateCommand)
    {
        this.SessionKey = sessionKey;
        this.EntityName = entityName;
        this.WorkspacePaneTitle = string.Empty;
        this.TabTitle = string.Empty;
        this.HasOpenTab = false;
        this.ActivateCommand = activateCommand;
    }

    /// <summary>The session key, used for deduplication across workspace panes.</summary>
    public string SessionKey { get; }

    /// <summary>The workspace pane title (only meaningful when <see cref="HasOpenTab"/> is true).</summary>
    public string WorkspacePaneTitle { get; }

    /// <summary>The tab title (only meaningful when <see cref="HasOpenTab"/> is true).</summary>
    public string TabTitle { get; }

    /// <summary>True if the agent has an open tab; false when using the fallback label.</summary>
    public bool HasOpenTab { get; }

    /// <summary>The agent entity name, shown as a fallback when <see cref="HasOpenTab"/> is false.</summary>
    public string? EntityName { get; }

    /// <summary>Whether the agent is actively thinking (drives the pulsating brain icon).</summary>
    public bool IsThinking
    {
        get => this.isThinking;
        internal set => this.SetProperty(ref this.isThinking, value);
    }

    /// <summary>The time of the most recent history activity for this session, used for sorting.</summary>
    internal DateTime LastActivityAt { get; private set; } = DateTime.UtcNow;

    internal void UpdateLastActivityAt(DateTime at)
    {
        this.LastActivityAt = at;
    }

    /// <summary>Navigates to the agent tab or opens a new one when clicked.</summary>
    public ICommand ActivateCommand { get; }
}
