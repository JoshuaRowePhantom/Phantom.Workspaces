namespace Phantom.Workspaces.Services;

/// <summary>
/// Opaque server-authoritative cursor value handed to the remote-session runtime when
/// re-attaching to a live session (issue #1485). Commit 2 defines the type only; transport
/// behaviour is added in a subsequent commit.
/// </summary>
public sealed record ReplayCursor
{
    /// <summary>
    /// Server-issued epoch string; combined with <see cref="GlobalSequence"/> forms the pair that
    /// the session stream replays from. Nonblank.
    /// </summary>
    public required string Epoch { get; init; }

    /// <summary>Last acknowledged global sequence number, monotonic within an epoch.</summary>
    public required long GlobalSequence { get; init; }
}
