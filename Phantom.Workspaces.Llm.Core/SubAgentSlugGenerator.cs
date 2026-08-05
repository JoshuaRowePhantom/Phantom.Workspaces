using System.Globalization;
using System.Text;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Generates a short, human-readable slug id for a sub-agent from the text of its initial prompt.
/// Slugs are lowercase, hyphen-separated, and limited to the first five words. When a generated
/// slug collides with an existing sub-agent id, a numeric deduplication suffix is appended.
/// </summary>
public static class SubAgentSlugGenerator
{
    private const int MaxWords = 5;
    private const string FallbackSlug = "sub-agent";

    /// <summary>
    /// Derives an id slug from <paramref name="prompt"/>: lowercase, hyphenated, at most five words.
    /// If the slug collides with an entry in <paramref name="existingIds"/> (case-insensitive), a
    /// deduplication suffix (<c>-2</c>, <c>-3</c>, …) is appended until the result is unique.
    /// </summary>
    public static string GenerateSlug(string prompt, IEnumerable<string>? existingIds = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var baseSlug = BuildBaseSlug(prompt);

        var taken = existingIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseSlug))
        {
            return baseSlug;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseSlug}-{suffix.ToString(CultureInfo.InvariantCulture)}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string BuildBaseSlug(string prompt)
    {
        var words = new List<string>(MaxWords);
        var current = new StringBuilder();

        foreach (var character in prompt)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
                if (words.Count == MaxWords)
                {
                    break;
                }
            }
        }

        if (current.Length > 0 && words.Count < MaxWords)
        {
            words.Add(current.ToString());
        }

        return words.Count == 0 ? FallbackSlug : string.Join('-', words);
    }
}
