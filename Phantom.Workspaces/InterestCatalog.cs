using System.Collections.Generic;
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
/// entity's applied interests into toggleable badge glyphs.
/// </summary>
public sealed class InterestCatalog
{
    private const string InterestTypeEntityType = "interest-type";

    public InterestCatalog(IReadOnlyList<InterestTypeDefinition> interestTypes)
    {
        this.InterestTypes = interestTypes;
    }

    public IReadOnlyList<InterestTypeDefinition> InterestTypes { get; }

    /// <summary>The set of interest-type names, for filtering an entity's relationships.</summary>
    public IReadOnlySet<string> InterestTypeNames => this.InterestTypes.Select(static type => type.Name).ToHashSet();

    /// <summary>Loads the interest catalog by querying the interest-type definition entities.</summary>
    public static async Task<InterestCatalog> LoadAsync(EntityBroker entityBroker, CancellationToken cancellationToken = default)
    {
        var definitions = await entityBroker.QueryEntitiesAsync(
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

        var interestTypes = new List<InterestTypeDefinition>();
        foreach (var definition in definitions)
        {
            if (TryReadInterestType(definition.Snapshot, out var interestType))
            {
                interestTypes.Add(interestType);
            }
        }

        return new InterestCatalog(interestTypes);
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
