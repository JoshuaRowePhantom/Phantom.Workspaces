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
    /// <summary>register (C→S), execute (S→C), update (C→S), complete (C→S), cancel (either way),
    /// open-stream (S→C), stream-data (both), or stream-close (both).</summary>
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

    /// <summary>execute/update/complete/cancel/open-stream/stream-data/stream-close: correlates a turn or stream across frames.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>execute: the agent request to run on C.</summary>
    public RemoteAgentRequest? Request { get; init; }

    /// <summary>update: a streamed response delta from C.</summary>
    public ChatResponseUpdate? Update { get; init; }

    /// <summary>complete: a non-null error indicates the turn failed.</summary>
    public ReverseExecutionError? Error { get; init; }

    /// <summary>open-stream: the stream kind (e.g. <c>"shell"</c>).</summary>
    public string? StreamKind { get; init; }

    /// <summary>open-stream: the kind-specific open payload, serialized as JSON.</summary>
    public string? StreamOpenPayload { get; init; }

    /// <summary>stream-data: the raw bytes of one stream frame payload.</summary>
    public byte[]? StreamData { get; init; }

    /// <summary>stream-data: the <see cref="Phantom.Workspaces.Llm.Shell.StreamFrameKind"/> byte (0=Data, 1=Control).</summary>
    public byte? StreamFrameKindByte { get; init; }

    /// <summary>run-tool: the tool execution request to run on C.</summary>
    public TrustedToolRequest? ToolRequest { get; init; }

    public static class Types
    {
        public const string Register = "register";
        public const string Execute = "execute";
        public const string Update = "update";
        public const string Complete = "complete";
        public const string Cancel = "cancel";

        /// <summary>S→C: open a bidirectional byte stream on the connecting instance.</summary>
        public const string OpenStream = "open-stream";

        /// <summary>Both directions: relay one <see cref="Phantom.Workspaces.Llm.Shell.StreamFrame"/> over the reverse channel.</summary>
        public const string StreamData = "stream-data";

        /// <summary>Both directions: the byte stream is closed (no more data will follow for this correlation).</summary>
        public const string StreamClose = "stream-close";

        /// <summary>S→C: run a workspace tool on the connecting instance.</summary>
        public const string RunTool = "run-tool";

        /// <summary>C→S: the tool run requested by a <c>run-tool</c> frame has completed (successfully or with an error).</summary>
        public const string RunToolComplete = "run-tool-complete";
    }
}

/// <summary>An error returned for a failed reverse execution turn.</summary>
public sealed record ReverseExecutionError(string Code, string Message);
