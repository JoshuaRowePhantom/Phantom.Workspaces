using Avalonia;
using System;
using System.CommandLine;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui;

class Program
{
    public static AgentDefinitionParseResult? ParseResult { get; private set; }
    public static string? ParseError { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (!TryParseArguments(args, out var parsed))
        {
            // ParseError is set — start Avalonia to show the error window.
        }
        else
        {
            ParseResult = parsed;
        }

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
            ParseError = string.Join(Environment.NewLine, parsedCommand.Errors.Select(e => e.Message));
            parseResult = null;
            return false;
        }

        try
        {
            parseResult = definitionParser.Parse(parsedCommand);
            return true;
        }
        catch (Exception ex)
        {
            ParseError = ex.Message;
            parseResult = null;
            return false;
        }
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
