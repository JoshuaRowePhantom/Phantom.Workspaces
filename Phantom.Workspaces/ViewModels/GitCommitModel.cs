using System;

namespace Phantom.Workspaces.ViewModels;

public sealed record GitCommitModel
{
    public static readonly string UnstagedSentinelId = "UNSTAGED";
    public static readonly string StagedSentinelId = "STAGED";

    public required string Oid { get; init; }
    public required string ShortMessage { get; init; }
    public required string AuthorName { get; init; }
    public required DateTimeOffset AuthorDate { get; init; }

    public bool IsUnstaged => string.Equals(this.Oid, UnstagedSentinelId, StringComparison.Ordinal);
    public bool IsStaged => string.Equals(this.Oid, StagedSentinelId, StringComparison.Ordinal);

    public static GitCommitModel CreateUnstaged() => new()
    {
        Oid = UnstagedSentinelId,
        ShortMessage = "Unstaged changes",
        AuthorName = string.Empty,
        AuthorDate = DateTimeOffset.MinValue,
    };

    public static GitCommitModel CreateStaged() => new()
    {
        Oid = StagedSentinelId,
        ShortMessage = "Staged changes",
        AuthorName = string.Empty,
        AuthorDate = DateTimeOffset.MinValue,
    };
}
