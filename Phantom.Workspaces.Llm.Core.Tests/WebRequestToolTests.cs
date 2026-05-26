using System.Net;
using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Linq;
namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class WebRequestToolTests
{
    [Fact]
    public void WebRequest_Description_SelfDescribesArgumentsAndHeaderBehavior()
    {
        var tool = new WebRequestTool();

        Assert.Contains("Required: url", tool.Description, StringComparison.Ordinal);
        Assert.Contains("Optional: method", tool.Description, StringComparison.Ordinal);
        Assert.Contains("headers", tool.Description, StringComparison.Ordinal);
        Assert.Contains("override defaults", tool.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void WebRequest_JsonSchema_DescribesWireArguments()
    {
        var tool = new WebRequestTool();

        var schema = tool.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("url", required);

        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("url").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("method").GetProperty("type").GetString());
        Assert.Equal("object", properties.GetProperty("headers").GetProperty("type").GetString());
    }

    [Fact]
    public async Task WebRequest_WithValidUrl_ReturnsResponse()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Test content"),
            };
            response.Headers.Add("X-Custom-Header", "test-value");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", "https://example.com" },
        });
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        var contentList = Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(result);
        Assert.Single(contentList);

        var bodyContent = Assert.IsType<TextContent>(contentList[0]);
        Assert.Equal("Test content", bodyContent.Text);
        Assert.Equal(200, (int)bodyContent.AdditionalProperties!["statusCode"]!);

        var headers = (Dictionary<string, string>)bodyContent.AdditionalProperties!["headers"]!;
        Assert.Equal("test-value", headers["X-Custom-Header"]);
    }

    [Fact]
    public async Task WebRequest_WithJsonStringArguments_UsesUrlAndMethod()
    {
        var capturedRequest = default(HttpRequestMessage);
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            };
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", JsonDocument.Parse("\"https://example.com/json\"").RootElement.Clone() },
            { "method", JsonDocument.Parse("\"POST\"").RootElement.Clone() },
        });

        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        var contentList = Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(result);
        Assert.Single(contentList);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://example.com/json", capturedRequest.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
    }

    [Fact]
    public async Task WebRequest_WithoutUrl_ReturnsError()
    {
        var tool = new WebRequestTool();

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>());
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        Assert.Contains("requires a 'url' parameter", result?.ToString() ?? "");
    }

    [Fact]
    public async Task WebRequest_DefaultsToGetMethod()
    {
        var capturedRequest = default(HttpRequestMessage);
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", "https://example.com" },
        });
        await tool.InvokeAsync(arguments, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest.Method);
    }

    [Fact]
    public async Task WebRequest_WithMethod_UsesSpecifiedMethod()
    {
        var capturedRequest = default(HttpRequestMessage);
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", "https://example.com" },
            { "method", "POST" },
        });
        await tool.InvokeAsync(arguments, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
    }

    [Fact]
    public async Task WebRequest_WithHeaders_IncludesHeadersInRequest()
    {
        var capturedRequest = default(HttpRequestMessage);
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        var headers = new Dictionary<string, object?>
        {
            { "Authorization", "Bearer token123" },
            { "X-Custom", "value" },
        };

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", "https://example.com" },
            { "headers", headers },
        });
        await tool.InvokeAsync(arguments, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains("Authorization"));
        Assert.Single(capturedRequest.Headers.GetValues("Authorization"));
        Assert.Equal("Bearer token123", capturedRequest.Headers.GetValues("Authorization").First());

        Assert.True(capturedRequest.Headers.Contains("X-Custom"));
        Assert.Single(capturedRequest.Headers.GetValues("X-Custom"));
        Assert.Equal("value", capturedRequest.Headers.GetValues("X-Custom").First());
    }

    [Fact]
    public async Task WebRequest_WithoutCustomHeaders_AppliesDefaultTextHeaders()
    {
        var capturedRequest = default(HttpRequestMessage);
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", "https://example.com" },
        });
        await tool.InvokeAsync(arguments, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains("User-Agent"));
        Assert.True(capturedRequest.Headers.Contains("Accept"));
        Assert.True(capturedRequest.Headers.Contains("Accept-Language"));
        Assert.Contains("text/html", string.Join(",", capturedRequest.Headers.GetValues("Accept")));
    }

    [Fact]
    public async Task WebRequest_WithCustomHeaders_OverridesDefaultHeaders()
    {
        var capturedRequest = default(HttpRequestMessage);
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        var headers = new Dictionary<string, object?>
        {
            { "User-Agent", "custom-agent/1.0" },
            { "Accept", "application/json" },
            { "Accept-Language", "fr-FR" },
        };

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", "https://example.com" },
            { "headers", headers },
        });
        await tool.InvokeAsync(arguments, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("custom-agent/1.0", string.Join(" ", capturedRequest.Headers.UserAgent.Select(ua => ua.ToString())));
        Assert.Equal("application/json", capturedRequest.Headers.GetValues("Accept").Single());
        Assert.Equal("fr-FR", capturedRequest.Headers.GetValues("Accept-Language").Single());
    }

    [Fact]
    public async Task WebRequest_WithErrorResponse_IncludesErrorStatus()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("Not found"),
            };
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", "https://example.com/notfound" },
        });
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        var contentList = Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(result);
        Assert.Single(contentList);
        Assert.Equal(404, (int)contentList[0].AdditionalProperties!["statusCode"]!);
    }

    [Fact]
    public async Task WebRequest_WithImageContent_ReturnsDataContentWithBytes()
    {
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(imageBytes),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            response.Headers.Add("X-Image-Name", "test-image.png");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", "https://example.com/image.png" },
        });
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        var contentList = Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(result);
        Assert.Single(contentList);

        var dataContent = Assert.IsType<DataContent>(contentList[0]);
        Assert.Equal("image/png", dataContent.MediaType);
        Assert.Equal(imageBytes, dataContent.Data.ToArray());

        var headers = (Dictionary<string, string>)dataContent.AdditionalProperties!["headers"]!;
        Assert.Equal("image/png", headers["Content-Type"]);
    }

    [Fact]
    public async Task WebRequest_WithTextContent_ReturnPlainText()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("This is plain text"),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", "https://example.com/text.txt" },
        });
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        var contentList = Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(result);
        Assert.Single(contentList);

        var bodyContent = Assert.IsType<TextContent>(contentList[0]);
        Assert.Equal("This is plain text", bodyContent.Text);
    }

    [Fact]
    public async Task WebRequest_WithPdfContent_ReturnsDataContentWithBytes()
    {
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pdfBytes),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", "https://example.com/document.pdf" },
        });
        var result = await tool.InvokeAsync(arguments, CancellationToken.None);

        var contentList = Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(result);
        Assert.Single(contentList);

        var dataContent = Assert.IsType<DataContent>(contentList[0]);
        Assert.Equal("application/pdf", dataContent.MediaType);
        Assert.Equal(pdfBytes, dataContent.Data.ToArray());
    }

    [Fact]
    public async Task ExecuteToolAsync_WithCancelledToken_ReturnsErrorMessage()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            throw new OperationCanceledException();
        });

        using var httpClient = new HttpClient(handler);
        var tool = new WebRequestTool(httpClient: httpClient);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            { "url", "https://example.com" },
        });
        var result = await tool.InvokeAsync(arguments, cts.Token);

        Assert.Contains("cancelled", result?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(createResponse(request));
    }
}
