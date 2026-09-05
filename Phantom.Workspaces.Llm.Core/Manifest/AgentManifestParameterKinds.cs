using System;

namespace Phantom.Workspaces.Llm.Core.Manifest;

/// <summary>
/// Well-known manifest parameter <c>kind</c> values (issue #1434, per-component-executor-binding).
/// </summary>
/// <remarks>
/// Parameter kind is read from the manifest parameter <c>kind</c> field
/// (<c>AgentSchema.Property.Kind</c>), not inferred by name. A parameter of kind
/// <see cref="Executor"/> lets the user pick, at launch, which executor a <c>parameter</c>-strategy
/// executor resource resolves to; its disambiguated selection is recorded in the session's typed
/// <c>parameter-selections</c> map (see <see cref="ExecutorParameterSelection"/>), NOT in the
/// <c>string→string</c> <c>parameter-values</c> text-templating map.
/// </remarks>
public static class AgentManifestParameterKinds
{
    /// <summary>The <c>executor</c> parameter kind (a structured launch-time executor selection).</summary>
    public const string Executor = "executor";

    /// <summary>Whether the given parameter <c>kind</c> is the <see cref="Executor"/> kind.</summary>
    public static bool IsExecutor(string? kind)
        => string.Equals(kind, Executor, StringComparison.Ordinal);
}
