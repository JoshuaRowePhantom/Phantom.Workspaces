using System.Text;
using System.Text.Json;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.Shell;
using Phantom.Workspaces.Transport.Tests.Infrastructure;

namespace Phantom.Workspaces.Transport.Tests.Scenarios;

/// <summary>
/// Issue #1126 — end-to-end coverage over a real <see cref="HttpTransport"/>
/// paired against a real <see cref="ServerHttpTransport"/> in-process via
/// <see cref="PairedWebSocket"/>. No fakes for framing; both sides use the
/// production dispatch loops.
/// </summary>
public class HttpShellOverTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task HttpTransport_StreamDuplex_ClientAndServerExchangeBytes()
    {
        await using var pair = await PairedHttpTransports.CreateAsync(new EchoStreamListener());

        await using var stream = await pair.Client.ConnectToStreamAsync(
            JsonElement("{\"type\":\"echo-stream\"}"));

        var payload = Encoding.UTF8.GetBytes("ping-from-client");
        await stream.WriteAsync(payload.AsMemory()).AsTask().WaitAsync(TestTimeout);

        var received = await ReadExactAsync(stream, payload.Length);

        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task ShellOverHttpTransport_ShellSession_StreamsProcessOutputToClient()
    {
        await using var pair = await PairedHttpTransports.CreateAsync(new ShellTransportListener());

        await using var shell = new ShellOverTransport(pair.Client, ShellEchoRequest("http-shell-hello"));
        await shell.OpenAsync(CancellationToken.None);

        var stdout = await ReadUntilAsync(shell.Stream, "http-shell-hello");

        Assert.Contains("http-shell-hello", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellOverHttpTransport_ShellSession_ClientWriteReachesShellStdin()
    {
        await using var pair = await PairedHttpTransports.CreateAsync(new ShellTransportListener());

        var command = OperatingSystem.IsWindows()
            ? JsonElement("{\"type\":\"shell\",\"command\":\"cmd\",\"args\":[\"/q\",\"/k\",\"prompt $g \"]}")
            : JsonElement("{\"type\":\"shell\",\"command\":\"/bin/cat\"}");

        await using var shell = new ShellOverTransport(pair.Client, command);
        await shell.OpenAsync(CancellationToken.None);

        var line = Encoding.UTF8.GetBytes(OperatingSystem.IsWindows()
            ? "echo stdin-marker-1126\r\nexit\r\n"
            : "stdin-marker-1126\n");
        await shell.Stream.WriteAsync(line.AsMemory()).AsTask().WaitAsync(TestTimeout);

        var stdout = await ReadUntilAsync(shell.Stream, "stdin-marker-1126");

        Assert.Contains("stdin-marker-1126", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellOverHttpTransport_ShellExit_ClosesClientStream()
    {
        await using var pair = await PairedHttpTransports.CreateAsync(new ShellTransportListener());

        await using var shell = new ShellOverTransport(pair.Client, ShellEchoRequest("shell-exit-marker"));
        await shell.OpenAsync(CancellationToken.None);

        // Drain to EOF — the server-side ShellSession completes the transport stream
        // when the child process exits, which triggers a StreamClose on the wire.
        var accumulated = new StringBuilder();
        var buffer = new byte[512];
        using var cts = new CancellationTokenSource(TestTimeout);
        while (true)
        {
            var read = await shell.Stream.ReadAsync(buffer.AsMemory(), cts.Token);
            if (read == 0)
            {
                break;
            }

            accumulated.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        Assert.Contains("shell-exit-marker", accumulated.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellOverHttpTransport_Headless_NoVisualDependencies()
    {
        // This is the same round-trip as StreamsProcessOutputToClient but pinned as
        // a headless / no-visual assertion: the entire path runs without any Avalonia
        // or UI dependency and only touches Transport, Http, Shell types.
        await using var pair = await PairedHttpTransports.CreateAsync(new ShellTransportListener());

        await using var shell = new ShellOverTransport(pair.Client, ShellEchoRequest("headless-http-shell"));
        await shell.OpenAsync(CancellationToken.None);

        var stdout = await ReadUntilAsync(shell.Stream, "headless-http-shell");

        Assert.Contains("headless-http-shell", stdout, StringComparison.Ordinal);
    }

    private static JsonElement JsonElement(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement ShellEchoRequest(string payload)
    {
        var json = OperatingSystem.IsWindows()
            ? $$"""{"type":"shell","command":"cmd","args":["/c","echo","{{payload}}"]}"""
            : $$"""{"type":"shell","command":"/bin/sh","args":["-c","echo {{payload}}"]}""";
        return JsonElement(json);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        using var cts = new CancellationTokenSource(TestTimeout);
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cts.Token);
            if (read == 0)
            {
                throw new IOException($"Stream closed after {offset}/{count} bytes.");
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<string> ReadUntilAsync(Stream stream, string marker)
    {
        var buffer = new byte[512];
        var accumulated = new StringBuilder();
        using var cts = new CancellationTokenSource(TestTimeout);
        while (!cts.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(), cts.Token);
            }
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            accumulated.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (accumulated.ToString().Contains(marker, StringComparison.Ordinal))
            {
                return accumulated.ToString();
            }
        }

        return accumulated.ToString();
    }

    private sealed class EchoStreamListener : ITransportListener
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        {
            var echo = new EchoLease(stream);
            return Task.FromResult<IAsyncDisposable?>(echo);
        }

        private sealed class EchoLease : IAsyncDisposable
        {
            private readonly Stream stream;
            private readonly CancellationTokenSource cts = new();
            private readonly Task pump;

            public EchoLease(Stream stream)
            {
                this.stream = stream;
                this.pump = Task.Run(this.PumpAsync);
            }

            private async Task PumpAsync()
            {
                var buffer = new byte[1024];
                try
                {
                    while (!this.cts.IsCancellationRequested)
                    {
                        var read = await this.stream.ReadAsync(buffer.AsMemory(), this.cts.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            return;
                        }

                        await this.stream.WriteAsync(buffer.AsMemory(0, read), this.cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            public async ValueTask DisposeAsync()
            {
                await this.cts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await this.pump.ConfigureAwait(false);
                }
                catch
                {
                }

                this.cts.Dispose();
                await this.stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class PairedHttpTransports : IAsyncDisposable
    {
        private readonly PairedWebSocket clientSocket;
        private readonly PairedWebSocket serverSocket;
        private readonly ServerHttpTransport server;
        private readonly Task serverRun;

        private PairedHttpTransports(
            PairedWebSocket clientSocket,
            PairedWebSocket serverSocket,
            HttpTransport client,
            ServerHttpTransport server,
            Task serverRun)
        {
            this.clientSocket = clientSocket;
            this.serverSocket = serverSocket;
            this.Client = client;
            this.server = server;
            this.serverRun = serverRun;
        }

        public HttpTransport Client { get; }

        public static Task<PairedHttpTransports> CreateAsync(ITransportListener listener)
        {
            var (clientSocket, serverSocket) = PairedWebSocket.CreatePair();
            var registry = new TransportRegistry();
            registry.Register(listener);
            var server = new ServerHttpTransport(serverSocket, registry, TimeSpan.FromHours(1));
            var serverRun = Task.Run(() => server.RunAsync(CancellationToken.None));
            var client = new HttpTransport(clientSocket, TimeSpan.FromHours(1));
            return Task.FromResult(new PairedHttpTransports(clientSocket, serverSocket, client, server, serverRun));
        }

        public async ValueTask DisposeAsync()
        {
            await this.Client.DisposeAsync().ConfigureAwait(false);
            await this.server.DisposeAsync().ConfigureAwait(false);
            try
            {
                await this.serverRun.WaitAsync(TestTimeout).ConfigureAwait(false);
            }
            catch
            {
            }

            this.clientSocket.Dispose();
            this.serverSocket.Dispose();
        }
    }
}
