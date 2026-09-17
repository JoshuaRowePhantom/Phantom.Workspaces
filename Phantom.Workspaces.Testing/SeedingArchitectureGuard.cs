using System.Text.RegularExpressions;

namespace Phantom.Workspaces.Testing;

public static partial class SeedingArchitectureGuard
{
    public static IReadOnlyCollection<string> FindViolations(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var violations = new List<string>();

        if (DataAccessLayerImplementation().IsMatch(source)
            && SnapshotConstructionOrCollection().IsMatch(source))
        {
            violations.Add(
                "Ad-hoc IDataAccessLayer implementations must not manufacture persisted entity snapshots.");
        }

        foreach (Match construction in InMemoryConstruction().Matches(source))
        {
            var variableName = construction.Groups["variable"].Value;
            if (Regex.IsMatch(
                    source,
                    $@"\b{Regex.Escape(variableName)}\s*\.\s*UpdateAsync\s*\(",
                    RegexOptions.CultureInvariant))
            {
                violations.Add(
                    $"Raw InMemoryDataAccessLayer variable '{variableName}' must not receive entity updates.");
            }
        }

        return violations;
    }

    [GeneratedRegex(@":\s*IDataAccessLayer\b", RegexOptions.CultureInvariant)]
    private static partial Regex DataAccessLayerImplementation();

    [GeneratedRegex(
        @"(?:new\s+|(?:IReadOnlyCollection|IReadOnlyList|List|Array)\s*<\s*)(?:Query)?EntitySnapshot\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotConstructionOrCollection();

    [GeneratedRegex(
        @"(?:var|InMemoryDataAccessLayer)\s+(?<variable>[A-Za-z_]\w*)\s*=\s*new\s+InMemoryDataAccessLayer\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex InMemoryConstruction();
}
