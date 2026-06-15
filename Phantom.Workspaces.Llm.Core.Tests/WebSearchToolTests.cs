using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Linq;
namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class WebSearchToolTests
{
    [Fact]
    public void WebSearch_Description_SelfDescribesArguments()
    {
        var tool = new WebSearchTool();

        Assert.Contains("Required: query", tool.Description, StringComparison.Ordinal);
        Assert.Contains("Optional: provider", tool.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void WebSearch_JsonSchema_DescribesWireArguments()
    {
        var tool = new WebSearchTool();

        var schema = tool.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("query", required);

        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("query").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("provider").GetProperty("type").GetString());
    }

    [Fact]
    public async Task WebSearch_WithPlaceholderProvider_ReturnsValidResults()
    {
        var config = new WebSearchToolConfiguration
        {
            DefaultSearchProvider = WebSearchProvider.Placeholder,
        };
        var tool = new WebSearchTool(configuration: config);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { { "query", "test search" } });
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.Equal("Placeholder", resultJson.GetProperty("provider").GetString());
        Assert.Equal("test search", resultJson.GetProperty("query").GetString());
        var results = resultJson.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());

        var firstResult = results[0];
        Assert.False(string.IsNullOrWhiteSpace(firstResult.GetProperty("url").GetString()));
        Assert.Contains("test search", firstResult.GetProperty("title").GetString() ?? "", StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(firstResult.GetProperty("snippet").GetString()));
    }

    [Fact]
    public async Task WebSearch_WithoutQuery_ReturnsError()
    {
        var config = new WebSearchToolConfiguration
        {
            DefaultSearchProvider = WebSearchProvider.Placeholder,
        };
        var tool = new WebSearchTool(configuration: config);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>());
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        Assert.Contains("requires a 'query' parameter", result?.ToString() ?? "");
    }

    [Fact]
    public async Task WebSearch_WithEmptyQuery_ReturnsError()
    {
        var config = new WebSearchToolConfiguration
        {
            DefaultSearchProvider = WebSearchProvider.Placeholder,
        };
        var tool = new WebSearchTool(configuration: config);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { { "query", "   " } });
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        Assert.Contains("requires a 'query' parameter", result?.ToString() ?? "");
    }

    [Fact]
    public async Task WebSearch_WithExplicitProvider_UsesProvidedProvider()
    {
        var config = new WebSearchToolConfiguration
        {
            DefaultSearchProvider = WebSearchProvider.Placeholder,
        };
        var tool = new WebSearchTool(configuration: config);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "query", "test" },
            { "provider", "Placeholder" },
        });
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.Equal("Placeholder", resultJson.GetProperty("provider").GetString());
        Assert.Equal(2, resultJson.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task WebSearch_WithJsonStringArguments_ParsesQueryAndProvider()
    {
        var config = new WebSearchToolConfiguration
        {
            DefaultSearchProvider = WebSearchProvider.Placeholder,
        };
        var tool = new WebSearchTool(configuration: config);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "query", JsonDocument.Parse("\"json test\"").RootElement.Clone() },
            { "provider", JsonDocument.Parse("\"placeholder\"").RootElement.Clone() },
        });
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.Equal("json test", resultJson.GetProperty("query").GetString());
        Assert.Equal(2, resultJson.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task WebSearch_WithInvalidProvider_FallsBackToDefault()
    {
        var config = new WebSearchToolConfiguration
        {
            DefaultSearchProvider = WebSearchProvider.Placeholder,
        };
        var tool = new WebSearchTool(configuration: config);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "query", "test" },
            { "provider", "InvalidProvider" },
        });
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.Equal("Placeholder", resultJson.GetProperty("provider").GetString());
        Assert.True(resultJson.GetProperty("results").GetArrayLength() > 0);
    }

    [Fact]
    public async Task WebSearch_WithCaseInsensitiveProvider_Works()
    {
        var config = new WebSearchToolConfiguration
        {
            DefaultSearchProvider = WebSearchProvider.Placeholder,
        };
        var tool = new WebSearchTool(configuration: config);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "query", "test" },
            { "provider", "placeholder" },
        });
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.Equal("Placeholder", resultJson.GetProperty("provider").GetString());
        Assert.Equal(2, resultJson.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task ExecuteToolAsync_WithCancelledToken_ReturnsErrorMessage()
    {
        var config = new WebSearchToolConfiguration
        {
            DefaultSearchProvider = WebSearchProvider.Placeholder,
        };
        var tool = new WebSearchTool(configuration: config);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { { "query", "test" } });
        var result = await tool.InvokeAsync(arguments, cts.Token);

        Assert.Contains("cancelled", result?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
