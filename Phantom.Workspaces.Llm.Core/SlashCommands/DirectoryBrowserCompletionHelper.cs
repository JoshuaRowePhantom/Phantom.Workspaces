using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Provides filesystem directory completions for a partial path argument.
/// Given a partial path, enumerates immediate child directories of the deepest
/// fully-typed prefix and returns one <see cref="SlashCommandCompletion"/> per match.
/// </summary>
public static class DirectoryBrowserCompletionHelper
{
    public static IReadOnlyList<SlashCommandCompletion> GetCompletions(string partialPath)
    {
        if (string.IsNullOrEmpty(partialPath))
        {
            return Array.Empty<SlashCommandCompletion>();
        }

        string dir;
        string prefix;

        if (Directory.Exists(partialPath))
        {
            dir = partialPath;
            prefix = string.Empty;
        }
        else
        {
            dir = Path.GetDirectoryName(partialPath) ?? string.Empty;
            prefix = Path.GetFileName(partialPath);
        }

        if (!Directory.Exists(dir))
        {
            return Array.Empty<SlashCommandCompletion>();
        }

        return Directory.EnumerateDirectories(dir)
            .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(d => new SlashCommandCompletion(
                CompletionText: d + Path.DirectorySeparatorChar,
                Label: Path.GetFileName(d)))
            .ToArray();
    }
}
