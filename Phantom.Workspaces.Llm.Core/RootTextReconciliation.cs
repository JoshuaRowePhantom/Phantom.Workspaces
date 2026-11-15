using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

internal static class RootTextReconciliation
{
    internal static bool IsReplacement(IList<AIContent> contents) =>
        contents.OfType<TextContent>().Any(content =>
            content.AdditionalProperties?.GetValueOrDefault(StoreRequestAgent.ReplacementPropertyName) is true);

    internal static T[] Normalize<T>(
        IEnumerable<T> updates,
        Func<T, string?> messageId,
        Func<T, IList<AIContent>> contents,
        Func<T, IList<AIContent>, T> withContents)
    {
        var result = new List<T>();
        foreach (var update in updates)
        {
            if (!IsReplacement(contents(update)))
            {
                result.Add(update);
                continue;
            }

            var replacement = contents(update).OfType<TextContent>().Single();
            var inserted = false;
            for (var i = 0; i < result.Count; i++)
            {
                if (!string.Equals(messageId(result[i]), messageId(update), StringComparison.Ordinal))
                    continue;
                var previous = contents(result[i]);
                if (!previous.OfType<TextContent>().Any()) continue;
                var reconciled = new List<AIContent>();
                foreach (var content in previous)
                {
                    if (content is not TextContent) reconciled.Add(content);
                    else if (!inserted)
                    {
                        reconciled.Add(new TextContent(replacement.Text));
                        inserted = true;
                    }
                }
                result[i] = withContents(result[i], reconciled);
            }
            if (!inserted) result.Add(withContents(update, [new TextContent(replacement.Text)]));
        }
        return result.ToArray();
    }
}
