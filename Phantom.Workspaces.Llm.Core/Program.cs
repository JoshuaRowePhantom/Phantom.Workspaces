namespace Phantom.Workspaces.Llm;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0
            || !string.Equals(args[0], "filesystem-mcp-server-stdio", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Unsupported mode. Expected first argument: filesystem-mcp-server-stdio");
            return 1;
        }

        string? editStoreConnectionJson = null;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--filesystem-edit-store-connection-base64", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    throw new InvalidOperationException("Missing value for --filesystem-edit-store-connection-base64.");
                }

                editStoreConnectionJson = DecodeBase64Utf8(args[index + 1]);
                index++;
            }
        }

        using var cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationTokenSource.Cancel();
        };

        await FilesystemMcpServerStdioHost.RunAsync(
            cancellationTokenSource.Token,
            editStoreConnectionJson);
        return 0;
    }

    private static string DecodeBase64Utf8(string base64Value)
    {
        var bytes = Convert.FromBase64String(base64Value);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
