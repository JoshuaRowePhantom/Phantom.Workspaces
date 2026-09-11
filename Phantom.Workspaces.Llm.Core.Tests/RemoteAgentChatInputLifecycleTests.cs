using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed partial class RemoteAgentChatTests
{
    [Fact]
    public async Task InputQueues_ImmediateSendFaultWithConcurrentInput_DisposalDrainsAndObservesOperations()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs args)
        {
            lock (unobserved)
                unobserved.AddRange(args.Exception.Flatten().InnerExceptions);
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await RunImmediateSendFaultAndAbandonConcurrentInputAsync();
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (unobserved)
            Assert.DoesNotContain(
                unobserved,
                exception => exception is RemoteAgentProtocolException
                    && exception.StackTrace?.Contains("RemoteInputQueues", StringComparison.Ordinal) is true);
    }

    [Fact]
    public async Task InputQueues_TerminalChannelClose_PropagatesPrimaryOperationFailure()
    {
        var (transport, chat) = await AttachLifecycleChatAsync();
        await using (chat)
        {
            var operation = chat.InputQueues.EnqueueAsync(Request());
            _ = await transport.ReadClientWriteAsync();
            await transport.SendServerAsync(Frame(2, new SessionTerminalEvent
            {
                Reason = "owner-stopped",
                CompletionState = JsonSerializer.SerializeToElement(new
                {
                    state = "failed",
                    code = "owner-stopped",
                    operation = "enqueue-input",
                    message = "The owner stopped.",
                }),
            }));
            transport.CompleteServer();

            var failure = await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => operation);
            Assert.Equal("The remote session channel closed.", failure.Message);
        }
    }

    [Fact]
    public async Task InputQueues_CallerCancellationThenLateCompletion_IsOwnedThroughDisposal()
    {
        var (transport, chat) = await AttachLifecycleChatAsync();
        using var cancellation = new CancellationTokenSource();
        var operation = chat.InputQueues.EnqueueAsync(Request(), cancellation.Token);
        var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.ReadClientWriteAsync());
        cancellation.Cancel();

        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);

        await transport.SendServerAsync(Frame(2, new CommandCompletedEvent
        {
            CommandId = command.CommandId,
            Result = JsonSerializer.SerializeToElement(new AgentInputQueueCommandResult
            {
                CommandId = command.CommandId,
                Status = AgentInputQueueCommandStatus.Applied,
                Revision = 2,
            }, AgentSessionProtocolCodec.Options),
        }, command.CorrelationId));

        await chat.DisposeAsync();
        Assert.True(transport.ChannelDisposed);
    }

    [Fact]
    public async Task DisposeAsync_PendingQueueInput_ClosesChannelBeforeReturningAndDrainsOperation()
    {
        var (transport, chat) = await AttachLifecycleChatAsync();
        var operation = chat.InputQueues.EnqueueAsync(Request());
        _ = await transport.ReadClientWriteAsync();

        await chat.DisposeAsync();

        Assert.True(transport.ChannelDisposed);
        Assert.True(operation.IsCompleted);
        await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => operation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunImmediateSendFaultAndAbandonConcurrentInputAsync()
    {
        var (transport, chat) = await AttachLifecycleChatAsync();
        var abandoned = chat.InputQueues.EnqueueAsync(Request());
        _ = await transport.ReadClientWriteAsync();

        var immediateFailure = new RemoteAgentProtocolException("Immediate remote input send failed.");
        transport.FailNextClientWrite(immediateFailure);
        var immediate = chat.InputQueues.EnqueueAsync(Request());
        Assert.Same(immediateFailure, await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => immediate));

        transport.CompleteServer();
        await chat.DisposeAsync();
        Assert.True(abandoned.IsCompleted);
    }

    private static EnqueueAgentInputRequest Request() => new()
    {
        TargetQueueId = "immediate",
        Messages = [new ChatMessage(ChatRole.User, "queued")],
        ExpectedRevision = 1,
        CommandId = Guid.NewGuid(),
    };

    private static async Task<(LifecycleTransport Transport, RemoteAgentChat Chat)> AttachLifecycleChatAsync()
    {
        var transport = new LifecycleTransport();
        var attaching = RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = new RemoteAgentSessionClient(transport),
            OpenRequest = AgentSessionProtocolCodecTests.Open(),
            ForegroundScheduler = TaskScheduler.Default,
        });
        await transport.SendServerAsync(Frame(1, new SessionSnapshotEvent
        {
            Snapshot = AgentSessionProtocolCodecTests.Snapshot(),
        }));
        return (transport, await attaching);
    }

    private sealed class LifecycleTransport : ITransport
    {
        private readonly LifecycleChannel channel = new();

        public bool ChannelDisposed => this.channel.Disposed;

        public Task<IMessageChannel> ConnectToMessageChannelAsync(
            JsonElement request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IMessageChannel>(this.channel);
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask SendServerAsync(JsonElement value) => this.channel.ServerWrites.Writer.WriteAsync(value);
        public ValueTask<JsonElement> ReadClientWriteAsync() => this.channel.ClientWrites.Reader.ReadAsync();
        public void CompleteServer() => this.channel.ServerWrites.Writer.TryComplete();
        public void FailNextClientWrite(Exception failure) => this.channel.Writer.FailNext(failure);
    }

    private sealed class LifecycleChannel : IMessageChannel
    {
        public Channel<JsonElement> ClientWrites { get; } = Channel.CreateUnbounded<JsonElement>();
        public Channel<JsonElement> ServerWrites { get; } = Channel.CreateUnbounded<JsonElement>();
        public FailingChannelWriter Writer { get; }
        ChannelWriter<JsonElement> IMessageChannel.Writer => this.Writer;
        public ChannelReader<JsonElement> Reader => this.ServerWrites.Reader;
        public bool Disposed { get; private set; }

        public LifecycleChannel()
        {
            this.Writer = new FailingChannelWriter(this.ClientWrites.Writer);
        }

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            this.ClientWrites.Writer.TryComplete();
            this.ServerWrites.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingChannelWriter(ChannelWriter<JsonElement> inner) : ChannelWriter<JsonElement>
    {
        private Exception? nextFailure;

        public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);
        public override bool TryWrite(JsonElement item) => inner.TryWrite(item);
        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            => inner.WaitToWriteAsync(cancellationToken);

        public override ValueTask WriteAsync(JsonElement item, CancellationToken cancellationToken = default)
        {
            var failure = Interlocked.Exchange(ref this.nextFailure, null);
            return failure is null
                ? inner.WriteAsync(item, cancellationToken)
                : ValueTask.FromException(failure);
        }

        public void FailNext(Exception failure)
            => this.nextFailure = failure ?? throw new ArgumentNullException(nameof(failure));
    }
}
