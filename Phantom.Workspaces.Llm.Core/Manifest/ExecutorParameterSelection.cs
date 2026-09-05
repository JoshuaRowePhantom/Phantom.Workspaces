using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm.Core.Manifest;

/// <summary>
/// The disambiguated selection recorded for an <c>executor</c> launch parameter (issue #1434,
/// per-component-executor-binding, M7).
/// </summary>
/// <remarks>
/// <para>
/// An <c>executor</c> parameter offers a choice among two selectable option kinds — a
/// <b>trust-profile</b> entity ("choose by trust policy") or a <b>user-computer-profile</b> entity
/// (which synthesizes an implicit trust profile). The selection identifies BOTH the kind and the id as
/// a small JSON object — <c>{"trust-profile":"&lt;name-or-id&gt;"}</c> or
/// <c>{"user-computer-profile":"&lt;entity-id&gt;"}</c>.
/// </para>
/// <para>
/// The selection is recorded as a typed <see cref="JsonElement"/> entry in the session's dedicated
/// <c>parameter-selections</c> map (<c>string → JsonElement</c>), a <b>sibling root key</b> of the
/// <c>string→string</c> <c>parameter-values</c> text-templating map. It is NOT stored as a
/// JSON-encoded string inside <c>parameter-values</c>, and <c>parameter-values</c> is not widened to
/// <c>string→object</c>. The resolver (#1436) reads the <see cref="JsonElement"/> selection directly.
/// </para>
/// </remarks>
public static class ExecutorParameterSelection
{
    /// <summary>Selection discriminator: the id names an <c>llm-trust-profile</c> entity.</summary>
    public const string TrustProfileKind = "trust-profile";

    /// <summary>Selection discriminator: the id is a user-computer-profile entity-id.</summary>
    public const string UserComputerProfileKind = "user-computer-profile";

    /// <summary>
    /// Builds the disambiguated selection <c>{"trust-profile":"&lt;name-or-id&gt;"}</c>.
    /// </summary>
    public static JsonElement ForTrustProfile(string trustProfileNameOrId)
        => Build(TrustProfileKind, trustProfileNameOrId);

    /// <summary>
    /// Builds the disambiguated selection <c>{"user-computer-profile":"&lt;entity-id&gt;"}</c>.
    /// </summary>
    public static JsonElement ForUserComputerProfile(string entityId)
        => Build(UserComputerProfileKind, entityId);

    /// <summary>
    /// Reads the trust-profile name-or-id when <paramref name="selection"/> is a
    /// <see cref="TrustProfileKind"/> selection.
    /// </summary>
    public static bool TryGetTrustProfile(JsonElement selection, out string? trustProfileNameOrId)
        => TryGet(selection, TrustProfileKind, out trustProfileNameOrId);

    /// <summary>
    /// Reads the user-computer-profile entity-id when <paramref name="selection"/> is a
    /// <see cref="UserComputerProfileKind"/> selection.
    /// </summary>
    public static bool TryGetUserComputerProfile(JsonElement selection, out string? entityId)
        => TryGet(selection, UserComputerProfileKind, out entityId);

    private static JsonElement Build(string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Executor selection value must be non-empty.", nameof(value));
        }

        var node = new JsonObject { [kind] = value };

        // Detach from any owning document so the value survives independently.
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }

    private static bool TryGet(JsonElement selection, string kind, out string? value)
    {
        value = null;
        if (selection.ValueKind == JsonValueKind.Object
            && selection.TryGetProperty(kind, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        return false;
    }
}
