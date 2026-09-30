using System.Threading;
using System.Text.Json;

namespace Phantom.Workspaces.Transport;

public static class SessionAttachDiagnosticScope
{
    private static readonly AsyncLocal<string?> Attempt = new();

    public static string? CurrentAttempt => Attempt.Value;

    public static IDisposable Begin()
    {
        var previous = Attempt.Value;
        Attempt.Value = Guid.NewGuid().ToString("N");
        return new Scope(previous);
    }

    public static string? ReadValidated(JsonElement request)
        => request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("diagnostic-attempt", out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } candidate
            && Guid.TryParseExact(candidate, "N", out _)
                ? candidate : null;

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Attempt.Value = previous;
    }
}
