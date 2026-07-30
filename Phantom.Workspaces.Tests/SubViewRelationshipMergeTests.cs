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

    private static InterestTypeDefinition StandardInterest(string name) =>
        new(name, "●", "○", "", "", "", "", null,
            TargetParticipant: "target",
            AppliesTo: [new InterestAppliesTo("user", new HashSet<string> { "user" }, InterestSessionValue.UserEntityId)]);

    private static InterestTypeDefinition DefaultInterest() =>
        new("default", "⭐", "☆", "", "", "", "", new HashSet<string> { "workspace" },
            TargetParticipant: "value",
            AppliesTo: [new InterestAppliesTo("applied-to", new HashSet<string> { "user-computer-profile" }, InterestSessionValue.UserComputerProfileEntityId)]);

    private static InterestCatalog CatalogWithInterestA() =>
        new([StandardInterest("interest-a")]);

    private static InterestCatalog CatalogWithAllExistingInterests() =>
        new([StandardInterest("actionable"), StandardInterest("blocked"), StandardInterest("assigned-to"), StandardInterest("not-interesting"), DefaultInterest()]);

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

    [Fact]
    public void WithInterestRelationships_WithAllExistingInterests_RequestsEveryInterestRelationshipType()
    {
        var query = MinimalQuery(null);
        var catalog = CatalogWithAllExistingInterests();

        var result = MainWindowViewModel.WithInterestRelationships(query, catalog);

        var typeNames = result.RelationshipsToReturn!
            .Select(static r => r.RelationshipTypeNames?.Values ?? [])
            .SelectMany(static v => v)
            .ToHashSet();

        Assert.Contains("related", typeNames);
        Assert.Contains("actionable", typeNames);
        Assert.Contains("blocked", typeNames);
        Assert.Contains("assigned-to", typeNames);
        Assert.Contains("not-interesting", typeNames);
        Assert.Contains("default", typeNames);
    }

    [Fact]
    public void WithInterestRelationships_WithJsonDeclaredRelationships_PreservesThemAlongsideInterests()
    {
        var jsonDeclared = new GetRelationshipRequest
        {
            RelationshipTypeNames = new RelationshipTypeNameSet(["custom-declared"]),
        };
        var query = MinimalQuery([jsonDeclared]);
        var catalog = CatalogWithAllExistingInterests();

        var result = MainWindowViewModel.WithInterestRelationships(query, catalog);

        var typeNames = result.RelationshipsToReturn!
            .Select(static r => r.RelationshipTypeNames?.Values ?? [])
            .SelectMany(static v => v)
            .ToHashSet();

        Assert.Contains("custom-declared", typeNames);
        Assert.Contains("related", typeNames);
        Assert.Contains("actionable", typeNames);
        Assert.Contains("default", typeNames);
    }
}
