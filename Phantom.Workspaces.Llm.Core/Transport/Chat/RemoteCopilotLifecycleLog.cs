using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Phantom.Workspaces.Llm.Core.Transport.Chat;

internal sealed class RemoteCopilotLifecycleLog
{
    private readonly ILogger logger;
    private readonly string correlationId;
    private readonly string role;
    private readonly long startedTimestamp;
    private readonly TimeProvider timeProvider;
    private string lastConfirmedStage = "none";

    public RemoteCopilotLifecycleLog(
        ILoggerFactory? loggerFactory,
        string correlationId,
        string role,
        TimeProvider? timeProvider = null)
    {
        this.logger = loggerFactory?.CreateLogger<RemoteCopilotLifecycleLog>()
            ?? NullLogger<RemoteCopilotLifecycleLog>.Instance;
        this.correlationId = correlationId;
        this.role = role;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.startedTimestamp = this.timeProvider.GetTimestamp();
    }

    public string LastConfirmedStage => Volatile.Read(ref this.lastConfirmedStage);

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
            "Remote Copilot lifecycle {CorrelationId} {Role} {Stage} after {ElapsedMilliseconds}ms; outcome {Outcome}; last confirmed {LastConfirmedStage}; category {ErrorCategory}.",
            this.correlationId,
            this.role,
            stage,
            elapsedMilliseconds,
            outcome,
            this.LastConfirmedStage,
            errorCategory);
    }
}
