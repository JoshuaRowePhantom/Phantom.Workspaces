using GitHub.Copilot;
using System.Threading.Channels;

namespace Phantom.Workspaces.Llm;

internal sealed class CopilotSdkRootBatch(
    CopilotSdkRootEventQueue owner, ChannelReader<SessionEvent> reader, bool continuation)
    : IDisposable
{
    internal const string OptionName = "copilot.sdk.admitted-root-batch";
    private int consumed;
    internal CopilotSdkRootEventQueue Owner { get; } = owner;
    internal ChannelReader<SessionEvent> Reader { get; } = reader;
    internal bool IsContinuation { get; private set; } = continuation;
    internal bool WasTaken => Volatile.Read(ref this.consumed) != 0;
    internal Task? RequestSend { get; set; }
    internal bool HasAcceptedOutput => this.RequestSend is not null || this.IsContinuation || this.Reader.TryPeek(out _);
    internal CopilotSdkRootBatch AsContinuation()
    {
        this.IsContinuation = true;
        return this;
    }
    internal ChannelReader<SessionEvent> Take()
    {
        if (Interlocked.Exchange(ref this.consumed, 1) != 0)
            throw new InvalidOperationException("The admitted root batch was consumed more than once.");
        return this.Reader;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.consumed, 1) == 0)
            this.Owner.ReturnUnconsumed(this.Reader, this.IsContinuation);
    }
}

/// <summary>Transfers an accepted batch to persistence cleanup before the native turn is retired.</summary>
internal sealed class CopilotSdkOutputDrain
{
    internal const string OptionName = "copilot.sdk.accepted-output-drain";
    internal const string CancellationOptionName = "copilot.sdk.owning-run-cancellation";
    private readonly object gate = new();
    private Action? seal;
    private Action<Exception>? fail;
    private Exception? failure;
    private bool stopRequested;
    internal bool StopRequested { get { lock (this.gate) return this.stopRequested; } }
    internal bool HasOwner { get { lock (this.gate) return this.seal is not null; } }

    internal void Attach(Action sealBatch, Action<Exception>? failBatch = null)
    {
        bool stop;
        Exception? failed;
        lock (this.gate)
        {
            this.seal = sealBatch;
            this.fail = failBatch;
            stop = this.stopRequested;
            failed = this.failure;
        }
        if (failed is not null && failBatch is not null) failBatch(failed);
        else if (stop) sealBatch();
    }

    internal void RequestFailure(Exception exception)
    {
        Action<Exception>? failBatch;
        Action? sealBatch;
        lock (this.gate)
        {
            this.failure ??= exception;
            failBatch = this.fail;
            sealBatch = this.seal;
        }
        if (failBatch is not null) failBatch(exception);
        else sealBatch?.Invoke();
        lock (this.gate) this.stopRequested = true;
    }

    internal void RequestStop()
    {
        Action? sealBatch;
        lock (this.gate)
        {
            this.stopRequested = true;
            sealBatch = this.seal;
        }
        sealBatch?.Invoke();
    }
}

/// <summary>Session-owned receipt queue, including autonomous root turns between requests.</summary>
internal sealed class CopilotSdkRootEventQueue
{
    private readonly object gate = new();
    private readonly Queue<ChannelReader<SessionEvent>> pending = new();
    private Channel<SessionEvent>? receiving;
    private bool stopped;
    private bool directResponse;
    private bool sealedForDrain;
    private Exception? failure;
    private long receiptVersion;

    internal event Action? ContinuationAvailable;

    internal bool HasPending
    {
        get { lock (this.gate) return this.pending.Count > 0; }
    }
    internal bool IsSealed
    {
        get { lock (this.gate) return this.sealedForDrain; }
    }
    internal long ReceiptVersion
    {
        get { lock (this.gate) return this.receiptVersion; }
    }

    internal void Accept(SessionEvent value)
    {
        var notify = false;
        lock (this.gate)
        {
            if (this.stopped || this.sealedForDrain) return;
            if (this.directResponse)
            {
                if (string.IsNullOrEmpty(value.AgentId) && value is SessionIdleEvent or SessionErrorEvent)
                    this.directResponse = false;
                return;
            }
            if (this.receiving is null)
            {
                if (!StartsRootOutput(value)) return;
                this.receiving = CreateChannel();
                this.pending.Enqueue(this.receiving.Reader);
                notify = true;
            }
            if (this.receiving.Writer.TryWrite(value)) this.receiptVersion++;
            if (string.IsNullOrEmpty(value.AgentId) && value is SessionIdleEvent or SessionErrorEvent)
            {
                this.receiving.Writer.TryComplete();
                this.receiving = null;
            }
        }
        if (notify) this.ContinuationAvailable?.Invoke();
    }

    // SendAndWait has its own tool subscription and final-message consumer. Those events are
    // already returned by the direct API and must not also become streaming continuations.
    internal void BeginDirectResponse()
    {
        lock (this.gate)
        {
            this.ThrowIfFailed();
            if (this.stopped || this.receiving is not null || this.pending.Count > 0)
                throw new InvalidOperationException("Pending root output must be consumed before a direct response.");
            this.directResponse = true;
        }
    }

    internal void EndDirectResponse()
    {
        lock (this.gate) this.directResponse = false;
    }

    internal Task<T> StartDirectResponse<T>(Func<Task<T>> send)
    {
        lock (this.gate)
        {
            this.ThrowIfFailed();
            if (this.stopped || this.sealedForDrain || !this.directResponse)
                throw new OperationCanceledException("Direct Copilot admission has stopped.");
            return send();
        }
    }

    internal ChannelReader<SessionEvent> Acquire(bool continuation) => this.Acquire(continuation, out _);

    internal ChannelReader<SessionEvent> Acquire(bool continuation, out bool acquiredContinuation)
    {
        lock (this.gate)
        {
            if (this.stopped) throw new InvalidOperationException("Copilot root event receipt has stopped.");
            acquiredContinuation = this.pending.TryDequeue(out var pendingTurn);
            if (acquiredContinuation) return pendingTurn!;
            this.ThrowIfFailed();
            var channel = CreateChannel();
            if (continuation) channel.Writer.Complete();
            else this.receiving = channel;
            return channel.Reader;
        }
    }

    internal void Release(ChannelReader<SessionEvent> reader)
    {
        lock (this.gate)
        {
            if (this.receiving?.Reader == reader)
            {
                this.receiving.Writer.TryComplete();
                this.receiving = null;
            }
            // An empty failed reservation may be released without advancing its reader.
            if (reader.Completion.IsFaulted) _ = reader.Completion.Exception;
        }
    }

    internal void ReturnUnconsumed(ChannelReader<SessionEvent> reader, bool continuation)
    {
        var notify = false;
        lock (this.gate)
        {
            if (continuation || reader.TryPeek(out _))
            {
                var later = this.pending.ToArray();
                this.pending.Clear();
                this.pending.Enqueue(reader);
                foreach (var batch in later) this.pending.Enqueue(batch);
                notify = true;
            }
            else if (ReferenceEquals(this.receiving?.Reader, reader))
            {
                this.receiving.Writer.TryComplete();
                this.receiving = null;
            }
            if (reader.Completion.IsFaulted) _ = reader.Completion.Exception;
        }
        if (notify) this.ContinuationAvailable?.Invoke();
    }

    internal void SealAccepted()
    {
        lock (this.gate)
        {
            this.sealedForDrain = true;
            this.receiving?.Writer.TryComplete();
            this.receiving = null;
        }
    }

    internal void Fail(Exception exception)
    {
        lock (this.gate)
        {
            this.RecordFailure(exception);
            this.sealedForDrain = true;
            this.receiving?.Writer.TryComplete(this.failure);
            this.receiving = null;
        }
    }

    // Completing routed output is ordered with failure publication, after buffered receipts
    // have had their opportunity to route. A slow persistence consumer does not own this fence.
    internal void CompleteDispatch(Action<Exception?> complete, Exception? exception = null)
    {
        lock (this.gate)
        {
            if (exception is not null) this.Fail(exception);
            complete(this.failure);
        }
    }

    private void RecordFailure(Exception exception)
    {
        if (this.failure is null) this.failure = exception;
        else if (exception is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) this.RecordFailure(inner);
        else if (exception.InnerException is { } cause && Contains(exception, this.failure))
            this.RecordFailure(cause);
        else if (!Contains(this.failure, exception))
            this.failure = new AggregateException("Copilot root output failed.", this.failure, exception);

        static bool Contains(Exception outer, Exception inner) => ReferenceEquals(outer, inner)
            || (outer is AggregateException aggregate
                ? aggregate.InnerExceptions.Any(value => Contains(value, inner))
                : outer.InnerException is { } cause && Contains(cause, inner));
    }

    private void ThrowIfFailed()
    {
        if (this.failure is { } failed)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed).Throw();
    }

    // The native invocation starts under the receipt gate, without awaiting under the lock.
    // Before this transition output belongs to the preceding request; afterwards the reserved
    // reader owns the new request. No prompt may be published before this decision.
    internal bool TryStartRequest(ChannelReader<SessionEvent> reader, Func<Task> send, out Task? sending, Action? verifyAdmission = null)
    {
        lock (this.gate)
        {
            sending = null;
            if (reader.TryPeek(out _) || this.pending.Count > 0) return false;
            this.ThrowIfFailed();
            if (this.stopped || this.sealedForDrain)
                throw new OperationCanceledException("Fresh Copilot admission has stopped.");
            verifyAdmission?.Invoke();
            try { sending = send(); }
            catch (Exception failure) { sending = Task.FromException(failure); }
            return true;
        }
    }

    internal bool IsReceiving(ChannelReader<SessionEvent> reader)
    {
        lock (this.gate)
            return this.failure is null && !this.stopped && !this.sealedForDrain && ReferenceEquals(this.receiving?.Reader, reader);
    }

    internal Task StartSteering(ChannelReader<SessionEvent> reader, Func<bool> admissionOpen, Func<Task> send)
    {
        lock (this.gate)
        {
            this.ThrowIfFailed();
            if (!this.IsReceiving(reader) || !admissionOpen())
                throw new OperationCanceledException("The owning Copilot turn no longer accepts steering.");
            // Native start, terminal receipt and sealing share this gate; never await here.
            return send();
        }
    }

    internal void Stop()
    {
        lock (this.gate)
        {
            if (this.pending.Count > 0)
                throw new InvalidOperationException("Accepted Copilot root continuations must be consumed before session retirement.");
            this.stopped = true;
            this.receiving?.Writer.TryComplete(new OperationCanceledException("Copilot session receipt stopped."));
            this.receiving = null;
        }
    }

    private static Channel<SessionEvent> CreateChannel() =>
        Channel.CreateUnbounded<SessionEvent>(new() { SingleReader = true, SingleWriter = false });

    private static bool StartsRootOutput(SessionEvent value) =>
        string.IsNullOrEmpty(value.AgentId)
        && value is AssistantTurnStartEvent or AssistantMessageDeltaEvent or AssistantMessageEvent
            or AssistantReasoningDeltaEvent or ToolExecutionStartEvent or ToolExecutionCompleteEvent;
}
