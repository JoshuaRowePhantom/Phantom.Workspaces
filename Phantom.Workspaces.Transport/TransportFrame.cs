using System.Text.Json.Serialization;
using System.Text.Json;

namespace Phantom.Workspaces.Transport;

/// <summary>
/// The unit of framed transport communication.
/// </summary>
public sealed record TransportFrame
{
    /// <summary>
    /// Frame type (see <see cref="Types"/> for constants).
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Channel identifier (for channel frames).
    /// </summary>
    [JsonPropertyName("channel-id")]
    public string? ChannelId { get; init; }

    /// <summary>
    /// Stream identifier (for stream frames).
    /// </summary>
    [JsonPropertyName("stream-id")]
    public string? StreamId { get; init; }

    /// <summary>
    /// Message payload (for channel-message frames).
    /// </summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// Binary data (for stream-data frames, base64-encoded).
    /// </summary>
    [JsonPropertyName("data")]
    public string? Data { get; init; }

    /// <summary>
    /// Error code (for channel-open-error frames).
    /// </summary>
    [JsonPropertyName("error-code")]
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Error message (for channel-open-error frames).
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// Connection request (for channel-open and stream-open frames).
    /// </summary>
    [JsonPropertyName("request")]
    public JsonElement? Request { get; init; }

    /// <summary>
    /// Frame type constants.
    /// </summary>
    public static class Types
    {
        /// <summary>
        /// Open a new message channel (client to server).
        /// </summary>
        public const string ChannelOpen = "channel-open";

        /// <summary>
        /// Channel open request rejected (server to client).
        /// </summary>
        public const string ChannelOpenError = "channel-open-error";

        /// <summary>
        /// Message on an open channel (bidirectional).
        /// </summary>
        public const string ChannelMessage = "channel-message";

        /// <summary>
        /// Close a channel (bidirectional).
        /// </summary>
        public const string ChannelClose = "channel-close";

        /// <summary>
        /// Open a new raw stream (client to server).
        /// </summary>
        public const string StreamOpen = "stream-open";

        /// <summary>
        /// Binary chunk on a stream (bidirectional).
        /// </summary>
        public const string StreamData = "stream-data";

        /// <summary>
        /// Close a stream (bidirectional).
        /// </summary>
        public const string StreamClose = "stream-close";

        /// <summary>
        /// Keepalive frame (either direction).
        /// </summary>
        public const string Keepalive = "keepalive";

        /// <summary>
        /// Transport close notification (client to server).
        /// </summary>
        public const string TransportClose = "transport-close";
    }
}
