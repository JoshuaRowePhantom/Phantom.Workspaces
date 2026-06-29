using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Gui.Shared.ViewModels;

/// <summary>
/// Binds the terminal byte stream and resize delegate to a terminal control. Holds the duplex
/// <see cref="Stream"/> (read = host output, write = keyboard input) and a resize callback
/// that the control calls when its pixel dimensions change. Has no dependency on
/// <c>Phantom.Workspaces.Llm.Core</c> or any trust/frame type so it can live in the shared GUI
/// assembly and serve both the workspace shell tab and the agent editor.
/// </summary>
public sealed class TerminalSessionViewModel
{
    private readonly TaskCompletionSource _exitedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The duplex terminal byte stream (read = host output, write = keyboard input).</summary>
    public required Stream Stream { get; init; }

    /// <summary>
    /// Delegate the terminal control calls when its column/row dimensions change (pty mode only;
    /// a no-op is acceptable in pipe mode).
    /// </summary>
    public required Func<int, int, CancellationToken, ValueTask> ResizeCallback { get; init; }

    /// <summary>
    /// Task that completes when the terminal session exits (stream reaches end-of-file or is
    /// closed). Completed by <see cref="NotifyExited"/>.
    /// </summary>
    public Task WhenExited => _exitedTcs.Task;

    /// <summary>Whether the session has exited.</summary>
    public bool IsExited => _exitedTcs.Task.IsCompleted;

    /// <summary>
    /// Called by the terminal control when the read loop reaches end-of-stream.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    internal void NotifyExited() => _exitedTcs.TrySetResult();
}
