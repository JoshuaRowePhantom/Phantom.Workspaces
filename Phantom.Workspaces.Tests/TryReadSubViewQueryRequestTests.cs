using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class TryReadSubViewQueryRequestTests
{
    private static InterestTypeDefinition StandardInterest(string name) =>
        new(name, "●", "○", "", "", "", "", null,
            TargetParticipant: "target",
            AppliesTo: [new InterestAppliesTo("user", new HashSet<string> { "user" }, InterestSessionValue.UserEntityId)]);

    private static InterestTypeDefinition DefaultInterest() =>
        new("default", "⭐", "☆", "", "", "", "", new HashSet<string> { "workspace" },
            TargetParticipant: "value",
            AppliesTo: [new InterestAppliesTo("applied-to", new HashSet<string> { "user-computer-profile" }, InterestSessionValue.UserComputerProfileEntityId)]);

    private static InterestCatalog CatalogWithAllExistingInterests() =>
        new([StandardInterest("actionable"), StandardInterest("blocked"), StandardInterest("assigned-to"), StandardInterest("not-interesting"), DefaultInterest()]);

    private static bool InvokeTryReadSubViewQueryRequest(JsonElement subView, InterestCatalog? catalog, out QueryRequest? queryRequest)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "TryReadSubViewQueryRequest",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var parameters = new object?[] { subView, catalog, null };
        var result = (bool)method!.Invoke(null, parameters)!;
        queryRequest = (QueryRequest?)parameters[2];
        return result;
    }

    private static bool InvokeTryReadSubViewGetRequest(JsonElement subView, InterestCatalog? catalog, out GetRequest? getRequest)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "TryReadSubViewGetRequest",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var parameters = new object?[] { subView, catalog, null };
        var result = (bool)method!.Invoke(null, parameters)!;
        getRequest = (GetRequest?)parameters[2];
        return result;
    }

    private static bool InvokeTryReadGetEntityRequest(JsonElement getEntityElement, InterestCatalog? catalog, out GetEntityRequest? getEntityRequest)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "TryReadGetEntityRequest",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var parameters = new object?[] { getEntityElement, catalog, null };
        var result = (bool)method!.Invoke(null, parameters)!;
        getEntityRequest = (GetEntityRequest?)parameters[2];
        return result;
    }

    private static HashSet<string> RelationshipTypeNames(IReadOnlyCollection<GetRelationshipRequest>? relationships)
        => relationships is null
            ? []
            : relationships
                .Select(static r => r.RelationshipTypeNames?.Values ?? [])
                .SelectMany(static v => v)
                .ToHashSet();

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

        var returned = InvokeTryReadSubViewQueryRequest(doc.RootElement, null, out var queryRequest);

        Assert.True(returned);
        Assert.NotNull(queryRequest);
        Assert.NotNull(queryRequest!.RelationshipsToReturn);
        var typeNames = RelationshipTypeNames(queryRequest.RelationshipsToReturn);
        Assert.Contains("related", typeNames);
    }

    [Fact]
    public void TryReadSubViewQueryRequest_WithoutRelationshipsToReturn_StillIncludesRelated()
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

        var returned = InvokeTryReadSubViewQueryRequest(doc.RootElement, null, out var queryRequest);

        Assert.True(returned);
        Assert.NotNull(queryRequest);
        var typeNames = RelationshipTypeNames(queryRequest!.RelationshipsToReturn);
        Assert.Contains("related", typeNames);
    }

    [Fact]
    public void TryReadSubViewQueryRequest_ForViewDefinition_GeneratedQueryRetrievesEveryInterestType()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "query": {
                "clauses": [
                  {
                    "clause-identifier": { "value": "workspaces" },
                    "clause": {
                      "clause-type": "entity-type",
                      "entity-type-names": { "values": ["workspace"] }
                    }
                  }
                ]
              },
              "relationships-to-return": [
                { "relationship-type-names": ["custom-declared"] }
              ]
            }
            """);

        var returned = InvokeTryReadSubViewQueryRequest(doc.RootElement, CatalogWithAllExistingInterests(), out var queryRequest);

        Assert.True(returned);
        var typeNames = RelationshipTypeNames(queryRequest!.RelationshipsToReturn);
        Assert.Contains("custom-declared", typeNames);
        Assert.Contains("related", typeNames);
        Assert.Contains("actionable", typeNames);
        Assert.Contains("blocked", typeNames);
        Assert.Contains("assigned-to", typeNames);
        Assert.Contains("not-interesting", typeNames);
        Assert.Contains("default", typeNames);
    }

    [Fact]
    public void TryReadSubViewGetRequest_ForViewDefinition_GeneratedGetRetrievesEveryInterestType()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "get-entity": [
                { "entity-name": ["workspaces", "team"] }
              ],
              "relationships-to-return": [
                { "relationship-type-names": ["custom-declared"] }
              ]
            }
            """);

        var returned = InvokeTryReadSubViewGetRequest(doc.RootElement, CatalogWithAllExistingInterests(), out var getRequest);

        Assert.True(returned);
        var typeNames = RelationshipTypeNames(getRequest!.RelationshipsToReturn);
        Assert.Contains("custom-declared", typeNames);
        Assert.Contains("related", typeNames);
        Assert.Contains("actionable", typeNames);
        Assert.Contains("blocked", typeNames);
        Assert.Contains("assigned-to", typeNames);
        Assert.Contains("not-interesting", typeNames);
        Assert.Contains("default", typeNames);
    }

    [Fact]
    public void TryReadGetEntityRequest_ForViewDefinition_GeneratedGetEntityRetrievesEveryInterestType()
    {
        using var doc = JsonDocument.Parse(
            """
            {
              "entity-name": ["workspaces", "team"],
              "relationships-to-return": [
                { "relationship-type-names": ["custom-declared"] }
              ]
            }
            """);

        var returned = InvokeTryReadGetEntityRequest(doc.RootElement, CatalogWithAllExistingInterests(), out var getEntityRequest);

        Assert.True(returned);
        var typeNames = RelationshipTypeNames(getEntityRequest!.RelationshipsToReturn);
        Assert.Contains("custom-declared", typeNames);
        Assert.Contains("related", typeNames);
        Assert.Contains("actionable", typeNames);
        Assert.Contains("blocked", typeNames);
        Assert.Contains("assigned-to", typeNames);
        Assert.Contains("not-interesting", typeNames);
        Assert.Contains("default", typeNames);
    }
}
