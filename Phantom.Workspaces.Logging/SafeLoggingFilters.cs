using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Services.Logging;

/// <summary>Only product-owned metadata categories may emit verbose diagnostics to durable/UI sinks.</summary>
public static class SafeLoggingFilters
{
    private static readonly string[] UntrustedSdkCategories =
        ["ModelContextProtocol", "GitHub.Copilot", "Microsoft.Extensions.AI",
            "System.Net.Http", "OpenAI", "OllamaSharp", "Azure.Identity"];

    public static void Configure(ILoggingBuilder builder, bool verboseMetadata)
    {
        builder.SetMinimumLevel(LogLevel.Information);
        foreach (var category in UntrustedSdkCategories)
            builder.AddFilter(category, LogLevel.None);
        if (verboseMetadata)
        {
            builder.AddFilter("Phantom.Workspaces.Transport.Logging", LogLevel.Debug);
            builder.AddFilter("Phantom.Workspaces.Transport.ReverseHttp", LogLevel.Debug);
            builder.AddFilter("Phantom.Workspaces.Llm.HttpRequestLoggingHandler", LogLevel.Debug);
            builder.AddFilter("Phantom.Workspaces.Llm.Mcp.ProcessExecutorBackedClientTransport", LogLevel.Debug);
        }
    }

    public static bool IsUntrustedSdkCategory(string category)
        => UntrustedSdkCategories.Any(prefix =>
            string.Equals(category, prefix, StringComparison.Ordinal)
            || category.StartsWith(prefix + ".", StringComparison.Ordinal));
}
