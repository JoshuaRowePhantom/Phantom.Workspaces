namespace Phantom.Workspaces.ViewModels;

public sealed record GitDiffLine
{
    public required GitDiffLineKind Kind { get; init; }
    public int? OldLineNumber { get; init; }
    public int? NewLineNumber { get; init; }
    public required string Content { get; init; }
}
