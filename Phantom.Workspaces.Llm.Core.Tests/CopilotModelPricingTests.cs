using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class CopilotModelPricingTests
{
    [Fact]
    public void SessionCost_WhenProviderReportsCost_UsesProviderCost()
    {
        // Provider-reported cost (1.23 USD) must win over price × tokens (which would be far larger).
        var details = new UsageDetails
        {
            InputTokenCount = 1_000_000,
            OutputTokenCount = 500_000,
            AdditionalCounts = new() { [CopilotSdkStreamAdapter.CostMicroUsdCountName] = 1_230_000 },
        };
        var prices = new CopilotTokenPrices { InputPrice = 0.001, OutputPrice = 0.002 };

        var cost = CopilotModelPricing.ResolveCostUsd(details, prices);

        Assert.Equal(1.23, cost);
    }

    [Fact]
    public void SessionCost_WhenNoProviderCostButPricesKnown_ComputesFromTokenPrices()
    {
        var details = new UsageDetails
        {
            InputTokenCount = 1000,
            OutputTokenCount = 200,
            AdditionalCounts = new()
            {
                [CopilotSdkStreamAdapter.CacheReadTokensCountName] = 400,
                [CopilotSdkStreamAdapter.CacheWriteTokensCountName] = 100,
            },
        };
        var prices = new CopilotTokenPrices
        {
            InputPrice = 0.01,
            OutputPrice = 0.03,
            CacheReadPrice = 0.002,
            CacheWritePrice = 0.005,
        };

        var cost = CopilotModelPricing.ResolveCostUsd(details, prices);

        // nonCachedInput=600*0.01 + cacheRead=400*0.002 + cacheWrite=100*0.005 + output=200*0.03
        // = 6 + 0.8 + 0.5 + 6 = 13.3
        Assert.Equal(13.3, cost!.Value, 6);
    }

    [Fact]
    public void SessionCost_WhenLongContextExceeded_UsesLongContextPrices()
    {
        var details = new UsageDetails
        {
            InputTokenCount = 2000,
            OutputTokenCount = 100,
        };
        var prices = new CopilotTokenPrices
        {
            InputPrice = 0.01,
            OutputPrice = 0.03,
            ContextMax = 1000,
            LongContext = new CopilotTokenPrices { InputPrice = 0.05, OutputPrice = 0.10 },
        };

        var cost = CopilotModelPricing.ComputeCostUsd(details, prices);

        // input 2000 > ContextMax 1000 => long-context: 2000*0.05 + 100*0.10 = 100 + 10 = 110
        Assert.Equal(110.0, cost, 6);
    }

    [Fact]
    public void SessionCost_WhenNoProviderCostAndNoPrices_ReturnsNull()
    {
        var details = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 200 };

        Assert.Null(CopilotModelPricing.ResolveCostUsd(details, prices: null));
    }
}
