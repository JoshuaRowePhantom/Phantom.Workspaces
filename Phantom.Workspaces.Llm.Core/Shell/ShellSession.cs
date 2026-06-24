using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.Shell;

/// <summary>
/// Presents an <see cref="IStreamMessageChannel"/> as an <see cref="ITerminalSession"/>. The terminal
/// byte data is exposed through a <see cref="StreamMessageChannelStream"/>; the out-of-band control
/// (resize/signal/exit) multiplexed over the same channel is mapped onto the terminal methods and the
/// <see cref="ControlMessageReceived"/> event, with an exit control message completing
/// <see cref="WaitForExitAsync"/>. A channel close without a reported exit code cancels the exit task
/// rather than inventing a code.
/// </summary>
public sealed class ShellSession : ITerminalSession
{
    private readonly StreamMessageChannelStream stream;
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task exitLink;

    public ShellSession(IStreamMessageChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        this.stream = new StreamMessageChannelStream(channel, this.HandleControlAsync, ownsChannel: true);
        this.exitLink = this.LinkExitToCloseAsync();
    }

    /// <summary>Raised on the pump thread for every received control frame (resize/signal/exit).</summary>
    public event EventHandler<StreamControlMessage>? ControlMessageReceived;

    public Stream Stream => this.stream;

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
        => this.stream.SendControlAsync(
            new StreamControlMessage
            {
                Type = StreamControlMessage.Types.Resize,
                Columns = columns,
                Rows = rows,
            },
            cancellationToken);

    public ValueTask SignalAsync(string signal, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(signal);
        return this.stream.SendControlAsync(
            new StreamControlMessage
            {
                Type = StreamControlMessage.Types.Signal,
                Signal = signal,
            },
            cancellationToken);
    }

    public Task<int> WaitForExitAsync() => this.exit.Task;

    public async ValueTask DisposeAsync()
    {
        await this.stream.DisposeAsync().ConfigureAwait(false);
        await this.exitLink.ConfigureAwait(false);
    }

    private ValueTask HandleControlAsync(StreamControlMessage message)
    {
        this.ControlMessageReceived?.Invoke(this, message);
        if (string.Equals(message.Type, StreamControlMessage.Types.Exit, StringComparison.Ordinal))
        {
            this.exit.TrySetResult(message.ExitCode ?? 0);
        }

        return ValueTask.CompletedTask;
    }

    // Once the channel closes, complete WaitForExitAsync: with the reported code if an exit control
    // arrived, otherwise cancelled (an unobserved canceled task is benign).
    private async Task LinkExitToCloseAsync()
    {
        await this.stream.Completion.ConfigureAwait(false);
        this.exit.TrySetCanceled();
    }
}
