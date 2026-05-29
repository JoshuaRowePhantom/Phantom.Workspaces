using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

var options = ProgramOptions.Parse(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

if (string.Equals(options.Mode, "http", StringComparison.OrdinalIgnoreCase))
{
    await RunHttpAsync(options, cts.Token);
    return;
}

if (string.Equals(options.Mode, "stdio", StringComparison.OrdinalIgnoreCase))
{
    await RunStdioAsync(cts.Token);
    return;
}

throw new InvalidOperationException($"Unsupported mode '{options.Mode}'. Expected 'stdio' or 'http'.");

static async Task RunStdioAsync(CancellationToken cancellationToken)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.AddConsole(consoleLogOptions =>
    {
        consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync(cancellationToken);
}

static async Task RunHttpAsync(ProgramOptions options, CancellationToken cancellationToken)
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls(options.Url);
    builder.Logging.AddConsole(consoleLogOptions =>
    {
        consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    builder.Services
        .AddMcpServer()
        .WithHttpTransport(httpOptions => httpOptions.Stateless = true)
        .WithToolsFromAssembly();

    var app = builder.Build();
    app.MapMcp();

    await app.StartAsync(cancellationToken);
    var server = app.Services.GetRequiredService<IServer>();
    var addressesFeature = server.Features.Get<IServerAddressesFeature>();
    var boundUrl = addressesFeature?.Addresses.FirstOrDefault() ?? options.Url;
    Console.Out.WriteLine(boundUrl);
    await Console.Out.FlushAsync();
    await app.WaitForShutdownAsync(cancellationToken);
}

internal sealed record ProgramOptions(string Mode, string Url)
{
    public static ProgramOptions Parse(string[] args)
    {
        var mode = "stdio";
        var url = "http://127.0.0.1:0";

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (string.Equals(arg, "--mode", StringComparison.OrdinalIgnoreCase))
            {
                mode = GetRequiredValue(args, ref index, "--mode");
                continue;
            }

            if (string.Equals(arg, "--url", StringComparison.OrdinalIgnoreCase))
            {
                url = GetRequiredValue(args, ref index, "--url");
            }
        }

        return new ProgramOptions(mode, url);
    }

    private static string GetRequiredValue(string[] args, ref int index, string optionName)
    {
        var nextIndex = index + 1;
        if (nextIndex >= args.Length)
        {
            throw new InvalidOperationException($"{optionName} requires a value.");
        }

        index = nextIndex;
        return args[nextIndex];
    }
}

[McpServerToolType]
public static class PingTool
{
    [McpServerTool, Description("Returns a pong response.")]
    public static string Ping(string? message = null)
        => string.IsNullOrWhiteSpace(message) ? "pong" : $"pong:{message.Trim()}";
}
