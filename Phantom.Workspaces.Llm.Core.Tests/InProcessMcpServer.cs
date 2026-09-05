using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Core.Tests;

internal sealed class InProcessMcpServer : IAsyncDisposable
{
    private readonly WebApplication app;

    private InProcessMcpServer(WebApplication app, string boundUrl)
    {
        this.app = app;
        this.BoundUrl = boundUrl;
    }

    public string BoundUrl { get; }

    public static async Task<InProcessMcpServer> StartAsync(AsyncBarrier barrier)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(httpOptions => httpOptions.Stateless = true)
            .WithToolsFromAssembly();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals("/mcp", StringComparison.OrdinalIgnoreCase)
                && string.Equals(context.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync().WaitAsync(context.RequestAborted).ConfigureAwait(false);
                context.Request.Body.Position = 0;

                if (IsToolsListRequest(body))
                {
                    await barrier.SignalAndWaitAsync(context.RequestAborted).ConfigureAwait(false);
                }
            }

            await next(context).ConfigureAwait(false);
        });
        app.MapMcp();

        await app.StartAsync().ConfigureAwait(false);
        var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var addressesFeature = server.Features.Get<IServerAddressesFeature>();
        var boundUrl = addressesFeature?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("In-process MCP server did not bind an address.");
        return new InProcessMcpServer(app, boundUrl);
    }

    public async ValueTask DisposeAsync()
    {
        await this.app.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsToolsListRequest(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("method", out var method)
                && string.Equals(method.GetString(), "tools/list", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed class AsyncBarrier
{
    private readonly int participantCount;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrivedCount;

    public AsyncBarrier(int participantCount)
    {
        if (participantCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(participantCount));
        }

        this.participantCount = participantCount;
    }

    public async Task SignalAndWaitAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref this.arrivedCount) >= this.participantCount)
        {
            this.completion.TrySetResult();
        }

        await this.completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

[McpServerToolType]
public static class InProcessPingTool
{
    [McpServerTool, System.ComponentModel.Description("Returns a pong response.")]
    public static string Ping(string? message = null)
        => string.IsNullOrWhiteSpace(message) ? "pong" : $"pong:{message.Trim()}";
}

[McpServerToolType]
public static class InProcessFailingTool
{
    public const string ErrorMarker = "boom-remote-tool-error";

    [McpServerTool, System.ComponentModel.Description("Always fails, to exercise the tool-error round-trip.")]
    public static string Fail(string? message = null)
        => throw new InvalidOperationException(ErrorMarker);
}
