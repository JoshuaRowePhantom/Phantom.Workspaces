using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Transport;

/// <summary>Bounded diagnostic cause sent between authenticated transport peers.</summary>
public sealed record TransportErrorDetails
{
    private const int MaxDepth = 4;
    private const int MaxText = 4096;

    [JsonPropertyName("exception-type")]
    public required string ExceptionType { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("stack-trace")]
    public string? StackTrace { get; init; }

    [JsonPropertyName("inner")]
    public TransportErrorDetails? Inner { get; init; }

    [JsonPropertyName("secondary")]
    public IReadOnlyList<TransportErrorDetails>? Secondary { get; init; }

    public static TransportErrorDetails FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        return Capture(exception, 0, visited);
    }

    public static TransportErrorDetails? Read(JsonElement frame)
    {
        if (!frame.TryGetProperty("error-details", out var details)
            || details.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            return ReadNode(details, 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Format()
    {
        var builder = new StringBuilder();
        for (TransportErrorDetails? current = this; current is not null; current = current.Inner)
        {
            if (builder.Length > 0)
                builder.AppendLine().Append("Caused by: ");
            builder.Append(current.ExceptionType).Append(": ").Append(current.Message);
            if (!string.IsNullOrEmpty(current.StackTrace))
                builder.AppendLine().Append(current.StackTrace);
        }
        if (this.Secondary is not null)
        {
            foreach (var secondary in this.Secondary)
                builder.AppendLine().Append("Cleanup failure: ").Append(secondary.Format());
        }
        return builder.ToString();
    }

    private static TransportErrorDetails Capture(Exception exception, int depth, HashSet<Exception> visited)
    {
        visited.Add(exception);
        var secondary = depth == 0 && exception is AggregateException aggregate
            ? aggregate.InnerExceptions.Skip(1).Take(2)
                .Where(inner => !visited.Contains(inner))
                .Select(inner => Capture(inner, 1, visited)).ToArray()
            : [];
        return new TransportErrorDetails
        {
            ExceptionType = Limit(exception.GetType().FullName ?? exception.GetType().Name),
            Message = Limit(exception.Message),
            StackTrace = exception.StackTrace is null ? null : Limit(exception.StackTrace),
            Inner = depth + 1 < MaxDepth && exception.InnerException is { } inner && !visited.Contains(inner)
                ? Capture(inner, depth + 1, visited) : null,
            Secondary = secondary.Length == 0 ? null : secondary,
        };
    }

    private static TransportErrorDetails ReadNode(JsonElement node, int depth)
    {
        static string ReadText(JsonElement element, string key)
            => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? Limit(value.GetString() ?? string.Empty) : string.Empty;

        var type = ReadText(node, "exception-type");
        if (string.IsNullOrWhiteSpace(type))
            throw new JsonException("Missing remote exception type.");

        TransportErrorDetails? inner = null;
        if (depth + 1 < MaxDepth && node.TryGetProperty("inner", out var child)
            && child.ValueKind == JsonValueKind.Object)
            inner = ReadNode(child, depth + 1);

        List<TransportErrorDetails>? secondary = null;
        if (depth == 0 && node.TryGetProperty("secondary", out var additional)
            && additional.ValueKind == JsonValueKind.Array)
        {
            secondary = [];
            foreach (var item in additional.EnumerateArray().Take(2))
            {
                if (item.ValueKind == JsonValueKind.Object)
                    secondary.Add(ReadNode(item, 1));
            }
        }

        return new TransportErrorDetails
        {
            ExceptionType = type,
            Message = ReadText(node, "message"),
            StackTrace = ReadText(node, "stack-trace"),
            Inner = inner,
            Secondary = secondary,
        };
    }

    private static string Limit(string value) => value.Length <= MaxText ? value : value[..MaxText] + "…";
}
