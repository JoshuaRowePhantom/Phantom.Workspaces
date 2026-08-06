namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// A remembered decision that a secret may be used at a given <see cref="SecretUseScope"/>.
/// Holds no secret value — only the scope, a human-readable display string, and the stable hash.
/// </summary>
public sealed record SecretUseMemory(SecretUseScope Scope, string DisplayString, string Hash);
