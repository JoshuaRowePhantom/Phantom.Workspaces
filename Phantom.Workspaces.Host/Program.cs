using Phantom.Workspaces.Host;

if (args.Length > 0
    && !string.Equals(args[0], "filesystem-mcp-server-stdio", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Unsupported mode. Expected first argument: filesystem-mcp-server-stdio");
    return;
}

using var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

await FilesystemMcpServerStdioHost.RunAsync(cancellationTokenSource.Token);
