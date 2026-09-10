using System.ComponentModel;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Enriches a <see cref="RunningAgentChat"/> from <c>Llm.Core</c> with workspace entity display
/// information for presentation in the running-agent brain popup.
/// </summary>
public sealed class RunningAgentChatWithEntityInfo : INotifyPropertyChanged
{
    private readonly RunningAgentChat? _chat;
    private readonly AgentSessionId _sessionId;
    private readonly bool _isSubAgent;
    private readonly Func<CancellationToken, Task<RunningAgentChatLease>>? acquireLease;
    private bool _continueInBackground;
    private int _viewerCount = 1;
    private bool _isRemote;

    /// <summary>The agent session identifier.</summary>
    public AgentSessionId SessionId => _chat?.SessionId ?? _sessionId;

    /// <summary>
    /// <see langword="true"/> when the underlying <see cref="RunningAgentChat"/> is a sub-agent.
    /// The running-agent brain popup filters these out (issue #1205) as a belt-and-braces
    /// safeguard against sub-agents leaking into the top-level session list.
    /// </summary>
    public bool IsSubAgent => _chat?.IsSubAgent ?? _isSubAgent;

    /// <summary>The display name of the agent entity that owns this session.</summary>
    public string EntityName { get; }

    /// <summary>
    /// The entity store ID (UUID) of the agent-session entity, if available.
    /// Used to navigate to the entity when no tab is open for this session.
    /// </summary>
    public string? EntityId { get; }

    /// <summary>
    /// The owning workspace-pane id (the pane the session was started/opened in), if known.
    /// Used by cross-workspace status-button navigation (#1135) to switch to (and load) the
    /// owning workspace before focusing the agent, so a click on the brain popup never routes
    /// the agent into the currently-active pane by mistake.
    /// </summary>
    public string? WorkspaceId { get; }

    /// <summary>
    /// <see langword="true"/> when the running chat is a remote proxy (issue #1485). Commit 2
    /// wires the property; the remote transport factory is added in a later commit.
    /// </summary>
    public bool IsRemote
    {
        get => _isRemote;
        init => _isRemote = value;
    }

    /// <summary>
    /// <see langword="true"/> when the running chat should continue after the last viewer detaches
    /// (issue #1485). Owner/client-authoritative; raises <see cref="INotifyPropertyChanged"/> when set.
    /// </summary>
    public bool ContinueInBackground
    {
        get => _continueInBackground;
        init => _continueInBackground = value;
    }

    /// <summary>
    /// Live viewer count (issue #1485). Owner-authoritative; owners bump this on attach/detach.
    /// </summary>
    public int ViewerCount
    {
        get => _viewerCount;
        init => _viewerCount = value;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    internal RunningAgentChatWithEntityInfo(RunningAgentChat chat, string entityName, string? entityId, string? workspaceId = null)
    {
        _chat = chat;
        _sessionId = chat.SessionId;
        _isSubAgent = chat.IsSubAgent;
        EntityName = entityName;
        EntityId = entityId;
        WorkspaceId = workspaceId;
    }

    internal RunningAgentChatWithEntityInfo(
        AgentSessionId sessionId,
        bool isSubAgent,
        Func<CancellationToken, Task<RunningAgentChatLease>> acquireLease,
        string entityName,
        string? entityId,
        string? workspaceId = null)
    {
        _sessionId = sessionId;
        _isSubAgent = isSubAgent;
        this.acquireLease = acquireLease ?? throw new ArgumentNullException(nameof(acquireLease));
        EntityName = entityName;
        EntityId = entityId;
        WorkspaceId = workspaceId;
    }

    /// <summary>
    /// Acquires a new ref-counted lease on this session's <see cref="AgentChat"/>.
    /// Delegates to the underlying <see cref="RunningAgentChat.AcquireLeaseAsync"/>.
    /// Dispose the lease when done.
    /// </summary>
    public async Task<RunningAgentChatLease> AcquireLeaseAsync(CancellationToken ct = default)
    {
        if (this.acquireLease is not null)
        {
            var delegatedLease = await this.acquireLease(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                return delegatedLease;
            }
            catch
            {
                await delegatedLease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var lease = await _chat!.AcquireLeaseAsync(ct).ConfigureAwait(false);
        var viewerCountIncremented = false;
        var ownershipTransferred = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            viewerCountIncremented = true;
            this.IncrementViewerCount();
            var result = new RunningAgentChatLease(
                lease.SessionId,
                lease.AgentChat,
                onDispose: lease.DisposeAsync,
                afterDispose: () =>
                {
                    this.DecrementViewerCount();
                    return ValueTask.CompletedTask;
                });
            ownershipTransferred = true;
            return result;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                try
                {
                    if (viewerCountIncremented)
                    {
                        this.DecrementViewerCount();
                    }
                }
                finally
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    internal void SetContinueInBackground(bool value)
    {
        if (_continueInBackground == value)
        {
            return;
        }
        _continueInBackground = value;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContinueInBackground)));
    }

    internal void SetViewerCount(int value)
    {
        if (_viewerCount == value)
        {
            return;
        }
        _viewerCount = value;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViewerCount)));
    }

    /// <summary>
    /// #1485: assigns the remote-proxy flag after construction. Raises
    /// <see cref="PropertyChanged"/> when the value changes, so retention-metadata observers
    /// (e.g. the brain popup) update authoritatively.
    /// </summary>
    internal void SetIsRemote(bool value)
    {
        if (_isRemote == value)
        {
            return;
        }
        _isRemote = value;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRemote)));
    }

    /// <summary>
    /// #1485: bump the viewer count. Used by <c>RunningAgentChatTable</c> on lease
    /// acquisition/release so viewer counts reflect actual attach state.
    /// </summary>
    internal void IncrementViewerCount() => this.SetViewerCount(_viewerCount + 1);

    /// <summary>
    /// #1485: decrement the viewer count (clamped at zero). Used when a lease is released.
    /// When the last viewer detaches and <see cref="ContinueInBackground"/> is false, the owner's
    /// final-lease teardown proceeds; when true, the running chat survives with zero viewers.
    /// </summary>
    internal void DecrementViewerCount() => this.SetViewerCount(Math.Max(0, _viewerCount - 1));
}
