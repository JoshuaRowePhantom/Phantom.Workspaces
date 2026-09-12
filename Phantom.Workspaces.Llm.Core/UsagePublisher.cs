namespace Phantom.Workspaces.Llm;

/// <summary>
/// #1485: owner-side validation for <see cref="Usage"/> value publications.
/// Token counts are optional; when present they must be non-negative. Cost is optional;
/// when present it must be a finite non-negative USD amount.
/// </summary>
public static class UsagePublisher
{
    /// <summary>Validates a candidate <see cref="Usage"/> before publication.</summary>
    /// <param name="candidate">The candidate value.</param>
    /// <param name="errorCode">Populated with a stable machine-readable error code on failure.</param>
    /// <returns><see langword="true"/> if the value satisfies the publisher invariants.</returns>
    public static bool TryValidate(Usage candidate, out string? errorCode)
    {
        if (candidate.TotalInputTokenCount is < 0
            || candidate.TotalOutputTokenCount is < 0
            || candidate.TotalCacheReadTokenCount is < 0
            || candidate.TotalCacheWriteTokenCount is < 0
            || candidate.TotalReasoningTokenCount is < 0)
        {
            errorCode = "negative-token-count";
            return false;
        }
        if (candidate.TotalSessionCostUsd is { } cost && (double.IsNaN(cost) || double.IsInfinity(cost) || cost < 0.0))
        {
            errorCode = "invalid-cost";
            return false;
        }
        errorCode = null;
        return true;
    }
}
