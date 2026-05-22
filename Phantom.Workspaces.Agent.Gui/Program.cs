using Avalonia;
using System;
using System.CommandLine;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui;

class Program
{
    public static AgentDefinitionParseResult? ParseResult { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (!TryParseArguments(args, out var parsed))
        {
            Environment.ExitCode = 1;
            return;
        }

        ParseResult = parsed;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static bool TryParseArguments(string[] args, out AgentDefinitionParseResult? parseResult)
    {
        var definitionParser = new AgentDefinitionCommandLineParser();
        var rootCommand = new RootCommand("Phantom Workspaces Agent GUI");
        definitionParser.AddOptions(rootCommand);
        var parsedCommand = rootCommand.Parse(args);
        if (parsedCommand.Errors.Count > 0)
        {
            parseResult = null;
            return false;
        }

        parseResult = definitionParser.Parse(parsedCommand);
        return true;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
