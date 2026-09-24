using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Phantom.Workspaces.Llm.Core.Transport.Chat;

internal sealed class RemoteCopilotLifecycleLog
{
    private readonly ILogger logger;
    private readonly string correlationId;
    private readonly string role;
    private volatile RemoteCopilotOperation operation;
    private readonly long startedTimestamp;
    private readonly TimeProvider timeProvider;
    private string lastConfirmedStage = "none";

    public RemoteCopilotLifecycleLog(
        ILoggerFactory? loggerFactory,
        string correlationId,
        string role,
        TimeProvider? timeProvider = null,
        RemoteCopilotOperation operation = RemoteCopilotOperation.Channel)
    {
        this.logger = loggerFactory?.CreateLogger<RemoteCopilotLifecycleLog>()
            ?? NullLogger<RemoteCopilotLifecycleLog>.Instance;
        this.correlationId = Guid.TryParseExact(correlationId, "N", out _)
            ? correlationId : Guid.NewGuid().ToString("N");
        this.role = role is "caller" or "worker" ? role : "unknown";
        this.operation = operation;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.startedTimestamp = this.timeProvider.GetTimestamp();
    }

    public string LastConfirmedStage => Volatile.Read(ref this.lastConfirmedStage);

    public void SetOperation(RemoteCopilotOperation operation) => this.operation = operation;

    public void Confirm(string stage, string outcome = "success")
    {
        Volatile.Write(ref this.lastConfirmedStage, stage);
        this.Write(stage, outcome, errorCategory: "none");
    }

    public void Fail(string stage, string errorCategory)
        => this.Write(stage, "failure", errorCategory);

    public void Cancel(string stage)
        => this.Write(stage, "cancelled", "cancelled");

    private void Write(string stage, string outcome, string errorCategory)
    {
        var elapsed = this.timeProvider.GetElapsedTime(this.startedTimestamp);
        var elapsedMilliseconds = Math.Clamp((long)elapsed.TotalMilliseconds, 0, int.MaxValue);
        this.logger.LogInformation(
            "Remote Copilot lifecycle {CorrelationId} {Role} {Stage} after {ElapsedMilliseconds}ms; operation {Operation}; outcome {Outcome}; last confirmed {LastConfirmedStage}; category {ErrorCategory}.",
            this.correlationId,
            this.role,
            stage,
            elapsedMilliseconds,
            this.operation.ToString().ToLowerInvariant(),
            outcome is "started" or "success" or "failure" or "cancelled" ? outcome : "other",
            this.LastConfirmedStage,
            SafeErrorCategory(errorCategory));
    }

    private static string SafeErrorCategory(string category) => category switch
    {
        "none" or "cancelled" or "timeout" or "transport" or "transport-cancelled"
            or "open-failed" or "invalid-envelope" or "launch-denied"
            or "provider-unavailable" or "sdk-create" or "sdk-operation"
            or "remote-error" or "tool-failed" or "other" => category,
        _ => "other",
    };
}

internal enum RemoteCopilotOperation
{
    Channel,
    Create,
    Resume,
}
