using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSchema;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Maps a <c>(manifest, secretName, useDisplayString)</c> triple to the ordered
/// <see cref="SecretUseMemory"/> candidate list expected by <see cref="SecretRequest.Memories"/>.
/// Operates purely on manifest names, identity and content; secret values are never inputs nor
/// outputs.
/// </summary>
public sealed class AgentManifestSecretUseMemoryFactory
{
    /// <summary>The metadata key carrying a manifest's stable entity identity.</summary>
    public const string EntityIdMetadataKey = "entity-id";

    /// <summary>
    /// The agent-definition metadata key carrying the stable identity (<c>entity-id</c>) of the
    /// manifest a definition was projected from. Written at manifest → definition projection so a
    /// manifest-less session launch can recompute the same <see cref="SecretUseScope.ManifestIdentity"/>
    /// hash.
    /// </summary>
    public const string OriginManifestIdMetadataKey = "origin-manifest-id";

    /// <summary>
    /// The agent-definition metadata key carrying the SHA-256 of the origin manifest template's
    /// canonical JSON. Written at manifest → definition projection so a manifest-less session launch
    /// can recompute the same <see cref="SecretUseScope.ManifestContent"/> hash.
    /// </summary>
    public const string OriginManifestContentHashMetadataKey = "origin-manifest-content-hash";

    // Human-readable labels per the secret-store design's ordered scope table.
    private static readonly IReadOnlyDictionary<SecretUseScope, string> DisplayStrings =
        new Dictionary<SecretUseScope, string>
        {
            [SecretUseScope.AllUses] = "All Uses",
            [SecretUseScope.AnyManifest] = "Any Manifest",
            [SecretUseScope.KeyInAnyManifest] = "This Key in Any Manifest",
            [SecretUseScope.ManifestIdentity] = "This Manifest, Even if Changed",
            [SecretUseScope.ManifestContent] = "This Manifest",
            [SecretUseScope.KeyInManifestContent] = "This Key in This Manifest",
            [SecretUseScope.SessionIdentity] = "This Session",
            [SecretUseScope.KeyInSession] = "This Key in This Session",
            [SecretUseScope.AlwaysAsk] = "Always Ask",
        };

    /// <summary>
    /// The serializable lineage inputs from which the consent-scope hashes are derived. Manifest
    /// identity/content are present only when a launch derives from a manifest (either a live
    /// manifest, or the <c>origin-manifest-*</c> metadata carried on a session definition);
    /// <see cref="SessionIdentity"/> is present whenever the launch has a stable session id.
    /// </summary>
    public sealed record SecretUseLineage(
        string? ManifestIdentity,
        string? ManifestContentHash,
        string? SessionIdentity);

    /// <summary>
    /// Builds the ordered candidate list <c>AllUses, AnyManifest, KeyInAnyManifest,
    /// ManifestIdentity, ManifestContent, KeyInManifestContent, AlwaysAsk</c>. The
    /// <see cref="SecretUseScope.ManifestIdentity"/> candidate is omitted when the manifest has no
    /// stable <c>entity-id</c>. Equivalent to <see cref="Build(SecretUseLineage, string, string)"/>
    /// with a manifest-only lineage (no session identity).
    /// </summary>
    public IReadOnlyList<SecretUseMemory> Build(
        AgentManifest manifest,
        string secretName,
        string useDisplayString)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var lineage = new SecretUseLineage(
            ReadStableManifestIdentity(manifest),
            ComputeManifestContentHash(manifest),
            SessionIdentity: null);

        return this.Build(lineage, secretName, useDisplayString);
    }

    /// <summary>
    /// Builds the ordered <see cref="SecretUseMemory"/> candidate list (broadest → narrowest) as the
    /// union of the scopes applicable to <paramref name="lineage"/>. Manifest scopes are included
    /// only when the lineage carries the corresponding manifest identity/content; session scopes are
    /// included only when the lineage carries a session identity. <see cref="SecretUseScope.AllUses"/>,
    /// <see cref="SecretUseScope.AnyManifest"/>, <see cref="SecretUseScope.KeyInAnyManifest"/> and
    /// <see cref="SecretUseScope.AlwaysAsk"/> are always present.
    /// </summary>
    public IReadOnlyList<SecretUseMemory> Build(
        SecretUseLineage lineage,
        string secretName,
        string useDisplayString)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentException.ThrowIfNullOrEmpty(secretName);
        ArgumentNullException.ThrowIfNull(useDisplayString);

        var manifestIdentity = string.IsNullOrEmpty(lineage.ManifestIdentity) ? null : lineage.ManifestIdentity;
        var manifestContentHash = string.IsNullOrEmpty(lineage.ManifestContentHash) ? null : lineage.ManifestContentHash;
        var sessionIdentity = string.IsNullOrEmpty(lineage.SessionIdentity) ? null : lineage.SessionIdentity;

        var scopes = new List<SecretUseScope>
        {
            SecretUseScope.AllUses,
            SecretUseScope.AnyManifest,
            SecretUseScope.KeyInAnyManifest,
        };

        if (manifestIdentity is not null)
        {
            scopes.Add(SecretUseScope.ManifestIdentity);
        }

        if (manifestContentHash is not null)
        {
            scopes.Add(SecretUseScope.ManifestContent);
            scopes.Add(SecretUseScope.KeyInManifestContent);
        }

        if (sessionIdentity is not null)
        {
            scopes.Add(SecretUseScope.SessionIdentity);
            scopes.Add(SecretUseScope.KeyInSession);
        }

        scopes.Add(SecretUseScope.AlwaysAsk);

        var memories = new List<SecretUseMemory>(scopes.Count);
        foreach (var scope in scopes)
        {
            var hash = SecretUseScopePreimage.ComputeHash(
                scope,
                secretName,
                useDisplayString,
                manifestIdentity,
                manifestContentHash,
                sessionIdentity);

            memories.Add(new SecretUseMemory(scope, DisplayStrings[scope], hash));
        }

        return memories;
    }

    /// <summary>
    /// Builds the serializable <see cref="SecretUseLineage"/> for a launch. Manifest identity and
    /// content are taken from <paramref name="manifest"/> when a live manifest is present, otherwise
    /// from the <c>origin-manifest-*</c> metadata carried on <paramref name="definition"/>. The
    /// session identity is <paramref name="agentSessionId"/> when present.
    /// </summary>
    public static SecretUseLineage CreateLineage(
        AgentManifest? manifest,
        AgentDefinition definition,
        string? agentSessionId)
    {
        ArgumentNullException.ThrowIfNull(definition);

        string? manifestIdentity;
        string? manifestContentHash;
        if (manifest is not null)
        {
            manifestIdentity = ReadStableManifestIdentity(manifest);
            manifestContentHash = ComputeManifestContentHash(manifest);
        }
        else
        {
            manifestIdentity = ReadDefinitionMetadataString(definition, OriginManifestIdMetadataKey);
            manifestContentHash = ReadDefinitionMetadataString(definition, OriginManifestContentHashMetadataKey);
        }

        return new SecretUseLineage(
            manifestIdentity,
            manifestContentHash,
            string.IsNullOrEmpty(agentSessionId) ? null : agentSessionId);
    }

    /// <summary>
    /// Reads the manifest's stable <c>entity-id</c>, or <see langword="null"/> when absent.
    /// </summary>
    public static string? ReadStableManifestIdentity(AgentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Metadata is null
            || !manifest.Metadata.TryGetValue(EntityIdMetadataKey, out var value)
            || value is null)
        {
            return null;
        }

        var identity = value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            string text => text,
            _ => value.ToString(),
        };

        return string.IsNullOrEmpty(identity) ? null : identity;
    }

    /// <summary>
    /// Computes the SHA-256 (lowercase hex) of the manifest template's canonical JSON.
    /// </summary>
    public static string ComputeManifestContentHash(AgentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var template = manifest.Template
            ?? throw new InvalidOperationException("Agent manifest does not specify a template agent definition.");

        using var document = JsonDocument.Parse(template.ToJson());
        var canonical = CanonicalJson.Encode(document.RootElement);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }

    private static string? ReadDefinitionMetadataString(AgentDefinition definition, string key)
    {
        if (definition.Metadata is null
            || !definition.Metadata.TryGetValue(key, out var value)
            || value is null)
        {
            return null;
        }

        var text = value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            string s => s,
            _ => value.ToString(),
        };

        return string.IsNullOrEmpty(text) ? null : text;
    }
}
