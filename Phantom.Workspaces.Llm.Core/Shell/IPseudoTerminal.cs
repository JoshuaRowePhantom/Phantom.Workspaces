using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.Shell;

/// <summary>
/// An OS-level pseudo-terminal (or process-pipe) session. The process is spawned when the
/// implementation is constructed; the caller drives I/O through <see cref="Output"/> and
/// <see cref="Input"/> and waits for the process to finish with <see cref="WaitForExitAsync"/>.
/// </summary>
internal interface IPseudoTerminal : IAsyncDisposable
{
    /// <summary>Reads output bytes from the PTY (process stdout/stderr combined).</summary>
    Stream Output { get; }

    /// <summary>Writes input bytes to the PTY (process stdin / keyboard).</summary>
    Stream Input { get; }

    /// <summary>Resize the terminal window. No-op in pipe mode.</summary>
    ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default);

    /// <summary>Wait for the process to exit and return its exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken ct = default);
}
