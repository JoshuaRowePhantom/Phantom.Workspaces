using System.Collections.Generic;

namespace Phantom.Workspaces.ViewModels;

public sealed record GitDiffHunk
{
    public required int OldStart { get; init; }
    public required int NewStart { get; init; }
    public required IReadOnlyList<GitDiffLine> Lines { get; init; }
}
