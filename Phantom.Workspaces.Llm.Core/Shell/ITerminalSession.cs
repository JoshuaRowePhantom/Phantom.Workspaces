using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.Shell;

/// <summary>
/// A live streamed-process ("shell") session. The terminal byte data flows through
/// <see cref="Stream"/> (read = process/PTY output, write = stdin/keyboard input); window-size and
/// signals are control, not data, and so are exposed as methods rather than written into the byte
/// stream. The terminal control consumes only <see cref="Stream"/> plus the resize delegate and never
/// sees frames (see <c>docs/design/shell-pty-terminal.md</c>).
/// </summary>
public interface ITerminalSession : IAsyncDisposable
{
    /// <summary>The duplex terminal byte stream (read = host output, write = client input).</summary>
    Stream Stream { get; }

    /// <summary>Requests a terminal resize (pty mode only; a no-op carrier in pipe mode).</summary>
    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken);

    /// <summary>Delivers a named signal (e.g. SIGINT) to the process.</summary>
    ValueTask SignalAsync(string signal, CancellationToken cancellationToken);

    /// <summary>Completes with the process exit code once the host reports it.</summary>
    Task<int> WaitForExitAsync();
}
