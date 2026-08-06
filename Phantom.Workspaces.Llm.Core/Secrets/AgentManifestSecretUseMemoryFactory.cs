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
            [SecretUseScope.AlwaysAsk] = "Always Ask",
        };

    /// <summary>
    /// Builds the ordered candidate list <c>AllUses, AnyManifest, KeyInAnyManifest,
    /// ManifestIdentity, ManifestContent, KeyInManifestContent, AlwaysAsk</c>. The
    /// <see cref="SecretUseScope.ManifestIdentity"/> candidate is omitted when the manifest has no
    /// stable <c>entity-id</c>.
    /// </summary>
    public IReadOnlyList<SecretUseMemory> Build(
        AgentManifest manifest,
        string secretName,
        string useDisplayString)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(secretName);
        ArgumentNullException.ThrowIfNull(useDisplayString);

        var stableManifestIdentity = ReadStableManifestIdentity(manifest);
        var manifestContentHash = ComputeManifestContentHash(manifest);

        var scopes = new List<SecretUseScope>
        {
            SecretUseScope.AllUses,
            SecretUseScope.AnyManifest,
            SecretUseScope.KeyInAnyManifest,
        };

        if (!string.IsNullOrEmpty(stableManifestIdentity))
        {
            scopes.Add(SecretUseScope.ManifestIdentity);
        }

        scopes.Add(SecretUseScope.ManifestContent);
        scopes.Add(SecretUseScope.KeyInManifestContent);
        scopes.Add(SecretUseScope.AlwaysAsk);

        var memories = new List<SecretUseMemory>(scopes.Count);
        foreach (var scope in scopes)
        {
            var hash = SecretUseScopePreimage.ComputeHash(
                scope,
                secretName,
                useDisplayString,
                stableManifestIdentity,
                manifestContentHash);

            memories.Add(new SecretUseMemory(scope, DisplayStrings[scope], hash));
        }

        return memories;
    }

    private static string? ReadStableManifestIdentity(AgentManifest manifest)
    {
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

    private static string ComputeManifestContentHash(AgentManifest manifest)
    {
        var template = manifest.Template
            ?? throw new InvalidOperationException("Agent manifest does not specify a template agent definition.");

        using var document = JsonDocument.Parse(template.ToJson());
        var canonical = CanonicalJson.Encode(document.RootElement);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }
}
