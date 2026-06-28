using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LibGit2Sharp;

namespace Phantom.Workspaces.ViewModels;

public sealed class GitDiffViewModel : ViewModelBase
{
    public required string RelativePath { get; init; }
    public required int LinesAdded { get; init; }
    public required int LinesRemoved { get; init; }
    public required IReadOnlyList<GitDiffHunk> Hunks { get; init; }

    public static GitDiffViewModel FromPatchEntry(PatchEntryChanges entry, int contextLines)
    {
        var hunks = ParseHunks(entry.Patch);
        return new GitDiffViewModel
        {
            RelativePath = entry.Path,
            LinesAdded = entry.LinesAdded,
            LinesRemoved = entry.LinesDeleted,
            Hunks = hunks,
        };
    }

    internal static IReadOnlyList<GitDiffHunk> ParseHunks(string patchText)
    {
        var result = new List<GitDiffHunk>();
        if (string.IsNullOrEmpty(patchText))
        {
            return result;
        }

        var lines = patchText.Split('\n');
        var hunkHeaderRegex = new Regex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

        var currentHunkLines = new List<GitDiffLine>();
        int oldLineNumber = 0;
        int newLineNumber = 0;
        int currentOldStart = 0;
        int currentNewStart = 0;
        bool inHunk = false;

        foreach (var rawLine in lines)
        {
            var match = hunkHeaderRegex.Match(rawLine);
            if (match.Success)
            {
                if (inHunk && currentHunkLines.Count > 0)
                {
                    result.Add(new GitDiffHunk
                    {
                        OldStart = currentOldStart,
                        NewStart = currentNewStart,
                        Lines = currentHunkLines.ToArray(),
                    });
                }

                currentHunkLines = new List<GitDiffLine>();
                currentOldStart = int.Parse(match.Groups[1].Value);
                currentNewStart = int.Parse(match.Groups[2].Value);
                oldLineNumber = currentOldStart;
                newLineNumber = currentNewStart;
                inHunk = true;
                continue;
            }

            if (!inHunk)
            {
                continue;
            }

            if (rawLine.StartsWith('+') && !rawLine.StartsWith("+++"))
            {
                currentHunkLines.Add(new GitDiffLine
                {
                    Kind = GitDiffLineKind.Added,
                    OldLineNumber = null,
                    NewLineNumber = newLineNumber++,
                    Content = rawLine[1..],
                });
            }
            else if (rawLine.StartsWith('-') && !rawLine.StartsWith("---"))
            {
                currentHunkLines.Add(new GitDiffLine
                {
                    Kind = GitDiffLineKind.Removed,
                    OldLineNumber = oldLineNumber++,
                    NewLineNumber = null,
                    Content = rawLine[1..],
                });
            }
            else if (rawLine.StartsWith(' '))
            {
                currentHunkLines.Add(new GitDiffLine
                {
                    Kind = GitDiffLineKind.Context,
                    OldLineNumber = oldLineNumber++,
                    NewLineNumber = newLineNumber++,
                    Content = rawLine[1..],
                });
            }
        }

        if (inHunk && currentHunkLines.Count > 0)
        {
            result.Add(new GitDiffHunk
            {
                OldStart = currentOldStart,
                NewStart = currentNewStart,
                Lines = currentHunkLines.ToArray(),
            });
        }

        return result;
    }
}
