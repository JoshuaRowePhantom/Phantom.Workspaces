namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// The scope at which a user's decision to allow a secret to be used is remembered.
/// Ordered from broadest (<see cref="AllUses"/>) to narrowest; <see cref="AlwaysAsk"/>
/// is never remembered and always re-prompts.
/// </summary>
public enum SecretUseScope
{
    /// <summary>Allow this source for every secret use, regardless of manifest or secret name.</summary>
    AllUses,

    /// <summary>Allow this secret name for any manifest.</summary>
    AnyManifest,

    /// <summary>Allow this secret name at this specific use path, for any manifest.</summary>
    KeyInAnyManifest,

    /// <summary>Allow this secret name for a manifest identified by its stable identity.</summary>
    ManifestIdentity,

    /// <summary>Allow this secret name for a manifest identified by the hash of its canonical content.</summary>
    ManifestContent,

    /// <summary>Allow this secret name at this specific use path, for a manifest identified by content hash.</summary>
    KeyInManifestContent,

    /// <summary>Allow this secret name for a single session identified by its stable agent-session-id.</summary>
    SessionIdentity,

    /// <summary>Allow this secret name at this specific use path, for a single session.</summary>
    KeyInSession,

    /// <summary>Never remember; always ask the user afresh.</summary>
    AlwaysAsk,
}
