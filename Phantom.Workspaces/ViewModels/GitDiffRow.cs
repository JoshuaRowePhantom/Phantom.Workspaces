using System.Collections.Generic;

namespace Phantom.Workspaces.ViewModels;

/// <summary>A single file header, hunk header or diff line in the virtualized review viewport.</summary>
public abstract record GitDiffRow(string RelativePath)
{
    /// <summary>Projects a nested diff graph to one contiguous row sequence off the UI thread.</summary>
    public static IReadOnlyList<GitDiffRow> Flatten(IReadOnlyList<GitDiffViewModel> diffs)
    {
        var rows = new List<GitDiffRow>();
        foreach (var diff in diffs)
        {
            rows.Add(new GitDiffFileRow(diff.RelativePath, diff.LinesAdded, diff.LinesRemoved));
            foreach (var hunk in diff.Hunks)
            {
                rows.Add(new GitDiffHunkRow(diff.RelativePath, hunk.OldStart, hunk.NewStart));
                foreach (var line in hunk.Lines)
                {
                    rows.Add(diff.SideBySide
                        ? new GitDiffSideBySideLineRow(diff.RelativePath, hunk.OldStart, hunk.NewStart, line)
                        : new GitDiffUnifiedLineRow(diff.RelativePath, hunk.OldStart, hunk.NewStart, line));
                }
            }
        }

        return rows.ToArray();
    }
}

/// <summary>The beginning of a file diff.</summary>
public sealed record GitDiffFileRow(string RelativePath, int LinesAdded, int LinesRemoved) : GitDiffRow(RelativePath);

/// <summary>The beginning of a hunk within a file diff.</summary>
public sealed record GitDiffHunkRow(string RelativePath, int OldStart, int NewStart) : GitDiffRow(RelativePath);

/// <summary>A line with stable file/hunk identity so selection survives diff rebuilds.</summary>
public abstract record GitDiffLineRow(
    string RelativePath, int OldStart, int NewStart, GitDiffLine Line) : GitDiffRow(RelativePath);

/// <summary>A diff line rendered in one unified grid.</summary>
public sealed record GitDiffUnifiedLineRow(
    string RelativePath, int OldStart, int NewStart, GitDiffLine Line)
    : GitDiffLineRow(RelativePath, OldStart, NewStart, Line);

/// <summary>A diff line rendered in one side-by-side grid.</summary>
public sealed record GitDiffSideBySideLineRow(
    string RelativePath, int OldStart, int NewStart, GitDiffLine Line)
    : GitDiffLineRow(RelativePath, OldStart, NewStart, Line);
