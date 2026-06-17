using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces;

/// <summary>
/// The presentation metadata for one interest type (for example <c>actionable</c>): the glyphs and
/// descriptions shown when the interest is or is not applied to an entity. Read from the interest-type
/// definition entity's <c>applied</c>/<c>notApplied</c> properties.
/// </summary>
public sealed record InterestTypeDefinition(
    string Name,
    string AppliedGlyph,
    string NotAppliedGlyph,
    string AppliedDescription,
    string NotAppliedDescription,
    string AppliedActionText,
    string NotAppliedActionText);

/// <summary>
/// The set of interest types known to the workspace (actionable, blocked, assigned-to,
/// not-interesting, ...), loaded from the interest-type definition entities. Used to project an
/// entity's applied interests into toggleable badge glyphs. Dynamically updates when interest-type
/// entities are added, removed, or changed.
/// </summary>
public sealed class InterestCatalog : IDisposable
{
    private const string InterestTypeEntityType = "interest-type";
    private readonly SubscribedQuery? subscribedQuery;
    private IReadOnlyList<InterestTypeDefinition> interestTypes;

    private InterestCatalog(SubscribedQuery subscribedQuery)
    {
        this.subscribedQuery = subscribedQuery;
        this.interestTypes = [];
        this.subscribedQuery.Results.CollectionChanged += this.OnQueryResultsChanged;
        this.RefreshInterestTypes();
    }

    /// <summary>Creates a static catalog for testing purposes.</summary>
    public InterestCatalog(IReadOnlyList<InterestTypeDefinition> interestTypes)
    {
        this.subscribedQuery = null;
        this.interestTypes = interestTypes;
    }

    public IReadOnlyList<InterestTypeDefinition> InterestTypes => this.interestTypes;

    /// <summary>The set of interest-type names, for filtering an entity's relationships.</summary>
    public IReadOnlySet<string> InterestTypeNames => this.InterestTypes.Select(static type => type.Name).ToHashSet();

    /// <summary>Raised when interest types are added, removed, or changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Creates a dynamic interest catalog that observes interest-type definition entities.</summary>
    public static async Task<InterestCatalog> CreateAsync(EntityBroker entityBroker, CancellationToken cancellationToken = default)
    {
        var query = await entityBroker.SubscribeQueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("interest-types"),
                        Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet([InterestTypeEntityType]) },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        return new InterestCatalog(query);
    }

    public void Dispose()
    {
        if (this.subscribedQuery is not null)
        {
            this.subscribedQuery.Results.CollectionChanged -= this.OnQueryResultsChanged;
        }
    }

    private void OnQueryResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.RefreshInterestTypes();
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshInterestTypes()
    {
        if (this.subscribedQuery is null)
        {
            return;
        }

        var types = new List<InterestTypeDefinition>();
        foreach (var definition in this.subscribedQuery.Results)
        {
            if (TryReadInterestType(definition.Snapshot, out var interestType))
            {
                types.Add(interestType);
            }
        }

        this.interestTypes = types;
    }

    private static bool TryReadInterestType(EntitySnapshot snapshot, out InterestTypeDefinition interestType)
    {
        interestType = null!;
        if (snapshot.Data is not { } data
            || !TryReadInterestTypeName(data, out var name))
        {
            return false;
        }

        ReadIndicator(data, "applied", out var appliedGlyph, out var appliedDescription, out var appliedActionText);
        ReadIndicator(data, "notApplied", out var notAppliedGlyph, out var notAppliedDescription, out var notAppliedActionText);

        interestType = new InterestTypeDefinition(
            name,
            string.IsNullOrEmpty(appliedGlyph) ? "●" : appliedGlyph,
            string.IsNullOrEmpty(notAppliedGlyph) ? "○" : notAppliedGlyph,
            appliedDescription,
            notAppliedDescription,
            appliedActionText,
            notAppliedActionText);
        return true;
    }

    private static bool TryReadInterestTypeName(JsonElement data, out string name)
    {
        name = string.Empty;
        if (!data.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // Use the ["entity-types", "<name>"] name as the interest type's canonical name.
        foreach (var nameComponents in names.EnumerateArray())
        {
            if (nameComponents.ValueKind == JsonValueKind.Array
                && nameComponents.GetArrayLength() == 2
                && nameComponents[0].ValueKind == JsonValueKind.String
                && string.Equals(nameComponents[0].GetString(), "entity-types", System.StringComparison.Ordinal)
                && nameComponents[1].ValueKind == JsonValueKind.String)
            {
                name = nameComponents[1].GetString()!;
                return true;
            }
        }

        return false;
    }

    private static void ReadIndicator(JsonElement data, string propertyName, out string glyph, out string description, out string actionText)
    {
        glyph = string.Empty;
        description = string.Empty;
        actionText = string.Empty;
        if (!data.TryGetProperty(propertyName, out var indicator) || indicator.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        glyph = ReadString(indicator, "indicator");
        description = ReadString(indicator, "description");
        actionText = ReadString(indicator, "actionText");
    }

    private static string ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;
}
