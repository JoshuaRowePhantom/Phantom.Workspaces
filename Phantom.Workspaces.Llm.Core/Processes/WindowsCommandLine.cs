using System.Text;

namespace Phantom.Workspaces.Llm.Processes;

/// <summary>Builds a Windows command line whose arguments round-trip through CommandLineToArgvW.</summary>
public static class WindowsCommandLine
{
    /// <summary>Quotes one argument according to the Windows C runtime parsing rules.</summary>
    public static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
            return argument;

        var result = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', (backslashes * 2) + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }

        result.Append('\\', backslashes * 2);
        return result.Append('"').ToString();
    }

    /// <summary>Quotes and joins an executable and its ordered arguments.</summary>
    public static string Build(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteArgument));
    }
}
