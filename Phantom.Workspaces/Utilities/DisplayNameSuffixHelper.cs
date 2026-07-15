using System.Text.RegularExpressions;

namespace Phantom.Workspaces.Utilities;

public static partial class DisplayNameSuffixHelper
{
    public static string GetNextAvailableName(string baseName, IEnumerable<string> existingNames)
    {
        var root = SuffixRegex().Replace(baseName.Trim(), string.Empty);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = baseName.Trim();
        }

        var used = new HashSet<int>();
        foreach (var existingName in existingNames)
        {
            var trimmed = existingName.Trim();
            if (string.Equals(trimmed, root, StringComparison.Ordinal))
            {
                used.Add(1);
                continue;
            }

            var match = SuffixRegex().Match(trimmed);
            if (match.Success
                && string.Equals(trimmed[..match.Index], root, StringComparison.Ordinal)
                && int.TryParse(match.Groups[1].Value, out var suffix))
            {
                used.Add(suffix);
            }
        }

        if (used.Count == 0)
        {
            return root;
        }

        for (var i = 2; ; i++)
        {
            if (!used.Contains(i))
            {
                return $"{root} ({i})";
            }
        }
    }

    [GeneratedRegex(@"\s\((\d+)\)$")]
    private static partial Regex SuffixRegex();
}
