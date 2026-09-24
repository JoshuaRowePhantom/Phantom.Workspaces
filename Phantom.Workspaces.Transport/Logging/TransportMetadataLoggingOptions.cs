using System.Text;
using System.Text.Json;

namespace Phantom.Workspaces.Transport.Logging;

/// <summary>
/// Temporary, default-on metadata diagnostics. Set the environment variable to false to
/// disable high-frequency events without suppressing Information-level lifecycle records.
/// </summary>
public sealed record TransportMetadataLoggingOptions(bool Enabled)
{
    public const string EnvironmentVariable = "PHANTOM_WORKSPACES_VERBOSE_TRANSPORT_METADATA";

    public static TransportMetadataLoggingOptions FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var disabled = value?.Trim();
        return new TransportMetadataLoggingOptions(
            !string.Equals(disabled, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(disabled, "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(disabled, "off", StringComparison.OrdinalIgnoreCase));
    }
}

internal static class TransportMetadataTrace
{
    internal static string NewMarker() => Guid.NewGuid().ToString("N");

    internal static string MarkerForRequest(JsonElement request)
    {
        if (request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String)
        {
            var correlation = type.GetString() == "channel-open"
                && request.TryGetProperty("request", out var nested) ? nested : request;
            if (correlation.ValueKind == JsonValueKind.Object
                && correlation.TryGetProperty("type", out var innerType)
                && innerType.ValueKind == JsonValueKind.String
                && innerType.GetString() == "copilot-sdk-session"
                && correlation.TryGetProperty("correlation-id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: 32 } candidate
                && Guid.TryParseExact(candidate, "N", out _))
                return candidate;
        }

        return NewMarker();
    }

    internal static string FrameType(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object
            || !frame.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
            return "other";

        return type.GetString() switch
        {
            "channel-open" or "channel-message" or "channel-close" or "channel-open-ack"
                or "channel-open-error" or "stream-open" or "stream-data" or "stream-close"
                or "reverse-register" or "reverse-http" or "reverse-registration-info"
                or "copilot-sdk-session" or "mcp" or "chat-client" or "agent-session"
                or "request" or "response" or "notification" or "error" => type.GetString()!,
            _ => "other",
        };
    }

    internal static string Method(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object)
            return "other";

        var candidate = FrameType(frame) == "channel-message"
            && frame.TryGetProperty("payload", out var payload)
            && payload.ValueKind == JsonValueKind.Object ? payload : frame;
        if (!candidate.TryGetProperty("method", out var method)
            || method.ValueKind != JsonValueKind.String)
            return "other";

        return method.GetString() switch
        {
            "initialize" or "ping" or "tools/list" or "tools/call"
                or "resources/list" or "resources/read" or "prompts/list"
                or "prompts/get" or "notifications/initialized" => method.GetString()!,
            _ => "other",
        };
    }

    internal static int ByteCount(JsonElement frame)
        => Math.Min(Encoding.UTF8.GetByteCount(frame.GetRawText()), 1_048_576);
}
