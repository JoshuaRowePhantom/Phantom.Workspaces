using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Per-token dollar prices for a Copilot-backed model, mirroring the shape of the SDK's
/// <c>GitHub.Copilot.Rpc.ModelBillingTokenPrices</c>. Prices are expressed in USD per token. When
/// <see cref="LongContext"/> is present it applies to requests whose input token count exceeds
/// <see cref="ContextMax"/>.
/// </summary>
public sealed record CopilotTokenPrices
{
    public double InputPrice { get; init; }

    public double OutputPrice { get; init; }

    /// <summary>Price for cache-read (prompt-cache hit) input tokens; falls back to <see cref="InputPrice"/> when null.</summary>
    public double? CacheReadPrice { get; init; }

    /// <summary>Price for cache-write (prompt-cache fill) input tokens.</summary>
    public double CacheWritePrice { get; init; }

    /// <summary>Input-token threshold above which <see cref="LongContext"/> prices apply.</summary>
    public long? ContextMax { get; init; }

    /// <summary>Alternative price schedule for long-context requests (same shape, no nested long-context).</summary>
    public CopilotTokenPrices? LongContext { get; init; }
}

/// <summary>
/// Computes the dollar cost of Copilot token usage. Provider-reported cost
/// (<see cref="CopilotSdkStreamAdapter.CostMicroUsdCountName"/>) is always preferred; the
/// price-times-tokens path is a fallback for providers that report token counts but no cost.
/// </summary>
public static class CopilotModelPricing
{
    /// <summary>
    /// Returns the provider-reported cost from <paramref name="details"/> when present, otherwise the
    /// cost computed from <paramref name="prices"/> and the usage token counts. Returns <c>null</c>
    /// when neither source can produce a cost.
    /// </summary>
    public static double? ResolveCostUsd(UsageDetails details, CopilotTokenPrices? prices)
    {
        if (details is null)
        {
            return null;
        }

        if (details.AdditionalCounts is { } counts
            && counts.TryGetValue(CopilotSdkStreamAdapter.CostMicroUsdCountName, out var costMicroUsd))
        {
            return costMicroUsd / 1_000_000.0;
        }

        return prices is null ? null : ComputeCostUsd(details, prices);
    }

    /// <summary>
    /// Computes cost as
    /// <c>nonCachedInput*InputPrice + cacheRead*CacheReadPrice + cacheWrite*CacheWritePrice + output*OutputPrice</c>,
    /// selecting the long-context price schedule when the input token count exceeds
    /// <see cref="CopilotTokenPrices.ContextMax"/>.
    /// </summary>
    public static double ComputeCostUsd(UsageDetails details, CopilotTokenPrices prices)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(prices);

        var input = details.InputTokenCount ?? 0L;
        var output = details.OutputTokenCount ?? 0L;
        var cacheRead = GetAdditionalCount(details, CopilotSdkStreamAdapter.CacheReadTokensCountName);
        var cacheWrite = GetAdditionalCount(details, CopilotSdkStreamAdapter.CacheWriteTokensCountName);

        var effective = SelectSchedule(prices, input);
        var nonCachedInput = Math.Max(0L, input - cacheRead);

        return (nonCachedInput * effective.InputPrice)
            + (cacheRead * (effective.CacheReadPrice ?? effective.InputPrice))
            + (cacheWrite * effective.CacheWritePrice)
            + (output * effective.OutputPrice);
    }

    private static CopilotTokenPrices SelectSchedule(CopilotTokenPrices prices, long inputTokens)
        => prices.LongContext is { } longContext && prices.ContextMax is long max && inputTokens > max
            ? longContext
            : prices;

    private static long GetAdditionalCount(UsageDetails details, string name)
        => details.AdditionalCounts is { } counts && counts.TryGetValue(name, out var value) ? value : 0L;
}
