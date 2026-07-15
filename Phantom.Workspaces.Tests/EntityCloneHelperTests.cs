using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Utilities;

namespace Phantom.Workspaces.Tests;

public sealed class EntityCloneHelperTests
{
    [Fact]
    public void EntityCloneHelper_RewriteEntityId_ReplacesEntityId()
    {
        var cloneId = new EntityId("11111111-1111-1111-1111-111111111111");
        using var document = JsonDocument.Parse("""
        {
          "entity-id": "00000000-0000-0000-0000-000000000000",
          "display-name": "Session"
        }
        """);

        var rewritten = EntityCloneHelper.RewriteEntityId(document.RootElement, cloneId);

        Assert.Equal(cloneId.ToString(), rewritten.GetProperty("entity-id").GetString());
        Assert.Equal("Session", rewritten.GetProperty("display-name").GetString());
    }

    [Fact]
    public void EntityCloneHelper_RewriteRelationshipParticipantIds_ReplacesSourceId()
    {
        var sourceId = new EntityId("00000000-0000-0000-0000-000000000000");
        var cloneId = new EntityId("11111111-1111-1111-1111-111111111111");
        using var document = JsonDocument.Parse("""
        {
          "participants": {
            "source": { "entity-id": "00000000-0000-0000-0000-000000000000" },
            "nested": [
              { "entity-id": "00000000-0000-0000-0000-000000000000" },
              { "entity-id": "22222222-2222-2222-2222-222222222222" }
            ]
          },
          "outside-participants": "00000000-0000-0000-0000-000000000000"
        }
        """);

        var rewritten = EntityCloneHelper.RewriteRelationshipParticipantIds(document.RootElement, sourceId, cloneId);

        Assert.Equal(cloneId.ToString(), rewritten.GetProperty("participants").GetProperty("source").GetProperty("entity-id").GetString());
        Assert.Equal(cloneId.ToString(), rewritten.GetProperty("participants").GetProperty("nested")[0].GetProperty("entity-id").GetString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", rewritten.GetProperty("participants").GetProperty("nested")[1].GetProperty("entity-id").GetString());
        Assert.Equal(sourceId.ToString(), rewritten.GetProperty("outside-participants").GetString());
    }
}
