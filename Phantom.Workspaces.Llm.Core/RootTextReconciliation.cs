using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

internal static class RootTextReconciliation
{
    internal static bool IsReplacement(IList<AIContent> contents) =>
        contents.OfType<TextContent>().Any(content =>
            content.AdditionalProperties?.GetValueOrDefault(StoreRequestAgent.ReplacementPropertyName) is true);
}
