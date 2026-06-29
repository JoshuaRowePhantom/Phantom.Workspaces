using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// A single framed message on the reverse-execution duplex channel (see
/// <c>docs/design/reverse-tunnel-trust-execution.md</c>). A <see cref="Type"/> discriminator selects
/// which optional fields are present.
/// </summary>
public sealed record ReverseFrame
{
    /// <summary>register (C→S), execute (S→C), update (C→S), complete (C→S), or cancel (either way).</summary>
    public required string Type { get; init; }

    /// <summary>register: the claimed client instance id (a user-computer-profile entity id).</summary>
    public string? ClientInstanceId { get; init; }

    /// <summary>
    /// register: the absolute base URL of C's own Phantom.Workspaces HTTP endpoint (optional). When
    /// present, S can create a <c>RemoteTrustedExecutor</c> targeting C's endpoint so S can route
    /// agent execution directly to C over the forward HTTP path without the reverse WebSocket.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>register: the agent definition names C will accept (optional allow-list).</summary>
    public IReadOnlyList<string>? AcceptedAgentDefinitionNames { get; init; }

    /// <summary>execute/update/complete/cancel: correlates a turn across frames.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>execute: the agent request to run on C.</summary>
    public RemoteAgentRequest? Request { get; init; }

    /// <summary>update: a streamed response delta from C.</summary>
    public ChatResponseUpdate? Update { get; init; }

    /// <summary>complete: a non-null error indicates the turn failed.</summary>
    public ReverseExecutionError? Error { get; init; }

    public static class Types
    {
        public const string Register = "register";
        public const string Execute = "execute";
        public const string Update = "update";
        public const string Complete = "complete";
        public const string Cancel = "cancel";
    }
}

/// <summary>An error returned for a failed reverse execution turn.</summary>
public sealed record ReverseExecutionError(string Code, string Message);
