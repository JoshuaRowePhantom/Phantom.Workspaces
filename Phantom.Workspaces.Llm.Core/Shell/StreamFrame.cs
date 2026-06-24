using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Llm.Shell;

/// <summary>
/// The kind discriminator carried in the one-byte prefix of a <see cref="StreamFrame"/> on the
/// streamed-process ("shell") transport (see <c>docs/design/shell-pty-terminal.md</c>).
/// </summary>
public enum StreamFrameKind : byte
{
    /// <summary>Raw terminal bytes (PTY/process output host→client, or stdin/keyboard client→host).</summary>
    Data = 0,

    /// <summary>Out-of-band control carrying a JSON <see cref="StreamControlMessage"/> body (resize/signal/exit).</summary>
    Control = 1,
}

/// <summary>
/// A single framed message on the shell transport. Terminal byte data and out-of-band control
/// (resize/signal/exit) are multiplexed over the same duplex channel; <see cref="ShellSession"/>
/// demultiplexes the <see cref="StreamFrameKind.Data"/> frames into a plain <see cref="System.IO.Stream"/>
/// and handles <see cref="StreamFrameKind.Control"/> frames as methods/events. The binary wire
/// encoding (one-byte kind prefix, then raw bytes or a small JSON control body) keeps terminal
/// throughput off JSON+base64.
/// </summary>
public sealed record StreamFrame(StreamFrameKind Kind, ReadOnlyMemory<byte> Payload);

/// <summary>
/// The JSON body of a <see cref="StreamFrameKind.Control"/> frame. A <see cref="Type"/> discriminator
/// selects which optional fields are present; window-size and signals are control, not terminal data,
/// so they ride here rather than in the byte stream.
/// </summary>
public sealed record StreamControlMessage
{
    /// <summary>One of <see cref="Types"/>: resize, signal, or exit.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>resize: the new terminal column count (pty mode only).</summary>
    [JsonPropertyName("columns")]
    public int? Columns { get; init; }

    /// <summary>resize: the new terminal row count (pty mode only).</summary>
    [JsonPropertyName("rows")]
    public int? Rows { get; init; }

    /// <summary>signal: the signal name to deliver to the process (e.g. SIGINT).</summary>
    [JsonPropertyName("signal")]
    public string? Signal { get; init; }

    /// <summary>exit: the process exit code.</summary>
    [JsonPropertyName("exit-code")]
    public int? ExitCode { get; init; }

    public static class Types
    {
        public const string Resize = "resize";
        public const string Signal = "signal";
        public const string Exit = "exit";
    }

    /// <summary>Serializes the message to the UTF-8 JSON body of a control frame.</summary>
    public byte[] ToPayload() => JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);

    /// <summary>Deserializes a control-frame body produced by <see cref="ToPayload"/>.</summary>
    public static StreamControlMessage FromPayload(ReadOnlyMemory<byte> payload)
        => JsonSerializer.Deserialize<StreamControlMessage>(payload.Span, SerializerOptions)
            ?? throw new FormatException("A shell control frame contained a null JSON body.");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
