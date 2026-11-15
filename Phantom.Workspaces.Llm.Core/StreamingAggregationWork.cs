namespace Phantom.Workspaces.Llm;

internal sealed record StreamingAggregationWork
{
    public long UpdateVisits { get; init; }
    public long ContentVisits { get; init; }
    public long AggregatedCharacters { get; init; }
    public long MaterializedCharacters { get; init; }
    public long SnapshotReferences { get; init; }
}
