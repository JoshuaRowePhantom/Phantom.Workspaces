using GitHub.Copilot.Rpc;
using Phantom.Workspaces.Llm.Copilot;

namespace Phantom.Workspaces.Llm.Core.Tests;

#pragma warning disable GHCP001
public sealed class RealCopilotClientAdapterTests
{
    [Fact]
    public void MapLiveSessionStates_UsesHostStatusWithoutInventingPublisherRevision()
    {
        var sessions = new SessionList
        {
            Sessions =
            [
                Entry("child", RemoteSessionHostStatus.Working),
                Entry("waiting", RemoteSessionHostStatus.InputNeeded),
                Entry("failed", RemoteSessionHostStatus.Error),
                Entry("done", RemoteSessionHostStatus.Idle),
                Entry("absent", null),
            ],
        };
        var mapped = RealCopilotClientAdapter.MapLiveSessionStates(sessions);
        Assert.Equal(["child", "waiting", "failed", "done"], mapped.Select(entry => entry.SessionId));
        Assert.Equal(["working", "waiting", "attention", "done"],
            mapped.Select(entry => entry.Status!.Value.Value));
        Assert.All(mapped, entry => Assert.Null(entry.StatusRevision));

        static SessionListEntry Entry(string id, RemoteSessionHostStatus? status) => new()
        {
            SessionId = id, StartTime = "2026-01-01T00:00:00Z", ModifiedTime = "2026-01-01T00:00:00Z",
            HostStatus = status,
        };
    }
}
