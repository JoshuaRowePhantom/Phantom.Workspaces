using System.Reflection;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class TryReadSubViewQueryRequestTests
{
    private static bool InvokeTryReadSubViewQueryRequest(JsonElement subView, out QueryRequest? queryRequest)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "TryReadSubViewQueryRequest",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var parameters = new object?[] { subView, null };
        var result = (bool)method!.Invoke(null, parameters)!;
        queryRequest = (QueryRequest?)parameters[1];
        return result;
    }

    [Fact]
    public void TryReadSubViewQueryRequest_WithRelationshipsToReturn_ParsesRelationshipsOntoQueryRequest()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "query": {
                "clauses": [
                  {
                    "clause-identifier": { "value": "test-type" },
                    "clause": {
                      "clause-type": "entity-type",
                      "entity-type-names": { "values": ["workspace"] }
                    }
                  }
                ]
              },
              "relationships-to-return": [
                {
                  "relationship-type-names": ["related"]
                }
              ]
            }
            """);

        var returned = InvokeTryReadSubViewQueryRequest(doc.RootElement, out var queryRequest);

        Assert.True(returned);
        Assert.NotNull(queryRequest);
        Assert.NotNull(queryRequest!.RelationshipsToReturn);
        var rel = Assert.Single(queryRequest.RelationshipsToReturn!);
        Assert.NotNull(rel.RelationshipTypeNames);
        Assert.Contains("related", rel.RelationshipTypeNames!.Value.Values);
    }

    [Fact]
    public void TryReadSubViewQueryRequest_WithoutRelationshipsToReturn_LeavesRelationshipsNull()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "query": {
                "clauses": [
                  {
                    "clause-identifier": { "value": "test-type" },
                    "clause": {
                      "clause-type": "entity-type",
                      "entity-type-names": { "values": ["workspace"] }
                    }
                  }
                ]
              }
            }
            """);

        var returned = InvokeTryReadSubViewQueryRequest(doc.RootElement, out var queryRequest);

        Assert.True(returned);
        Assert.NotNull(queryRequest);
        Assert.Null(queryRequest!.RelationshipsToReturn);
    }
}
