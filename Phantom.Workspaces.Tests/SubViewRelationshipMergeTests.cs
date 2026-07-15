using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class SubViewRelationshipMergeTests
{
    private static readonly GetRelationshipRequest RelatedRequest = new()
    {
        RelationshipTypeNames = new RelationshipTypeNameSet(["related"]),
    };

    private static InterestCatalog CatalogWithInterestA() =>
        new([new InterestTypeDefinition("interest-a", "●", "○", "", "", "", "", null)]);

    private static QueryRequest MinimalQuery(IReadOnlyCollection<GetRelationshipRequest>? relationships) =>
        new()
        {
            Clauses = [],
            RelationshipsToReturn = relationships,
        };

    [Fact]
    public void WithInterestRelationships_WhenQueryHasRelationshipsToReturn_MergesWithInterestRelationships()
    {
        var query = MinimalQuery([RelatedRequest]);
        var catalog = CatalogWithInterestA();

        var result = MainWindowViewModel.WithInterestRelationships(query, catalog);

        var typeNameSets = result.RelationshipsToReturn!
            .Select(static r => r.RelationshipTypeNames?.Values ?? [])
            .SelectMany(static v => v)
            .ToHashSet();

        Assert.Contains("related", typeNameSets);
        Assert.Contains("interest-a", typeNameSets);
    }

    [Fact]
    public void WithInterestRelationships_WhenQueryHasNoRelationshipsToReturn_IncludesRelatedAndInterestRelationships()
    {
        var query = MinimalQuery(null);
        var catalog = CatalogWithInterestA();

        var result = MainWindowViewModel.WithInterestRelationships(query, catalog);

        var typeNameSets = result.RelationshipsToReturn!
            .Select(static r => r.RelationshipTypeNames?.Values ?? [])
            .SelectMany(static v => v)
            .ToHashSet();

        Assert.Contains("interest-a", typeNameSets);
        Assert.Contains("related", typeNameSets);
    }

    [Fact]
    public void WithInterestRelationships_WhenNoCatalog_AddsRelatedRelationships()
    {
        var query = MinimalQuery(null);

        var result = MainWindowViewModel.WithInterestRelationships(query, null);

        var relationship = Assert.Single(result.RelationshipsToReturn!);
        Assert.Contains("related", relationship.RelationshipTypeNames?.Values ?? []);
    }
}
