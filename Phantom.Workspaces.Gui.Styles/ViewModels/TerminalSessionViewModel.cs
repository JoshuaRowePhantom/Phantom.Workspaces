using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Gui.Styles.ViewModels;

/// <summary>
/// Binds the terminal byte stream and resize delegate to a terminal control. Holds the duplex
/// <see cref="Stream"/> (read = host output, write = keyboard input) and a resize callback
/// that the control calls when its pixel dimensions change. Has no dependency on
/// <c>Phantom.Workspaces.Llm.Core</c> or any trust/frame type so it can live in the shared GUI
/// assembly and serve both the workspace shell tab and the agent editor.
/// </summary>
public sealed class TerminalSessionViewModel
{
    /// <summary>The duplex terminal byte stream (read = host output, write = keyboard input).</summary>
    public required Stream Stream { get; init; }

    /// <summary>
    /// Delegate the terminal control calls when its column/row dimensions change (pty mode only;
    /// a no-op is acceptable in pipe mode).
    /// </summary>
    public required Func<int, int, CancellationToken, ValueTask> ResizeCallback { get; init; }
}
