using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.Shell;

/// <summary>
/// A fake <see cref="IPseudoTerminal"/> for unit tests. Bytes written to <see cref="Input"/>
/// are echoed back on <see cref="Output"/> via an in-memory pipe. Exit timing is controlled by
/// the caller via a <see cref="TaskCompletionSource{T}"/>; <see cref="ResizeAsync"/> records the
/// last dimensions for assertion.
/// </summary>
internal sealed class FakePseudoTerminal : IPseudoTerminal
{
    private readonly TaskCompletionSource<int> _exitTcs;

    public FakePseudoTerminal(TaskCompletionSource<int> exitTcs)
    {
        _exitTcs = exitTcs;
        var pipe = new Pipe();
        Input = pipe.Writer.AsStream();
        Output = pipe.Reader.AsStream();
    }

    public Stream Output { get; }
    public Stream Input { get; }

    public (int Columns, int Rows) LastResize { get; private set; }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        LastResize = (columns, rows);
        return ValueTask.CompletedTask;
    }

    public Task<int> WaitForExitAsync(CancellationToken ct = default) => _exitTcs.Task;

    public ValueTask DisposeAsync()
    {
        _exitTcs.TrySetCanceled();
        return ValueTask.CompletedTask;
    }
}
