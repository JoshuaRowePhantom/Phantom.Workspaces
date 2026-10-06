using GitHub.Copilot;

if (args.Length != 1 || !Directory.Exists(args[0]))
    throw new ArgumentException("Expected the installed or published payload directory.");

AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", Path.GetFullPath(args[0]) + Path.DirectorySeparatorChar);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
await using var client = new CopilotClient(new CopilotClientOptions
{
    Mode = CopilotClientMode.CopilotCli,
});
await client.StartAsync(timeout.Token);
Console.WriteLine("OK  Installed payload Copilot SDK StartAsync succeeded.");
