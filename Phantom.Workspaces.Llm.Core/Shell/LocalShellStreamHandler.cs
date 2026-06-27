using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Handles a <c>"shell"</c> stream session locally: deserialises <see cref="ShellOpenPayload"/>,
/// spawns an <see cref="IPseudoTerminal"/> (PTY or pipe mode), and bridges it to the
/// <see cref="IStreamMessageChannel"/> transport. Out-of-band resize control frames are forwarded
/// to <see cref="IPseudoTerminal.ResizeAsync"/>; an exit control frame is sent when the process
/// exits before the channel is closed.
/// </summary>
internal sealed class LocalShellStreamHandler : ILocalStreamHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<ShellOpenPayload, IPseudoTerminal>? _ptyFactory;

    /// <summary>Production constructor: uses <see cref="ConPtyPseudoTerminal"/> on Windows, pipe mode on other platforms.</summary>
    public LocalShellStreamHandler() { }

    /// <summary>Test constructor: injects a custom PTY factory.</summary>
    internal LocalShellStreamHandler(Func<ShellOpenPayload, IPseudoTerminal> ptyFactory)
    {
        _ptyFactory = ptyFactory;
    }

    private IPseudoTerminal CreatePty(ShellOpenPayload payload)
    {
        if (_ptyFactory is not null)
            return _ptyFactory(payload);

        if (string.Equals(payload.Mode, "pipe", StringComparison.OrdinalIgnoreCase))
            return new PipeModeTerminal(payload);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new ConPtyPseudoTerminal(payload);

        throw new PlatformNotSupportedException(
            "PTY mode ('pty') is currently only supported on Windows. Use mode 'pipe' on other platforms.");
    }

    /// <inheritdoc />
    public async Task HandleAsync(JsonElement openPayload, IStreamMessageChannel hostEnd, CancellationToken ct)
    {
        var payload = openPayload.Deserialize<ShellOpenPayload>(JsonOptions)
            ?? throw new InvalidOperationException("Shell open payload deserialised to null.");

        await using var pty = CreatePty(payload);

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var outputTask = PumpOutputAsync(pty.Output, hostEnd, pumpCts.Token);
        var inputTask = PumpInputAsync(pty.Input, pty, hostEnd, pumpCts.Token);

        int exitCode;
        try
        {
            exitCode = await pty.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            exitCode = -1;
        }

        await pumpCts.CancelAsync().ConfigureAwait(false);

        try { await outputTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        try { await inputTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        if (!ct.IsCancellationRequested)
        {
            await hostEnd.SendAsync(
                new StreamFrame(
                    StreamFrameKind.Control,
                    new StreamControlMessage
                    {
                        Type = StreamControlMessage.Types.Exit,
                        ExitCode = exitCode,
                    }.ToPayload()),
                CancellationToken.None).ConfigureAwait(false);
        }

        await hostEnd.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task PumpOutputAsync(Stream output, IStreamMessageChannel hostEnd, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (true)
        {
            int read;
            try
            {
                read = await output.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
                break;

            try
            {
                await hostEnd.SendAsync(
                    new StreamFrame(StreamFrameKind.Data, buffer.AsMemory(0, read)),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task PumpInputAsync(
        Stream ptyInput, IPseudoTerminal pty, IStreamMessageChannel hostEnd, CancellationToken ct)
    {
        while (true)
        {
            StreamFrame? frame;
            try
            {
                frame = await hostEnd.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (frame is null)
                break;

            if (frame.Kind == StreamFrameKind.Data)
            {
                if (!frame.Payload.IsEmpty)
                {
                    try
                    {
                        await ptyInput.WriteAsync(frame.Payload, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            else if (frame.Kind == StreamFrameKind.Control)
            {
                var control = StreamControlMessage.FromPayload(frame.Payload);
                if (string.Equals(control.Type, StreamControlMessage.Types.Resize, StringComparison.Ordinal)
                    && control.Columns.HasValue && control.Rows.HasValue)
                {
                    try
                    {
                        await pty.ResizeAsync(control.Columns.Value, control.Rows.Value, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
    }

    // ── Pipe-mode terminal ───────────────────────────────────────────────────

    /// <summary>
    /// An <see cref="IPseudoTerminal"/> that bridges to a <see cref="Process"/> with redirected
    /// stdin/stdout/stderr. <see cref="ResizeAsync"/> is a no-op.
    /// </summary>
    private sealed class PipeModeTerminal : IPseudoTerminal
    {
        private readonly Process _process;

        public PipeModeTerminal(ShellOpenPayload payload)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = payload.Command,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
                WorkingDirectory = payload.WorkingDirectory ?? string.Empty,
            };

            foreach (var arg in payload.CommandArguments)
                startInfo.ArgumentList.Add(arg);

            if (payload.Environment is not null)
            {
                foreach (var (key, value) in payload.Environment)
                    startInfo.Environment[key] = value;
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Start();

            Output = _process.StandardOutput.BaseStream;
            Input = _process.StandardInput.BaseStream;
        }

        public Stream Output { get; }
        public Stream Input { get; }

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public Task<int> WaitForExitAsync(CancellationToken ct = default)
            => _process.WaitForExitAsync(ct).ContinueWith(
                _ => _process.ExitCode, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }

            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
